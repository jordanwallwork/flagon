using Flagon.Internal.Targeting;
using Flagon.Internal.Targeting.OwnJsonLogic;
using Flagon.Targeting.JsonEverything;

namespace Flagon.Benchmarks;

/// <summary>The targeting compilers under comparison, by benchmark parameter name.</summary>
internal static class Engines
{
    public const string OwnJsonLogic = "own-json-logic";
    public const string JsonEverything = "json-everything";

    public static IEnumerable<string> Names => [OwnJsonLogic, JsonEverything];

    public static ITargetingCompiler Create(string name) => name switch
    {
        OwnJsonLogic => new OwnJsonLogicCompiler(),
        JsonEverything => new JsonEverythingTargetingCompiler(),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown engine."),
    };
}
