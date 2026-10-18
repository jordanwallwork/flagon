# Targeting engines: benchmark results (#24)

Compares the two candidate engines behind the internal `ITargetingCompiler` seam:

- **own-json-logic** (Spike A, #22): Flagon's own JsonLogic-to-node-tree compiler. Now
  `src/Flagon/Internal/Targeting/JsonLogic/` (`JsonLogicTargetingCompiler`), the default.
- **json-everything** (Spike B, #23): a compiler over json-everything's `JsonLogic` 5.5.0 NuGet package
  (`src/Flagon.Targeting.JsonEverything/`, deleted after this run; source at commit `4894d11`).

Decision and trade-offs: [ADR 0001](../../docs/adr/0001-targeting-engine.md).

## Current state (2026-10-18)

The numbers below are historical: measured at commit `64a7588` and not re-run since. What has changed:

- **Parity**: the flagd-testbed harness now passes **295 / 295** runnable cases with the default engine; its
  expected-failure list (`tests/Flagon.Tests/FlagdParity/expected-failures/default.txt`) is empty.
- **Divergences closed**: the `no-default-variant.feature` failures and both seam-level divergences listed below
  (runtime rule failure, `targeting: {}`) now follow flagd (see the Consequences in
  [ADR 0001](../../docs/adr/0001-targeting-engine.md)).
- **Engine size**: about 2,150 lines (about 1,780 non-blank, non-comment) in 5 files.
- The **nested** scenario was renamed from "nested if/and/$ref" to "nested if/and (inlined $ref)": the rule has no
  `$ref`, because Flagon requires evaluators to be inlined on write.

## How it was run

Suite: `benchmarks/Flagon.Benchmarks` at commit `64a7588`, which still referenced both engines.

```sh
dotnet build benchmarks/Flagon.Benchmarks -c Release
dotnet run -c Release -f net10.0 --no-build --project benchmarks/Flagon.Benchmarks -- \
  --filter '*' --job short --runtimes net8.0 net10.0
```

The suite now measures only the default registration, as a regression guard. It runs with the same command; drop
`--job short` to get a full-length run. CI builds the project but does not run it.

- BenchmarkDotNet 0.15.8, `ShortRun` job (`IterationCount=3, LaunchCount=1, WarmupCount=3`), `MemoryDiagnoser`.
- Linux Ubuntu 24.04.5 in a shared 4-core cloud container (Intel Xeon 2.10 GHz). .NET 8.0.32 and .NET 10.0.13, x64 RyuJIT.
- Short runs on a shared host are noisy: the error columns are wide (up to ±100% for single json-everything rows).
  The gap between the engines is 5–12× on time and 20–115× on allocation, far beyond the noise. Allocated bytes are
  deterministic and exact.

Hot-path scenarios call `IFlagon.GetBooleanDetails(key, false, context)` on a loaded snapshot. The context has a
targeting key and 8 attributes, and each targeted rule matches it. Rules (`Rules.cs`):

- **static flag**: no targeting; the baseline and regression guard.
- **== on attribute**: `if(== var "country" "GB")`.
- **in on list**: `if(in var "country" ["FR","DE","ES","GB"])`.
- **fractional 3-way**: `fractional` 34/33/33 on the implicit targeting key.
- **nested if/and (inlined $ref)**: `if(and(in, or(==, >=), starts_with)) … else if(==)`. This is the body of a
  `$ref` evaluator, already inlined: the rule contains no `$ref`. Flagon stores flags one at a time and rejects
  `$ref` on write, so a flag-set's `$evaluators` must be inlined before writing, as the parity harness does. The
  scenario measures the inlined rule, not `$ref` resolution. (Called "nested if/and/$ref" in the `64a7588` run.)

Compile cost: `FlagSnapshot.Create` over 1,000 flags. The flags cycle through the four targeted rules above.

## Comparison (as of `64a7588`)

### Hot path: ns/op and allocated B/op (`GetBooleanDetails`)

| Scenario | own-json-logic net8 | own-json-logic net10 | json-everything net8 | json-everything net10 |
|---|---:|---:|---:|---:|
| static flag | 87 ns / 56 B | 88 ns / 56 B | 100 ns / 56 B | 90 ns / 56 B |
| `==` on attribute | 224 ns / 56 B | 193 ns / 56 B | 1,825 ns / 1,568 B | 1,554 ns / 1,216 B |
| `in` on list | 266 ns / 56 B | 220 ns / 56 B | 2,610 ns / 2,464 B | 2,100 ns / 2,040 B |
| `fractional` 3-way | 404 ns / 56 B | 293 ns / 56 B | 4,564 ns / 6,472 B | 2,116 ns / 5,064 B |
| nested `if`/`and` (inlined `$ref`) | 458 ns / 56 B | 430 ns / 56 B | 5,266 ns / 4,480 B | 2,850 ns / 3,240 B |

The 56 B in every row is the `FlagEvaluationDetails<bool>` result, which the static flag allocates too. Evaluating a
rule with own-json-logic adds **0 B**. json-everything adds 1.2–6.4 KB per evaluation: the per-call `JsonObject` data
document, deep-cloned array literals, boxed `JsonValue` results, LINQ enumerators and the `EvaluationContext` stack.

### Snapshot load: 1,000 flags with rules

| | net8 | net10 |
|---|---:|---:|
| own-json-logic | 2.25 ms / 2.43 MB | 1.95 ms / 1.92 MB |
| json-everything | 3.02 ms / 4.53 MB | 2.42 ms / 4.27 MB |

Both are fine at snapshot-reload frequency. own-json-logic is ~25% faster and allocates about half as much.

### Parity, footprint, size

As of `64a7588`; see [Current state](#current-state-2026-10-18) for parity and size now.

| | own-json-logic | json-everything |
|---|---|---|
| flagd-testbed v3.10.4 boolean subset | 290 / 295 runnable pass (98.3%); all 290 compiler-dependent cases pass | 288 / 293 runnable pass (98.3%); all 288 compiler-dependent cases pass |
| Remaining failures | 5 × `no-default-variant.feature` (flag has no `defaultVariant`, which Flagon cannot store; independent of the compiler) | the same 5 |
| Known untested divergences from flagd | runtime panic maps to GENERAL, flagd gives PARSE_ERROR; `targeting: {}` gives DEFAULT, flagd gives STATIC (both are seam-level and shared) | the same two, plus `decimal` arithmetic (`0.1+0.2==0.3` is true here, false in flagd; overflow above ±7.9e28), `cat` on bool, no `TrimSpace`, number formatting |
| New package dependencies | **none** (BCL only) | 4: `JsonLogic` 5.5.0, `JsonPointer.Net` 6.0.0, `Json.More.Net` 2.2.0, `Humanizer.Core` 3.0.1 |
| Third-party assemblies shipped | 0 KB | ~860 KB on net8, ~825 KB on net10 (Humanizer is ~78%) |
| Licence | MIT (Flagon's own) | 5.5.0 is MIT but frozen. Every release ≥ 6.0 ships binaries under the json-everything Open Source Maintenance Fee EULA |
| Ecosystem conflict | none | `OpenFeature.Providers.Flagd.Core` 2.0.0 pins `JsonLogic [6.0.2]`. An app with both gets NU1605/NU1107, or is forced onto the EULA version |
| Engine LOC (non-blank, non-comment) | ~1,740 (5 files, 2,096 lines) | ~800 (5 files, 985 lines) plus the library |
| Engine-specific tests | 25 (`JsonLogicTargetingCompilerTests`, which asserts 0 B for common rules) | 6 |

Released `Flagon.dll` with the own engine inside: 160 KB on net8 and net10.
