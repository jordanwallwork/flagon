using Flagon.Storage;

namespace Flagon.Testing.Tests;

public class StandaloneTests
{
    [Fact]
    public void A_boolean_flag_set_in_a_test_is_served_to_code_that_takes_IFlagon()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("new-checkout", true);

        IFlagon flagon = flags;
        var details = flagon.GetBooleanDetails("new-checkout", defaultValue: false);
        Assert.True(details.Value);
        Assert.Equal("on", details.Variant);
        Assert.Equal(EvaluationReason.Static, details.Reason);
    }

    [Fact]
    public void A_key_that_was_never_set_returns_the_callers_default_with_flag_not_found()
    {
        using var flags = TestFlagon.Create();

        var details = flags.GetBooleanDetails("never-set", defaultValue: true);

        Assert.True(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(EvaluationReason.Error, details.Reason);
        Assert.Equal(ErrorCode.FlagNotFound, details.ErrorCode);
    }

    [Fact]
    public void Setting_a_flag_again_replaces_its_value_and_SetFlag_chains()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("new-checkout", true).SetFlag("new-checkout", false).SetFlag("dark-mode", true);

        Assert.False(flags.IsEnabled("new-checkout", defaultValue: true));
        Assert.True(flags.IsEnabled("dark-mode"));
    }

    [Fact]
    public void Every_write_is_seen_by_the_very_next_evaluation()
    {
        using var flags = TestFlagon.Create();

        for (var i = 0; i < 200; i++)
        {
            var value = i % 2 == 0;
            flags.Flags.SetFlag("toggled", value);
            Assert.Equal(value, flags.IsEnabled("toggled", defaultValue: !value));
        }
    }

    [Fact]
    public async Task Writes_from_parallel_tasks_are_each_seen_by_the_writers_next_evaluation()
    {
        using var flags = TestFlagon.Create();

        await Task.WhenAll(Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            for (var round = 0; round < 20; round++)
            {
                flags.Flags.SetFlag($"flag-{i}", round % 2 == 0);
                Assert.Equal(round % 2 == 0, flags.IsEnabled($"flag-{i}", defaultValue: round % 2 != 0));
                // Every task also writes one shared key, so concurrent writes to the same flag are exercised.
                flags.Flags.SetFlag("shared", true);
                Assert.True(flags.IsEnabled("shared"));
            }
        })));
    }

    [Fact]
    public void An_invalid_key_throws_at_SetFlag()
    {
        using var flags = TestFlagon.Create();

        Assert.Throws<FlagValidationException>(() => flags.Flags.SetFlag("not a key", true));
    }

    [Fact]
    public async Task Test_writes_leave_no_audit_history()
    {
        using var flags = TestFlagon.Create();

        flags.Flags.SetFlag("new-checkout", true).SetFlag("new-checkout", false);

        Assert.Empty(await flags.Admin.GetAuditHistoryAsync("new-checkout"));
    }

    [Fact]
    public async Task Admin_writes_are_audited_and_seen_by_the_next_evaluation()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("new-checkout", true);

        var flag = await flags.Admin.GetAsync("new-checkout");
        await flags.Admin.SetEnabledAsync("new-checkout", false, flag!.Version);

        Assert.False(flags.IsEnabled("new-checkout", defaultValue: true));
        Assert.Single(await flags.Admin.GetAuditHistoryAsync("new-checkout"));
    }

    [Fact]
    public async Task A_flag_archived_by_an_admin_is_replaced_by_SetFlag()
    {
        using var flags = TestFlagon.Create();
        flags.Flags.SetFlag("new-checkout", true);
        var flag = await flags.Admin.GetAsync("new-checkout");
        await flags.Admin.ArchiveAsync("new-checkout", flag!.Version);
        Assert.Equal(EvaluationReason.Archived, flags.GetBooleanDetails("new-checkout").Reason);

        flags.Flags.SetFlag("new-checkout", true);

        Assert.Equal(EvaluationReason.Static, flags.GetBooleanDetails("new-checkout").Reason);
        Assert.True(flags.IsEnabled("new-checkout"));
    }

    [Fact]
    public void A_storage_choice_in_the_app_configuration_is_never_built()
    {
        using var flags = TestFlagon.Create(f => f.UseStorage(_ => throw new InvalidOperationException("The app's store was built.")));

        flags.Flags.SetFlag("new-checkout", true);

        Assert.True(flags.IsEnabled("new-checkout"));
    }

    [Fact]
    public void An_app_configured_for_SQL_Server_runs_without_a_database()
    {
        using var flags = TestFlagon.Create(f => f.UseSqlServerStorage("Server=nowhere;Connect Timeout=1"));

        flags.Flags.SetFlag("new-checkout", true);

        Assert.True(flags.IsEnabled("new-checkout"));
    }

    [Fact]
    public async Task Disposing_asynchronously_releases_the_flagon()
    {
        var flags = TestFlagon.Create();
        flags.Flags.SetFlag("new-checkout", true);

        await flags.DisposeAsync();
        await flags.DisposeAsync();
        flags.Dispose();
    }
}
