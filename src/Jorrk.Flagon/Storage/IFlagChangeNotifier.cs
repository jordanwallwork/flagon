namespace Jorrk.Flagon.Storage;

/// <summary>
/// An optional store capability: pushes a signal when the store's change version may have moved, so apps see a change
/// without waiting for the next poll. A store opts in by implementing this interface alongside <see cref="IFlagStore"/>;
/// stores that do not are polled exactly as before.
/// </summary>
/// <remarks>
/// <para>Signals carry no payload: on a signal the app reads the change version and reloads as it does on a poll tick.
/// Spurious signals are allowed. Missed signals are tolerated because polling still runs as the backstop, but a healthy
/// notifier must not miss a change that commits while it is connected.</para>
/// <para>The <c>onChanged</c> callback may run on any thread and must return quickly without calling back into the store.
/// Once the token passed to <see cref="StartAsync"/> is cancelled the notifier stops and must not invoke the callback
/// again.</para>
/// </remarks>
public interface IFlagChangeNotifier
{
    /// <summary>
    /// Starts listening until <paramref name="cancellationToken"/> is cancelled. Calls <paramref name="onChanged"/> (with no
    /// payload) for every change it observes, on any thread.
    /// </summary>
    /// <param name="onChanged">Called when the store's change version may have moved.</param>
    /// <param name="cancellationToken">Stops the notifier and releases its connection when cancelled.</param>
    Task StartAsync(Action onChanged, CancellationToken cancellationToken = default);

    /// <summary>Whether the notifier currently holds a live subscription. Reading it performs no I/O.</summary>
    bool IsConnected { get; }
}
