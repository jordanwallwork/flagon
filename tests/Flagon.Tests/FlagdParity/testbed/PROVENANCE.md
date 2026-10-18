# Vendored flagd-testbed fixtures

Unmodified copies of evaluation fixtures from [open-feature/flagd-testbed](https://github.com/open-feature/flagd-testbed),
run by `FlagdParityTests` (see `../FlagdParityTests.cs`).

| | |
|---|---|
| Release | `v3.10.4` (2026-10-15; its image bundles flagd 0.18.0) |
| Commit | `2f8f72176b99d85a31b23cd331a775a7c3015423` |
| Licence | Apache License 2.0 (`LICENSE`, copied from open-feature/flagd) |

The testbed repository has no `LICENSE` file of its own at this commit. All OpenFeature projects are published
under the Apache License 2.0 as the CNCF IP policy requires
([open-feature/community](https://github.com/open-feature/community#license)), so the licence text is vendored
alongside.

## Files

Paths mirror the testbed repository.

| Path | Suite | What it is |
|---|---|---|
| `evaluator/flags/testkit-flags.json` | evaluator | Flags for the in-process evaluator testkit |
| `evaluator/gherkin/*.feature` | evaluator | The evaluator testkit scenarios (`Given an evaluator`) |
| `flags/custom-ops.json`, `flags/edge-case-flags.json`, `flags/evaluator-refs.json`, `flags/testing-flags.json` | provider | The flag files `gherkin/targeting.feature` references |
| `gherkin/targeting.feature` | provider | The provider suite's targeting scenarios: adds `$flagd.timestamp`, rule error cases and fractional v3 |

Each suite runs against its own flag files, in its own store.

## How cases are interpreted

- **Case id**: `<feature path>:<line>`, the line of the scenario or of its Examples row. Expected-failure lists use
  these ids.
- **`$ref` / `$evaluators`**: flagd substitutes `{"$ref": "<name>"}` with the flag set's `$evaluators` entry when it
  loads a file. Flagon stores flags one at a time and has no flag-set `$evaluators`, so the harness does the same
  substitution before storing. A reference to an unknown evaluator stays in the rule, and the compiler must then
  report PARSE_ERROR, as flagd does. Because the substitution works on parsed JSON, the "whitespace around the colon"
  cases pass trivially.
- **Typed getters**: every fixture flag is stored as it is, typed variant values and all. Each case is evaluated
  through the `IFlagon` getter its `<type>-flag` step names (`Boolean` → `GetBooleanDetails`, `String` →
  `GetStringDetails`, `Integer` → `GetIntDetails`, `Float` → `GetDoubleDetails`, `Object` → `GetObjectDetails`),
  with the Gherkin's fallback value as the caller's default. So the typed cases of `evaluation`, `zero-values`,
  `string`, `errors` (TYPE_MISMATCH) and targeting/fractional over typed flags all run. The expected result is a
  variant whose value equals the Gherkin's expected value (compared as the step's type: numbers numerically, objects
  as JSON). Where the Gherkin cannot tell a variant from the caller's default (the expected value is both a variant
  value and the caller default), either result is accepted.
- **Excluded**: a key not in the suite's flags (FLAG_NOT_FOUND: type-agnostic and covered by Flagon's own tests),
  metadata assertions (Flagon's evaluation details have no flag metadata), and fractional examples for other
  bucketing generations. Flagon targets `@fractional-v3`, the generation flagd 0.18
  implements; `@fractional-v1` and `@fractional-v2` examples expect different buckets for the same inputs. The
  `Summary` test prints every excluded case and why.
- **No `defaultVariant`**: flagd allows a null or absent `defaultVariant`, and so does Flagon: when no rule applies the
  caller's default is served with reason `DEFAULT`, as the `no-default-variant` scenarios assert.
- **Not storable**: a fixture flag that `FlagdJson` rejects is left out of the store, and its cases fail whatever the
  compiler, naming the reason. No flag in this release is affected.

## Updating

Copy the same paths from a newer tag, update the release and commit above, and regenerate each compiler's
`../expected-failures/<compiler>.txt`. A line number that moves changes the case id.
