namespace Jorrk.Flagon;

/// <summary>
/// Supplies the actor (who is making a change) recorded in audit entries and in flag metadata's
/// <c>createdBy</c>/<c>updatedBy</c> by <see cref="IFlagonAdmin"/> writes. Resolved once per write.
/// </summary>
/// <remarks>
/// The default implementation returns <see cref="FlagonActor.Current"/>. Register your own singleton to source the actor
/// elsewhere (for example from the current HTTP user); implementations should normally prefer
/// <see cref="FlagonActor.Current"/> when it is set so explicit scopes still win.
/// </remarks>
public interface IFlagonActorAccessor
{
    /// <summary>The current actor, or null for an anonymous/system change.</summary>
    string? Actor { get; }
}

/// <summary>
/// An ambient actor that flows with the async context. Wrap admin calls in <c>using (FlagonActor.Use("alice")) { ... }</c>
/// to attribute them to <c>alice</c>.
/// </summary>
public static class FlagonActor
{
    private static readonly AsyncLocal<string?> Ambient = new();

    /// <summary>The ambient actor for the current async flow, or null when no scope is active.</summary>
    public static string? Current => Ambient.Value;

    /// <summary>Sets the ambient actor until the returned scope is disposed (scopes nest; disposing restores the outer actor).</summary>
    public static IDisposable Use(string? actor)
    {
        var previous = Ambient.Value;
        Ambient.Value = actor;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Ambient.Value = previous;
            }
        }
    }
}
