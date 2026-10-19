namespace Jorrk.Flagon.Storage;

/// <summary>
/// A process-local <see cref="IFlagStore"/>. The default when no storage is configured. Share one instance between
/// several hosts to simulate apps sharing a database. Implements <see cref="IFlagChangeNotifier"/>: every write that moves
/// the change version signals the listeners, usage writes do not.
/// </summary>
public sealed class InMemoryFlagStore : IFlagStore, IFlagChangeNotifier
{
    private readonly object _gate = new();
    private readonly object _listenersGate = new();
    private readonly List<Action<FlagChangeSignal>> _listeners = [];
    private readonly Dictionary<string, FlagRecord> _flags = new(StringComparer.Ordinal);
    private readonly List<FlagAuditEntry> _audit = [];
    private readonly Dictionary<(string Key, string ApplicationName), FlagUsage> _usage = new(UsageKeyComparer.Instance);
    private long _changeVersion;

    /// <inheritdoc />
    public bool IsReadOnly => false;

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_listenersGate)
            {
                return _listeners.Count > 0;
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(Action<FlagChangeSignal> onChanged, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onChanged);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.CompletedTask;
        }

        lock (_listenersGate)
        {
            _listeners.Add(onChanged);
        }

        // Removing under the same lock that signalling holds means no callback runs once cancellation has been observed.
        cancellationToken.Register(() =>
        {
            lock (_listenersGate)
            {
                _listeners.Remove(onChanged);
            }
        });
        return Task.CompletedTask;
    }

    private void SignalChanged()
    {
        lock (_listenersGate)
        {
            // Callbacks are required to be quick and not to call back into the store, so running them under the lock is safe.
            foreach (var listener in _listeners)
            {
                listener(default);
            }
        }
    }

    /// <inheritdoc />
    public Task ApplyMigrationsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

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
    public Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            if (_flags.ContainsKey(key))
            {
                return Task.FromResult<FlagRecord?>(null);
            }

            var record = new FlagRecord(key, document, ++_changeVersion);
            _flags[key] = record;
            Audit(change, key, FlagAuditAction.Created, record.Version, before: null, after: document);
            SignalChanged();
            return Task.FromResult<FlagRecord?>(record);
        }
    }

    /// <inheritdoc />
    public Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        lock (_gate)
        {
            var current = GetForWrite(key, expectedVersion);
            var record = current with { Document = document, Version = ++_changeVersion };
            _flags[key] = record;
            Audit(change, key, FlagAuditAction.Updated, record.Version, current.Document, document);
            SignalChanged();
            return Task.FromResult(record);
        }
    }

    /// <inheritdoc />
    public Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(SetArchived(key, expectedVersion, archived: true, change));

    /// <inheritdoc />
    public Task<FlagRecord> RestoreAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(SetArchived(key, expectedVersion, archived: false, change));

    /// <inheritdoc />
    public Task<long> PurgeAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var current = GetForWrite(key, expectedVersion);
            if (!current.Archived)
            {
                throw new FlagValidationException($"Flag '{key}' must be archived before it can be purged.");
            }

            _flags.Remove(key);
            foreach (var usageKey in _usage.Keys.Where(k => string.Equals(k.Key, key, StringComparison.Ordinal)).ToArray())
            {
                _usage.Remove(usageKey);
            }

            var version = ++_changeVersion;
            Audit(change, key, FlagAuditAction.Purged, version, current.Document, after: null);
            SignalChanged();
            return Task.FromResult(version);
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FlagAuditEntry>> GetAuditAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FlagAuditEntry>>(_audit.Where(e => string.Equals(e.Key, key, StringComparison.Ordinal)).ToArray());
        }
    }

    /// <inheritdoc />
    public Task UpsertUsageAsync(IReadOnlyCollection<FlagUsage> usage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(usage);
        lock (_gate)
        {
            foreach (var record in usage)
            {
                var key = (record.Key, record.ApplicationName);
                if (!_usage.TryGetValue(key, out var existing) || record.LastEvaluatedAt >= existing.LastEvaluatedAt)
                {
                    _usage[key] = record;
                }
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FlagUsage>> GetUsageAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FlagUsage>>(_usage.Values.Where(u => string.Equals(u.Key, key, StringComparison.Ordinal)).ToArray());
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<FlagUsage>> LoadAllUsageAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<FlagUsage>>(_usage.Values.ToArray());
        }
    }

    private FlagRecord SetArchived(string key, long expectedVersion, bool archived, FlagChangeContext? change)
    {
        lock (_gate)
        {
            var current = GetForWrite(key, expectedVersion);
            if (current.Archived == archived)
            {
                throw new FlagValidationException(archived ? $"Flag '{key}' is already archived." : $"Flag '{key}' is not archived.");
            }

            var record = current with { Archived = archived, Version = ++_changeVersion };
            _flags[key] = record;
            Audit(change, key, archived ? FlagAuditAction.Archived : FlagAuditAction.Restored, record.Version, current.Document, current.Document);
            SignalChanged();
            return record;
        }
    }

    private FlagRecord GetForWrite(string key, long expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!_flags.TryGetValue(key, out var current))
        {
            throw new FlagNotFoundException(key);
        }

        if (current.Version != expectedVersion)
        {
            throw new FlagVersionConflictException(key, expectedVersion, current.Version);
        }

        return current;
    }

    private void Audit(FlagChangeContext? change, string key, FlagAuditAction action, long version, FlagDocument? before, FlagDocument? after)
    {
        if (change is null)
        {
            return;
        }

        _audit.Add(new FlagAuditEntry(_audit.Count + 1, key, action, version, change.Timestamp, change.Actor, change.ApplicationName, before, after));
    }

    private sealed class UsageKeyComparer : IEqualityComparer<(string Key, string ApplicationName)>
    {
        public static readonly UsageKeyComparer Instance = new();

        public bool Equals((string Key, string ApplicationName) x, (string Key, string ApplicationName) y) =>
            string.Equals(x.Key, y.Key, StringComparison.Ordinal) && string.Equals(x.ApplicationName, y.ApplicationName, StringComparison.Ordinal);

        public int GetHashCode((string Key, string ApplicationName) obj) =>
            HashCode.Combine(StringComparer.Ordinal.GetHashCode(obj.Key), StringComparer.Ordinal.GetHashCode(obj.ApplicationName));
    }
}
