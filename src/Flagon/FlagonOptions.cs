using Flagon.Storage;

namespace Flagon;

/// <summary>Flagon configuration, supplied via <c>services.AddFlagon(opts =&gt; ...)</c>.</summary>
public sealed partial class FlagonOptions
{
    private TimeSpan _refreshInterval = TimeSpan.FromSeconds(10);
    private TimeSpan _usageFlushInterval = TimeSpan.FromSeconds(60);

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
    /// This application's name, recorded on audit entries (and usage) so changes can be traced to the app that made them.
    /// Defaults (when null or blank) to the host's <c>IHostEnvironment.ApplicationName</c>, else the entry assembly name.
    /// </summary>
    public string? ApplicationName { get; set; }

    /// <summary>
    /// When true, host startup fails if the initial flag load from the store fails. When false (the default) the app
    /// starts with an empty snapshot (code defaults apply) and keeps retrying every <see cref="RefreshInterval"/>.
    /// </summary>
    public bool FailFastOnStartup { get; set; }

    /// <summary>
    /// When true (the default) each evaluation records, in memory, the key and the code default supplied; the aggregate is
    /// written to the store every <see cref="UsageFlushInterval"/> and on graceful shutdown so admins can see when each app
    /// last evaluated each flag (<see cref="IFlagonAdmin.GetUsageAsync"/>). Set false to avoid the writes entirely.
    /// Tracking is skipped silently for read-only stores.
    /// </summary>
    public bool UsageTracking { get; set; } = true;

    /// <summary>How often recorded usage is written to the store (default 60 seconds).</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan UsageFlushInterval
    {
        get => _usageFlushInterval;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
            _usageFlushInterval = value;
        }
    }

    /// <summary>
    /// When true (the default) Flagon calls <see cref="IFlagStore.ApplyMigrationsAsync"/> on startup, before the initial
    /// flag load, retrying on each refresh until it succeeds. Turn off when a DBA applies the store's scripts.
    /// A migration failure fails startup only when <see cref="FailFastOnStartup"/> is set.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>
    /// When true (the default), evaluating a key that does not exist creates it in the background (off the evaluation path)
    /// as an enabled boolean flag serving the caller's default, with source <see cref="FlagSource.Auto"/>, so it appears for
    /// management without changing behaviour. Archived keys are never re-created. Skipped when the store is read-only.
    /// </summary>
    public bool AutoCreate { get; set; } = true;

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
