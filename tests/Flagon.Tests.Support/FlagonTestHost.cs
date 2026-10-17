using Flagon.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Flagon.Tests.Support;

/// <summary>Configures a <see cref="FlagonTestHost"/>.</summary>
public sealed class FlagonTestHostOptions
{
    /// <summary>
    /// The store the host uses. Leave null to let <c>AddFlagon</c> pick its default (InMemory).
    /// Pass the same instance to two hosts to simulate two apps sharing one database.
    /// </summary>
    public IFlagStore? Store { get; set; }

    /// <summary>The fake clock. Share one instance between hosts to keep them in lockstep. Created if null.</summary>
    public FakeTimeProvider? Time { get; set; }

    /// <summary>Host application name.</summary>
    public string ApplicationName { get; set; } = "TestApp";

    /// <summary>Host environment name.</summary>
    public string EnvironmentName { get; set; } = Environments.Development;

    /// <summary>
    /// Extra Flagon configuration, applied inside <c>AddFlagon(opts =&gt; ...)</c>. The harness turns
    /// <see cref="FlagonOptions.AutoCreate"/> off first (evaluating an unknown key would otherwise race a background
    /// create against the test's own assertions); tests that exercise auto-create opt back in here.
    /// </summary>
    public Action<FlagonOptions>? Flagon { get; set; }

    /// <summary>Extra service registrations, applied after <c>AddFlagon</c>.</summary>
    public Action<IServiceCollection>? Services { get; set; }

    /// <summary>
    /// Makes the host a <see cref="WebApplication"/> served by an in-memory <see cref="TestServer"/>: this delegate
    /// maps endpoints (e.g. <c>app =&gt; app.MapFlagonOfrep()</c>) after the authentication/authorization middleware.
    /// Call the app with <see cref="FlagonTestHost.CreateClient"/>. Null (default) builds a plain generic host.
    /// </summary>
    public Action<WebApplication>? Endpoints { get; set; }
}

/// <summary>
/// Host-seam test harness: a real generic host built with <c>AddFlagon(...)</c>, a fake <see cref="TimeProvider"/>
/// and captured logs. Tests interact only via the public surfaces exposed here.
/// </summary>
public sealed class FlagonTestHost : IAsyncDisposable
{
    /// <summary>The instant every fresh <see cref="FakeTimeProvider"/> starts at.</summary>
    public static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IHost _host;

    private FlagonTestHost(IHost host, FakeTimeProvider time, TestLogSink logs)
    {
        _host = host;
        Time = time;
        Logs = logs;
    }

    public IServiceProvider Services => _host.Services;
    public IFlagon Flagon => Services.GetRequiredService<IFlagon>();
    public IFlagonAdmin Admin => Services.GetRequiredService<IFlagonAdmin>();
    public IFlagStore Store => Services.GetRequiredService<IFlagStore>();
    public FakeTimeProvider Time { get; }
    public TestLogSink Logs { get; }

    /// <summary>Builds and starts a host.</summary>
    public static async Task<FlagonTestHost> StartAsync(Action<FlagonTestHostOptions>? configure = null)
    {
        var options = new FlagonTestHostOptions();
        configure?.Invoke(options);

        var time = options.Time ?? new FakeTimeProvider(Epoch);
        var logs = new TestLogSink();

        IHost host;
        if (options.Endpoints is null)
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
            {
                ApplicationName = options.ApplicationName,
                EnvironmentName = options.EnvironmentName,
                DisableDefaults = true,
            });
            ConfigureServices(builder.Services, builder.Logging, options, time, logs);
            host = builder.Build();
        }
        else
        {
            var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions
            {
                ApplicationName = options.ApplicationName,
                EnvironmentName = options.EnvironmentName,
            });
            builder.WebHost.UseTestServer();
            builder.Services.AddRouting();
            builder.Services
                .AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            builder.Services.AddAuthorization();
            ConfigureServices(builder.Services, builder.Logging, options, time, logs);

            var app = builder.Build();
            app.UseRouting();
            app.UseAuthentication();
            app.UseAuthorization();
            options.Endpoints(app);
            host = app;
        }

        await host.StartAsync();
        return new FlagonTestHost(host, time, logs);
    }

    private static void ConfigureServices(
        IServiceCollection services, ILoggingBuilder logging, FlagonTestHostOptions options, FakeTimeProvider time, TestLogSink logs)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(LogLevel.Trace);
        logging.AddProvider(logs);
        services.AddSingleton<TimeProvider>(time);
        services.AddFlagon(o =>
        {
            o.AutoCreate = false;
            if (options.Store is not null)
            {
                o.UseStorage(options.Store);
            }
            options.Flagon?.Invoke(o);
        });
        options.Services?.Invoke(services);
    }

    /// <summary>
    /// An <see cref="HttpClient"/> for the in-memory test server (requires <see cref="FlagonTestHostOptions.Endpoints"/>).
    /// With <paramref name="user"/>, requests are authenticated as that user id with the given roles
    /// (see <see cref="TestAuthHandler"/>); otherwise they are anonymous.
    /// </summary>
    public HttpClient CreateClient(string? user = null, params string[] roles)
    {
        if (_host is not WebApplication app)
        {
            throw new InvalidOperationException(
                $"Set {nameof(FlagonTestHostOptions)}.{nameof(FlagonTestHostOptions.Endpoints)} to host a web application.");
        }

        var client = app.GetTestClient();
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
            foreach (var role in roles)
            {
                client.DefaultRequestHeaders.Add(TestAuthHandler.RoleHeader, role);
            }
        }

        return client;
    }

    /// <summary>The snapshot freshness status (what a health check would read).</summary>
    public FlagonSnapshotStatus Status => Services.GetRequiredService<IFlagonSnapshotStatus>().Current;

    /// <summary>
    /// Advances the fake clock by one refresh interval and waits until every host has finished the refresh attempt
    /// that tick triggered. Hosts sharing one <see cref="FakeTimeProvider"/> are advanced once.
    /// </summary>
    public static async Task AdvanceToNextRefreshAsync(params FlagonTestHost[] hosts)
    {
        foreach (var group in hosts.GroupBy(h => h.Time))
        {
            group.Key.Advance(group.First().Status.RefreshInterval);
        }

        foreach (var host in hosts)
        {
            await host.WaitForRefreshAttemptAsync();
        }
    }

    /// <summary>Advances this host's clock by one refresh interval and waits for the triggered refresh attempt.</summary>
    public Task AdvanceToNextRefreshAsync() => AdvanceToNextRefreshAsync(this);

    /// <summary>Waits (bounded by a real-time safety timeout) until a refresh attempt has completed at the current fake time.</summary>
    public async Task WaitForRefreshAttemptAsync()
    {
        var now = Time.GetUtcNow();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!(Status.LastAttemptAt >= now))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"No refresh attempt completed at fake time {now:O}. Status: {Status}");
            }

            await Task.Delay(1);
        }
    }

    /// <summary>
    /// Waits (bounded by a real-time safety timeout) until <paramref name="condition"/> holds. For background work that is
    /// not clock-driven (e.g. the auto-create queue), where there is no fake-time tick to wait on.
    /// </summary>
    public static async Task EventuallyAsync(Func<Task<bool>> condition, string because)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting until {because}.");
            }

            await Task.Delay(1);
        }
    }

    /// <summary>Stops the host gracefully (background queues drain) without disposing it, so its services stay readable.</summary>
    public Task StopAsync() => _host.StopAsync();

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        if (_host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else
        {
            _host.Dispose();
        }
    }
}
