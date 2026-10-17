using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flagon.Storage;
using Xunit.Abstractions;

namespace Flagon.Tests.FlagdParity;

/// <summary>A vendored testbed suite: the flag files it runs against and its feature files (paths relative to <c>testbed/</c>).</summary>
internal sealed record FlagdSuite(string Name, IReadOnlyList<string> FlagFiles, string FeatureDirectory, string FeaturePattern);

/// <summary>
/// The vendored flagd-testbed fixtures, interpreted into runnable cases. See <c>testbed/PROVENANCE.md</c> for what is
/// vendored and for the boolean-subset rules applied here.
/// </summary>
internal static partial class FlagdTestbed
{
    /// <summary>
    /// The fractional bucketing generation Flagon targets. flagd 0.18 (testbed 3.10.4's image) implements v3; examples
    /// tagged with the other generations assert different buckets for the same input and are excluded.
    /// </summary>
    public const string FractionalVersionTag = "@fractional-v3";

    private static readonly string[] FractionalVersionTags = ["@fractional-v1", "@fractional-v2", "@fractional-v3"];

    public static readonly FlagdSuite[] Suites =
    [
        // The in-process evaluator contract ("Given an evaluator").
        new("evaluator", ["evaluator/flags/testkit-flags.json"], "evaluator/gherkin", "*.feature"),
        // The provider suite's targeting scenarios (adds $flagd.timestamp, edge-case errors and fractional v3).
        new("provider", ["flags/custom-ops.json", "flags/edge-case-flags.json", "flags/evaluator-refs.json", "flags/testing-flags.json"], "gherkin", "targeting.feature"),
    ];

    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "FlagdParity", "testbed");

    private static readonly Lazy<Catalogue> Loaded = new(Load);

    /// <summary>Cases in the boolean subset, in file order.</summary>
    public static IReadOnlyList<RunnableCase> Runnable => Loaded.Value.Runnable;

    /// <summary>Cases outside the boolean subset, with why.</summary>
    public static IReadOnlyList<(GherkinCase Case, string Reason)> Excluded => Loaded.Value.Excluded;

    public static RunnableCase Get(string id) => Loaded.Value.ById[id];

    /// <summary>
    /// A fresh InMemory store holding the suite's flags as Flagon stores them: <c>$ref</c>s resolved and each flag
    /// projected to booleans (see <see cref="FixtureFlag"/>). Flags Flagon cannot represent are left out.
    /// </summary>
    public static InMemoryFlagStore SeedStore(FlagdSuite suite)
    {
        var store = new InMemoryFlagStore();
        foreach (var flag in Loaded.Value.Flags[suite.Name].Values)
        {
            if (flag.Projected is { } document)
            {
                store.TryCreateAsync(flag.Key, document).GetAwaiter().GetResult();
            }
        }

        return store;
    }

    private sealed record Catalogue(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, FixtureFlag>> Flags,
        IReadOnlyList<RunnableCase> Runnable,
        IReadOnlyDictionary<string, RunnableCase> ById,
        IReadOnlyList<(GherkinCase, string)> Excluded);

    private static Catalogue Load()
    {
        var flags = new Dictionary<string, IReadOnlyDictionary<string, FixtureFlag>>(StringComparer.Ordinal);
        var runnable = new List<RunnableCase>();
        var excluded = new List<(GherkinCase, string)>();

        foreach (var suite in Suites)
        {
            var suiteFlags = LoadFlags(suite);
            flags[suite.Name] = suiteFlags;

            var featureDirectory = Path.Combine(Root, suite.FeatureDirectory);
            foreach (var file in Directory.GetFiles(featureDirectory, suite.FeaturePattern).OrderBy(f => f, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(Root, file).Replace('\\', '/');
                foreach (var gherkinCase in Gherkin.Parse(relative, File.ReadAllText(file)))
                {
                    var (result, reason) = Interpret(suite, suiteFlags, gherkinCase);
                    if (result is not null)
                    {
                        runnable.Add(result);
                    }
                    else
                    {
                        excluded.Add((gherkinCase, reason!));
                    }
                }
            }
        }

        return new Catalogue(flags, runnable, runnable.ToDictionary(c => c.Id, StringComparer.Ordinal), excluded);
    }

    private static Dictionary<string, FixtureFlag> LoadFlags(FlagdSuite suite)
    {
        var result = new Dictionary<string, FixtureFlag>(StringComparer.Ordinal);
        foreach (var file in suite.FlagFiles)
        {
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, file)))!.AsObject();
            var evaluators = root["$evaluators"]?.AsObject();
            foreach (var (key, node) in root["flags"]!.AsObject())
            {
                result.Add(key, FixtureFlag.Create(key, node!.AsObject(), evaluators));
            }
        }

        return result;
    }

    private static (RunnableCase?, string?) Interpret(FlagdSuite suite, IReadOnlyDictionary<string, FixtureFlag> flags, GherkinCase c)
    {
        var versionTags = c.Tags.Where(t => FractionalVersionTags.Contains(t)).ToList();
        if (versionTags.Count > 0 && !versionTags.Contains(FractionalVersionTag))
        {
            return (null, $"fractional bucketing generation {string.Join(",", versionTags)}; Flagon targets {FractionalVersionTag}");
        }

        string? flagType = null, flagKey = null, callerDefault = null, expectedValue = null, expectedReason = null, expectedError = null;
        string? targetingKey = null;
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var step in c.Steps)
        {
            Match m;
            if (IgnoredStep().IsMatch(step))
            {
                continue;
            }

            if ((m = FlagStep().Match(step)).Success)
            {
                (flagType, flagKey, callerDefault) = (m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value);
            }
            else if ((m = TargetingKeyStep().Match(step)).Success)
            {
                targetingKey = m.Groups[1].Value;
            }
            else if ((m = AttributeStep().Match(step)).Success)
            {
                attributes[m.Groups[1].Value] = TypedValue(m.Groups[2].Value, m.Groups[3].Value);
            }
            else if ((m = NestedAttributeStep().Match(step)).Success)
            {
                var outer = attributes.TryGetValue(m.Groups[1].Value, out var existing) && existing is Dictionary<string, object?> d
                    ? d
                    : new Dictionary<string, object?>(StringComparer.Ordinal);
                outer[m.Groups[2].Value] = m.Groups[3].Value;
                attributes[m.Groups[1].Value] = outer;
            }
            else if ((m = ValueStep().Match(step)).Success)
            {
                expectedValue = m.Groups[1].Value;
            }
            else if ((m = ReasonStep().Match(step)).Success)
            {
                expectedReason = m.Groups[1].Value;
            }
            else if ((m = ErrorStep().Match(step)).Success)
            {
                expectedError = m.Groups[1].Value;
            }
            else
            {
                return (null, $"unsupported step '{step}' (Flagon evaluation details have no counterpart)");
            }
        }

        if (flagKey is null || flagType is null || callerDefault is null)
        {
            return (null, "no flag step");
        }

        if (!flags.TryGetValue(flagKey, out var flag))
        {
            return (null, $"flag '{flagKey}' is not in the suite's flag files (FLAG_NOT_FOUND is type-agnostic and covered by Flagon's own tests)");
        }

        if (!flag.IsBoolean && !flag.HasTargeting)
        {
            return (null, $"{flagType} flag without targeting: resolves a typed value with no rule, outside the boolean subset");
        }

        var context = new EvaluationContext(targetingKey, attributes);
        var expectation = flag.Expect(flagType, callerDefault, expectedValue, expectedReason, expectedError);
        return (new RunnableCase(c, suite, flagKey, expectation.CallerDefault, context, expectation, flag.UnstorableReason), null);
    }

    private static object? TypedValue(string type, string value) => type switch
    {
        "String" => value,
        "Integer" => long.Parse(value, CultureInfo.InvariantCulture),
        "Float" => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture),
        "Boolean" => bool.Parse(value),
        "Object" => ToClr(JsonDocument.Parse(value).RootElement),
        _ => throw new FormatException($"Unknown context value type '{type}'."),
    };

    // JSON object/array/primitive to the plain CLR shapes an EvaluationContext attribute carries. Duplicate keys: last wins.
    private static object? ToClr(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => ToDictionary(e),
        JsonValueKind.Array => e.EnumerateArray().Select(ToClr).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static Dictionary<string, object?> ToDictionary(JsonElement e)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in e.EnumerateObject())
        {
            result[property.Name] = ToClr(property.Value);
        }

        return result;
    }

    [GeneratedRegex(@"^(an evaluator|a stable flagd provider|an option "".*"" of type "".*"" with value "".*""|the flag was evaluated with details)$")]
    private static partial Regex IgnoredStep();

    [GeneratedRegex(@"^an? (\w+)-flag with key ""(.*)"" and a (?:fallback|default) value ""(.*)""$")]
    private static partial Regex FlagStep();

    [GeneratedRegex(@"^a context containing a targeting key with value ""(.*)""$")]
    private static partial Regex TargetingKeyStep();

    [GeneratedRegex(@"^a context containing a key ""(.*?)"", with type ""(\w+)"" and with value ""(.*)""$")]
    private static partial Regex AttributeStep();

    [GeneratedRegex(@"^a context containing a nested property with outer key ""(.*?)"" and inner key ""(.*?)"", with value ""(.*)""$")]
    private static partial Regex NestedAttributeStep();

    [GeneratedRegex(@"^the resolved details value should be ""(.*)""$")]
    private static partial Regex ValueStep();

    [GeneratedRegex(@"^the reason should be ""(.*)""$")]
    private static partial Regex ReasonStep();

    [GeneratedRegex(@"^the error-code should be ""(.*)""$")]
    private static partial Regex ErrorStep();
}

/// <summary>
/// A fixture flag as the testbed defines it, plus the boolean projection Flagon stores. The projection keeps the
/// state, variant names, <c>defaultVariant</c> and (with <c>$ref</c>s resolved) the targeting rule, so variant
/// selection is untouched; it only maps values to booleans so <see cref="IFlagon"/> can serve them. A boolean flag
/// keeps its values; any other flag maps every variant to <c>true</c> and is evaluated with a caller default of
/// <c>false</c>, so <c>(variant, true)</c> means "rule picked a variant" and <c>(null, false)</c> "caller default served".
/// </summary>
internal sealed class FixtureFlag
{
    private FixtureFlag(string key, JsonObject original, FlagDocument? projected, string? unstorableReason)
    {
        Key = key;
        Original = original;
        Projected = projected;
        UnstorableReason = unstorableReason;
    }

    public string Key { get; }

    /// <summary>The flag as the testbed defines it, <c>$ref</c>s resolved.</summary>
    public JsonObject Original { get; }

    /// <summary>The boolean projection, or null if Flagon cannot store this flag (see <see cref="UnstorableReason"/>).</summary>
    public FlagDocument? Projected { get; }

    public string? UnstorableReason { get; }

    public bool IsBoolean => Variants.All(v => v.Value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False);

    public bool HasTargeting => Original["targeting"] is not null;

    private JsonObject Variants => Original["variants"]!.AsObject();

    public static FixtureFlag Create(string key, JsonObject flag, JsonObject? evaluators)
    {
        var original = flag.DeepClone().AsObject();
        if (original["targeting"] is { } targeting)
        {
            original["targeting"] = ResolveRefs(targeting, evaluators);
        }

        var projection = original.DeepClone().AsObject();
        var isBoolean = original["variants"]!.AsObject().All(v => v.Value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False);
        if (!isBoolean)
        {
            foreach (var name in projection["variants"]!.AsObject().Select(v => v.Key).ToList())
            {
                projection["variants"]![name] = true;
            }
        }

        try
        {
            return new FixtureFlag(key, original, FlagdJson.Deserialize(projection.ToJsonString()), null);
        }
        catch (FlagValidationException ex)
        {
            return new FixtureFlag(key, original, null, ex.Message);
        }
    }

    // flagd's loader substitutes {"$ref": "<name>"} with the flag set's $evaluators entry before compiling. Flagon stores
    // flags individually (no flag-set $evaluators), so the fixture loader does the same substitution on import.
    // An unknown name is left in place: the rule then uses an unknown operator, which flagd reports as PARSE_ERROR.
    private static JsonNode? ResolveRefs(JsonNode? node, JsonObject? evaluators)
    {
        switch (node)
        {
            case JsonObject obj when obj.Count == 1 && obj["$ref"] is JsonValue r && r.TryGetValue<string>(out var name)
                                     && (evaluators?[name]) is { } evaluator:
                return ResolveRefs(evaluator.DeepClone(), evaluators);
            case JsonObject obj:
                foreach (var prop in obj.Select(p => p.Key).ToList())
                {
                    obj[prop] = ResolveRefs(obj[prop]?.DeepClone(), evaluators);
                }

                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = ResolveRefs(array[i]?.DeepClone(), evaluators);
                }

                return array;
            default:
                return node;
        }
    }

    /// <summary>Turns a case's Gherkin expectations into what <see cref="IFlagon"/> must return for the projection.</summary>
    public CaseExpectation Expect(string flagType, string callerDefault, string? value, string? reason, string? errorCode)
    {
        var projectedDefault = IsBoolean && bool.TryParse(callerDefault, out var b) && b;
        List<(string? Variant, bool Value)>? outcomes = null;

        if (!string.IsNullOrEmpty(errorCode))
        {
            // On error flagd returns the caller's default and no variant.
            outcomes = [(null, projectedDefault)];
        }
        else if (value is not null)
        {
            outcomes = Variants
                .Where(v => ValueEquals(flagType, v.Value, value))
                .Select(v => ((string?)v.Key, ProjectedValue(v.Key)))
                .ToList();
            if (value == callerDefault)
            {
                // The caller default can also be the answer (disabled, error, no default variant). When it equals a
                // variant's value too, the Gherkin cannot tell the two apart, so either is accepted.
                outcomes.Add((null, projectedDefault));
            }

            if (outcomes.Count == 0)
            {
                throw new InvalidOperationException($"Flag '{Key}': expected value '{value}' is neither a variant value nor the caller default.");
            }
        }

        return new CaseExpectation(projectedDefault, outcomes, reason, errorCode);
    }

    private bool ProjectedValue(string variant) => !IsBoolean || Variants[variant]!.GetValue<bool>();

    private static bool ValueEquals(string flagType, JsonNode? variantValue, string expected)
    {
        if (variantValue is null)
        {
            return false;
        }

        var kind = variantValue.GetValueKind();
        return flagType switch
        {
            "String" => kind == JsonValueKind.String && variantValue.GetValue<string>() == expected,
            "Boolean" => kind is JsonValueKind.True or JsonValueKind.False && bool.TryParse(expected, out var b) && variantValue.GetValue<bool>() == b,
            "Integer" or "Float" => kind == JsonValueKind.Number
                                    && decimal.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                                    && variantValue.GetValue<decimal>() == d,
            "Object" => JsonNode.DeepEquals(variantValue, JsonNode.Parse(expected.Replace("\\\"", "\""))),
            _ => false,
        };
    }
}

/// <summary>What a case requires of the evaluation result; null members are not asserted.</summary>
/// <param name="CallerDefault">The (projected) default passed to <see cref="IFlagon.GetBooleanDetails"/>.</param>
/// <param name="Outcomes">Acceptable (variant, value) results.</param>
/// <param name="Reason">Expected OpenFeature reason wire name.</param>
/// <param name="ErrorCode">Expected OpenFeature error code wire name; empty means no error.</param>
internal sealed record CaseExpectation(bool CallerDefault, IReadOnlyList<(string? Variant, bool Value)>? Outcomes, string? Reason, string? ErrorCode);

/// <summary>A boolean-subset case ready to run.</summary>
/// <param name="UnstorableReason">Why Flagon cannot store the case's flag (it is then missing from the store), or null.</param>
internal sealed record RunnableCase(
    GherkinCase Source, FlagdSuite Suite, string FlagKey, bool CallerDefault, EvaluationContext Context, CaseExpectation Expected, string? UnstorableReason)
{
    public string Id => Source.Id;

    /// <summary>Compares a result with the expectation; returns the mismatches (empty when the case passes).</summary>
    public IReadOnlyList<string> Check(FlagEvaluationDetails<bool> details)
    {
        var mismatches = new List<string>();
        if (UnstorableReason is not null)
        {
            mismatches.Add($"the fixture flag cannot be stored in Flagon ({UnstorableReason})");
        }

        if (Expected.Outcomes is { } outcomes && !outcomes.Contains((details.Variant, details.Value)))
        {
            mismatches.Add($"expected (variant, value) in [{string.Join(", ", outcomes.Select(o => $"({o.Variant ?? "null"}, {o.Value})"))}], got ({details.Variant ?? "null"}, {details.Value})");
        }

        if (Expected.Reason is { } reason && details.Reason.ToWireName() != reason)
        {
            mismatches.Add($"expected reason {reason}, got {details.Reason.ToWireName()}");
        }

        if (Expected.ErrorCode is { } code)
        {
            var actual = details.ErrorCode == Flagon.ErrorCode.None ? string.Empty : details.ErrorCode.ToWireName();
            if (actual != code)
            {
                mismatches.Add($"expected error code '{code}', got '{actual}'");
            }
        }

        return mismatches;
    }

    public override string ToString() => FlagdCase.Describe(Source);
}

/// <summary>A theory row: serialises only the case id, so every case is discovered (and reported) individually.</summary>
public sealed class FlagdCase : IXunitSerializable
{
    [Obsolete("For xunit deserialisation only.")]
    public FlagdCase()
    {
        Id = string.Empty;
    }

    private FlagdCase(string id) => Id = id;

    public string Id { get; private set; }

    internal static FlagdCase From(RunnableCase c) => new(c.Id);

    public void Deserialize(IXunitSerializationInfo info) => Id = info.GetValue<string>(nameof(Id));

    public void Serialize(IXunitSerializationInfo info) => info.AddValue(nameof(Id), Id);

    public override string ToString() => FlagdTestbed.Runnable.FirstOrDefault(c => c.Id == Id) is { } c ? c.ToString() : Id;

    internal static string Describe(GherkinCase c)
    {
        var row = string.Join(" | ", c.Row.Select(v => v.Length > 24 ? v[..21] + "..." : v));
        return row.Length == 0 ? $"{c.Id} {c.Scenario}" : $"{c.Id} {c.Scenario} [{row}]";
    }
}
