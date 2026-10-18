using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;

namespace Flagon.Tests;

/// <summary>Typed getters (string, int, double, object) on <see cref="IFlagon"/>, through the host seam.</summary>
public class TypedEvaluationTests
{
    private static async Task<FlagonTestHost> HostAsync(params (string Key, string Json)[] flags)
    {
        var store = new InMemoryFlagStore();
        foreach (var (key, json) in flags)
        {
            await store.TryCreateAsync(key, FlagdJson.Deserialize(json));
        }

        return await FlagonTestHost.StartAsync(o => o.Store = store);
    }

    private static string Static(string variantsJson, string defaultVariant) =>
        $$"""{ "state": "ENABLED", "variants": {{variantsJson}}, "defaultVariant": "{{defaultVariant}}" }""";

    /// <summary>One <see cref="FlagEvaluationDetails{T}"/> of a bool or int on 64-bit: the static flag's 56 B in ADR-0001.</summary>
    private const long OneDetailsRecord = 56;

    [Theory]
    [InlineData("IsEnabled", "checkout")]
    [InlineData("GetBooleanDetails", "checkout")]
    [InlineData("GetIntDetails", "page-size")]
    [InlineData("GetInt", "page-size")]
    [InlineData("IsEnabled", "gold-checkout")]
    [InlineData("GetBooleanDetails", "gold-checkout")]
    [InlineData("GetIntDetails", "gold-page-size")]
    public async Task An_evaluation_allocates_at_most_its_details_record_with_or_without_a_rule(string getter, string key)
    {
        const string Gold = """{ "if": [{ "==": [{ "var": "tier" }, "gold"] }, "large", "small"] }""";
        await using var host = await HostAsync(
            ("checkout", Static("""{ "on": true, "off": false }""", "on")),
            ("gold-checkout", """{ "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "off", "targeting": { "if": [{ "==": [{ "var": "tier" }, "gold"] }, "on", "off"] } }"""),
            ("page-size", Static("""{ "small": 10, "large": 100 }""", "large")),
            ("gold-page-size", $$"""{ "state": "ENABLED", "variants": { "small": 10, "large": 100 }, "defaultVariant": "small", "targeting": {{Gold}} }"""));
        var context = new EvaluationContext("user-1", new Dictionary<string, object?> { ["tier"] = "gold" });
        Action evaluate = getter switch
        {
            "IsEnabled" => () => host.Flagon.IsEnabled(key, false, context),
            "GetBooleanDetails" => () => host.Flagon.GetBooleanDetails(key, false, context),
            "GetInt" => () => host.Flagon.GetInt(key, 1, context),
            _ => () => host.Flagon.GetIntDetails(key, 1, context),
        };

        evaluate();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            evaluate();
        }

        var perCall = (GC.GetAllocatedBytesForCurrentThread() - before) / 100.0;
        Assert.True(perCall <= OneDetailsRecord, $"{getter}('{key}') allocated {perCall} B per call");
    }

    [Fact]
    public async Task A_string_flag_serves_its_default_variant_statically()
    {
        await using var host = await HostAsync(("theme", Static("""{ "light": "light", "dark": "dark" }""", "dark")));

        Assert.Equal("dark", host.Flagon.GetString("theme", "caller-default"));
        Assert.Equal(new FlagEvaluationDetails<string>("theme", "dark", "dark", EvaluationReason.Static),
            host.Flagon.GetStringDetails("theme", "caller-default"));
    }

    [Fact]
    public async Task A_number_flag_serves_its_default_variant_as_an_int()
    {
        await using var host = await HostAsync(("page-size", Static("""{ "small": 10, "large": 100 }""", "large")));

        Assert.Equal(100, host.Flagon.GetInt("page-size", 1));
        Assert.Equal(new FlagEvaluationDetails<int>("page-size", 100, "large", EvaluationReason.Static),
            host.Flagon.GetIntDetails("page-size", 1));
    }

    [Fact]
    public async Task A_number_flag_serves_its_default_variant_as_a_double()
    {
        await using var host = await HostAsync(("ratio", Static("""{ "half": 0.5, "whole": 1 }""", "half")));

        Assert.Equal(0.5, host.Flagon.GetDouble("ratio", 0.1));
        Assert.Equal(new FlagEvaluationDetails<double>("ratio", 0.5, "half", EvaluationReason.Static),
            host.Flagon.GetDoubleDetails("ratio", 0.1));
    }

    [Fact]
    public async Task An_object_flag_serves_its_default_variant_as_json()
    {
        await using var host = await HostAsync(("layout", Static("""{ "grid": { "columns": 3, "title": "Pics" }, "list": {} }""", "grid")));

        var details = host.Flagon.GetObjectDetails("layout", JsonDocument.Parse("{}").RootElement);

        Assert.Equal("grid", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
        Assert.Equal(3, details.Value.GetProperty("columns").GetInt32());
        Assert.Equal("Pics", host.Flagon.GetObject("layout", default(JsonElement)).GetProperty("title").GetString());
    }

    private sealed record Layout(int Columns, string Title);

    [Fact]
    public async Task An_object_flag_deserialises_into_the_callers_type()
    {
        await using var host = await HostAsync(("layout", Static("""{ "grid": { "columns": 3, "title": "Pics" } }""", "grid")));

        var callerDefault = new Layout(1, "caller-default");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal(new Layout(3, "Pics"), host.Flagon.GetObject("layout", callerDefault, options: options));
        Assert.Equal(new FlagEvaluationDetails<Layout>("layout", new Layout(3, "Pics"), "grid", EvaluationReason.Static),
            host.Flagon.GetObjectDetails("layout", callerDefault, options: options));
    }

    /// <summary>The getter a case requests a flag through.</summary>
    public enum Getter { Boolean, String, Int, Double, Object }

    /// <summary>A result with its value as JSON text, so results of every getter compare alike.</summary>
    private sealed record Result(string Value, string? Variant, EvaluationReason Reason, ErrorCode ErrorCode = ErrorCode.None);

    private static readonly JsonSerializerOptions Compact = new();

    // Each getter's caller default, as JSON text, and the flag type it reads.
    private static string CallerDefault(Getter getter) => getter switch
    {
        Getter.Boolean => "true",
        Getter.String => "\"caller\"",
        Getter.Int => "42",
        Getter.Double => "4.25",
        _ => """{"caller":true}""",
    };

    private static FlagType FlagTypeOf(Getter getter) => getter switch
    {
        Getter.Boolean => FlagType.Boolean,
        Getter.String => FlagType.String,
        Getter.Int or Getter.Double => FlagType.Number,
        _ => FlagType.Object,
    };

    // Two variants of each flag type, "a" and "b", as JSON text.
    private static (string A, string B) Variants(FlagType type) => type switch
    {
        FlagType.Boolean => ("false", "true"),
        FlagType.String => ("\"x\"", "\"y\""),
        FlagType.Number => ("1", "2"),
        _ => ("""{"n":1}""", """{"n":2}"""),
    };

    private static string Flag(FlagType type, string state = "ENABLED", string defaultVariant = "\"a\"", string? targeting = null)
    {
        var (a, b) = Variants(type);
        var rule = targeting is null ? string.Empty : $$""", "targeting": {{targeting}}""";
        return $$"""{ "state": "{{state}}", "variants": { "a": {{a}}, "b": {{b}} }, "defaultVariant": {{defaultVariant}}{{rule}} }""";
    }

    // Served variants' values as each getter returns them (an int for 2, a double 2.0 for 2), as JSON text.
    private static string Served(Getter getter, string variantJson) => getter switch
    {
        Getter.Double => JsonSerializer.Serialize(double.Parse(variantJson, System.Globalization.CultureInfo.InvariantCulture)),
        _ => variantJson,
    };

    private static Result Evaluate(FlagonTestHost host, Getter getter, string key, EvaluationContext? context = null)
    {
        var callerDefault = JsonDocument.Parse(CallerDefault(getter)).RootElement;
        var flagon = host.Flagon;
        return getter switch
        {
            Getter.Boolean => From(flagon.GetBooleanDetails(key, callerDefault.GetBoolean(), context)),
            Getter.String => From(flagon.GetStringDetails(key, callerDefault.GetString()!, context)),
            Getter.Int => From(flagon.GetIntDetails(key, callerDefault.GetInt32(), context)),
            Getter.Double => From(flagon.GetDoubleDetails(key, callerDefault.GetDouble(), context)),
            _ => From(flagon.GetObjectDetails(key, callerDefault, context)),
        };
    }

    private static async Task<Result> EvaluateAsync(FlagonTestHost host, Getter getter, string key)
    {
        var callerDefault = JsonDocument.Parse(CallerDefault(getter)).RootElement;
        var flagon = host.Flagon;
        var (value, details) = getter switch
        {
            Getter.Boolean => (Json(await flagon.IsEnabledAsync(key, callerDefault.GetBoolean())), From(await flagon.GetBooleanDetailsAsync(key, callerDefault.GetBoolean()))),
            Getter.String => (Json(await flagon.GetStringAsync(key, callerDefault.GetString()!)), From(await flagon.GetStringDetailsAsync(key, callerDefault.GetString()!))),
            Getter.Int => (Json(await flagon.GetIntAsync(key, callerDefault.GetInt32())), From(await flagon.GetIntDetailsAsync(key, callerDefault.GetInt32()))),
            Getter.Double => (Json(await flagon.GetDoubleAsync(key, callerDefault.GetDouble())), From(await flagon.GetDoubleDetailsAsync(key, callerDefault.GetDouble()))),
            _ => (Json(await flagon.GetObjectAsync(key, callerDefault)), From(await flagon.GetObjectDetailsAsync(key, callerDefault))),
        };
        Assert.Equal(details.Value, value);
        return details;
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Compact);

    private static Result From<T>(FlagEvaluationDetails<T> d) => new(Json(d.Value), d.Variant, d.Reason, d.ErrorCode);

    public static TheoryData<Getter> Getters => new(Enum.GetValues<Getter>());

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_serves_the_default_variant_of_a_static_flag(Getter getter)
    {
        var type = FlagTypeOf(getter);
        await using var host = await HostAsync(("f", Flag(type)));

        Assert.Equal(new Result(Served(getter, Variants(type).A), "a", EvaluationReason.Static), Evaluate(host, getter, "f"));
        Assert.Equal(new Result(Served(getter, Variants(type).A), "a", EvaluationReason.Static), await EvaluateAsync(host, getter, "f"));
    }

    private const string AliceGetsB = """{ "if": [{ "==": [{ "var": "targetingKey" }, "alice"] }, "b", null] }""";

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_serves_the_variant_a_targeting_rule_matches(Getter getter)
    {
        var type = FlagTypeOf(getter);
        await using var host = await HostAsync(("f", Flag(type, targeting: AliceGetsB)));

        Assert.Equal(new Result(Served(getter, Variants(type).B), "b", EvaluationReason.TargetingMatch),
            Evaluate(host, getter, "f", new EvaluationContext("alice")));
    }

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_serves_the_default_variant_when_the_rule_matches_nothing(Getter getter)
    {
        var type = FlagTypeOf(getter);
        await using var host = await HostAsync(("f", Flag(type, targeting: AliceGetsB)));

        Assert.Equal(new Result(Served(getter, Variants(type).A), "a", EvaluationReason.Default),
            Evaluate(host, getter, "f", new EvaluationContext("bob")));
    }

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_returns_the_callers_default_for_a_disabled_flag(Getter getter)
    {
        await using var host = await HostAsync(("f", Flag(FlagTypeOf(getter), state: "DISABLED")));

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Disabled), Evaluate(host, getter, "f"));
    }

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_returns_the_callers_default_for_an_archived_flag(Getter getter)
    {
        await using var host = await HostAsync(("f", Flag(FlagTypeOf(getter))));
        var record = await host.Admin.GetAsync("f");
        await host.Admin.ArchiveAsync("f", record!.Version);

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Archived), Evaluate(host, getter, "f"));
    }

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_returns_the_callers_default_for_a_missing_flag(Getter getter)
    {
        await using var host = await HostAsync();

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Error, ErrorCode.FlagNotFound),
            Evaluate(host, getter, "missing"));
    }

    [Theory]
    [MemberData(nameof(Getters))]
    public async Task Each_getter_returns_the_callers_default_for_a_flag_with_no_default_variant(Getter getter)
    {
        await using var host = await HostAsync(("f", Flag(FlagTypeOf(getter), defaultVariant: "null")));

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Default), Evaluate(host, getter, "f"));
    }

    /// <summary>Every getter against every flag type it does not read.</summary>
    public static TheoryData<Getter, FlagType> Mismatches
    {
        get
        {
            var data = new TheoryData<Getter, FlagType>();
            foreach (var getter in Enum.GetValues<Getter>())
            {
                foreach (var type in Enum.GetValues<FlagType>().Where(t => t != FlagTypeOf(getter)))
                {
                    data.Add(getter, type);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task A_served_variant_of_another_type_returns_the_callers_default_with_type_mismatch(Getter getter, FlagType type)
    {
        await using var host = await HostAsync(("f", Flag(type)));

        var result = Evaluate(host, getter, "f");

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Error, ErrorCode.TypeMismatch), result);
    }

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task The_type_check_applies_only_to_a_served_variant(Getter getter, FlagType type)
    {
        await using var host = await HostAsync(
            ("disabled", Flag(type, state: "DISABLED")),
            ("no-default", Flag(type, defaultVariant: "null")));

        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Disabled), Evaluate(host, getter, "disabled"));
        Assert.Equal(new Result(CallerDefault(getter), null, EvaluationReason.Default), Evaluate(host, getter, "no-default"));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("-7", -7)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData("-2147483648", int.MinValue)]
    [InlineData("10.0", 10)]
    [InlineData("1e2", 100)]
    [InlineData("-0.0", 0)]
    [InlineData("21474836.47e2", int.MaxValue)]
    public async Task GetInt_serves_an_integral_number_within_int32_range(string number, int expected)
    {
        await using var host = await HostAsync(("n", Static($$"""{ "v": {{number}} }""", "v")));

        Assert.Equal(new FlagEvaluationDetails<int>("n", expected, "v", EvaluationReason.Static), host.Flagon.GetIntDetails("n", 42));
    }

    [Theory]
    [InlineData("1.5")]
    [InlineData("-0.1")]
    [InlineData("2147483648")]
    [InlineData("-2147483649")]
    [InlineData("1e20")]
    [InlineData("1e-29")]
    [InlineData("1e-400")]
    [InlineData("1.00000000000000000000000000001")]
    [InlineData("2147483647.0000000000000000000001")]
    public async Task GetInt_never_truncates_a_fractional_or_out_of_range_number(string number)
    {
        await using var host = await HostAsync(("n", Static($$"""{ "v": {{number}} }""", "v")));

        var details = host.Flagon.GetIntDetails("n", 42);

        Assert.Equal((42, (string?)null, EvaluationReason.Error, ErrorCode.TypeMismatch), (details.Value, details.Variant, details.Reason, details.ErrorCode));
        Assert.Equal(double.Parse(number, System.Globalization.CultureInfo.InvariantCulture), host.Flagon.GetDouble("n", 0));
    }

    [Fact]
    public async Task GetObject_of_a_type_the_variant_does_not_deserialise_into_is_a_type_mismatch()
    {
        await using var host = await HostAsync(("layout", Static("""{ "grid": { "columns": "three", "title": "Pics" } }""", "grid")));
        var callerDefault = new Layout(1, "caller-default");
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        var details = host.Flagon.GetObjectDetails("layout", callerDefault, options: options);

        Assert.Equal(callerDefault, details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.TypeMismatch, details.ErrorCode);
        Assert.Equal(callerDefault, await host.Flagon.GetObjectAsync("layout", callerDefault, options: options));
        Assert.Equal(ErrorCode.TypeMismatch, (await host.Flagon.GetObjectDetailsAsync("layout", callerDefault, options: options)).ErrorCode);
    }

    [Fact]
    public async Task Zero_values_are_served_not_mistaken_for_a_missing_value()
    {
        await using var host = await HostAsync(
            ("string", Static("""{ "zero": "" }""", "zero")),
            ("integer", Static("""{ "zero": 0 }""", "zero")),
            ("float", Static("""{ "zero": 0.0 }""", "zero")),
            ("object", Static("""{ "zero": {} }""", "zero")),
            ("boolean", Static("""{ "zero": false }""", "zero")));

        Assert.Equal(new FlagEvaluationDetails<string>("string", "", "zero", EvaluationReason.Static), host.Flagon.GetStringDetails("string", "hi"));
        Assert.Equal(new FlagEvaluationDetails<int>("integer", 0, "zero", EvaluationReason.Static), host.Flagon.GetIntDetails("integer", 1));
        Assert.Equal(new FlagEvaluationDetails<double>("float", 0.0, "zero", EvaluationReason.Static), host.Flagon.GetDoubleDetails("float", 0.1));
        Assert.Equal(new FlagEvaluationDetails<bool>("boolean", false, "zero", EvaluationReason.Static), host.Flagon.GetBooleanDetails("boolean", true));
        var json = host.Flagon.GetObjectDetails("object", JsonDocument.Parse("""{"a":1}""").RootElement);
        Assert.Equal(("{}", "zero", EvaluationReason.Static), (json.Value.GetRawText(), json.Variant, json.Reason));
    }

    [Fact]
    public async Task Bulk_evaluation_serves_each_flags_variant_as_json_ordered_by_key()
    {
        await using var host = await HostAsync(
            ("theme", Flag(FlagType.String)),
            ("layout", Flag(FlagType.Object, targeting: AliceGetsB)),
            ("checkout", Flag(FlagType.Boolean, defaultVariant: "\"b\"")),
            ("ratio", Flag(FlagType.Number, state: "DISABLED")));

        var set = host.Flagon.GetAllDetails(new EvaluationContext("alice"));

        Assert.Equal(host.Status.Version, set.Version);
        Assert.Equal(
            [
                ("checkout", "true", "b", EvaluationReason.Static),
                ("layout", """{"n":2}""", "b", EvaluationReason.TargetingMatch),
                ("ratio", "(none)", null, EvaluationReason.Disabled),
                ("theme", "\"x\"", "a", EvaluationReason.Static),
            ],
            set.Flags.Select(d => (d.FlagKey, d.Value.ValueKind == JsonValueKind.Undefined ? "(none)" : d.Value.GetRawText(), d.Variant, d.Reason)));
    }

    [Fact]
    public async Task A_typed_getter_warns_about_a_missing_key_once()
    {
        await using var host = await HostAsync();

        host.Flagon.GetString("missing", "x");
        host.Flagon.GetInt("missing", 1);
        host.Flagon.GetDouble("missing", 1);
        host.Flagon.GetObject("missing", default(JsonElement));

        Assert.Single(host.Logs.Flagon(Microsoft.Extensions.Logging.LogLevel.Warning), w => w.Message.Contains("'missing'", StringComparison.Ordinal));
    }
}
