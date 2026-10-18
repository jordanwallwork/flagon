using System.Text.Json;
using Jorrk.Flagon.Internal;

namespace Jorrk.Flagon.Testing.Internal;

/// <summary>
/// Records every evaluation the engine serves into the <see cref="EvaluationLog"/>, stamped by the host's
/// <see cref="TimeProvider"/>, unless <see cref="FlagonTestingSwitches.RecordEvaluations"/> is off.
/// </summary>
internal sealed class EvaluationRecorder(EvaluationLog log, FlagonTestingSwitches switches, TimeProvider time) : IFlagonEvaluationObserver
{
    public bool IsEnabled => switches.RecordEvaluations;

    public void OnEvaluated(in ObservedEvaluation evaluation)
    {
        if (!IsEnabled)
        {
            return;
        }

        log.Add(new RecordedEvaluation(
            evaluation.FlagKey,
            evaluation.RequestedType,
            // The value may belong to a snapshot that is replaced later; the log outlives it.
            evaluation.Value.ValueKind == JsonValueKind.Undefined ? default : evaluation.Value.Clone(),
            evaluation.Variant,
            evaluation.Reason,
            evaluation.ErrorCode,
            evaluation.ErrorMessage,
            evaluation.Context ?? EvaluationContext.Empty,
            Source(evaluation.Origin),
            time.GetUtcNow()));
    }

    /// <summary>The public name of the engine's <see cref="EvaluationOrigin"/>.</summary>
    private static EvaluationSource Source(EvaluationOrigin origin) => origin switch
    {
        EvaluationOrigin.InProcess => EvaluationSource.InProcess,
        EvaluationOrigin.Remote => EvaluationSource.Remote,
        _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, null),
    };
}
