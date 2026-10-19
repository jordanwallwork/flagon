# Segments: snapshot-load benchmark results (#39)

Segments are inlined into each flag's rule when a snapshot is built, so their only cost is at load; evaluation runs the
same compiled rule as the inlined equivalent (`SegmentRefsTests` checks it allocates the same). This measures that load
cost: `SegmentBenchmarks` builds a snapshot of 1,000 flags whose rules are each
`{"if":[{"or":[c1, …, c10]},"on",null]}` over 50 segments (half 20-key lists, half attribute conditions), once with the
ten conditions as `{"$ref": …}` and once with the same conditions written inline.

## How it was run

Suite: `benchmarks/Jorrk.Flagon.Benchmarks` at commit `7db5bb6` plus `SegmentBenchmarks`.

```sh
dotnet build benchmarks/Jorrk.Flagon.Benchmarks -c Release
dotnet run -c Release -f net10.0 --no-build --project benchmarks/Jorrk.Flagon.Benchmarks -- \
  --filter '*SegmentBenchmarks*' '*CompileBenchmarks*' --job short --runtimes net8.0 net10.0
```

- BenchmarkDotNet 0.15.8, `ShortRun` job (`IterationCount=3, LaunchCount=1, WarmupCount=3`), `MemoryDiagnoser`.
- Linux in a shared 4-core cloud container (Intel Xeon 2.10 GHz). .NET 8.0.32 and .NET 10.0.13, x64 RyuJIT.
- Short runs in a shared container are noisy (see the Error column); read the ratios, not the last digit.

## Results (2026-10-19)

| Scenario                                               | Runtime   | Mean     | Error    | Allocated |
|--------------------------------------------------------|-----------|---------:|---------:|----------:|
| snapshot load, 1k flags, 10 inlined conditions each    | .NET 10.0 | 27.88 ms | 19.72 ms |  27.35 MB |
| snapshot load, 1k flags, 10 refs each over 50 segments | .NET 10.0 | 35.50 ms | 65.59 ms |  30.65 MB |
| snapshot load, 1k flags, 10 inlined conditions each    | .NET 8.0  | 33.88 ms | 40.17 ms |  30.53 MB |
| snapshot load, 1k flags, 10 refs each over 50 segments | .NET 8.0  | 43.81 ms | 40.74 ms |  34.08 MB |

For reference, the existing `CompileBenchmarks` scenario (1,000 flags with one small rule each, no segments) in the
same run: 2.20 ms / 2.36 MB on .NET 10.0, 2.76 ms / 2.61 MB on .NET 8.0.

## Reading

- Resolving references adds about 27-29% to the load time and 12% to allocation of this deliberately heavy scenario
  (10,000 references): about 8-10 ms per reload, paid once per change-version move, never per evaluation. It is the
  cost of writing each inlined rule and parsing it back before compiling.
- If very large flag sets with many references make this matter, the inliner could hand the compiler the segment's
  parsed rule instead of re-serialising it; that is not needed at this scale.
