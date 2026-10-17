using BenchmarkDotNet.Attributes;
using Flagon.Internal;
using Flagon.Internal.Targeting;

namespace Flagon.Benchmarks;

/// <summary>Snapshot-load compile cost: building a <see cref="FlagSnapshot"/> of 1,000 flags that all have rules.</summary>
[MemoryDiagnoser]
public class CompileBenchmarks
{
    private const int FlagCount = 1_000;

    private FlagRecord[] _records = [];
    private ITargetingCompiler _compiler = null!;

    [ParamsSource(nameof(EngineNames))]
    public string Engine { get; set; } = Engines.OwnJsonLogic;

    public static IEnumerable<string> EngineNames => Engines.Names;

    [GlobalSetup]
    public void Setup()
    {
        _compiler = Engines.Create(Engine);
        string[] rules = [Rules.EqualsAttribute, Rules.InList, Rules.Fractional, Rules.Nested];
        _records = Enumerable.Range(0, FlagCount)
            .Select(i => new FlagRecord($"flag-{i}", FlagdJson.Deserialize(rules[i % rules.Length]), 1))
            .ToArray();

        if (FlagSnapshot.Create(1, _records, _compiler).Flags.Values.Any(f => f.Targeting is not { Succeeded: true }))
        {
            throw new InvalidOperationException($"{Engine}: a rule failed to compile; the scenario is broken.");
        }
    }

    [Benchmark(Description = "snapshot load, 1k flags with rules")]
    public object Load() => FlagSnapshot.Create(1, _records, _compiler);
}
