using System.Text.Json;
using Flagon.Internal;

namespace Flagon.Testing.Internal;

/// <summary>
/// Records every evaluation the engine serves into the <see cref="EvaluationLog"/>, stamped by the host's
/// <see cref="TimeProvider"/>, unless <see cref="FlagonTestingSwitches.RecordEvaluations"/> is off.
/// </summary>
internal sealed class EvaluationRecorder(EvaluationLog log, FlagonTestingSwitches switches, TimeProvider time) : IFlagonEvaluationObserver
{
    public void OnEvaluated(in ObservedEvaluation evaluation)
    {
        if (!switches.RecordEvaluations)
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
            evaluation.Remote ? EvaluationSource.Remote : EvaluationSource.InProcess,
            time.GetUtcNow()));
    }
}
