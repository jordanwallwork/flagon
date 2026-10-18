# Flagon

Feature flags for .NET apps, stored as flagd flag definitions and evaluated in-process from a snapshot of the store.

## Language

**Flag document**:
A flag's definition in the flagd schema: state, variants, default variant, optional targeting rule and Flagon metadata.
_Avoid_: Flag config, flag definition object

**Variant**:
A named value a flag can serve (e.g. `on: true`, `dark: "dark"`).
_Avoid_: Option, value name

**Default variant**:
The variant served when no targeting rule applies. May be absent, in which case the caller's default is served.
_Avoid_: Fallback variant

**Flag type**:
The type of a flag's values (boolean, string, number or object), derived from its variants as flagd does and never stored. A flag with no variants, or variants of mixed types, has no flag type. Integral and fractional numbers are both number.
_Avoid_: Value type, kind

**Caller's default**:
The value the calling code supplies, returned whenever no variant is served (missing, archived, disabled, no default variant, or error).
_Avoid_: Fallback, code value

**Flag state**:
ENABLED or DISABLED, for a flag of any type. A disabled flag serves no variant, so every caller gets the caller's default (reason `DISABLED`). Distinct from a boolean flag's on/off toggle, which switches its default variant.
_Avoid_: Enabled (for the state of a non-boolean flag), active
