using System.Reflection;
using System.Runtime.ExceptionServices;
using Flagon.Storage;

namespace Flagon.Tests.Support;

/// <summary>
/// Wraps a real store and can simulate an outage: while <see cref="IsDown"/> is true every call throws.
/// Built on <see cref="DispatchProxy"/> so it forwards every <see cref="IFlagStore"/> member, including ones added later.
/// </summary>
public class OutageFlagStore : DispatchProxy
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);
    private IFlagStore _inner = null!;

    /// <summary>How many times the <see cref="IFlagStore"/> method named <paramref name="methodName"/> was called (including failed calls).</summary>
    public int CallCount(string methodName) => _calls.GetValueOrDefault(methodName);

    /// <summary>When true, every store call throws <see cref="StoreOutageException"/>.</summary>
    public bool IsDown { get; set; }

    /// <summary>
    /// When set, <see cref="IFlagStore.GetChangeVersionAsync"/> does not complete until this task does (or the call is
    /// cancelled), simulating a hung store call: refreshes stall without failing.
    /// </summary>
    public Task? Hang { get; set; }

    /// <summary>When set, <see cref="IFlagStore.TryCreateAsync"/> does not start until this task completes.</summary>
    public Task? HangCreate { get; set; }

    /// <summary>When true, the store reports <see cref="IFlagStore.IsReadOnly"/> as true (like a remote, read-only store).</summary>
    public bool ReportsReadOnly { get; set; }

    /// <summary>The store as seen by Flagon (pass this to the host).</summary>
    public IFlagStore Store { get; private set; } = null!;

    /// <summary>The wrapped store, reachable even while the outage is simulated (stands in for another app's view).</summary>
    public IFlagStore Inner => _inner;

    /// <summary>Wraps <paramref name="inner"/> (a fresh <see cref="InMemoryFlagStore"/> if null).</summary>
    public static OutageFlagStore Wrap(IFlagStore? inner = null, bool isDown = false)
    {
        var proxy = Create<IFlagStore, OutageFlagStore>();
        var outage = (OutageFlagStore)(object)proxy;
        outage._inner = inner ?? new InMemoryFlagStore();
        outage.Store = proxy;
        outage.IsDown = isDown;
        return outage;
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        _calls.AddOrUpdate(targetMethod.Name, 1, (_, n) => n + 1);
        if (ReportsReadOnly && targetMethod.Name == "get_" + nameof(IFlagStore.IsReadOnly))
        {
            return true;
        }

        if (IsDown && !targetMethod.Name.StartsWith("get_", StringComparison.Ordinal))
        {
            throw new StoreOutageException();
        }

        if (Hang is { } hang && targetMethod.Name == nameof(IFlagStore.GetChangeVersionAsync))
        {
            var cancellationToken = args?.OfType<CancellationToken>().FirstOrDefault() ?? default;
            return new ValueTask<long>(HangThenGetChangeVersionAsync(hang, cancellationToken));
        }

        if (HangCreate is { } hangCreate && targetMethod.Name == nameof(IFlagStore.TryCreateAsync))
        {
            return HangThenCreateAsync(hangCreate, targetMethod, args);
        }

        try
        {
            return targetMethod.Invoke(_inner, args);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            ExceptionDispatchInfo.Throw(e.InnerException);
            throw;
        }
    }

    private async Task<FlagRecord?> HangThenCreateAsync(Task hang, MethodInfo targetMethod, object?[]? args)
    {
        await hang;
        return await (Task<FlagRecord?>)targetMethod.Invoke(_inner, args)!;
    }

    private async Task<long> HangThenGetChangeVersionAsync(Task hang, CancellationToken cancellationToken)
    {
        await hang.WaitAsync(cancellationToken);
        return await _inner.GetChangeVersionAsync(cancellationToken);
    }
}

/// <summary>Thrown by <see cref="OutageFlagStore"/> while it is down.</summary>
public sealed class StoreOutageException() : Exception("The flag store is unavailable (simulated outage).");
