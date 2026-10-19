using Jorrk.Flagon.Storage;

namespace Jorrk.Flagon.Tests.Support;

/// <summary>
/// An <see cref="IFlagStore"/> that also implements <see cref="IFlagChangeNotifier"/>, with a settable connection state and
/// signals the test raises by hand. Writes go to an inner store and do not signal.
/// </summary>
public sealed class FakeNotifyingFlagStore : IFlagStore, IFlagChangeNotifier
{
    private readonly IFlagStore _inner;
    private Action? _onChanged;

    public FakeNotifyingFlagStore(IFlagStore? inner = null)
    {
        _inner = inner ?? new InMemoryFlagStore();
    }

    /// <summary>What <see cref="IsConnected"/> reports.</summary>
    public bool Connected { get; set; }

    /// <inheritdoc />
    public bool IsConnected => Connected;

    /// <inheritdoc />
    public Task StartAsync(Action onChanged, CancellationToken cancellationToken = default)
    {
        _onChanged = onChanged;
        return Task.CompletedTask;
    }

    /// <summary>Raises a change signal as if the database had notified the listener.</summary>
    public void Signal() => _onChanged?.Invoke();

    public bool IsReadOnly => _inner.IsReadOnly;
    public Task ApplyMigrationsAsync(CancellationToken cancellationToken = default) => _inner.ApplyMigrationsAsync(cancellationToken);
    public ValueTask<long> GetChangeVersionAsync(CancellationToken cancellationToken = default) => _inner.GetChangeVersionAsync(cancellationToken);
    public Task<IReadOnlyList<FlagRecord>> LoadAllAsync(CancellationToken cancellationToken = default) => _inner.LoadAllAsync(cancellationToken);
    public Task<FlagRecord?> GetAsync(string key, CancellationToken cancellationToken = default) => _inner.GetAsync(key, cancellationToken);

    public Task<FlagRecord?> TryCreateAsync(string key, FlagDocument document, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        _inner.TryCreateAsync(key, document, change, cancellationToken);

    public Task<FlagRecord> UpdateAsync(string key, FlagDocument document, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        _inner.UpdateAsync(key, document, expectedVersion, change, cancellationToken);

    public Task<FlagRecord> ArchiveAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        _inner.ArchiveAsync(key, expectedVersion, change, cancellationToken);

    public Task<FlagRecord> RestoreAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        _inner.RestoreAsync(key, expectedVersion, change, cancellationToken);

    public Task<long> PurgeAsync(string key, long expectedVersion, FlagChangeContext? change = null, CancellationToken cancellationToken = default) =>
        _inner.PurgeAsync(key, expectedVersion, change, cancellationToken);

    public Task<IReadOnlyList<FlagAuditEntry>> GetAuditAsync(string key, CancellationToken cancellationToken = default) => _inner.GetAuditAsync(key, cancellationToken);
    public Task UpsertUsageAsync(IReadOnlyCollection<FlagUsage> usage, CancellationToken cancellationToken = default) => _inner.UpsertUsageAsync(usage, cancellationToken);
    public Task<IReadOnlyList<FlagUsage>> GetUsageAsync(string key, CancellationToken cancellationToken = default) => _inner.GetUsageAsync(key, cancellationToken);
    public Task<IReadOnlyList<FlagUsage>> LoadAllUsageAsync(CancellationToken cancellationToken = default) => _inner.LoadAllUsageAsync(cancellationToken);
}
