# Evaluation observer seam: benchmark results (#36)

Jorrk.Flagon.Testing (#36, #62) added an internal `IFlagonEvaluationObserver` to `FlagonEvaluator`. Its evaluation log
uses this seam. With no observer registered, the hot path pays one null check. Code review also reshaped the typed
getters around one shared `Typed` path. This run checks that neither change slowed down the hot path.

## How it was run (2026-10-18)

The `EvaluationBenchmarks` suite (see [targeting-engines.md](targeting-engines.md) for the scenarios) was run with the
**default (full-length) job** on net10.0 only. The other suite, `CompileBenchmarks`, was not run, because snapshot
loading did not change.

```sh
dotnet build benchmarks/Jorrk.Flagon.Benchmarks -c Release
dotnet run -c Release -f net10.0 --no-build --project benchmarks/Jorrk.Flagon.Benchmarks -- \
  --filter '*EvaluationBenchmarks*'
```

- BenchmarkDotNet 0.15.8, default job, `MemoryDiagnoser`. .NET 10.0.13, x64 RyuJIT x86-64-v4.
- Linux Ubuntu 24.04.5 in a shared 4-core cloud container (Intel Xeon 2.10 GHz).
- Each commit's source was exported (`git archive`) and benchmarked on its own. Runs were sequential, with nothing
  else running.
- The shared host drifts by up to about 10% between runs of the same code (base: 106 ns in the first run, 97 ns in a
  later one, for the static flag). Only runs taken next to each other are compared.

Commits:

- **base** `0516c95`: main before Jorrk.Flagon.Testing.
- **seam** `95560bf`: observer seam added. Typed getters each called `Evaluate`, then `Tracked`.
- **delegates** `5051b85` (getter change in `6c8b52f`): first review fix. Each getter passed a static lambda (`TypedView<T>` delegate) to one
  `Typed<T>`. This version regressed and was replaced.
- **final** `324a201`: `Typed<T, TView>` over static-abstract struct views (`BooleanView`, `StringView`, …), which the
  JIT specialises per getter.

## Results: mean ns/op (`GetBooleanDetails`)

Every row in every run allocates **56 B**: the `FlagEvaluationDetails<bool>` result. Neither change adds any
allocation.

### The regression, and where it came from

These four runs were taken one after another. Error is the 99.9% half-interval, about 2% of the mean.

| Scenario | base | delegates | delegates (rerun) | base (rerun) | seam |
|---|---:|---:|---:|---:|---:|
| static flag | 106.2 | 109.0 | 105.3 | 105.1 | 109.2 |
| `==` on attribute | 218.1 | 239.2 | 238.1 | 214.0 | 220.9 |
| `in` on list | 236.1 | 247.0 | 245.1 | 237.5 | 235.6 |
| `fractional` 3-way | 349.3 | 370.6 | 365.5 | 361.2 | 352.0 |
| nested `if`/`and` | 414.8 | 482.6 | 450.8 | 427.8 | 428.1 |

- **The seam itself costs nothing measurable.** The seam column matches base on every row.
- **The delegate version was a real regression.** In both runs, `==` was 21 to 24 ns slower (+11%) and nested was 23
  to 68 ns slower. The static flag did not change.
- **The fix:** static-abstract struct views replaced the delegates.

### Final against base (base run between two final runs)

| Scenario | final | base | final (rerun) |
|---|---:|---:|---:|
| static flag | 97.8 ± 1.9 | 96.7 ± 1.9 | 94.4 ± 2.0 |
| `==` on attribute | 198.6 ± 4.0 | 193.1 ± 3.9 | 182.5 ± 3.7 |
| `in` on list | 212.4 ± 3.8 | 218.0 ± 4.4 | 200.2 ± 4.1 |
| `fractional` 3-way | 300.7 ± 6.0 | 308.8 ± 5.3 | 288.2 ± 5.6 |
| nested `if`/`and` | 369.7 ± 7.4 | 358.4 ± 6.3 | 351.0 ± 6.9 |

**The final version matches base.** Every difference is within the run-to-run drift, and none points the same way in
both runs.
