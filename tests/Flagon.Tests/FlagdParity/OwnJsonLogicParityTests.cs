using Flagon.Internal.Targeting;
using Flagon.Internal.Targeting.OwnJsonLogic;
using Xunit.Abstractions;

namespace Flagon.Tests.FlagdParity;

/// <summary>The parity harness against Spike A (#22): Flagon's own JsonLogic-to-delegate compiler.</summary>
public sealed class OwnJsonLogicParityTests(ITestOutputHelper output) : FlagdParityTests(output)
{
    protected override string CompilerName => "own-json-logic";

    private protected override ITargetingCompiler Compiler { get; } = new OwnJsonLogicCompiler();
}
