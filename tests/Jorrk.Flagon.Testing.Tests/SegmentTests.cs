using System.Text.Json;

namespace Jorrk.Flagon.Testing.Tests;

/// <summary>Segments in <see cref="TestFlagon"/>: defined in code, managed through <see cref="TestFlagon.Admin"/> and referenced by test flags.</summary>
public class SegmentTests
{
    private const string Staff = """{"ends_with":[{"var":"email"},"@example.com"]}""";
    private const string UsesStaff = """{"if":[{"$ref":"staff"},"on",null]}""";

    private static EvaluationContext Email(string email) =>
        new(attributes: new Dictionary<string, object?> { ["email"] = email });

    [Fact]
    public void A_flag_defined_in_code_can_reference_a_segment_defined_in_code()
    {
        using var flags = TestFlagon.Create(o => o
            .DefineSegment("staff", Staff)
            .Define("staff-tools", targeting: UsesStaff));

        Assert.True(flags.IsEnabled("staff-tools", context: Email("ann@example.com")));
        Assert.False(flags.IsEnabled("staff-tools", context: Email("bob@elsewhere.org")));
    }

    [Fact]
    public async Task A_test_flag_can_reference_a_segment_created_through_admin_and_follows_its_edits()
    {
        using var flags = TestFlagon.Create();
        var staff = await flags.Admin.CreateSegmentAsync(new CreateSegmentRequest("staff", JsonDocument.Parse(Staff).RootElement));

        flags.Flags.SetFlag("staff-tools", false, targeting: UsesStaff);
        Assert.True(flags.IsEnabled("staff-tools", context: Email("ann@example.com")));

        await flags.Admin.SetSegmentRuleAsync("staff", JsonDocument.Parse("""{"ends_with":[{"var":"email"},"@elsewhere.org"]}""").RootElement, staff.Version);

        Assert.False(flags.IsEnabled("staff-tools", context: Email("ann@example.com")));
        Assert.True(flags.IsEnabled("staff-tools", context: Email("bob@elsewhere.org")));
    }

    [Fact]
    public void A_test_flag_referencing_a_segment_that_does_not_exist_is_rejected()
    {
        using var flags = TestFlagon.Create();

        var ex = Assert.Throws<FlagTargetingException>(() => flags.Flags.SetFlag("staff-tools", false, targeting: UsesStaff));

        Assert.Equal("$ref", ex.Operator);
    }

    [Fact]
    public async Task Reset_removes_admin_segments_and_reseeds_the_ones_defined_in_code()
    {
        using var flags = TestFlagon.Create(o => o.DefineSegment("staff", Staff));
        await flags.Admin.CreateSegmentAsync(new CreateSegmentRequest("temporary", JsonDocument.Parse(Staff).RootElement));
        var staff = await flags.Admin.GetSegmentAsync("staff");
        await flags.Admin.ArchiveSegmentAsync("staff", staff!.Version);

        flags.Flags.Reset();

        Assert.Equal(["staff"], (await flags.Admin.ListSegmentsAsync()).Select(s => s.Key));
        Assert.False((await flags.Admin.GetSegmentAsync("staff"))!.Archived);
    }
}
