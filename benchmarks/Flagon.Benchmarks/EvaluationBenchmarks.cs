using BenchmarkDotNet.Attributes;
using Flagon.Internal;
using Flagon.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Flagon.Benchmarks;

/// <summary>
/// The hot path: <see cref="IFlagon.GetBooleanDetails"/> on a loaded snapshot with an explicit context, through the
/// default registration. Every targeted scenario's context makes its rule match.
/// </summary>
[MemoryDiagnoser]
public class EvaluationBenchmarks
{
    private static readonly (string Key, string Json)[] Flags =
    [
        ("static", Rules.Static), ("equals", Rules.EqualsAttribute), ("in-list", Rules.InList),
        ("fractional", Rules.Fractional), ("nested", Rules.Nested),
    ];

    private ServiceProvider _services = null!;
    private IFlagon _flagon = null!;
    private EvaluationContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        var store = new InMemoryFlagStore();
        foreach (var (key, json) in Flags)
        {
            store.TryCreateAsync(key, FlagdJson.Deserialize(json)).GetAwaiter().GetResult();
        }

        var services = new ServiceCollection();
        services.AddFlagon(o => o.UseStorage(store));
        _services = services.BuildServiceProvider();
        _services.GetRequiredService<FlagSnapshotManager>().RefreshAsync().GetAwaiter().GetResult();
        _flagon = _services.GetRequiredService<IFlagon>();

        // Eight attributes, as a typical app context; the nested rule reads four of them.
        _context = new EvaluationContext("user-42", new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["country"] = "GB", ["email"] = "ada@example.com", ["plan"] = "free", ["seats"] = 12,
            ["region"] = "eu-west-2", ["appVersion"] = "3.4.1", ["beta"] = true, ["locale"] = "en-GB",
        });

        foreach (var (key, _) in Flags)
        {
            var details = _flagon.GetBooleanDetails(key, false, _context);
            if (details.ErrorCode != ErrorCode.None || (key != "static" && details.Reason != EvaluationReason.TargetingMatch))
            {
                throw new InvalidOperationException($"'{key}' evaluated to {details}; the scenario is broken.");
            }
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _services.Dispose();

    [Benchmark(Baseline = true, Description = "static flag")]
    public FlagEvaluationDetails<bool> Static() => _flagon.GetBooleanDetails("static", false, _context);

    [Benchmark(Description = "== on attribute")]
    public FlagEvaluationDetails<bool> EqualsAttribute() => _flagon.GetBooleanDetails("equals", false, _context);

    [Benchmark(Description = "in on list")]
    public FlagEvaluationDetails<bool> InList() => _flagon.GetBooleanDetails("in-list", false, _context);

    [Benchmark(Description = "fractional 3-way")]
    public FlagEvaluationDetails<bool> Fractional() => _flagon.GetBooleanDetails("fractional", false, _context);

    [Benchmark(Description = "nested if/and (inlined $ref)")]
    public FlagEvaluationDetails<bool> Nested() => _flagon.GetBooleanDetails("nested", false, _context);
}
