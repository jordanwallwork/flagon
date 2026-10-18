using BenchmarkDotNet.Attributes;
using Flagon.Internal;
using Flagon.Internal.Targeting;
using Flagon.Internal.Targeting.JsonLogic;

namespace Flagon.Benchmarks;

/// <summary>Snapshot-load compile cost: building a <see cref="FlagSnapshot"/> of 1,000 flags that all have rules.</summary>
[MemoryDiagnoser]
public class CompileBenchmarks
{
    private const int FlagCount = 1_000;

    private FlagRecord[] _records = [];
    private readonly ITargetingCompiler _compiler = new JsonLogicTargetingCompiler();

    [GlobalSetup]
    public void Setup()
    {
        string[] rules = [Rules.EqualsAttribute, Rules.InList, Rules.Fractional, Rules.Nested];
        _records = Enumerable.Range(0, FlagCount)
            .Select(i => new FlagRecord($"flag-{i}", FlagdJson.Deserialize(rules[i % rules.Length]), 1))
            .ToArray();

        if (FlagSnapshot.Create(1, _records, _compiler).Flags.Values.Any(f => f.Targeting is not { Succeeded: true }))
        {
            throw new InvalidOperationException("A rule failed to compile; the scenario is broken.");
        }
    }

    [Benchmark(Description = "snapshot load, 1k flags with rules")]
    public object Load() => FlagSnapshot.Create(1, _records, _compiler);
}
