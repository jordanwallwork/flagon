using System.Text.Json;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Internal.Targeting.JsonLogic;

namespace Jorrk.Flagon.Tests;

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

    // Verified against flagd core v0.19.0 (diegoholiveira/jsonlogic v3.9.1): inside map/filter/... an operator's data is
    // the element, so fractional buckets on the element's targetingKey and $flagd.flagKey (empty when absent), and
    // returns null when the element is not an object.
    [Theory]
    [InlineData("""{"map": [{"var": "members"}, {"fractional": [["on", 1]]}]}""", false, "[on,on,on,on]")]
    [InlineData("""{"map": [{"var": "members"}, {"fractional": [["on", 1], ["off", 1]]}]}""", false, "[off,on,on,on]")]
    [InlineData("""{"map": [{"var": "members"}, {"fractional": [["on", 1], ["off", 1]]}]}""", true, "[off,on,off,on]")]
    [InlineData("""{"map": [["a"], {"fractional": ["x", ["on", 1]]}]}""", false, "[null]")]
    public void Fractional_inside_an_iteration_buckets_on_the_element_as_flagd_does(string json, bool memberFlagKeys, string expected)
    {
        var rule = Compile("f", json);
        var members = new List<object?>();
        foreach (var key in new[] { "m1", "m2", "m3", "m4" })
        {
            var member = new Dictionary<string, object?> { ["targetingKey"] = key };
            if (memberFlagKeys)
            {
                member["$flagd"] = new Dictionary<string, object?> { ["flagKey"] = "f" };
            }

            members.Add(member);
        }

        var context = new EvaluationContext(null, new Dictionary<string, object?> { ["members"] = members });

        Assert.Equal(expected, Run(rule, "f", context));
    }

    [Fact]
    public void Nested_fractionals_evaluate_each_variant_once()
    {
        var json = """{"var": "variant"}""";
        for (var i = 0; i < 10; i++)
        {
            json = $$"""{"fractional": [[{{json}}, 1]]}""";
        }

        var attributes = new CountingAttributes(new Dictionary<string, object?> { ["variant"] = "on" });

        Assert.Equal("on", Run(Compile("flag", json), "flag", new EvaluationContext("k", attributes)));
        Assert.Equal(1, attributes.Lookups("variant"));
    }

    // flagd JSON-encodes the rule's result, and Go's encoder rejects NaN and infinities: PARSE_ERROR, not a variant name.
    [Theory]
    [InlineData("""{"/": [1, 0]}""")]
    [InlineData("""{"%": [1, 0]}""")]
    [InlineData("""{"map": [[1], {"/": [1, 0]}]}""")]
    public void A_non_finite_result_is_an_evaluation_error(string json)
    {
        var rule = Compile("flag", json);

        Assert.Throws<JsonLogicException>(() => Run(rule, "flag", new EvaluationContext("k", new Dictionary<string, object?>())));
    }

    [Fact]
    public void A_non_finite_number_turned_into_a_string_is_a_variant_name()
    {
        Assert.Equal("+Inf", Run(Compile("flag", """{"cat": [{"/": [1, 0]}]}"""), "flag", new EvaluationContext("k", new Dictionary<string, object?>())));
    }

    // The Go engine evaluates every argument of an ordinary operator before running it, so an argument it then ignores
    // still fails the rule; if/and/or are lazy.
    [Theory]
    [InlineData("""{"==": [1, 1, {"cat": [true]}]}""")]
    [InlineData("""{"===": [1, 1, {"cat": [true]}]}""")]
    [InlineData("""{"!": [true, {"cat": [true]}]}""")]
    [InlineData("""{"<": [1, 2, 3, {"cat": [true]}]}""")]
    [InlineData("""{"in": ["a", ["a"], {"cat": [true]}]}""")]
    [InlineData("""{"substr": ["abc", 9, {"cat": [true]}]}""")]
    [InlineData("""{"%": [3, 2, {"cat": [true]}]}""")]
    [InlineData("""{"missing_some": [1, ["a"], {"cat": [true]}]}""")]
    [InlineData("""{"starts_with": ["a", "a", {"cat": [true]}]}""")]
    [InlineData("""{"sem_ver": ["1.0.0", "=", "1.0.0", {"cat": [true]}]}""")]
    [InlineData("""{"fractional": [["on", 1], ["off", 1, {"cat": [true]}]]}""")]
    [InlineData("""{"fractional": [["on", "bad"], {"cat": [true]}]}""")]
    public void A_failing_argument_the_operator_ignores_still_fails_the_rule(string json)
    {
        var rule = Compile("flag", json);

        Assert.Throws<JsonLogicException>(() => Run(rule, "flag", new EvaluationContext("k", new Dictionary<string, object?>())));
    }

    [Theory]
    [InlineData("""{"if": [true, "on", {"cat": [true]}]}""", "on")]
    [InlineData("""{"and": [false, {"cat": [true]}]}""", "false")]
    public void A_failing_branch_that_if_and_or_skip_does_not_fail_the_rule(string json, string expected)
    {
        Assert.Equal(expected, Run(Compile("flag", json), "flag", new EvaluationContext("k", new Dictionary<string, object?>())));
    }

    // flagd formats a numeric version with Go's %v, which switches to an exponent from 1e6 ("v1e+06" is not a version).
    [Theory]
    [InlineData(999999.0, "999999.0.0", "on")]
    [InlineData(1000000.0, "1000000.0.0", "off")]
    [InlineData(1.5, "1.5.0", "on")]
    public void Sem_ver_reads_a_numeric_version_as_go_formats_it(double version, string target, string expected)
    {
        var rule = Compile("flag", $$"""{"if": [{"sem_ver": [{"var": "v"}, "=", "{{target}}"]}, "on", "off"]}""");

        Assert.Equal(expected, Run(rule, "flag", new EvaluationContext("k", new Dictionary<string, object?> { ["v"] = version })));
    }
}

/// <summary>Attributes that count how often each one is read, to observe how many times a rule evaluates a <c>var</c>.</summary>
internal sealed class CountingAttributes(IReadOnlyDictionary<string, object?> inner) : IReadOnlyDictionary<string, object?>
{
    private readonly Dictionary<string, int> _lookups = new(StringComparer.Ordinal);

    public int Lookups(string key) => _lookups.GetValueOrDefault(key);

    public bool TryGetValue(string key, out object? value)
    {
        _lookups[key] = Lookups(key) + 1;
        return inner.TryGetValue(key, out value);
    }

    public object? this[string key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException(key);

    public IEnumerable<string> Keys => inner.Keys;

    public IEnumerable<object?> Values => inner.Values;

    public int Count => inner.Count;

    public bool ContainsKey(string key) => TryGetValue(key, out _);

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => inner.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
