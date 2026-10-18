using System.Text.Json;
using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Flagon.Tests;

/// <summary>Typed flags declared in code with <c>Define</c>, through the options and host seams.</summary>
public class TypedDefineTests
{
    [Fact]
    public async Task A_string_flag_defined_in_code_is_created_with_a_single_default_variant_and_source_code()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
            f.Define("theme", "light", "Site theme", ["ui"]));

        Assert.Equal(new FlagEvaluationDetails<string>("theme", "light", "default", EvaluationReason.Static),
            host.Flagon.GetStringDetails("theme", "fallback"));

        var flag = await host.Admin.GetAsync("theme");
        Assert.Equal(FlagType.String, flag!.Document.Type);
        Assert.Equal(["default"], flag.Document.Variants.Keys);
        Assert.Equal("default", flag.Document.DefaultVariant);
        Assert.Equal(FlagState.Enabled, flag.Document.State);
        Assert.Equal(FlagSource.Code, flag.Document.Metadata.Source);
        Assert.Equal("Site theme", flag.Document.Metadata.Description);
        Assert.Equal(["ui"], flag.Document.Metadata.Tags);
    }

    [Fact]
    public async Task Literal_defaults_pick_the_matching_type_int_and_double_are_number_flags_bool_stays_boolean()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
            .Define("page-size", 20)
            .Define("ratio", 0.5)
            .Define("beta", true)
            .Define("plain"));

        Assert.Equal(new FlagEvaluationDetails<int>("page-size", 20, "default", EvaluationReason.Static),
            host.Flagon.GetIntDetails("page-size", 1));
        Assert.Equal(new FlagEvaluationDetails<double>("ratio", 0.5, "default", EvaluationReason.Static),
            host.Flagon.GetDoubleDetails("ratio", 1));
        Assert.True(host.Flagon.IsEnabled("beta"));

        Assert.Equal(FlagType.Number, (await host.Admin.GetAsync("page-size"))!.Document.Type);
        Assert.Equal(FlagType.Number, (await host.Admin.GetAsync("ratio"))!.Document.Type);
        var beta = (await host.Admin.GetAsync("beta"))!.Document;
        Assert.Equal(FlagType.Boolean, beta.Type);
        Assert.Equal(FlagDocument.OnVariant, beta.DefaultVariant);
        Assert.Equal(FlagDocument.OffVariant, (await host.Admin.GetAsync("plain"))!.Document.DefaultVariant);
    }

    private sealed record Layout(string Header, int Columns);

    [Fact]
    public async Task An_object_flag_defined_from_json_text_serves_that_object()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f =>
            f.DefineObject("layout", """{ "Header": "big", "Columns": 3 }""", "Home layout"));

        Assert.Equal(new Layout("big", 3), host.Flagon.GetObject("layout", new Layout("none", 0)));
        var flag = await host.Admin.GetAsync("layout");
        Assert.Equal(FlagType.Object, flag!.Document.Type);
        Assert.Equal("default", flag.Document.DefaultVariant);
        Assert.Equal("Home layout", flag.Document.Metadata.Description);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void An_object_definition_whose_json_is_not_an_object_is_rejected(string json)
    {
        Assert.Throws<FlagValidationException>(() => new FlagonOptions().DefineObject("layout", json));
    }

    [Fact]
    public async Task A_flag_defined_with_named_variants_serves_its_default_variant_and_keeps_every_variant()
    {
        await using var host = await FlagonTestHost.StartAsync(o => o.Flagon = f => f
            .Define("size", new Dictionary<string, string> { ["small"] = "S", ["medium"] = "M", ["large"] = "L" }, "medium", "T-shirt size")
            .Define("limit", new Dictionary<string, int> { ["low"] = 10, ["high"] = 100 }, defaultVariant: null)
            .Define("mode", new Dictionary<string, bool> { ["yes"] = true, ["no"] = false }, "no")
            .Define("layouts", new Dictionary<string, JsonElement>
            {
                ["one"] = JsonSerializer.SerializeToElement(new { columns = 1 }),
                ["two"] = JsonSerializer.SerializeToElement(new { columns = 2 }),
            }, "two"));

        Assert.Equal(new FlagEvaluationDetails<string>("size", "M", "medium", EvaluationReason.Static),
            host.Flagon.GetStringDetails("size", "fallback"));
        var size = (await host.Admin.GetAsync("size"))!.Document;
        Assert.Equal(FlagType.String, size.Type);
        Assert.Equal(["large", "medium", "small"], size.Variants.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("T-shirt size", size.Metadata.Description);

        Assert.Equal(7, host.Flagon.GetInt("limit", 7));
        var limit = (await host.Admin.GetAsync("limit"))!.Document;
        Assert.Equal(FlagType.Number, limit.Type);
        Assert.Null(limit.DefaultVariant);

        Assert.Equal(new FlagEvaluationDetails<bool>("mode", false, "no", EvaluationReason.Static),
            host.Flagon.GetBooleanDetails("mode", true));
        Assert.Equal(2, host.Flagon.GetObject("layouts", default(JsonElement)).GetProperty("columns").GetInt32());
    }

    [Fact]
    public void Invalid_named_variant_definitions_are_rejected_when_Define_is_called()
    {
        var options = new FlagonOptions();

        Assert.Throws<FlagValidationException>(() => options.Define("empty", new Dictionary<string, string>(), null));
        Assert.Throws<FlagValidationException>(() => options.Define("unknown-default", new Dictionary<string, string> { ["a"] = "A" }, "b"));
        Assert.Throws<FlagValidationException>(() => options.Define("blank-name", new Dictionary<string, string> { [""] = "A" }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("long-name", new Dictionary<string, string> { [new string('n', 129)] = "A" }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("nan", new Dictionary<string, double> { ["x"] = double.NaN }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("array", new Dictionary<string, JsonElement> { ["x"] = JsonSerializer.SerializeToElement(new[] { 1 }) }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("mixed", new Dictionary<string, JsonElement>
        {
            ["a"] = JsonSerializer.SerializeToElement("text"),
            ["b"] = JsonSerializer.SerializeToElement(1),
        }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("null-string", new Dictionary<string, string> { ["x"] = null! }, null));
        Assert.Throws<ArgumentException>(() => options.Define("unsupported", new Dictionary<string, long> { ["x"] = 1 }, null));
        Assert.Throws<FlagValidationException>(() => options.Define("inf", double.PositiveInfinity));

        Assert.Empty(options.Definitions);
    }
}
