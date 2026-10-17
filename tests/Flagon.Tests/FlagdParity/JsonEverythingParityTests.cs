using Flagon.Internal.Targeting;
using Flagon.Targeting.JsonEverything;
using Xunit.Abstractions;

namespace Flagon.Tests.FlagdParity;

/// <summary>The parity harness against Spike B (#23): the json-everything JsonLogic compiler.</summary>
public sealed class JsonEverythingParityTests(ITestOutputHelper output) : FlagdParityTests(output)
{
    protected override string CompilerName => "json-everything";

    private protected override ITargetingCompiler Compiler { get; } = new JsonEverythingTargetingCompiler();
}
