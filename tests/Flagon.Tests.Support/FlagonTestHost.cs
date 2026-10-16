using Flagon.Storage;
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

    /// <summary>Extra Flagon configuration, applied inside <c>AddFlagon(opts =&gt; ...)</c>.</summary>
    public Action<FlagonOptions>? Flagon { get; set; }

    /// <summary>Extra service registrations, applied after <c>AddFlagon</c>.</summary>
    public Action<IServiceCollection>? Services { get; set; }
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

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = options.ApplicationName,
            EnvironmentName = options.EnvironmentName,
            DisableDefaults = true,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.AddFlagon(o =>
        {
            if (options.Store is not null)
            {
                o.UseStorage(options.Store);
            }
            options.Flagon?.Invoke(o);
        });
        options.Services?.Invoke(builder.Services);

        var host = builder.Build();
        await host.StartAsync();
        return new FlagonTestHost(host, time, logs);
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

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
