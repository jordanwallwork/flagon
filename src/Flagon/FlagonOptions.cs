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
    /// When true (the default) the store's schema is created or upgraded (<see cref="IFlagStore.ApplyMigrationsAsync"/>)
    /// when the host starts, before flags are loaded; a failed migration is retried on each refresh until it succeeds.
    /// Turn off when schema changes are applied by a DBA from the store's exported scripts.
    /// </summary>
    public bool AutoMigrate { get; set; } = true;

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
