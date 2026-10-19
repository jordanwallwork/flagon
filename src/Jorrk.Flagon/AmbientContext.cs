namespace Jorrk.Flagon;

/// <summary>
/// Supplies the ambient <see cref="EvaluationContext"/> (e.g. the current request's user), so call sites need not pass
/// one explicitly. Flagon registers an accessor with no ambient context; <c>Jorrk.Flagon.AspNetCore</c>'s
/// <c>AddFlagonAmbientContext()</c> replaces it with one that resolves the context from the current <c>HttpContext</c>.
/// </summary>
public interface IEvaluationContextAccessor
{
    /// <summary>The ambient context, or null when there is none (e.g. outside a request).</summary>
    EvaluationContext? Current { get; }
}

/// <summary>The default <see cref="IEvaluationContextAccessor"/>: there is never an ambient context.</summary>
public sealed class NullEvaluationContextAccessor : IEvaluationContextAccessor
{
    /// <summary>The shared instance.</summary>
    public static NullEvaluationContextAccessor Instance { get; } = new();

    /// <inheritdoc />
    public EvaluationContext? Current => null;
}
