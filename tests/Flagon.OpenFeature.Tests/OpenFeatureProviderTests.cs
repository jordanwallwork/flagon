using Flagon.Storage;
using Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using OpenFeature;
using OpenFeature.Constant;
using OpenFeature.Hosting;
using OpenFeature.Model;

namespace Flagon.OpenFeature.Tests;

public class OpenFeatureProviderTests
{
    private static Task<FlagonTestHost> StartAsync(IFlagStore? store = null, Action<OpenFeatureBuilder>? openFeature = null) =>
        FlagonTestHost.StartAsync(o =>
        {
            o.Store = store;
            o.Services = services => services.AddOpenFeature(of =>
            {
                of.AddFlagon();
                openFeature?.Invoke(of);
            });
        });

    private static async Task<FlagonTestHost> StartWithStoredFlagAsync(string key, string flagdJson)
    {
        var store = new InMemoryFlagStore();
        await store.TryCreateAsync(key, FlagdJson.Deserialize(flagdJson));
        return await StartAsync(store);
    }

    private static IFeatureClient Client(FlagonTestHost host) => host.Services.GetRequiredService<IFeatureClient>();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_flag_evaluated_through_the_OpenFeature_client_returns_its_stored_value(bool stored)
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: stored));

        Assert.Equal(stored, await Client(host).GetBooleanValueAsync("new-checkout", !stored));
    }

    [Fact]
    public async Task OpenFeature_details_carry_the_served_variant_and_a_static_reason()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: false));

        var details = await Client(host).GetBooleanDetailsAsync("new-checkout", true);

        Assert.False(details.Value);
        Assert.Equal("off", details.Variant);
        Assert.Equal(Reason.Static, details.Reason);
        Assert.Equal(ErrorType.None, details.ErrorType);
        Assert.Null(details.ErrorMessage);
    }

    [Fact]
    public async Task A_flag_disabled_through_flagon_resolves_to_the_callers_default_with_reason_disabled()
    {
        await using var host = await StartAsync();
        var flag = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        await host.Admin.SetStateAsync("new-checkout", FlagState.Disabled, flag.Version);

        var details = await Client(host).GetBooleanDetailsAsync("new-checkout", false);

        Assert.False(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(Reason.Disabled, details.Reason);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task A_toggle_made_through_flagon_is_seen_by_the_OpenFeature_client()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: false));

        await host.Admin.SetEnabledAsync("new-checkout", true, created.Version);

        Assert.True(await Client(host).GetBooleanValueAsync("new-checkout", false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_missing_flag_returns_the_callers_default_with_OpenFeatures_flag_not_found_error(bool callerDefault)
    {
        await using var host = await StartAsync();

        var details = await Client(host).GetBooleanDetailsAsync("does-not-exist", callerDefault);

        Assert.Equal(callerDefault, details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(Reason.Error, details.Reason);
        Assert.Equal(ErrorType.FlagNotFound, details.ErrorType);
        Assert.NotNull(details.ErrorMessage);
    }

    [Fact]
    public async Task A_disabled_flag_returns_the_callers_default_with_OpenFeatures_disabled_reason()
    {
        await using var host = await StartWithStoredFlagAsync("kill-switch", """
            { "state": "DISABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on" }
            """);

        var details = await Client(host).GetBooleanDetailsAsync("kill-switch", false);

        Assert.False(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal(Reason.Disabled, details.Reason);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task An_archived_flag_returns_the_callers_default_with_the_archived_reason()
    {
        await using var host = await StartAsync();
        var created = await host.Admin.CreateAsync(new CreateFlagRequest("old-feature", Enabled: true));
        await host.Admin.ArchiveAsync("old-feature", created.Version);

        var details = await Client(host).GetBooleanDetailsAsync("old-feature", false);

        Assert.False(details.Value);
        Assert.Null(details.Variant);
        Assert.Equal("ARCHIVED", details.Reason);
        Assert.Equal(ErrorType.None, details.ErrorType);
    }

    [Fact]
    public async Task The_context_sent_through_OpenFeature_drives_targeting()
    {
        await using var host = await StartWithStoredFlagAsync("targeted", """
            {
              "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "on",
              "targeting": { "if": [ { "==": [ { "var": "country" }, "GB" ] }, "off", null ] }
            }
            """);
        var context = global::OpenFeature.Model.EvaluationContext.Builder().SetTargetingKey("u1").Set("country", "GB").Build();

        var details = await Client(host).GetBooleanDetailsAsync("targeted", true, context);

        Assert.False(details.Value);
        Assert.Equal("off", details.Variant);
        Assert.Equal(Reason.TargetingMatch, details.Reason);
    }

    [Fact]
    public async Task A_stored_flag_whose_value_is_not_boolean_reports_OpenFeatures_type_mismatch()
    {
        await using var host = await StartWithStoredFlagAsync("colour", """
            { "state": "ENABLED", "variants": { "red": "#f00" }, "defaultVariant": "red" }
            """);

        var details = await Client(host).GetBooleanDetailsAsync("colour", true);

        Assert.True(details.Value);
        Assert.Equal(Reason.Error, details.Reason);
        Assert.Equal(ErrorType.TypeMismatch, details.ErrorType);
    }

    [Fact]
    public async Task A_flag_whose_default_variant_is_missing_reports_OpenFeatures_parse_error()
    {
        await using var host = await StartWithStoredFlagAsync("broken", """
            { "state": "ENABLED", "variants": { "on": true, "off": false }, "defaultVariant": "maybe" }
            """);

        var details = await Client(host).GetBooleanDetailsAsync("broken", true);

        Assert.True(details.Value);
        Assert.Equal(Reason.Error, details.Reason);
        Assert.Equal(ErrorType.ParseError, details.ErrorType);
    }

    [Fact]
    public async Task Non_boolean_resolution_of_a_boolean_flag_returns_the_callers_default_with_OpenFeatures_type_mismatch()
    {
        await using var host = await StartAsync();
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));
        var client = Client(host);

        var asString = await client.GetStringDetailsAsync("new-checkout", "fallback");
        var asInteger = await client.GetIntegerDetailsAsync("new-checkout", 7);
        var asDouble = await client.GetDoubleDetailsAsync("new-checkout", 1.5);
        var asObject = await client.GetObjectDetailsAsync("new-checkout", new Value("fallback"));

        Assert.Equal("fallback", asString.Value);
        Assert.Equal(7, asInteger.Value);
        Assert.Equal(1.5, asDouble.Value);
        Assert.Equal("fallback", asObject.Value.AsString);
        Assert.All(
            [asString.ErrorType, asInteger.ErrorType, asDouble.ErrorType, asObject.ErrorType],
            e => Assert.Equal(ErrorType.TypeMismatch, e));
        Assert.All(
            [asString.Reason, asInteger.Reason, asDouble.Reason, asObject.Reason],
            r => Assert.Equal(Reason.Error, r));
    }

    [Fact]
    public async Task The_registered_provider_identifies_itself_as_flagon()
    {
        await using var host = await StartAsync();

        Assert.Equal("Flagon", host.Services.GetRequiredService<Api>().GetProviderMetadata()?.Name);
    }

    [Fact]
    public async Task Existing_OpenFeature_hooks_run_around_a_flagon_evaluation()
    {
        var hook = new RecordingHook();
        await using var host = await StartAsync(openFeature: of => of.AddHook(hook));
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        await Client(host).GetBooleanValueAsync("new-checkout", false);

        Assert.Equal(["before:new-checkout", "after:new-checkout:True:on:STATIC", "finally:new-checkout"], hook.Calls);
    }

    [Fact]
    public async Task OpenFeature_error_hooks_see_a_missing_flag()
    {
        var hook = new RecordingHook();
        await using var host = await StartAsync(openFeature: of => of.AddHook(hook));

        await Client(host).GetBooleanValueAsync("does-not-exist", false);

        Assert.Contains("error:does-not-exist", hook.Calls);
    }

    [Fact]
    public async Task Flagon_can_be_registered_as_the_provider_for_a_named_domain()
    {
        await using var host = await FlagonTestHost.StartAsync(o =>
            o.Services = services => services.AddOpenFeature(of => of.AddFlagon("flags")));
        await host.Admin.CreateAsync(new CreateFlagRequest("new-checkout", Enabled: true));

        var client = host.Services.GetRequiredKeyedService<IFeatureClient>("flags");

        Assert.True(await client.GetBooleanValueAsync("new-checkout", false));
        Assert.Equal("Flagon", host.Services.GetRequiredService<Api>().GetProviderMetadata("flags")?.Name);
    }

    private sealed class RecordingHook : Hook
    {
        private readonly object _gate = new();
        private readonly List<string> _calls = [];

        public IReadOnlyList<string> Calls
        {
            get { lock (_gate) { return [.. _calls]; } }
        }

        private void Record(string call)
        {
            lock (_gate) { _calls.Add(call); }
        }

        public override ValueTask<global::OpenFeature.Model.EvaluationContext> BeforeAsync<T>(HookContext<T> context, IReadOnlyDictionary<string, object>? hints = null, CancellationToken cancellationToken = default)
        {
            Record($"before:{context.FlagKey}");
            return base.BeforeAsync(context, hints, cancellationToken);
        }

        public override ValueTask AfterAsync<T>(HookContext<T> context, global::OpenFeature.Model.FlagEvaluationDetails<T> details, IReadOnlyDictionary<string, object>? hints = null, CancellationToken cancellationToken = default)
        {
            Record($"after:{details.FlagKey}:{details.Value}:{details.Variant}:{details.Reason}");
            return base.AfterAsync(context, details, hints, cancellationToken);
        }

        public override ValueTask ErrorAsync<T>(HookContext<T> context, Exception error, IReadOnlyDictionary<string, object>? hints = null, CancellationToken cancellationToken = default)
        {
            Record($"error:{context.FlagKey}");
            return base.ErrorAsync(context, error, hints, cancellationToken);
        }

        public override ValueTask FinallyAsync<T>(HookContext<T> context, global::OpenFeature.Model.FlagEvaluationDetails<T> evaluationDetails, IReadOnlyDictionary<string, object>? hints = null, CancellationToken cancellationToken = default)
        {
            Record($"finally:{context.FlagKey}");
            return base.FinallyAsync(context, evaluationDetails, hints, cancellationToken);
        }
    }
}
