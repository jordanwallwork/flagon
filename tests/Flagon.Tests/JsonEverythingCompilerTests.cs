using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Targeting.JsonEverything;

namespace Flagon.Tests;

/// <summary>
/// Spike B (#23) behaviour at the targeting-compiler seam that the flagd-testbed fixtures do not cover.
/// Expected results are flagd's (Go evaluator, see the flagd-semantics research notes).
/// </summary>
public class JsonEverythingCompilerTests
{
    private readonly ITargetingCompiler _compiler = new JsonEverythingTargetingCompiler();

    [Theory]
    [InlineData("""{"log": "on"}""")]
    [InlineData("""{"?:": [true, "on", "off"]}""")]
    [InlineData("""{"abs": [-1]}""")]
    [InlineData("""{"if": [{"set": ["x", 1]}, "on", "off"]}""")]
    [InlineData("""{"flagon/fractional": [["on", 1]]}""")]
    public void Operators_outside_flagds_schema_fail_to_compile(string rule)
    {
        var compilation = _compiler.Compile("flag", Parse(rule));

        Assert.False(compilation.Succeeded);
    }

    [Fact]
    public void Var_default_applies_when_the_attribute_is_present_but_null()
    {
        var rule = Compile("""{"if": [{"==": [{"var": ["tier", "basic"]}, "basic"]}, "on", "off"]}""");

        var variant = rule(Context(new() { ["tier"] = null }));

        Assert.Equal("on", variant);
    }

    private CompiledRule Compile(string rule)
    {
        var compilation = _compiler.Compile("flag", Parse(rule));
        Assert.True(compilation.Succeeded, compilation.Error);
        return compilation.Rule!;
    }

    private static TargetingContext Context(Dictionary<string, object?> attributes, string? targetingKey = null) =>
        new("flag", new EvaluationContext(targetingKey, attributes), timestamp: 1_767_225_600);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
