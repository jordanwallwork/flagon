using System.Collections;

namespace Jorrk.Flagon.Testing;

/// <summary>
/// Every evaluation served since the log was last cleared, in evaluation order: which flags the code evaluated, for
/// whom, and what it got. Plain .NET types, so any assertion library works. Thread-safe; enumerating it takes a
/// snapshot, so evaluations made meanwhile don't affect an assertion in progress.
/// </summary>
/// <example>
/// <code>
/// var checkout = Assert.Single(flags.Evaluations.For("new-checkout"));
/// Assert.Equal("user-42", checkout.Context.TargetingKey);
/// Assert.Equal("on", checkout.Variant);
/// </code>
/// </example>
public sealed class EvaluationLog : IReadOnlyList<RecordedEvaluation>
{
    private readonly List<RecordedEvaluation> _evaluations = [];
    private readonly object _gate = new();

    internal EvaluationLog()
    {
    }

    /// <summary>The number of evaluations recorded.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _evaluations.Count;
            }
        }
    }

    /// <summary>The evaluation at <paramref name="index"/>, in evaluation order.</summary>
    /// <param name="index">The zero-based position.</param>
    public RecordedEvaluation this[int index]
    {
        get
        {
            lock (_gate)
            {
                return _evaluations[index];
            }
        }
    }

    /// <summary>The evaluations of <paramref name="key"/>, in evaluation order.</summary>
    /// <param name="key">The flag key (compared ordinally).</param>
    /// <returns>A snapshot copy; empty when the key was not evaluated.</returns>
    public IReadOnlyList<RecordedEvaluation> For(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _evaluations.FindAll(e => string.Equals(e.FlagKey, key, StringComparison.Ordinal));
        }
    }

    /// <summary>True when <paramref name="key"/> was evaluated at least once.</summary>
    /// <param name="key">The flag key (compared ordinally).</param>
    /// <returns>Whether any recorded evaluation is of <paramref name="key"/>.</returns>
    public bool WasEvaluated(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            return _evaluations.Exists(e => string.Equals(e.FlagKey, key, StringComparison.Ordinal));
        }
    }

    /// <summary>Forgets every evaluation recorded so far.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _evaluations.Clear();
        }
    }

    /// <summary>Enumerates a snapshot of the evaluations recorded so far, in evaluation order.</summary>
    /// <returns>An enumerator over the snapshot.</returns>
    public IEnumerator<RecordedEvaluation> GetEnumerator()
    {
        RecordedEvaluation[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _evaluations];
        }

        return ((IEnumerable<RecordedEvaluation>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal void Add(RecordedEvaluation evaluation)
    {
        lock (_gate)
        {
            _evaluations.Add(evaluation);
        }
    }
}
