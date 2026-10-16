using System.Text.Json.Nodes;

namespace Flagon.Tests;

/// <summary>Flags are persisted as flagd flag-definition documents so other OpenFeature tooling can read them.</summary>
public class FlagdJsonTests
{
    private static readonly DateTimeOffset Created = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

    [Fact]
    public void A_boolean_flag_is_written_in_the_flagd_flag_definition_shape()
    {
        var document = FlagDocument.Boolean(true, new FlagMetadata(
            Description: "New checkout flow",
            Tags: ["checkout", "web"],
            Source: FlagSource.Manual,
            CreatedAt: Created,
            CreatedBy: "alice"));

        var json = JsonNode.Parse(FlagdJson.Serialize(document))!;

        var expected = JsonNode.Parse("""
            {
              "state": "ENABLED",
              "variants": { "on": true, "off": false },
              "defaultVariant": "on",
              "metadata": {
                "description": "New checkout flow",
                "tags": "checkout,web",
                "source": "manual",
                "createdAt": "2026-03-04T05:06:07+00:00",
                "createdBy": "alice"
              }
            }
            """);
        Assert.True(JsonNode.DeepEquals(expected, json), json.ToJsonString());
    }

    [Fact]
    public void A_flagd_document_written_by_another_tool_can_be_read()
    {
        var document = FlagdJson.Deserialize("""
            {
              "state": "DISABLED",
              "variants": { "on": true, "off": false },
              "defaultVariant": "off",
              "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "on", null ] },
              "metadata": { "source": "auto", "tags": "a,b", "updatedAt": "2026-03-04T05:06:07Z" }
            }
            """);

        Assert.Equal(FlagState.Disabled, document.State);
        Assert.Equal("off", document.DefaultVariant);
        Assert.False(document.Variants["off"].GetBoolean());
        Assert.NotNull(document.Targeting);
        Assert.Equal(FlagSource.Auto, document.Metadata.Source);
        Assert.Equal(["a", "b"], document.Metadata.Tags);
        Assert.Equal(Created, document.Metadata.UpdatedAt);
        Assert.Null(document.Metadata.Description);
    }

    [Fact]
    public void A_document_survives_a_round_trip()
    {
        var original = FlagDocument.Boolean(false, new FlagMetadata("d", ["t"], FlagSource.Code, Created, "a", Created, "b"));

        var roundTripped = FlagdJson.Deserialize(FlagdJson.Serialize(original));

        Assert.Equal(FlagdJson.Serialize(original), FlagdJson.Serialize(roundTripped));
        Assert.Equal(original.Metadata with { Tags = [] }, roundTripped.Metadata with { Tags = [] });
        Assert.Equal(original.Metadata.Tags, roundTripped.Metadata.Tags);
    }
}
