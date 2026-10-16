using System.Collections.Frozen;
using Flagon.Storage;

namespace Flagon.Internal;

/// <summary>An immutable view of every flag at one change version. Swapped atomically; read lock-free by evaluation.</summary>
internal sealed class FlagSnapshot
{
    public static FlagSnapshot Empty { get; } = new(0, FrozenDictionary<string, FlagRecord>.Empty);

    public FlagSnapshot(long version, FrozenDictionary<string, FlagRecord> flags)
    {
        Version = version;
        Flags = flags;
    }

    /// <summary>The store change version this snapshot reflects.</summary>
    public long Version { get; }

    public FrozenDictionary<string, FlagRecord> Flags { get; }
}

/// <summary>Owns the current <see cref="FlagSnapshot"/> and reloads it from the store.</summary>
internal sealed class FlagSnapshotManager(IFlagStore store)
{
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private volatile FlagSnapshot _current = FlagSnapshot.Empty;

    public FlagSnapshot Current => _current;

    /// <summary>Reloads all flags if the store's change version moved past the current snapshot's.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var version = await store.GetChangeVersionAsync(cancellationToken).ConfigureAwait(false);
            if (version == _current.Version && version != 0)
            {
                return;
            }

            var flags = await store.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            // Stamp with the version read *before* loading: a write racing the load then just causes one extra reload,
            // whereas stamping with the highest loaded version could skip a lower-numbered write that commits late.
            _current = new FlagSnapshot(version, flags.ToFrozenDictionary(f => f.Key, StringComparer.Ordinal));
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}
