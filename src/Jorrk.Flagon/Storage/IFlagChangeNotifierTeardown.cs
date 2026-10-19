namespace Jorrk.Flagon.Storage;

/// <summary>
/// An optional companion to <see cref="IFlagChangeNotifier"/> for stores whose listeners hold connections. After cancelling
/// the token it passed to <see cref="IFlagChangeNotifier.StartAsync"/>, the host awaits <see cref="WhenStoppedAsync"/>, so
/// no connection outlives the host's stop.
/// </summary>
public interface IFlagChangeNotifierTeardown
{
    /// <summary>
    /// Completes once the listener started with <paramref name="startToken"/> has stopped and released its connection.
    /// Listeners started with other tokens (another host sharing the store) are not waited on. Completes at once when no
    /// listener was started with the token.
    /// </summary>
    /// <param name="startToken">The token that was passed to <see cref="IFlagChangeNotifier.StartAsync"/>.</param>
    Task WhenStoppedAsync(CancellationToken startToken);
}
