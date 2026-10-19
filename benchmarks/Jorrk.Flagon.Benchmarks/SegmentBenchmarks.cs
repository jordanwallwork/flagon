using System.Text.Json;
using BenchmarkDotNet.Attributes;
using Jorrk.Flagon.Internal;
using Jorrk.Flagon.Internal.Targeting;
using Jorrk.Flagon.Internal.Targeting.JsonLogic;

namespace Jorrk.Flagon.Benchmarks;

/// <summary>
/// Snapshot-load cost with segments (#39): 1,000 flags each referencing 10 of 50 segments, against the same flags with
/// the segments' rules written inline. The difference is the cost of inlining <c>$ref</c>s on load.
/// </summary>
[MemoryDiagnoser]
public class SegmentBenchmarks
{
    private const int FlagCount = 1_000;
    private const int SegmentCount = 50;
    private const int RefsPerFlag = 10;

    private readonly ITargetingCompiler _compiler = new JsonLogicTargetingCompiler();
    private FlagRecord[] _withRefs = [];
    private FlagRecord[] _inlined = [];
    private SegmentRecord[] _segments = [];

    [GlobalSetup]
    public void Setup()
    {
        // A mix of list and attribute segments, like real audiences.
        _segments = Enumerable.Range(0, SegmentCount)
            .Select(s => new SegmentRecord($"segment-{s}", new SegmentDocument(Json(s % 2 == 0
                ? $$"""{"in":[{"var":"targetingKey"},[{{string.Join(",", Enumerable.Range(0, 20).Select(k => $"\"user-{s}-{k}\""))}}]]}"""
                : $$"""{"==":[{"var":"plan"},"plan-{{s}}"]}"""), new FlagMetadata()), 1))
            .ToArray();

        _withRefs = Flags(s => $$"""{"$ref":"segment-{{s}}"}""");
        _inlined = Flags(s => _segments[s].Document.Rule.GetRawText());

        if (FlagSnapshot.Create(1, _withRefs, _segments, _compiler).Flags.Values.Any(f => f.Targeting is not { Succeeded: true }))
        {
            throw new InvalidOperationException("A rule failed to compile; the scenario is broken.");
        }
    }

    [Benchmark(Baseline = true, Description = "snapshot load, 1k flags, 10 inlined conditions each")]
    public object LoadInlined() => FlagSnapshot.Create(1, _inlined, _segments, _compiler);

    [Benchmark(Description = "snapshot load, 1k flags, 10 refs each over 50 segments")]
    public object LoadWithRefs() => FlagSnapshot.Create(1, _withRefs, _segments, _compiler);

    private static FlagRecord[] Flags(Func<int, string> condition) => Enumerable.Range(0, FlagCount)
        .Select(i =>
        {
            var conditions = Enumerable.Range(0, RefsPerFlag).Select(r => condition((i + r * 7) % SegmentCount));
            var rule = Json($$"""{"if":[{"or":[{{string.Join(",", conditions)}}]},"on",null]}""");
            return new FlagRecord($"flag-{i}", FlagDocument.Boolean(false, new FlagMetadata()) with { Targeting = rule }, 1);
        })
        .ToArray();

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
