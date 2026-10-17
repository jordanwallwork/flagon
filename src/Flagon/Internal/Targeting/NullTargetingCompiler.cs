using System.Text.Json;

namespace Flagon.Internal.Targeting;

/// <summary>
/// The default compiler until a targeting engine lands: every rule compiles to one that never matches, so a flag
/// with targeting serves its <c>defaultVariant</c> with reason DEFAULT.
/// </summary>
internal sealed class NullTargetingCompiler : ITargetingCompiler
{
    private static readonly TargetingCompilation NeverMatches = TargetingCompilation.Success(static (in TargetingContext _) => null);

    public TargetingCompilation Compile(string flagKey, JsonElement targeting) => NeverMatches;
}
