using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Loads the initial snapshot when the host starts, then polls the store every <see cref="FlagonOptions.RefreshInterval"/>.
/// When <see cref="FlagonOptions.AutoMigrate"/> is on, store migrations are applied before the first load and retried
/// before each refresh until they succeed.
/// A store outage at startup does not block the host unless <see cref="FlagonOptions.FailFastOnStartup"/> is set.
/// </summary>
internal sealed partial class FlagonHostedService(
    FlagSnapshotManager snapshots,
    IFlagStore store,
    FlagSeeder seeder,
    FlagonOptions options,
    TimeProvider time,
    FlagonInstrumentation instrumentation,
    ILogger<FlagonHostedService> logger) : IHostedService, IAsyncDisposable
{
    private readonly CancellationTokenSource _stopping = new();
    private PeriodicTimer? _timer;
    private Task? _polling;
    private volatile bool _migrated = !options.AutoMigrate;
    private volatile bool _seeded;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Create the timer before the first await so its schedule starts at host start, not after the initial load.
        _timer = new PeriodicTimer(options.RefreshInterval, time);

        if (await TryMigrateAsync(cancellationToken).ConfigureAwait(false) is { } migrationError && options.FailFastOnStartup)
        {
            _timer.Dispose();
            throw new FlagonException(
                "Flagon could not apply store migrations at startup and FailFastOnStartup is enabled.", migrationError);
        }

        // Seeding (Define) runs after migrations and before the initial load, so defined flags are in the first snapshot.
        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (options.FailFastOnStartup)
            {
                _timer.Dispose();
                throw new FlagonException(
                    "Flagon could not seed flags defined in code at startup and FailFastOnStartup is enabled.", ex);
            }

            // Not fatal: logged by SeedAsync and retried on each poll.
        }

        try
        {
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (options.FailFastOnStartup)
            {
                _timer.Dispose();
                throw new FlagonException(
                    "Flagon could not load flags from the store at startup and FailFastOnStartup is enabled.", ex);
            }

            LogStartingWithoutFlags(logger, options.RefreshInterval);
        }

        _polling = PollAsync(_timer, _stopping.Token);
    }

    private async Task PollAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TryMigrateAsync(stoppingToken).ConfigureAwait(false);
                if (!_seeded)
                {
                    await TrySeedAsync(stoppingToken).ConfigureAwait(false);
                }

                try
                {
                    await snapshots.RefreshAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Already recorded and logged by the snapshot manager; keep serving last-known-good and retry next tick.
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Applies pending migrations once; on failure logs and returns the error (the refresh still runs and records the outage).</summary>
    private async Task<Exception?> TryMigrateAsync(CancellationToken cancellationToken)
    {
        if (_migrated)
        {
            return null;
        }

        try
        {
            await instrumentation.TraceStoreOperationAsync("migrate", flagKey: null, async _ =>
            {
                await store.ApplyMigrationsAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
            _migrated = true;
            return null;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            LogMigrationFailed(logger, ex);
            return ex;
        }
    }

    /// <summary>Seeds the flags declared with Define. Throws on failure; the failure is logged and seeding is retried each poll.</summary>
    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await seeder.SeedAsync(cancellationToken).ConfigureAwait(false);
            _seeded = true;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            LogSeedingFailed(logger, ex, options.RefreshInterval);
            throw;
        }
    }

    private async Task TrySeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged by SeedAsync; retried on the next tick.
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_polling is null)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);
        await _polling.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _timer?.Dispose();
        _stopping.Dispose();
        return ValueTask.CompletedTask;
    }

    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning,
        Message = "Flagon could not load flags from the store at startup; starting with code defaults and retrying every {RefreshInterval}.")]
    private static partial void LogStartingWithoutFlags(ILogger logger, TimeSpan refreshInterval);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Warning,
        Message = "Flagon could not seed the flags defined in code; retrying every {RefreshInterval}.")]
    private static partial void LogSeedingFailed(ILogger logger, Exception exception, TimeSpan refreshInterval);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning,
        Message = "Flagon could not apply store migrations; retrying before the next refresh.")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception);
}
