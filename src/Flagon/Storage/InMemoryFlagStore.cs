namespace Flagon.Storage;

/// <summary>
/// A process-local <see cref="IFlagStore"/>. The default when no storage is configured. Share one instance between
/// several hosts to simulate apps sharing a database.
/// </summary>
public sealed class InMemoryFlagStore : IFlagStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FlagRecord> _flags = new(StringComparer.Ordinal);
    private long _changeVersion;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return ValueTask.FromResult(_changeVersion);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FlagRecord>>(_flags.Values.ToArray());
        }
    }

    /// <inheritdoc />
    public Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_flags.GetValueOrDefault(key));
        }
    }

    /// <inheritdoc />
    public Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_flags.ContainsKey(key))
            {
                return Task.FromResult<FlagRecord?>(null);
            }

            var record = new FlagRecord(key, document, ++_changeVersion);
            _flags[key] = record;
            return Task.FromResult<FlagRecord?>(record);
        }
    }

    /// <inheritdoc />
    public Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_flags.TryGetValue(key, out var current))
            {
                throw new FlagNotFoundException(key);
            }

            if (current.Version != expectedVersion)
            {
                throw new FlagVersionConflictException(key, expectedVersion, current.Version);
            }

            var record = new FlagRecord(key, document, ++_changeVersion);
            _flags[key] = record;
            return Task.FromResult(record);
        }
    }
}
