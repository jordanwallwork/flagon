using System.Text.Json;
using System.Text.Json.Nodes;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Internal.Targeting.JsonLogic;

namespace Jorrk.Flagon.Tests;

/// <summary><c>$ref</c> resolution: structural inlining with flagd's semantics, and errors located at the reference.</summary>
public class SegmentRefsTests
{
    private static readonly IReadOnlyDictionary<string, JsonElement> Segments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
    {
        ["staff"] = Json("""{"ends_with":[{"var":"email"},"@example.com"]}"""),
        ["beta"] = Json("""{"in":[{"var":"targetingKey"},["alice","bob"]]}"""),
        ["broken"] = Json("""{"and":[true,{"unknown_op":[1]}]}"""),
    };

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static void AssertJson(string expected, JsonElement actual) =>
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())), $"Expected {expected} but got {actual.GetRawText()}.");

    [Theory]
    [InlineData("""{"$ref":"staff"}""", """{"ends_with":[{"var":"email"},"@example.com"]}""")]
    [InlineData("""{"if":[{"$ref":"staff"},"on",null]}""", """{"if":[{"ends_with":[{"var":"email"},"@example.com"]},"on",null]}""")]
    [InlineData(
        """{"if":[{"or":[{"$ref":"staff"},{"and":[{"$ref":"beta"},true]}]},"on",{"if":[{"$ref":"staff"},"x",null]}]}""",
        """{"if":[{"or":[{"ends_with":[{"var":"email"},"@example.com"]},{"and":[{"in":[{"var":"targetingKey"},["alice","bob"]]},true]}]},"on",{"if":[{"ends_with":[{"var":"email"},"@example.com"]},"x",null]}]}""")]
    public void References_are_replaced_by_the_segments_rule_at_any_depth(string rule, string expected)
    {
        var inlined = SegmentRefs.Inline(Json(rule), Segments);

        Assert.Null(inlined.Error);
        AssertJson(expected, inlined.Rule);
    }

    [Fact]
    public void A_rule_without_references_is_returned_as_it_is()
    {
        var rule = Json("""{"if":[{"==":[{"var":"tier"},"gold"]},"on",null]}""");

        var inlined = SegmentRefs.Inline(rule, Segments);

        Assert.Null(inlined.Error);
        Assert.Empty(inlined.Sites);
        Assert.Equal(rule.GetRawText(), inlined.Rule.GetRawText());
    }

    [Theory]
    [InlineData("""{"if":[{"$ref":"missing"},"on",null]}""", "/if/0", "segment 'missing' does not exist")]
    [InlineData("""{"$ref":"missing"}""", "", "segment 'missing' does not exist")]
    [InlineData("""{"if":[{"or":[true,{"$ref":42}]},"on",null]}""", "/if/0/or/1", "the $ref operand must be a segment key (a string), got 42")]
    [InlineData("""{"if":[{"$ref":["staff"]},"on",null]}""", "/if/0", """the $ref operand must be a segment key (a string), got ["staff"]""")]
    [InlineData("""{"if":[{"$ref":"staff","x":1},"on",null]}""", "/if/0", """a $ref object must have no other keys, got {"$ref":"staff","x":1}""")]
    public void A_missing_or_malformed_reference_is_an_error_at_the_reference(string rule, string path, string message)
    {
        var error = SegmentRefs.Inline(Json(rule), Segments).Error;

        Assert.NotNull(error);
        Assert.Equal(message, error.Message);
        Assert.Equal(path, error.Path);
        Assert.Equal("$ref", error.Operator);
    }

    [Fact]
    public void Pointers_escape_tilde_and_slash_in_keys()
    {
        var error = SegmentRefs.Inline(Json("""{"a/b":{"c~d":[{"$ref":"missing"}]}}"""), Segments).Error;

        Assert.Equal("/a~1b/c~0d/0", error!.Path);
    }

    [Fact]
    public void References_lists_each_referenced_segment_once_in_order_of_first_use()
    {
        var rule = Json("""{"if":[{"or":[{"$ref":"beta"},{"$ref":"staff"},{"$ref":"beta"}]},"on",{"if":[{"$ref":"staff"},"x",{"$ref":7}]}]}""");

        Assert.Equal(["beta", "staff"], SegmentRefs.References(rule));
        Assert.Empty(SegmentRefs.References(Json("""{"==":[1,1]}""")));
        Assert.Empty(SegmentRefs.References(null));
    }

    [Fact]
    public void An_error_inside_an_inlined_segment_is_reported_at_its_reference_naming_the_segment()
    {
        var compilation = TargetingRules.Compile(
            new JsonLogicTargetingCompiler(), "my-flag", Json("""{"if":[true,"on",{"if":[{"$ref":"broken"},"x",null]}]}"""), Segments);

        Assert.False(compilation.Succeeded);
        Assert.Equal("in segment 'broken': unsupported operator 'unknown_op'", compilation.Error!.Message);
        Assert.Equal("/if/2/if/0", compilation.Error.Path);
        Assert.Equal("unknown_op", compilation.Error.Operator);
    }

    [Fact]
    public void An_error_outside_any_reference_is_reported_as_it_is()
    {
        var compilation = TargetingRules.Compile(
            new JsonLogicTargetingCompiler(), "my-flag", Json("""{"if":[{"$ref":"staff"},"on",{"nope":[1]}]}"""), Segments);

        Assert.Equal("unsupported operator 'nope'", compilation.Error!.Message);
        Assert.Equal("/if/2", compilation.Error.Path);
    }

    [Fact]
    public void A_combination_too_deep_to_inline_is_a_compile_failure_not_a_crash()
    {
        // Each is within JSON's default depth limit (64); the inlined rule is not.
        const int depth = 20; // each {"!":[ is two levels
        var segment = Json(string.Concat(Enumerable.Repeat("{\"!\":[", depth)) + "true" + string.Concat(Enumerable.Repeat("]}", depth)));
        var flag = Json(string.Concat(Enumerable.Repeat("{\"!\":[", depth)) + "{\"$ref\":\"deep\"}" + string.Concat(Enumerable.Repeat("]}", depth)));

        var compilation = TargetingRules.Compile(new JsonLogicTargetingCompiler(), "my-flag", flag,
            new Dictionary<string, JsonElement>(StringComparer.Ordinal) { ["deep"] = segment });

        Assert.False(compilation.Succeeded);
        Assert.Contains("could not be inlined", compilation.Error!.Message);
    }

    [Fact]
    public void Without_segments_a_reference_reaches_the_compiler_which_says_the_store_has_none()
    {
        var compilation = TargetingRules.Compile(new JsonLogicTargetingCompiler(), "my-flag", Json("""{"if":[{"$ref":"staff"},"on",null]}"""), segments: null);

        Assert.Equal("$ref", compilation.Error!.Operator);
        Assert.Equal("/if/0", compilation.Error.Path);
        Assert.Contains("store that supports segments", compilation.Error.Message);
    }

    [Fact]
    public void A_flag_using_a_segment_evaluates_like_its_inlined_equivalent_without_allocating_more()
    {
        var compiler = new JsonLogicTargetingCompiler();
        var viaRef = TargetingRules.Compile(compiler, "my-flag", Json("""{"if":[{"$ref":"staff"},"on","off"]}"""), Segments).Rule!;
        var inline = TargetingRules.Compile(compiler, "my-flag", Json("""{"if":[{"ends_with":[{"var":"email"},"@example.com"]},"on","off"]}""")).Rule!;
        var staff = new EvaluationContext("user-1", new Dictionary<string, object?> { ["email"] = "ada@example.com" });
        var other = new EvaluationContext("user-2", new Dictionary<string, object?> { ["email"] = "bob@elsewhere.test" });

        Assert.Equal(Run(inline, staff), Run(viaRef, staff));
        Assert.Equal(Run(inline, other), Run(viaRef, other));
        Assert.Equal(Allocated(inline, staff), Allocated(viaRef, staff));

        static string? Run(CompiledRule rule, EvaluationContext context) => rule(new TargetingContext("my-flag", context, 0));

        static long Allocated(CompiledRule rule, EvaluationContext context)
        {
            Run(rule, context);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 100; i++)
            {
                Run(rule, context);
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
