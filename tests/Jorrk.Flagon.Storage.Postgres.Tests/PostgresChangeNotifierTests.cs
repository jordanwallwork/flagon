using Jorrk.Flagon.Storage;
using Jorrk.Flagon.Tests.Support;
using Npgsql;

namespace Jorrk.Flagon.Storage.Postgres.Tests;

public class PostgresChangeNotifierTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private static readonly FlagDocument Document = FlagDocument.Boolean(true, new FlagMetadata());

    [Fact]
    public async Task Each_write_that_moves_the_change_version_notifies_a_listener_on_another_store_instance()
    {
        var schema = PostgresFixture.NewSchema();
        await using var writer = new PostgresFlagStore(db.ConnectionString, schema);
        await writer.ApplyMigrationsAsync();
        await using var reader = new PostgresFlagStore(db.ConnectionString, schema);
        await using var listener = await Listening.StartAsync(reader);

        var created = await writer.TryCreateAsync("checkout", Document);
        await listener.AwaitAsync(1);
        var updated = await writer.UpdateAsync("checkout", Document, created!.Version);
        await listener.AwaitAsync(2);
        var archived = await writer.ArchiveAsync("checkout", updated.Version);
        await listener.AwaitAsync(3);
        var restored = await writer.RestoreAsync("checkout", archived.Version);
        await listener.AwaitAsync(4);
        var rearchived = await writer.ArchiveAsync("checkout", restored.Version);
        await listener.AwaitAsync(5);
        await writer.PurgeAsync("checkout", rearchived.Version);
        await listener.AwaitAsync(6);

        await Task.Delay(300);
        Assert.Equal(6, listener.Count);
    }

    [Fact]
    public async Task Usage_upserts_do_not_notify()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        await store.ApplyMigrationsAsync();
        await store.TryCreateAsync("checkout", Document);
        await using var listener = await Listening.StartAsync(store);

        await store.UpsertUsageAsync([new FlagUsage("checkout", "Web", FlagonTestHost.Epoch)]);

        await Task.Delay(500);
        Assert.Equal(0, listener.Count);
    }

    [Fact]
    public async Task Rejected_writes_do_not_notify()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        await store.ApplyMigrationsAsync();
        var created = await store.TryCreateAsync("checkout", Document);
        await using var listener = await Listening.StartAsync(store);

        Assert.Null(await store.TryCreateAsync("checkout", Document));
        await Assert.ThrowsAsync<FlagVersionConflictException>(() => store.UpdateAsync("checkout", Document, created!.Version + 99));
        await Assert.ThrowsAsync<FlagValidationException>(() => store.PurgeAsync("checkout", created!.Version));

        await Task.Delay(500);
        Assert.Equal(0, listener.Count);
    }

    [Fact]
    public async Task IsConnected_is_true_while_listening_and_false_once_the_token_is_cancelled()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        var notifier = (IFlagChangeNotifier)store;
        Assert.False(notifier.IsConnected);

        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(() => { }, stop.Token);
        Assert.True(notifier.IsConnected);

        await stop.CancelAsync();
        await Eventually(() => !notifier.IsConnected);
    }

    [Fact]
    public async Task Losing_the_listener_connection_reports_disconnected_and_leaves_polling_to_cover()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        await store.ApplyMigrationsAsync();
        var notifier = (IFlagChangeNotifier)store;
        using var stop = new CancellationTokenSource();
        await notifier.StartAsync(() => { }, stop.Token);
        Assert.True(notifier.IsConnected);

        // The listener is the only idle session whose last query is its LISTEN statement.
        var pid = Convert.ToInt32(await db.ScalarAsync(
            $"SELECT pid FROM pg_stat_activity WHERE query = 'LISTEN \"flagon_{schema}\"' AND pid <> pg_backend_pid()"));
        await db.ExecuteAsync($"SELECT pg_terminate_backend({pid})");

        await Eventually(() => !notifier.IsConnected);
        Assert.Null(await store.GetAsync("missing")); // the store itself is still usable
    }

    [Fact]
    public async Task The_payload_carries_the_new_version_and_the_database_commit_time()
    {
        var schema = PostgresFixture.NewSchema();
        await using var store = new PostgresFlagStore(db.ConnectionString, schema);
        await store.ApplyMigrationsAsync();

        await using var raw = new NpgsqlConnection(db.ConnectionString);
        await raw.OpenAsync();
        await new NpgsqlCommand($"LISTEN \"flagon_{schema}\"", raw).ExecuteNonQueryAsync();
        var payloads = new List<string>();
        raw.Notification += (_, e) => payloads.Add(e.Payload);

        var before = DateTimeOffset.UtcNow;
        var created = await store.TryCreateAsync("checkout", Document);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (payloads.Count == 0)
        {
            await raw.WaitAsync(timeout.Token);
        }

        Assert.True(PostgresChangePayload.TryParse(payloads[0], out var version, out var commitTime), payloads[0]);
        Assert.Equal(created!.Version, version);
        Assert.InRange(commitTime, before.AddSeconds(-5), DateTimeOffset.UtcNow.AddSeconds(5));
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not become true in time.");
            await Task.Delay(10);
        }
    }

    /// <summary>Counts the signals a store's listener raises, and stops it on disposal.</summary>
    private sealed class Listening : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private int _count;

        private Listening()
        {
        }

        public int Count => Volatile.Read(ref _count);

        public static async Task<Listening> StartAsync(PostgresFlagStore store)
        {
            var listening = new Listening();
            await ((IFlagChangeNotifier)store).StartAsync(() => Interlocked.Increment(ref listening._count), listening._stop.Token);
            return listening;
        }

        public Task AwaitAsync(int expected) => Eventually(() => Count >= expected);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _stop.Dispose();
        }
    }
}
