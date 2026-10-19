namespace Jorrk.Flagon.Storage;

/// <summary>
/// An optional companion to <see cref="IFlagChangeNotifier"/> for stores whose listeners hold connections. The host awaits
/// <see cref="ListenersStopped"/> after cancelling the notifier, so no connection outlives the host's stop.
/// </summary>
public interface IFlagChangeNotifierTeardown
{
    /// <summary>Completes once every listener started so far has stopped and released its connection.</summary>
    Task ListenersStopped { get; }
}
