using Jorrk.Flagon.Storage;
using Microsoft.Extensions.Logging;

namespace Jorrk.Flagon.Internal;

/// <summary>
/// The startup sequence, shared by <see cref="FlagonHostedService"/> and <c>Jorrk.Flagon.Testing</c>: validate the flags
/// declared with <c>Define</c>, apply store migrations (when <see cref="FlagonOptions.AutoMigrate"/> is on), seed the
/// declared flags, then load the first snapshot. A store outage does not fail startup unless
/// <see cref="FlagonOptions.FailFastOnStartup"/> is set; the steps that failed are retried by
/// <see cref="RetryPendingAsync"/>.
/// </summary>
internal sealed partial class FlagonStartup(
    FlagSnapshotManager snapshots,
    IFlagStore store,
    FlagSeeder seeder,
    FlagonOptions options,
    FlagonInstrumentation instrumentation,
    ILogger<FlagonStartup> logger)
{
    private volatile bool _migrated = !options.AutoMigrate;
    private volatile bool _seeded;

    /// <summary>Runs the startup sequence once.</summary>
    /// <exception cref="FlagValidationException">
    /// A declared flag's targeting rule (<see cref="FlagTargetingException"/>) or segment's rule
    /// (<see cref="SegmentRuleException"/>) does not compile, or a declared flag references a segment that does not exist or
    /// is archived (whatever <see cref="FlagonOptions.FailFastOnStartup"/> says).
    /// </exception>
    /// <exception cref="FlagonException">
    /// A migration, seed or load failed and <see cref="FlagonOptions.FailFastOnStartup"/> is set.
    /// </exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        // A definition whose targeting the registered compiler rejects is a bug in the app's code: fail startup whatever
        // FailFastOnStartup says, before anything is written (Define could only check the JSON).
        seeder.ValidateDefinitions();

        if (await TryMigrateAsync(cancellationToken).ConfigureAwait(false) is { } migrationError && options.FailFastOnStartup)
        {
            throw new FlagonException(
                "Flagon could not apply store migrations at startup and FailFastOnStartup is enabled.", migrationError);
        }

        // Seeding (Define) runs after migrations and before the initial load, so defined flags are in the first snapshot.
        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (FlagValidationException)
        {
            // The store answered, and a definition was refused (a flag referencing a segment that does not exist, or is
            // archived): a bug in the app's code, like a rule that does not compile, so it fails startup.
            throw;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (options.FailFastOnStartup)
            {
                throw new FlagonException(
                    "Flagon could not seed flags defined in code at startup and FailFastOnStartup is enabled.", ex);
            }

            // Not fatal: logged by SeedAsync and retried by RetryPendingAsync.
        }

        try
        {
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            if (options.FailFastOnStartup)
            {
                throw new FlagonException(
                    "Flagon could not load flags from the store at startup and FailFastOnStartup is enabled.", ex);
            }

            LogStartingWithoutFlags(logger, options.RefreshInterval);
        }
    }

    /// <summary>
    /// Retries the startup steps that have not yet succeeded (migrations, then seeding), logging failures; the caller
    /// refreshes the snapshot afterwards.
    /// </summary>
    public async Task RetryPendingAsync(CancellationToken cancellationToken)
    {
        await TryMigrateAsync(cancellationToken).ConfigureAwait(false);
        if (_seeded)
        {
            return;
        }

        try
        {
            await SeedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Logged by SeedAsync; retried next time.
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

    /// <summary>Seeds the flags declared with Define. Throws on failure, after logging it.</summary>
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
