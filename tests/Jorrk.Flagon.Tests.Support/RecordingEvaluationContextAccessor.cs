namespace Jorrk.Flagon.Tests.Support;

/// <summary>
/// An ambient <see cref="IEvaluationContextAccessor"/> that supplies a fixed context and counts how often evaluation
/// asked for it. Register it with <c>services.AddSingleton&lt;IEvaluationContextAccessor&gt;(accessor)</c>.
/// </summary>
public sealed class RecordingEvaluationContextAccessor(EvaluationContext? current = null) : IEvaluationContextAccessor
{
    private int _reads;

    /// <summary>How many times <see cref="Current"/> has been read.</summary>
    public int Reads => Volatile.Read(ref _reads);

    /// <inheritdoc />
    public EvaluationContext? Current
    {
        get
        {
            Interlocked.Increment(ref _reads);
            return current;
        }
    }
}
