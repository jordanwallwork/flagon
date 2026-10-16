using Flagon.Storage;

namespace Flagon;

/// <summary>Flagon configuration, supplied via <c>services.AddFlagon(opts =&gt; ...)</c>.</summary>
public sealed class FlagonOptions
{
    internal Func<IServiceProvider, IFlagStore>? StoreFactory { get; private set; }

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
