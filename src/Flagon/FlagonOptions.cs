using Flagon.Storage;

namespace Flagon;

/// <summary>Flagon configuration, supplied via <c>services.AddFlagon(opts =&gt; ...)</c>.</summary>
public sealed class FlagonOptions
{
    private TimeSpan _refreshInterval = TimeSpan.FromSeconds(10);

    internal Func<IServiceProvider, IFlagStore>? StoreFactory { get; private set; }

    /// <summary>
    /// How often the background refresher polls the store's change version (default 10 seconds). The full flag set is
    /// reloaded only when the version has changed, so changes made by any app sharing the store are seen within one interval.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _refreshInterval = value;
        }
    }

    /// <summary>
    /// When true, host startup fails if the initial flag load from the store fails. When false (the default) the app
    /// starts with an empty snapshot (code defaults apply) and keeps retrying every <see cref="RefreshInterval"/>.
    /// </summary>
    public bool FailFastOnStartup { get; set; }

    /// <summary>
    /// When true (the default) Flagon calls <see cref="IFlagStore.ApplyMigrationsAsync"/> on startup, before the initial
    /// flag load, retrying on each refresh until it succeeds. Turn off when a DBA applies the store's scripts.
    /// A migration failure fails startup only when <see cref="FailFastOnStartup"/> is set.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// Uses the store built by <paramref name="factory"/>. Storage packages wrap this in their own extension
    /// (e.g. <c>UseSqlServerStorage</c>). The last storage call wins.
    /// </summary>
    public FlagonOptions UseStorage(Func<IServiceProvider, IFlagStore> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        StoreFactory = factory;
        return this;
    }

    /// <summary>Uses an existing store instance (e.g. one <see cref="InMemoryFlagStore"/> shared by several hosts).</summary>
    public FlagonOptions UseStorage(IFlagStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return UseStorage(_ => store);
    }

    /// <summary>Uses a new process-local <see cref="InMemoryFlagStore"/> (the default when no storage is configured).</summary>
    public FlagonOptions UseInMemoryStorage() => UseStorage(_ => new InMemoryFlagStore());
}
