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
}
