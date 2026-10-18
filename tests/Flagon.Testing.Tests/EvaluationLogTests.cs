using System.Text.Json;

namespace Flagon.Testing.Tests;

public class EvaluationLogTests
{
    [Fact]
    public void A_typed_evaluation_is_recorded_with_its_key_type_value_variant_reason_and_context()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("new-checkout", true);
        var user42 = new EvaluationContext("user-42");

        flags.IsEnabled("new-checkout", defaultValue: false, user42);

        var evaluation = Assert.Single(flags.Evaluations);
        Assert.Equal("new-checkout", evaluation.FlagKey);
        Assert.Equal(FlagType.Boolean, evaluation.RequestedType);
        Assert.Equal(JsonValueKind.True, evaluation.Value.ValueKind);
        Assert.Equal("on", evaluation.Variant);
        Assert.Equal(EvaluationReason.Static, evaluation.Reason);
        Assert.Equal(ErrorCode.None, evaluation.ErrorCode);
        Assert.Null(evaluation.ErrorMessage);
        Assert.Same(user42, evaluation.Context);
        Assert.Equal(EvaluationSource.InProcess, evaluation.Source);
    }

    [Fact]
    public void A_bulk_evaluation_records_each_flag_untyped()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("dark-mode", false).SetFlag("new-checkout", true);

        flags.GetAllDetails(new EvaluationContext("user-42"));

        Assert.Equal(["dark-mode", "new-checkout"], flags.Evaluations.Select(e => e.FlagKey));
        Assert.All(flags.Evaluations, e =>
        {
            Assert.Null(e.RequestedType);
            Assert.Equal(EvaluationSource.InProcess, e.Source);
            Assert.Equal("user-42", e.Context.TargetingKey);
        });
        Assert.Equal(JsonValueKind.True, flags.Evaluations.For("new-checkout").Single().Value.ValueKind);
    }

    [Fact]
    public void A_missing_key_is_recorded_with_the_callers_default_and_flag_not_found()
    {
        using var flags = TestFlagon.Create();

        flags.GetInt("max-items", 10);

        var evaluation = Assert.Single(flags.Evaluations);
        Assert.Equal(FlagType.Number, evaluation.RequestedType);
        Assert.Equal(10, evaluation.Value.GetInt32());
        Assert.Null(evaluation.Variant);
        Assert.Equal(EvaluationReason.Error, evaluation.Reason);
        Assert.Equal(ErrorCode.FlagNotFound, evaluation.ErrorCode);
        Assert.NotNull(evaluation.ErrorMessage);
        Assert.Same(EvaluationContext.Empty, evaluation.Context);
    }

    [Fact]
    public void A_type_mismatch_is_recorded_with_the_requested_type_and_the_callers_default()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("new-checkout", true);

        flags.GetString("new-checkout", "fallback");

        var evaluation = Assert.Single(flags.Evaluations);
        Assert.Equal(FlagType.String, evaluation.RequestedType);
        Assert.Equal("fallback", evaluation.Value.GetString());
        Assert.Equal(ErrorCode.TypeMismatch, evaluation.ErrorCode);
    }

    [Fact]
    public void Nothing_is_recorded_when_recording_is_switched_off()
    {
        using var flags = TestFlagon.Create(testing: t => t.RecordEvaluations = false);
        flags.Flags.SetFlag("new-checkout", true);

        flags.IsEnabled("new-checkout");
        flags.GetAllDetails();

        Assert.Empty(flags.Evaluations);
        Assert.Same(flags.Flags.Evaluations, flags.Evaluations);
    }

    [Fact]
    public void The_log_keeps_evaluation_order_and_filters_by_key()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("a", true).SetFlag("b", false);

        flags.IsEnabled("a", context: new EvaluationContext("1"));
        flags.IsEnabled("b");
        flags.IsEnabled("a", context: new EvaluationContext("2"));

        Assert.Equal(["a", "b", "a"], flags.Evaluations.Select(e => e.FlagKey));
        Assert.Equal(3, flags.Evaluations.Count);
        Assert.Equal("b", flags.Evaluations[1].FlagKey);
        Assert.Equal(["1", "2"], flags.Evaluations.For("a").Select(e => e.Context.TargetingKey));
        Assert.True(flags.Evaluations.WasEvaluated("b"));
        Assert.False(flags.Evaluations.WasEvaluated("c"));
        Assert.Empty(flags.Evaluations.For("c"));
    }

    [Fact]
    public void Clear_forgets_what_was_recorded_and_recording_continues()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("a", true);
        flags.IsEnabled("a");

        flags.Evaluations.Clear();
        Assert.Empty(flags.Evaluations);
        Assert.False(flags.Evaluations.WasEvaluated("a"));

        flags.IsEnabled("a");
        Assert.Single(flags.Evaluations);
    }

    [Fact]
    public void Enumerating_takes_a_snapshot_so_evaluations_made_meanwhile_do_not_break_it()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("a", true);
        flags.IsEnabled("a");
        flags.IsEnabled("a");

        var seen = 0;
        foreach (var _ in flags.Evaluations)
        {
            flags.IsEnabled("a");
            seen++;
        }

        Assert.Equal(2, seen);
        Assert.Equal(4, flags.Evaluations.Count);
    }

    [Fact]
    public async Task Evaluations_from_parallel_tasks_are_all_recorded()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("a", true);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                flags.IsEnabled("a");
            }
        })));

        Assert.Equal(4000, flags.Evaluations.Count);
    }
}
