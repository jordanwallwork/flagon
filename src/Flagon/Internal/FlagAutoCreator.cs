using System.Collections.Concurrent;
using System.Threading.Channels;
using Flagon.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Flagon.Internal;

/// <summary>
/// Auto-creates flags for unknown keys. The evaluator calls <see cref="Enqueue"/> (lock-free, no I/O); a background
/// worker dedupes and calls create-if-absent with <see cref="FlagSource.Auto"/>, then refreshes the snapshot so this app
/// sees the new flag. Create-if-absent never revives an archived key and returns null when another app won the race,
/// so many apps hitting the same unknown key create exactly one flag. Pending work is drained on graceful shutdown.
/// </summary>
internal sealed partial class FlagAutoCreator(
    IFlagStore store,
    FlagSnapshotManager snapshots,
    FlagChangeStamper changes,
    FlagonOptions options,
    ILogger<FlagAutoCreator> logger) : IHostedService, IDisposable
{
    private readonly Channel<(string Key, bool DefaultValue)> _queue =
        Channel.CreateUnbounded<(string, bool)>(new UnboundedChannelOptions { SingleReader = true });

    private const long InFlight = long.MaxValue;

    // Key -> "do not queue again while the snapshot version is at or below this". In-flight and invalid keys hold
    // InFlight; a key the store already had (another app won, or it is archived) is parked until the snapshot moves on.
    private readonly ConcurrentDictionary<string, long> _pending = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private int _disposed;

    /// <summary>Hot path: called by the evaluator when <paramref name="key"/> is not in the snapshot.</summary>
    public void Enqueue(string key, bool defaultValue)
    {
        // Skip silently when switched off, against read-only stores, and until the first snapshot has loaded (before
        // then every key looks missing and the store may be down).
        if (!options.AutoCreate || store.IsReadOnly || !((IFlagonSnapshotStatus)snapshots).Current.IsLoaded)
        {
            return;
        }

        if (_pending.TryGetValue(key, out var parkedUntil))
        {
            if (parkedUntil >= snapshots.Current.Version || !_pending.TryUpdate(key, InFlight, parkedUntil))
            {
                return;
            }
        }
        else if (!_pending.TryAdd(key, InFlight))
        {
            return;
        }

        if (FlagKey.IsValid(key))
        {
            _queue.Writer.TryWrite((key, defaultValue));
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Writer.TryComplete();
        if (_worker is null)
        {
            return;
        }

        // Drain what is already queued unless the host's shutdown timeout expires first.
        using var abort = cancellationToken.Register(() => _stopping.Cancel());
        await _worker.ConfigureAwait(false);
    }

    private async Task ProcessAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var (key, defaultValue) in _queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await CreateAsync(key, defaultValue, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task CreateAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        try
        {
            // Another path (refresh, admin) may have loaded it since it was queued.
            if (snapshots.Current.Flags.ContainsKey(key))
            {
                _pending.TryRemove(key, out _);
                return;
            }

            var change = changes.Begin();
            var metadata = new FlagMetadata(Source: FlagSource.Auto).StampCreated(change);
            var created = await store.TryCreateAsync(key, FlagDocument.Boolean(defaultValue, metadata), change, cancellationToken)
                .ConfigureAwait(false);

            if (created is null)
            {
                // Exists already (another app won the race, or the key is archived): the next refresh shows it. Park the
                // key so evaluations until then do not keep asking the store.
                _pending[key] = snapshots.Current.Version;
                return;
            }

            LogAutoCreated(logger, key, defaultValue, change.ApplicationName);
            await snapshots.RefreshAsync(cancellationToken).ConfigureAwait(false);
            _pending.TryRemove(key, out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogAutoCreateFailed(logger, ex, key);
            // A later evaluation retries.
            _pending.TryRemove(key, out _);
        }
    }

    public void Dispose()
    {
        // Disposed twice by DI (the singleton and its hosted-service registration share one instance).
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _stopping.Cancel();
            _stopping.Dispose();
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information,
        Message = "Auto-created flag '{FlagKey}' with default {DefaultValue} on first evaluation by '{ApplicationName}'.")]
    private static partial void LogAutoCreated(ILogger logger, string flagKey, bool defaultValue, string? applicationName);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning,
        Message = "Could not auto-create flag '{FlagKey}'; it will be retried the next time it is evaluated.")]
    private static partial void LogAutoCreateFailed(ILogger logger, Exception exception, string flagKey);
}
