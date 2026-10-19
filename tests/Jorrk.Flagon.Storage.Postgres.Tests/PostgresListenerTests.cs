using System.Collections.Concurrent;
using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.Logging;
using System.Net.Sockets;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresListenerTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task A_dropped_listener_reconnects_relistens_and_signals_a_resubscribe_that_is_followed_by_normal_notifications()
    {
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        var notifier = (IFlagChangeNotifier)store;
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(signals.Enqueue, stop.Token);
        var pid = await ListenerPidAsync(schema);

        await db.ExecuteAsync($"SELECT pg_terminate_backend({pid})");
        var sawDisconnected = false;
        await Eventually(() =>
        {
            sawDisconnected |= !notifier.IsConnected;
            return signals.Any(s => s.Resubscribed);
        });

        Assert.True(sawDisconnected, "The listener never reported the lost connection.");
        Assert.True(notifier.IsConnected);
        Assert.NotEqual(pid, await ListenerPidAsync(schema));

        await writer.TryCreateAsync("checkout", Document);
        await Eventually(() => signals.Any(s => !s.Resubscribed));
    }

    [Fact]
    public async Task A_first_connect_that_fails_is_retried_in_the_background_until_it_succeeds()
    {
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        await using var proxy = new RefusingProxy(db.ConnectionString);
        await using var store = new PostgresFlagStore(proxy.ConnectionString, schema);
        var notifier = (IFlagChangeNotifier)store;
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();

        // The first connect is refused. StartAsync returns anyway and the listener keeps retrying.
        await notifier.StartAsync(signals.Enqueue, stop.Token);
        Assert.False(notifier.IsConnected);

        proxy.Accepting = true;
        await Eventually(() => notifier.IsConnected);

        await writer.TryCreateAsync("checkout", Document);
        await Eventually(() => signals.Any(s => !s.Resubscribed));
    }

    /// <summary>A TCP relay to the database that drops every connection while <see cref="Accepting"/> is false.</summary>
    private sealed class RefusingProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private readonly string _target;
        private readonly NpgsqlConnectionStringBuilder _builder;

        public RefusingProxy(string connectionString)
        {
            _builder = new NpgsqlConnectionStringBuilder(connectionString);
            _listener.Start();
            ConnectionString = new NpgsqlConnectionStringBuilder(connectionString) { Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port }.ConnectionString;
            _target = _builder.Host ?? "localhost";
            _accepting = AcceptLoopAsync();
        }

        public string ConnectionString { get; }

        public bool Accepting { get; set; }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if (!Accepting)
                {
                    client.Dispose();
                    continue;
                }

                _ = RelayAsync(client);
            }
        }

        private async Task RelayAsync(TcpClient client)
        {
            using var upstream = new TcpClient();
            await upstream.ConnectAsync(_target, _builder.Port).ConfigureAwait(false);
            var up = client.GetStream().CopyToAsync(upstream.GetStream());
            var down = upstream.GetStream().CopyToAsync(client.GetStream());
            await Task.WhenAny(up, down).ConfigureAwait(false);
            client.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _accepting;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    [Fact]
    public async Task Losing_the_connection_logs_a_disconnect_and_a_backoff_and_each_notification_is_logged_at_debug()
    {
        const int connected = 1107, disconnected = 1108, backingOff = 1109, notification = 1110;
        var sink = new TestLogSink();
        using var loggers = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(sink));
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema, loggers.CreateLogger("Jorrk.Flagon.Storage.Postgres"));
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await ((IFlagChangeNotifier)store).StartAsync(signals.Enqueue, stop.Token);
        var pid = await ListenerPidAsync(schema);

        await db.ExecuteAsync($"SELECT pg_terminate_backend({pid})");
        await Eventually(() => signals.Any(s => s.Resubscribed));
        await writer.TryCreateAsync("checkout", Document);
        await Eventually(() => signals.Any(s => !s.Resubscribed));

        Assert.Equal(2, sink.Entries.Count(e => e.EventId.Id == connected && e.Level == LogLevel.Information));
        Assert.Single(sink.Entries, e => e.EventId.Id == disconnected && e.Level == LogLevel.Information);
        Assert.True(sink.Entries.Count(e => e.EventId.Id == backingOff && e.Level == LogLevel.Warning) >= 1);
        Assert.True(sink.Entries.Count(e => e.EventId.Id == notification && e.Level == LogLevel.Debug) >= 1);
    }

    [Fact]
    public async Task The_keepalive_runs_on_an_idle_listener_without_breaking_it()
    {
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema) { ListenerKeepaliveInterval = TimeSpan.FromMilliseconds(200) };
        var notifier = (IFlagChangeNotifier)store;
        var signals = new ConcurrentQueue<FlagChangeSignal>();
        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(signals.Enqueue, stop.Token);
        var pid = await ListenerPidAsync(schema);

        // Several keepalive intervals pass. The last statement the backend ran is the keepalive, and it stays connected.
        var sawDisconnected = false;
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            sawDisconnected |= !notifier.IsConnected;
            await Task.Delay(5);
        }

        Assert.False(sawDisconnected);
        Assert.Equal("SELECT 1", await db.ScalarAsync($"SELECT query FROM pg_stat_activity WHERE pid = {pid}"));

        await writer.TryCreateAsync("checkout", Document);
        await Eventually(() => signals.Count > 0);
        Assert.All(signals, s => Assert.False(s.Resubscribed));
    }

    [Fact]
    public async Task Cancelling_the_notifier_closes_its_listener_connection()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        await store.ApplyMigrationsAsync();
        var notifier = (IFlagChangeNotifier)store;
        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(_ => { }, stop.Token);
        var pid = await ListenerPidAsync(schema);

        await stop.CancelAsync();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Convert.ToInt64(await db.ScalarAsync($"SELECT count(*) FROM pg_stat_activity WHERE pid = {pid}")) != 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "The listener connection outlived its notifier.");
            await Task.Delay(20);
        }
        Assert.False(notifier.IsConnected);
    }

    [Fact]
    public void Reconnect_backoff_starts_at_one_second_doubles_is_jittered_and_caps_at_thirty_seconds()
    {
        var random = new Random(7);
        for (var sample = 0; sample < 200; sample++)
        {
            AssertWithin(ReconnectBackoff.Delay(0, random), 500, 1000);
            AssertWithin(ReconnectBackoff.Delay(1, random), 1000, 2000);
            AssertWithin(ReconnectBackoff.Delay(2, random), 2000, 4000);
            AssertWithin(ReconnectBackoff.Delay(5, random), 15000, 30000);
            AssertWithin(ReconnectBackoff.Delay(50, random), 15000, 30000);
        }
    }

    private static void AssertWithin(TimeSpan delay, double minMilliseconds, double maxMilliseconds) =>
        Assert.InRange(delay.TotalMilliseconds, minMilliseconds, maxMilliseconds);

    private async Task<int> ListenerPidAsync(string schema) =>
        Convert.ToInt32(await db.ScalarAsync(
            $"SELECT pid FROM pg_stat_activity WHERE query = 'LISTEN \"flagon_{schema}\"' AND pid <> pg_backend_pid()"));

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }
}
