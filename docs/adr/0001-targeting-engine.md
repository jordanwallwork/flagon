# Own JsonLogic compiler for flagd targeting, not json-everything

Status: accepted (#24, spec #19)

Flagon evaluates flagd `targeting` rules with its own compiler, `Flagon.Internal.Targeting.JsonLogic`. At snapshot
load the compiler turns each rule into a tree of stateless nodes. Evaluation walks that tree over a `ref struct` env
and returns a 24-byte value struct. We did not wrap json-everything's `JsonLogic` package. We built both behind the
`ITargetingCompiler` seam (#22, #23), and they tied on parity. The library lost on the hot path, on footprint and on
licensing, and those are the criteria spec #19 names. Numbers: [`benchmarks/results/targeting-engines.md`](../../benchmarks/results/targeting-engines.md)
(BenchmarkDotNet ShortRun, net8 and net10).

| | own compiler | json-everything 5.5.0 |
|---|---|---|
| `GetBooleanDetails`, `==` rule (net10) | 193 ns, 56 B (= static flag's 56 B) | 1,554 ns, 1,216 B |
| `fractional` 3-way (net10) | 293 ns, 56 B | 2,116 ns, 5,064 B |
| nested `if`/`and`/`$ref` (net10) | 430 ns, 56 B | 2,850 ns, 3,240 B |
| Snapshot load, 1k rules (net10) | 1.95 ms, 1.9 MB | 2.42 ms, 4.3 MB |
| flagd-testbed boolean subset | 100% of compiler-dependent cases | 100% of compiler-dependent cases |
| New dependencies | none | 4 packages, ~860 KB (78% Humanizer) |

## Why not the library

- **Hot path.** The spec requires nanoseconds and near-zero allocation. The library's model-less API needs a
  `JsonObject` data document built on every call. It deep-clones array literals, boxes every result and allocates a
  context stack. That is 1–6 KB and microseconds per evaluation, and it is inherent to the API. The own compiler adds
  0 B over a static flag, and its tests assert that.
- **Licence.** Every `JsonLogic` release from 6.0 ships its binaries under the json-everything Open Source Maintenance
  Fee EULA. That fee applies to revenue-generating users with at least US$10k in revenue, and every Flagon consumer
  would accept it transitively. 5.5.0 is the last MIT release. It is frozen, and it already lacks later fixes.
- **Version conflict.** `OpenFeature.Providers.Flagd.Core` pins `JsonLogic [6.0.2]` exactly, so an app that uses
  both it and Flagon hits a NuGet downgrade or conflict error.
- **Parity work.** Most of the library approach is hand-written anyway: the flagd operators, fractional v3 (CBOR and
  murmur3), the semver port, an operator allowlist (the library passes unknown operators through silently) and a
  `var` override. Real divergences would remain: `decimal` arithmetic, `cat` semantics and number formatting.
  Closing them means overriding more of the library.

## Consequences

- Flagon owns about 1,740 lines of JsonLogic semantics: a port of diegoholiveira/jsonlogic v3.9.1 (flagd 0.18)
  restricted to flagd's schema operators. A flagd engine upgrade that changes semantics has to be ported by hand. The
  vendored flagd-testbed (`tests/Flagon.Tests/FlagdParity`) is the guard: bump the fixtures, and every case that
  diverges fails `DefaultCompilerParityTests`.
- Core stays dependency-free beyond `Microsoft.Extensions.*` abstractions and the BCL.
- Two known divergences were seam-level and shared by both candidates, and were left for the hardening work: a
  runtime panic in Go gave PARSE_ERROR in flagd but GENERAL in Flagon, and `targeting: {}` gave STATIC in flagd but
  DEFAULT in Flagon. Both now follow flagd (#25).
