using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Jorrk.Flagon.Tests.FlagdParity;

/// <summary>
/// Runs the vendored flagd-testbed evaluation fixtures (see <c>testbed/PROVENANCE.md</c>) through the host seam:
/// <c>AddFlagon</c> with an InMemory store seeded with the fixture flags as they are, each case evaluated via the
/// <see cref="IFlagon"/> typed getter its Gherkin flag type names.
/// </summary>
/// <remarks>
/// One concrete subclass per targeting compiler. A subclass selects its compiler by overriding <see cref="Compiler"/>
/// (registered as the <see cref="ITargetingCompiler"/> after <c>AddFlagon</c>, replacing the default; null keeps
/// <c>AddFlagon</c>'s own registration) and names its expected-failure list with <see cref="CompilerName"/>
/// (<c>expected-failures/&lt;name&gt;.txt</c>). <see cref="ConfigureServices"/> is there for anything else a compiler needs.
/// The list is exact: a listed case that fails passes (reported as XFAIL), a listed case that passes fails the run
/// ("remove it from the list"), so the list of each compiler is its parity result and lists compare directly.
/// </remarks>
public abstract class FlagdParityTests(ITestOutputHelper output)
{
    /// <summary>Names this compiler's expected-failure list, <c>expected-failures/&lt;name&gt;.txt</c>.</summary>
    protected abstract string CompilerName { get; }

    /// <summary>The compiler under test, or null for the one <c>AddFlagon</c> registers by default.</summary>
    private protected virtual ITargetingCompiler? Compiler => null;

    /// <summary>
    /// Service registrations applied after <c>AddFlagon</c>. The default registers <see cref="Compiler"/> (when set);
    /// an override that needs more should call the base.
    /// </summary>
    protected virtual void ConfigureServices(IServiceCollection services)
    {
        if (Compiler is { } compiler)
        {
            services.AddSingleton(compiler);
        }
    }

    /// <summary>Every runnable case, one theory row each.</summary>
    public static TheoryData<FlagdCase> Cases
    {
        get
        {
            var data = new TheoryData<FlagdCase>();
            foreach (var c in FlagdTestbed.Runnable)
            {
                data.Add(FlagdCase.From(c));
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases), MemberType = typeof(FlagdParityTests))]
    public async Task Fixture_case_resolves_as_flagd_does(FlagdCase testCase)
    {
        var @case = FlagdTestbed.Get(testCase.Id);
        await using var host = await FlagonTestHost.StartAsync(o =>
        {
            o.Store = FlagdTestbed.SeedStore(@case.Suite);
            o.Services = ConfigureServices;
        });

        var details = @case.Evaluate(host.Flagon);
        var mismatches = @case.Check(details);
        var expectedToFail = ExpectedFailures.For(CompilerName).Contains(@case.Id);

        if (mismatches.Count == 0)
        {
            Assert.False(expectedToFail,
                $"{@case.Id} now passes but is listed in {ExpectedFailures.FileName(CompilerName)}: remove it from the list.");
            output.WriteLine($"PASS {@case}");
            return;
        }

        var report = $"{@case}\n  got: {details}\n  " + string.Join("\n  ", mismatches);
        if (expectedToFail)
        {
            output.WriteLine($"XFAIL {report}");
            return;
        }

        Assert.Fail($"FAIL {report}");
    }

    [Fact]
    public void Expected_failure_list_names_only_runnable_cases()
    {
        var runnable = FlagdTestbed.Runnable.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var unknown = ExpectedFailures.For(CompilerName).Where(id => !runnable.Contains(id)).ToList();

        Assert.True(unknown.Count == 0,
            $"{ExpectedFailures.FileName(CompilerName)} lists cases that are not runnable fixture cases: {string.Join(", ", unknown)}");
    }

    [Fact]
    public void Summary()
    {
        var expected = ExpectedFailures.For(CompilerName);
        var runnable = FlagdTestbed.Runnable;
        output.WriteLine($"flagd-testbed parity [{CompilerName}]: {runnable.Count} runnable cases, " +
                         $"{runnable.Count - expected.Count} expected to pass, {expected.Count} expected to fail, " +
                         $"{FlagdTestbed.Excluded.Count} excluded.");
        foreach (var (c, reason) in FlagdTestbed.Excluded)
        {
            output.WriteLine($"  excluded {FlagdCase.Describe(c)}: {reason}");
        }

        Assert.NotEmpty(runnable);
    }
}

/// <summary>
/// The parity harness against Flagon's default registration (whatever compiler <c>AddFlagon</c> wires up:
/// <see cref="Internal.Targeting.JsonLogic.JsonLogicTargetingCompiler"/>). Its expected-failure list is empty, and
/// <see cref="The_default_registration_has_no_expected_failures"/> keeps it so. An alternative compiler adds a sibling, e.g.
/// <c>public sealed class OtherParityTests(ITestOutputHelper o) : FlagdParityTests(o) { protected override string CompilerName =&gt; "other"; private protected override ITargetingCompiler Compiler { get; } = new OtherTargetingCompiler(); }</c>.
/// </summary>
public sealed class DefaultCompilerParityTests(ITestOutputHelper output) : FlagdParityTests(output)
{
    protected override string CompilerName => "default";

    [Fact]
    public void The_default_registration_has_no_expected_failures() =>
        Assert.Empty(ExpectedFailures.For(CompilerName));
}
