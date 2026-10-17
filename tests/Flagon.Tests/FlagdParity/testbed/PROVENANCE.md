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
- **Boolean subset**: Flagon evaluates boolean flags only (`IFlagon`). A case is included when its flag is boolean,
  or when its flag has a `targeting` rule. Choosing a variant from a rule does not depend on the variant values'
  type, so a targeted flag of any type is stored as a *boolean projection*: the state, variant names,
  `defaultVariant` and rule are kept, and every variant value becomes `true`. The case is then evaluated with a
  caller default of `false`. The expected variant is the one whose original value is the Gherkin's expected value,
  so `(variant, true)` means "the rule chose this variant" and `(null, false)` means "the caller's default was served".
  Where the Gherkin cannot tell these apart (the expected value is both a variant value and the caller default),
  either result is accepted.
- **Excluded**: non-boolean flags without targeting (typed-value resolution, including TYPE_MISMATCH), a key not in
  the suite's flags (FLAG_NOT_FOUND), metadata assertions (Flagon's evaluation details have no flag metadata), and
  fractional examples for other bucketing generations. Flagon targets `@fractional-v3`, the generation flagd 0.18
  implements; `@fractional-v1` and `@fractional-v2` examples expect different buckets for the same inputs. The
  `Summary` test prints every excluded case and why.
- **No `defaultVariant`**: flagd allows a null or absent `defaultVariant`, and so does Flagon: when no rule applies the
  caller's default is served with reason `DEFAULT`, as the `no-default-variant` scenarios assert.
- **Not storable**: a fixture flag that `FlagdJson` rejects is left out of the store, and its cases fail whatever the
  compiler, naming the reason. No flag in this release is affected.

## Updating

Copy the same paths from a newer tag, update the release and commit above, and regenerate each compiler's
`../expected-failures/<compiler>.txt`. A line number that moves changes the case id.
