using System.Text.Json;
using Flagon.Internal.Targeting;
using Flagon.Internal.Targeting.JsonLogic;

namespace Flagon.Tests;

/// <summary>
/// The default targeting engine at the <see cref="ITargetingCompiler"/> seam: what the flagd-testbed does not cover (zero-allocation
/// evaluation, out-of-schema operators, rules over non-string context values). Parity itself is
/// <c>DefaultCompilerParityTests</c>.
/// </summary>
public class JsonLogicTargetingCompilerTests
{
    private static readonly JsonLogicTargetingCompiler Compiler = new();

    private static CompiledRule Compile(string flagKey, string rule)
    {
        var compilation = Compiler.Compile(flagKey, JsonDocument.Parse(rule).RootElement);
        Assert.True(compilation.Succeeded, compilation.Error?.Message);
        return compilation.Rule!;
    }

    private static string? Run(CompiledRule rule, string flagKey, EvaluationContext context) =>
        rule(new TargetingContext(flagKey, context, 1_767_225_600));

    public static TheoryData<string, string> CommonRules => new()
    {
        { "equality", """{"if": [{"==": [{"var": "email"}, "ballmer@macrosoft.com"]}, "on", "off"]}""" },
        { "in list", """{"if": [{"in": [{"var": "country"}, ["GB", "FR", "DE"]]}, "on", "off"]}""" },
        { "nested if/and", """{"if": [{"and": [{">=": [{"var": "age"}, 18]}, {"!": {"var": "banned"}}]}, {"if": [{"starts_with": [{"var": "email"}, "ballmer"]}, "on", "off"]}, "off"]}""" },
        { "fractional (targeting key)", """{"fractional": [["on", 50], ["off", 50]]}""" },
        { "fractional (explicit string)", """{"fractional": [{"var": "email"}, ["on", 10], ["off", 90]]}""" },
        { "$flagd.flagKey", """{"if": [{"==": [{"var": "$flagd.flagKey"}, "my-flag"]}, "on", "off"]}""" },
        { "some over a list", """{"if": [{"some": [{"var": "roles"}, {"==": [{"var": ""}, "admin"]}]}, "on", "off"]}""" },
    };

    [Theory]
    [MemberData(nameof(CommonRules))]
    public void A_common_rule_evaluates_without_allocating(string name, string json)
    {
        var rule = Compile("my-flag", json);
        var context = new EvaluationContext("user-42", new Dictionary<string, object?>
        {
            ["email"] = "ballmer@macrosoft.com", ["country"] = "FR", ["age"] = 30L, ["banned"] = false,
            ["roles"] = new List<object?> { "user", "admin" },
        });
        Run(rule, "my-flag", context);

        var before = GC.GetAllocatedBytesForCurrentThread();
        string? variant = null;
        for (var i = 0; i < 100; i++)
        {
            variant = Run(rule, "my-flag", context);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.NotNull(variant);
        Assert.True(allocated == 0, $"{name}: {allocated} bytes allocated over 100 evaluations");
    }

    [Theory]
    [InlineData("""{"log": "x"}""")]
    [InlineData("""{"?:": [true, "on", "off"]}""")]
    [InlineData("""{"abs": -1}""")]
    [InlineData("""{"contains_any": [["a"], ["a"]]}""")]
    [InlineData("""{"if": [{"$ref": "missing"}, "on", "off"]}""")]
    [InlineData("""{"if": "on"}""")]
    [InlineData("""{"==": 1}""")]
    [InlineData("""{"var": [1]}""")]
    public void A_rule_outside_flagds_operator_set_or_shape_fails_to_compile(string json)
    {
        var compilation = Compiler.Compile("flag", JsonDocument.Parse(json).RootElement);

        Assert.False(compilation.Succeeded);
        Assert.NotNull(compilation.Error);
    }

    // testing-flags.json "context-aware": not referenced by any testbed feature.
    [Theory]
    [InlineData("Sulisław", "Świętopełk", 29L, false, "internal")]
    [InlineData("Sulisław", "Świętopełk", 29.0, false, "internal")]
    [InlineData("Sulisław", "Świętopełk", 30L, false, "external")]
    [InlineData("Sulisław", "Świętopełk", 29L, true, "external")]
    public void A_rule_compares_unicode_strings_numbers_and_booleans_as_flagd_does(string fn, string ln, object age, bool customer, string expected)
    {
        var rule = Compile("context-aware", """
            {"if": [{"and": [{"==": [{"var": ["fn"]}, "Sulisław"]}, {"==": [{"var": ["ln"]}, "Świętopełk"]},
                             {"==": [{"var": ["age"]}, 29]}, {"==": [{"var": ["customer"]}, false]}]}, "internal", "external"]}
            """);
        var context = new EvaluationContext(null, new Dictionary<string, object?> { ["fn"] = fn, ["ln"] = ln, ["age"] = age, ["customer"] = customer });

        Assert.Equal(expected, Run(rule, "context-aware", context));
    }

    [Theory]
    [InlineData("""{"var": ["user.tier", "free"]}""", "free")] // missing path: default
    [InlineData("""{"cat": [{"var": "user.name"}, "-", 3]}""", "ada-3")]
    [InlineData("""{"if": [{"missing": ["user.name", "user.tier"]}, "incomplete", "complete"]}""", "incomplete")]
    [InlineData("""{"if": [{"some": [{"var": "roles"}, {"==": [{"var": ""}, "admin"]}]}, "admin", "user"]}""", "admin")]
    [InlineData("""{"if": [{"<": [1, {"+": [{"var": "n"}, "1"]}, 10]}, "between", "outside"]}""", "between")]
    [InlineData("""{"substr": [{"var": "user.name"}, -2]}""", "da")]
    [InlineData("""{"if": [{"==": [{"var": "$flagd.timestamp"}, 1767225600]}, "now", "then"]}""", "now")]
    public void Standard_operators_follow_the_go_engine(string json, string expected)
    {
        var rule = Compile("flag", json);
        var context = new EvaluationContext("k", new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["name"] = "ada" },
            ["roles"] = new List<object?> { "user", "admin" },
            ["n"] = 2L,
        });

        Assert.Equal(expected, Run(rule, "flag", context));
    }
}
