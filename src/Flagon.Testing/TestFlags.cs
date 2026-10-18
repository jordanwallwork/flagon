using Flagon.Internal;
using Flagon.Storage;

namespace Flagon.Testing;

/// <summary>
/// Synchronous, chainable flag setup for tests. Every write is an upsert that replaces a flag of any type and state, and
/// is visible to the very next evaluation: the snapshot is refreshed before the method returns. Writes go straight to
/// the store without an audit entry, so the audit history shows only the app's own admin writes.
/// </summary>
/// <remarks>Safe to call from parallel tasks and from inside a single-threaded <see cref="SynchronizationContext"/>.</remarks>
public sealed class TestFlags
{
    private readonly IFlagStore _store;
    private readonly FlagSnapshotManager _snapshots;

    internal TestFlags(IFlagStore store, FlagSnapshotManager snapshots, EvaluationLog evaluations)
    {
        _store = store;
        _snapshots = snapshots;
        Evaluations = evaluations;
    }

    /// <summary>
    /// Every evaluation served since startup (or the last <see cref="EvaluationLog.Clear"/>): in-process through
    /// <see cref="IFlagon"/> and remote through OFREP, but not management previews. Empty when
    /// <see cref="FlagonTestingSwitches.RecordEvaluations"/> is off.
    /// </summary>
    public EvaluationLog Evaluations { get; }

    /// <summary>Sets <paramref name="key"/> to an enabled boolean flag (<c>on</c>/<c>off</c> variants) serving <paramref name="value"/>.</summary>
    /// <param name="key">The flag key.</param>
    /// <param name="value">The value every evaluation of the flag is served.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="FlagValidationException"><paramref name="key"/> is not a valid flag key.</exception>
    public TestFlags SetFlag(string key, bool value) => Upsert(key, FlagDocument.Boolean(value));

    private TestFlags Upsert(string key, FlagDocument document)
    {
        FlagKey.Validate(key);
        // The in-memory store completes synchronously and every awaited path uses ConfigureAwait(false), so blocking here
        // cannot deadlock a test framework's synchronization context.
        UpsertAsync(key, document).GetAwaiter().GetResult();
        return this;
    }

    private async Task UpsertAsync(string key, FlagDocument document)
    {
        // A concurrent write to the same key (another task, or the app's own admin write) moves the version: re-read and retry.
        while (!await TryWriteAsync(key, document).ConfigureAwait(false))
        {
        }

        await _snapshots.RefreshAsync().ConfigureAwait(false);
    }

    private async Task<bool> TryWriteAsync(string key, FlagDocument document)
    {
        var current = await _store.GetAsync(key).ConfigureAwait(false);
        if (current is null)
        {
            return await _store.TryCreateAsync(key, document).ConfigureAwait(false) is not null;
        }

        try
        {
            if (current.Archived)
            {
                current = await _store.RestoreAsync(key, current.Version).ConfigureAwait(false);
            }

            await _store.UpdateAsync(key, document, current.Version).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is FlagVersionConflictException or FlagNotFoundException)
        {
            return false;
        }
    }
}
