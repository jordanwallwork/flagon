# Flagon

Self-hosted, OpenFeature-compatible feature flags for .NET.

Flagon evaluates flags in-process from an in-memory snapshot (no I/O on the hot path), stores them in the database your app
already has as [flagd](https://flagd.dev/reference/flag-definitions/)-schema JSON, ships its own management UI, and lets
other apps (including non-.NET ones, over [OFREP](https://openfeature.dev/specification/appendix-c)) share the same flags.

- One line of setup; in-memory storage when no store is configured.
- PostgreSQL and SQL Server stores (raw ADO.NET, no EF Core), with automatic or DBA-managed migrations.
- Unknown keys are auto-created with the default your code supplied, so creating a flag never changes behaviour.
- Several apps can share one flag database: optimistic concurrency, an audit log and per-app usage tracking.
- OpenFeature provider, OFREP endpoints, management API and an embeddable UI (no Node build step).
- Targets .NET 8 and .NET 10. MIT licensed.

## Packages

| Package | What it adds |
| --- | --- |
| `Jorrk.Flagon` | Core engine: `AddFlagon`, `IFlagon` (evaluation), `IFlagonAdmin` (management), in-memory store |
| `Jorrk.Flagon.Storage.Postgres` | `UsePostgresStorage(...)` |
| `Jorrk.Flagon.Storage.SqlServer` | `UseSqlServerStorage(...)` |
| `Jorrk.Flagon.AspNetCore` | OFREP endpoints, management HTTP API, ambient evaluation context, health check |
| `Jorrk.Flagon.Ui` | Embeddable management console: `MapFlagonUi(path)` |
| `Jorrk.Flagon.OpenFeature` | OpenFeature provider for the standard `IFeatureClient` API |
| `Jorrk.Flagon.Storage.Conformance` | xUnit conformance suite for writing your own `IFlagStore` provider |
| `Jorrk.Flagon.Testing` | `TestFlagon`: a real `IFlagon` over an in-memory store for unit tests, with one-line setup of any flag type, variants, targeting and state |

## Install

```sh
dotnet add package Jorrk.Flagon
```

Add a storage provider and the ASP.NET Core packages as you need them:

```sh
dotnet add package Jorrk.Flagon.Storage.Postgres     # or Jorrk.Flagon.Storage.SqlServer
dotnet add package Jorrk.Flagon.AspNetCore
dotnet add package Jorrk.Flagon.Ui
dotnet add package Jorrk.Flagon.OpenFeature
```

## Quickstart

```csharp
using Jorrk.Flagon;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddFlagon();   // no storage configured => in-memory store

var app = builder.Build();

app.MapGet("/", (IFlagon flags) =>
    flags.IsEnabled("new-checkout", defaultValue: false) ? "New checkout" : "Old checkout");

app.Run();
```

`IFlagon` is a singleton you can inject anywhere. Evaluation never touches the database: it reads a snapshot that is
refreshed in the background (every 10 seconds by default), so it is safe on hot paths and returns your default when a
flag doesn't exist.

```csharp
bool on = flags.IsEnabled("new-checkout");                        // default: false
bool on2 = await flags.IsEnabledAsync("new-checkout", true);

FlagEvaluationDetails<bool> details = flags.GetBooleanDetails("new-checkout");
// details.Value, details.Variant ("on"/"off"), details.Reason (Static, TargetingMatch, Default, Disabled, Archived, Error, ...),
// details.ErrorCode (None, FlagNotFound, ...)
```

String, number and object flags (flagd's other flag types) have typed getters, each with `*Details` and `*Async`
counterparts. The default is required:

```csharp
string theme = flags.GetString("theme", "light");
int pageSize = flags.GetInt("page-size", 20);
double ratio = await flags.GetDoubleAsync("ratio", 0.5);
JsonElement layout = flags.GetObject("layout", defaultJson);               // the variant's JSON object, as is
Layout typed = flags.GetObject("layout", Layout.Default, options: jsonOptions); // deserialised into your type

FlagEvaluationDetails<int> sizeDetails = flags.GetIntDetails("page-size", 20);
```

A served value of the wrong type never throws: you get your default with reason `Error` and error code `TypeMismatch`.
That includes a number that is not an integer within `Int32` range from `GetInt` (never truncated), and an object that
does not deserialise into your type from `GetObject<T>`. The type check applies only to a served variant: a missing,
archived or disabled flag, or one with no default variant, returns your default with its usual reason, whatever the
type you asked for. Targeting rules and `fractional` rollouts work for every type.

`GetAllDetails()` evaluates every flag at once (e.g. for a frontend): each entry's `Value` is the served variant's JSON,
or `default` (`JsonValueKind.Undefined`) when no variant was served (disabled, no default variant, error), so apply
your own default.

Every evaluation method takes an optional `EvaluationContext` (targeting key plus attributes). When you don't pass one,
Flagon uses the ambient context: with `builder.Services.AddFlagonAmbientContext()` (Jorrk.Flagon.AspNetCore) that is the
current request's user; a context you pass always wins. A flag's flagd `targeting` rule is evaluated against that
context (see [Targeting](#targeting)).

The first time code evaluates an unknown key, Flagon creates the flag (in the background, off the evaluation path) with
the default the code supplied, marked `source: auto`, so behaviour does not change. The flag takes the getter's type:
`IsEnabled` creates the usual `on`/`off` boolean flag, and a typed getter creates
`{ "variants": { "default": <your default> }, "defaultVariant": "default" }` (a `null` or array default creates
nothing). Archived keys are never re-created, and an evaluation that gets `TypeMismatch` never creates anything. If two
apps first evaluate the same key with different types at once, the first create wins and the other app gets
`TypeMismatch` (and its default) until its code or the flag is fixed; the usage report shows the mismatch.

Every getter also records usage: per app, when it last evaluated the key and the default it supplied (written as JSON in
batches, see `UsageFlushInterval`). Recording is lock-free and, for boolean and number defaults, allocation-free;
object defaults are serialised when written, not on each evaluation. Apps whose defaults differ, including in type, are
flagged as a default mismatch; defaults are compared by JSON content, so object key order and `1` versus `1.0` do not
count.

### Options

```csharp
builder.Services.AddFlagon(opts =>
{
    opts.ApplicationName = "checkout-api";            // default: the host's application name
    opts.RefreshInterval = TimeSpan.FromSeconds(10);  // how often to poll the store for changes
    opts.PushRefresh = true;                          // refresh at once when a store pushes a change (see below)
    opts.PushMinInterval = TimeSpan.FromMilliseconds(100); // debounce: a burst of pushes becomes one refresh
    opts.AutoCreate = true;                           // create unknown keys on first evaluation
    opts.AutoMigrate = true;                          // create/upgrade Flagon's tables on startup
    opts.UsageTracking = true;                        // record last-evaluated time per flag and app
    opts.FailFastOnStartup = false;                   // true: throw at startup if the store is unreachable

    // Flags as code (see below).
    opts.Define("new-checkout", defaultValue: false, description: "New checkout flow", tags: ["checkout"]);
});
```

If the store is unavailable at startup, the app still boots with code defaults and keeps retrying; if it fails later,
Flagon keeps serving the last snapshot it loaded.

`PushRefresh` (default on) matters only for stores that can push change notifications, which implement the optional
`IFlagChangeNotifier` capability. For those stores, a change made by any app sharing the store refreshes this app's
snapshot rather than waiting for the next `RefreshInterval` tick. The first push starts a `PushMinInterval` window
(default 100 ms); the app refreshes once when the window ends, and pushes that arrive inside it coalesce into that
refresh. Pushes never reset the poll timer, and polling stays on as the backstop, so a missed push costs latency, never
correctness. Set `PushRefresh = false` to poll only, even when the store can push. `InMemoryFlagStore` and PostgreSQL push;
SQL Server pushes only when you opt in (see below). Usage writes do not push.

**PostgreSQL push.** `UsePostgresStorage` notifies on `flagon_{schema}` inside each change's transaction, so the
notification arrives at commit. Each app listens on one dedicated connection outside the connection pool. No trigger or
migration is needed. Writes made directly in SQL are not pushed; other apps see them at the next poll tick.

- The listener checks its connection with `SELECT 1` every 30 seconds, bounded by a 5-second timeout. A lost connection or
  a failed check reconnects with exponential backoff (1 second doubling, capped at 30 seconds, jittered), re-issues
  `LISTEN` and refreshes once, so changes made during the gap are picked up. Until it reconnects, polling covers the gap.
  A failed first connect is retried the same way. Logs: Information on connect and disconnect, Warning on each backoff,
  Debug per notification. The `flagon.push.reconnects` counter and `IsPushConnected` in the snapshot status show how often
  it has happened.
- Stopping the host closes the listener connection before `StopAsync` returns.
- **PgBouncer in transaction-pool mode is not supported for push.** It drops `LISTEN` state between transactions, so no
  notifications arrive, and Flagon falls back to polling without an error. Session-pool mode and direct connections work.
- **Timestamps and push lag.** The notification payload carries the database's `clock_timestamp()` (epoch milliseconds),
  taken inside the write transaction just before it commits. It is the notify time, not the exact commit instant.
  `flagon.push.lag` is the time from that timestamp to the snapshot applying the change, measured on the app host's clock,
  so clock skew between the database and the app hosts is included: a host ahead of the database reports too large a lag,
  and one behind reports a negative lag. Keep them on NTP-synchronised clocks before reading the histogram as absolute
  latency. The lag is measured from the earliest timestamp a refresh covers, so a burst reports its worst case.

**SQL Server push (opt-in).** `UseSqlServerStorage("Flags", push: true)` subscribes to query notifications
(`SqlDependency`) on the change counter that every version-moving write already updates. Apps sharing the database see a
change within milliseconds. No trigger or migration is needed, and writes are unchanged. Push is off by default because
of its requirements:

- **Service Broker** must be enabled on the database. It is on for databases made with `CREATE DATABASE`; restored or
  attached databases need `ALTER DATABASE ... SET ENABLE_BROKER`.
- **The login needs `db_owner`** (or equivalent rights): `SqlDependency` creates a queue, service and procedure for each
  app process, and removes them on stop. A crashed process's objects clean themselves up within a few minutes.
- **Azure SQL Database** supports neither Service Broker nor query notifications. On-premises SQL Server and Azure SQL
  Managed Instance do. Amazon RDS is untested.
- **`QUOTED_IDENTIFIER`.** While apps are subscribed, a write to the change counter from a session with
  `QUOTED_IDENTIFIER OFF` fails with error 1934. Flagon's own writes are unaffected; hand-run scripts should use
  `sqlcmd -I` or `SET QUOTED_IDENTIFIER ON`.

When push cannot start or is lost (no broker, missing rights, database offline), the listener logs a Warning, retries
with the same backoff as PostgreSQL, refreshes once when it is back, and polling covers the gap meanwhile. Subscriptions
are renewed every 30 seconds. Notifications carry no commit time, so `flagon.push.lag` is not recorded for SQL Server.

### Flags as code

`Define` declares a flag so it exists before first use. On startup (after migrations) a missing flag is created with
`source: code`; an existing one only has its description, tags and source kept in sync. Its variants, default variant,
on/off state and targeting are applied **only when the flag is created**, so a redeploy never overwrites what an admin
chose. Archived flags are left alone.

```csharp
opts.Define("new-checkout", true);                    // boolean: { on: true, off: false }, serving on
opts.Define("theme", "light");                        // string
opts.Define("page-size", 20);                         // number (an int literal)
opts.Define("ratio", 0.5);                            // number (a double literal)
opts.DefineObject("layout", """{ "columns": 3 }""");  // object, from JSON text

// Named variants and the default variant (null: none, so the caller's default is served).
// Values are bool, string, int, double or JsonElement (for objects).
opts.Define("size", new Dictionary<string, string>
{
    ["small"] = "S", ["medium"] = "M", ["large"] = "L",
}, defaultVariant: "medium", description: "T-shirt size", tags: ["catalog"]);
```

The single-value overloads create one variant named `default`. Every overload takes the same `description`, `tags`
and `targeting` parameters. Definitions are checked when `Define` is called, with the same rules as an admin create (at
least one variant; names non-empty, at most 128 characters and unique; values of one type, no arrays or nulls; a
default variant that exists, or none; boolean variants exactly `{ on: true, off: false }`), and an invalid one
throws `FlagValidationException`.

If an existing flag's type differs from its definition (say `theme` was created as a boolean and is now defined as a
string), Flagon leaves the flag untouched, metadata included, and logs a warning (event 1106) once at startup. The host
still starts; change the definition or the flag so the types match.

## Targeting

A flag can carry a flagd [`targeting` rule](https://flagd.dev/reference/flag-definitions/#targeting-rules): JsonLogic
plus flagd's `fractional`, `sem_ver`, `starts_with` and `ends_with`. The rule is evaluated against the
`EvaluationContext` and names the variant to serve. This one turns `new-checkout` on for staff and for 10% of everyone
else, bucketed on the targeting key exactly as flagd buckets it:

```json
{
  "if": [
    { "ends_with": [ { "var": "email" }, "@example.com" ] }, "on",
    { "fractional": [ [ "on", 10 ], [ "off", 90 ] ] }
  ]
}
```

```csharp
// rule: the JSON above, as a string.
// In code: created with the rule if the flag is missing (an existing flag keeps its targeting).
opts.Define("new-checkout", defaultValue: false, targeting: rule);

// Or at runtime, through IFlagonAdmin (null removes the rule).
await admin.SetTargetingAsync("new-checkout", JsonDocument.Parse(rule).RootElement, expectedVersion: flag.Version);

// The context: a targeting key (fractional's default bucketing input) and attributes for "var".
flags.IsEnabled("new-checkout", context: new EvaluationContext("user-123", new Dictionary<string, object?>
{
    ["email"] = "ada@example.com",
}));
```

The management API takes the rule on `POST /flags` (`targeting`) and `PUT /flags/{key}/targeting` (`{ "targeting": ... }`),
and `DELETE /flags/{key}/targeting` removes it; an archived flag's targeting can't be changed until it is restored. Flags
it returns carry the raw `targeting` and, when the rule is exactly a percentage split, a `rollout` summary; any other
rule has `rollout: null`.

A percentage split is exactly `{"fractional": [[v1, w1], …, [vn, wn]]}` with at least two buckets, literal and distinct
variant names that the flag defines, whole-number weights summing to 100, and default bucketing (no explicit bucketing
expression). Its summary lists the buckets in rule order, plus `percentage` for the boolean on/off shape
`[["on", N], ["off", 100 - N]]`:

```jsonc
// a three-way split of a string flag
{ "rollout": { "buckets": [ { "variant": "light", "percentage": 34 }, { "variant": "dark", "percentage": 33 }, { "variant": "solar", "percentage": 33 } ] } }
// a 10% rollout of a boolean flag
{ "rollout": { "buckets": [ { "variant": "on", "percentage": 10 }, { "variant": "off", "percentage": 90 } ], "percentage": 10 } }
```

Anything else, however close (weights that don't add up to 100, an unknown or repeated variant, a bucketing expression,
a split nested in an `if`), is a custom rule. The UI edits a split with one weight per variant (with an even-split action
and a live check that they add up to 100) and a boolean rollout with its slider; it writes this same shape through
`PUT /flags/{key}/targeting`, and leaves custom rules to the JSON editor, never rewriting them.

OFREP and the OpenFeature provider pass the caller's context through, so all three agree with in-process `IFlagon`.
To try a rule before saving it, `POST /flags/{key}/evaluate` with `{ "context": ..., "document": <flagd flag> }`
evaluates the draft exactly as `IFlagon` would once saved; a draft rule that does not compile gets the same `400` as a write.

| The rule... | Result |
|---|---|
| is absent or `{}` | the default variant, reason `STATIC` |
| names a variant | that variant, reason `TARGETING_MATCH` (including `fractional`; flagd never reports `SPLIT`) |
| returns `null` (no match) | the default variant, reason `DEFAULT`; with no `defaultVariant`, the caller's default |
| names a variant the flag doesn't have | the caller's default, `ERROR` / `GENERAL` |
| cannot compile, or fails while evaluating (e.g. `cat` on a boolean) | the caller's default, `ERROR` / `PARSE_ERROR` |

A disabled or archived flag never runs its rule. Each rule is compiled once when the snapshot loads, not per
evaluation, and common rules evaluate without allocating. A rule that cannot compile breaks only its own flag and is
logged once per snapshot version.

**Validated on write.** `IFlagonAdmin`, the management API and `Define` compile the rule with the same engine before
anything is stored. A rule that does not compile throws `FlagTargetingException` (a `FlagValidationException`) naming
the failing operator and a JSON Pointer to it. `Define` checks only the JSON when called; its rule is compiled when the
host starts, and one that does not compile fails startup with `FlagTargetingException`. The management API answers `400` problem details titled
`Invalid targeting rule` with `key`, `operator` and `path` (e.g. `/if/0`) extensions.

**`$ref` and `$evaluators`.** In flagd, `$evaluators` belong to a flag-set file and `{"$ref": "name"}` is replaced with
the named rule when the file loads. Flagon stores each flag on its own, so the named rules live as
[segments](#segments) instead, and `$ref` works exactly as in flagd.

**flagd parity.** Flagon follows flagd 0.18 rather than generic JsonLogic: only the operators in flagd's targeting
schema, Go JsonLogic's coercions and truthiness, `targetingKey`, `$flagd.flagKey` and `$flagd.timestamp` in the context,
and flagd's `fractional` (v3) bucketing. The vendored [flagd-testbed](https://github.com/open-feature/flagd-testbed)
evaluation suite passes in full, every flag type through its typed getter (TYPE_MISMATCH and zero values included).
The flags Flagon creates have the variants `on` and `off`.
See [ADR 0001](docs/adr/0001-targeting-engine.md) for the engine choice.

### Segments

A segment is a named, reusable targeting condition ("beta-testers", "internal-staff", "EU users"). Define the audience
once and reference it from any flag's rule with flagd's own syntax, `{"$ref": "<segment-key>"}`; change the segment and
every flag that uses it follows on its next refresh.

```csharp
// In code: seeded before flags, so a defined flag can reference it on an empty store.
builder.Services.AddFlagon(opts => opts
    .DefineSegment("internal-staff", """{"ends_with": [{"var": "email"}, "@example.com"]}""", description: "Employees")
    .Define("new-checkout", targeting: """{"if": [{"$ref": "internal-staff"}, "on", null]}"""));

// Or through IFlagonAdmin (and the management API and UI).
await admin.CreateSegmentAsync(new CreateSegmentRequest("beta-testers",
    JsonDocument.Parse("""{"in": [{"var": "targetingKey"}, ["user-1", "user-2"]]}""").RootElement));
await admin.SetTargetingAsync("new-checkout",
    JsonDocument.Parse("""{"if": [{"or": [{"$ref": "internal-staff"}, {"$ref": "beta-testers"}]}, "on", null]}""").RootElement, version);
```

- **flagd semantics.** Each `{"$ref": "k"}` object, at any depth, is replaced by segment `k`'s rule before the flag's rule
  is compiled, as flagd substitutes `$evaluators`. A flag using a segment evaluates, and allocates, exactly like its
  inlined equivalent; a match is reported as `TARGETING_MATCH`, and remote clients never see segments.
- **Rules.** A segment's rule is any rule the engine compiles on its own, conventionally a boolean condition. It may not
  contain `$ref` (segments don't nest; compose them in the flag's rule with `or`/`and`). Segment keys follow the flag key
  rules in a namespace of their own. A **list** segment is just the rule `{"in": [{"var": "targetingKey"}, [...]]}`, which
  the console edits as a list of keys (one per line); every evaluation scans the list, so prefer a rule on an attribute
  for thousands of keys.
- **Checked on write, against the store.** A reference to a segment that does not exist is a `FlagTargetingException`
  (operator `$ref`, path to the reference; `400 Invalid targeting rule`). A flag cannot newly reference an archived
  segment (`SegmentArchivedException`, `400 Segment archived`). A segment rule that is rejected, or that would stop a
  flag referencing it from compiling, is a `SegmentRuleException` (`400 Invalid segment rule`).
- **Archive and purge.** An archived segment keeps resolving, so archiving never changes evaluation, but it can't be
  newly referenced or edited until restored. Archiving is refused while a non-archived flag references the segment, and
  purging while any flag does, archived ones included (`SegmentInUseException`, `409 Segment in use` with the `flags`).
  A flag whose rule references archived or missing segments can't be restored until they are.
- **The purge race.** The in-use checks run just before the write and are not atomic with a concurrent flag write. In
  the worst case a flag ends up referencing a purged segment: it serves the caller's default with `PARSE_ERROR`, logs
  once per snapshot (event 1103) and shows as broken until it is edited.
- **`DefineSegment`** follows `Define`: a missing segment is created with source `code`; an existing one gets its
  description, tags and source synced but its rule is never overwritten. A defined segment whose rule does not compile,
  or a defined flag referencing a segment that does not exist, fails startup.
- **Stores.** Segments need a store implementing `ISegmentStore` (in-memory, PostgreSQL and SQL Server do). With any
  other store the segment admin calls throw `NotSupportedException`, the management API answers `501` for its segment
  routes, the console hides segments, and a `$ref` is rejected.
- **Upgrading.** Segments arrive with schema migration 2, which only adds tables, so it is safe for a rolling deploy.
  Upgrade every app before anyone writes a `$ref`: an instance still on the previous release can't resolve it and serves
  that flag's callers their default with `PARSE_ERROR`.

## Storage

Without a storage call Flagon uses an in-memory store, which is ideal for trying it out and for tests. For real use,
install a provider and pass the name of a connection string in `ConnectionStrings`, or a connection string itself. A
configured name wins; otherwise a value containing `=` is used as the connection string; anything else fails with a
clear error when the store is created. Both extensions live in the `Jorrk.Flagon` namespace.

```csharp
using Jorrk.Flagon;

// PostgreSQL (Jorrk.Flagon.Storage.Postgres)
builder.Services.AddFlagon(opts => opts.UsePostgresStorage("Flags"));                      // ConnectionStrings:Flags

// SQL Server (Jorrk.Flagon.Storage.SqlServer)
builder.Services.AddFlagon(opts => opts.UseSqlServerStorage("Flags", schema: "flagon"));
builder.Services.AddFlagon(opts => opts.UseSqlServerStorage("Flags", push: true));        // opt-in push (see above)
```

Flagon's tables live in their own schema (default `flagon`). Apps that point at the same database and schema share
flags; use different schemas to keep them apart.

Tables are created and upgraded on startup, serialised across instances by a database lock. To manage the schema
yourself, set `opts.AutoMigrate = false` and export the versioned scripts. Each migration runs in its own transaction
together with its `schema_version` row, so a run that fails part way leaves nothing behind and can simply be repeated:

```csharp
using Jorrk.Flagon.Storage.Postgres;
using Jorrk.Flagon.Storage.SqlServer;

string sqlServerScript = SqlServerMigrations.GenerateScript("flagon");           // idempotent, GO-separated
foreach (var script in PostgresFlagStore.GetMigrationScripts("flagon"))
{
    File.WriteAllText($"{script.Version:D4}_{script.Name}.sql", script.Sql);
}
```

To write your own provider, implement `Jorrk.Flagon.Storage.IFlagStore` and run the `Jorrk.Flagon.Storage.Conformance` suite
against it by deriving a test class from `FlagStoreConformanceTests`. Optional capabilities are separate interfaces that
the suite checks when a store implements them: `IFlagChangeNotifier` (push) and `ISegmentStore` (segments, on the same
change sequence as flags, with a consistent `LoadAllWithSegmentsAsync`).

## Endpoints, UI and authorization

`Jorrk.Flagon.AspNetCore` and `Jorrk.Flagon.Ui` add HTTP endpoints. Each `Map*` call returns a convention builder, so
`.RequireAuthorization(...)` composes as usual.

```csharp
builder.Services.AddAuthorization(o => o.AddPolicy("flag-admins", p => p.RequireRole("flag-admin")));
builder.Services.AddHealthChecks().AddFlagon();

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapFlagonUi("/flags").RequireAuthorization("flag-admins");   // console at /flags
app.MapFlagonApi().RequireAuthorization("flag-admins");          // optional: API for your own tools at /flagon/api
app.MapFlagonOfrep();                                            // POST /ofrep/v1/evaluate/flags[/{key}]
app.MapHealthChecks("/health");
```

- **Management UI** (`MapFlagonUi(path)`, default `/flagon`): list, search and filter flags (including by type); create
  boolean, string, number and object flags; toggle, enable or disable, edit, archive, restore and purge them; edit a typed
  flag's variants and pick its default variant; split traffic across variants by percentage (or roll a boolean flag out
  with a slider) without writing JsonLogic; manage segments (a key list or a JSON rule, tested against a context, with
  the flags that use each) and target them from a flag with a picker; view audit history (summarising variant, default, state and split changes),
  last-evaluated time and code default (as typed JSON) per app, and default-mismatch warnings. It is plain
  HTML/JavaScript embedded in the package and mounts a private instance of the management API for itself at
  `{path}/_api`, so it never collides with `MapFlagonApi()`.
- **Management API** (`MapFlagonApi(path)`, default `/flagon/api`): map it for your own tooling, with or without the UI
  (`GET/POST /flags`, `GET/PATCH/DELETE /flags/{key}`, `PUT /flags/{key}/enabled`, `PUT /flags/{key}/state`, `PUT/DELETE /flags/{key}/targeting`,
  `PUT /flags/{key}/variants`, `PUT /flags/{key}/default-variant`, `POST /flags/{key}/archive|restore`, `GET /flags/{key}/audit`). Writes need the flag's current version (`If-Match` header or `version` field); a stale
  version gets `409 Conflict`. `POST /flags` creates a boolean flag from `enabled`, or a string, number or object flag
  from `variants` and `defaultVariant` (see below). Flags are returned with their `type` (`boolean`, `string`,
  `number` or `object`), `variants`, `value` (the default variant's value, or `null`) and `enabled` (the on/off
  toggle; `null` for non-boolean flags) and `state` (`enabled` or `disabled`); `GET /flags?type=string` filters by
  type. `PUT /flags/{key}/enabled` (`{ "enabled": true }`) is the boolean on/off toggle and answers `400` for other
  types; `PUT /flags/{key}/state` (`{ "state": "disabled" }`) is the kill switch for a flag of any type. `POST /flags/{key}/evaluate`
  (`{ "context": {...}, "document"?: {...} }`) previews the stored flag, or an unsaved flagd flag document, against a
  context without side effects (no usage, auto-create, metric or store write) and returns
  `{ value, variant, reason, errorCode?, errorMessage? }`, where `value` is the served variant's JSON value of any type
  (`null` when no variant is served). Segments have their own routes (`GET/POST /segments`, `GET/PATCH/DELETE
  /segments/{key}`, `PUT /segments/{key}/rule`, `POST /segments/{key}/archive|restore`, `GET /segments/{key}/audit`,
  `POST /segments/{key}/evaluate`), answered with `501` when the store has none; flags carry the `segments` their rule
  references, and `GET /flags?segment=beta-testers` filters by one.
- **Safe by default:** the UI and the management API answer `404` outside the `Development` environment unless you add
  authorization to them. Changes made through them are recorded in the audit log under the signed-in user's name.
- **OFREP** (`MapFlagonOfrep(prefix)`) is public unless you call `.RequireAuthorization(...)` on it. It evaluates with
  the context in the request body only, and remote evaluations never auto-create flags or count as the app's usage.
- **Health check:** `AddHealthChecks().AddFlagon()` reports `Degraded` when the store is unreachable or the snapshot is stale.

To manage flags from your own code, inject `IFlagonAdmin`:

```csharp
var flag = await admin.CreateAsync(new CreateFlagRequest("dark-mode", Enabled: false, Description: "Dark theme"));
using (FlagonActor.Use("release-bot"))   // who the audit log attributes the change to
{
    flag = await admin.SetEnabledAsync("dark-mode", enabled: true, expectedVersion: flag.Version);
}
```

A flag with named variants takes its type from their values (string, number or object), and that type never changes:

```csharp
var theme = await admin.CreateAsync(new CreateFlagRequest("theme",
    Variants: new Dictionary<string, JsonElement>
    {
        ["light"] = JsonSerializer.SerializeToElement("light"),
        ["dark"] = JsonSerializer.SerializeToElement("dark"),
    },
    DefaultVariant: "light"));
```

```http
POST /flagon/api/flags
{ "key": "page-size", "variants": { "small": 10, "large": 50 }, "defaultVariant": "small" }
```

Variants are validated on write (`FlagValidationException` in-process, `400` problem details over HTTP): at least one;
names non-empty, at most 128 characters and unique; values all of one type (booleans, strings, numbers or JSON
objects, never arrays or `null`); `defaultVariant` one of them or `null` (no default variant: callers get their own
default when no rule matches). Boolean variants are fixed: they must be exactly `{ "on": true, "off": false }`.
Variants can't be combined with `enabled: true`.

Any flag can be disabled, and enabled again. A disabled flag serves each app its own code default with reason
`DISABLED` (OFREP answers with no `value`, so remote clients use theirs too); its variants, default variant and
targeting are kept. `SetEnabledAsync` stays the boolean on/off toggle and throws `FlagValidationException` for other
types. Both are version-checked, refuse archived flags and are audited as `Updated`:

```csharp
flag = await admin.SetStateAsync("theme", FlagState.Disabled, expectedVersion: flag.Version);
```

```http
PUT /flagon/api/flags/theme/state
{ "state": "disabled", "version": 3 }
```

A string, number or object flag's variants can be edited later: `SetVariantsAsync` (`PUT /flags/{key}/variants` with
`{ "variants": {...}, "defaultVariant": ..., "version": N }`) replaces the variants and the default variant in one write.
The same rules apply, and the values must keep the flag's type. The current default variant can only be removed by
choosing another default (or `null`) in the same edit, and a variant that is a bucket of the flag's percentage split
can't be removed until the split changes (`400`). Renaming a variant is a remove plus an add. Custom targeting rules are
not checked for removed names; a rule that returns one fails at evaluation (each app gets its code default), and the UI
warns when the rule mentions it. A boolean flag's `on`/`off` variants are fixed. `SetDefaultVariantAsync`
(`PUT /flags/{key}/default-variant` with `{ "variant": "dark" }`, or `null` for none) switches the served variant for a
flag of any type; for a boolean flag only `on` or `off` is accepted (not `null`), so it is the same as toggling it.
Archived flags reject both. Both writes are
version-checked (`409` on a stale version) and audited as `Updated` with the documents before and after.

```csharp
theme = await admin.SetVariantsAsync("theme",
    new Dictionary<string, JsonElement>
    {
        ["light"] = JsonSerializer.SerializeToElement("light"),
        ["solar"] = JsonSerializer.SerializeToElement("solarized"),
    },
    defaultVariant: "solar", expectedVersion: theme.Version);
theme = await admin.SetDefaultVariantAsync("theme", "light", theme.Version);
```

## OpenFeature

`Jorrk.Flagon.OpenFeature` plugs Flagon into the [OpenFeature](https://openfeature.dev) .NET SDK, so your code can use the
vendor-neutral `IFeatureClient` (and any OpenFeature hooks you already have):

```csharp
using OpenFeature;
using OpenFeature.Hosting;
using OpenFeature.Model;

builder.Services.AddFlagon(opts => opts.UsePostgresStorage("Flags"));
builder.Services.AddOpenFeature(of => of.AddFlagon());

// ...
app.MapGet("/checkout", async (IFeatureClient features) =>
    await features.GetBooleanValueAsync("new-checkout", false) ? "New checkout" : "Old checkout");

app.MapGet("/settings", async (IFeatureClient features) => new
{
    Theme = await features.GetStringValueAsync("theme", "light"),
    PageSize = await features.GetIntegerValueAsync("page-size", 20),
    Ratio = await features.GetDoubleValueAsync("ratio", 0.5),
    Layout = (await features.GetObjectValueAsync("layout", new Value(Structure.Empty))).AsStructure, // a JSON object flag
});
```

Every OpenFeature type resolves through Flagon's typed getters, with the same rules: a flag of another type returns
your default with `ERROR` / `TYPE_MISMATCH`, and integer resolution accepts only a whole number within `int` range (as
`GetInt`), so `2.5` is a mismatch, never truncated. Structures map to and from JSON objects (numbers become doubles).

## Other languages and the browser (OFREP)

`app.MapFlagonOfrep()` exposes the [OpenFeature Remote Evaluation Protocol](https://openfeature.dev/specification/appendix-c),
so existing OpenFeature SDKs can read your flags without a Flagon-specific client. Point an OFREP provider at the app's
base URL (plus the prefix, if you passed one):

```ts
// Browser: @openfeature/web-sdk + @openfeature/ofrep-web-provider
import { OpenFeature } from '@openfeature/web-sdk';
import { OFREPWebProvider } from '@openfeature/ofrep-web-provider';

await OpenFeature.setProviderAndWait(new OFREPWebProvider({ baseUrl: 'https://my-app.example.com' }));
const enabled = OpenFeature.getClient().getBooleanValue('new-checkout', false);
```

```sh
curl -X POST https://my-app.example.com/ofrep/v1/evaluate/flags/new-checkout \
     -H 'Content-Type: application/json' -d '{"context":{"targetingKey":"user-123"}}'
```

Responses carry the served variant's value of any type (a boolean, string, number or JSON object). OFREP requests
don't say which type the client wants, so Flagon never reports `TYPE_MISMATCH` over OFREP: the client SDK checks the
type, as with flagd. Disabled flags, and flags with no default variant when no rule matches, come back without a
value, which tells the SDK to use the code default.

The bulk endpoint (`POST /ofrep/v1/evaluate/flags`) supports `ETag`/`If-None-Match`, so polling clients only download
flags when something has changed. The ETag tracks flag changes, not the context: a client that changes its context
should drop its ETag.

## Observability

- Metrics and traces on a `Meter` and `ActivitySource` named `Flagon` (`FlagonDiagnostics.MeterName`), e.g. the
  `flagon.evaluations` counter tagged by flag key, variant and reason (never by targeting key or attribute, so its
  cardinality does not grow with your users).
- Structured logs for refresh failures, migrations, auto-creation and version conflicts.

## Testing

`Jorrk.Flagon.Testing` gives tests a real `IFlagon`: the same snapshot, targeting and evaluation as production, over an
in-memory store. It works with any test framework and needs no host or DI.

```sh
dotnet add package Jorrk.Flagon.Testing
```

```csharp
using Jorrk.Flagon.Testing;

[Fact]
public void Checkout_uses_the_new_flow_when_the_flag_is_on()
{
    using var flags = TestFlagon.Create();
    flags.Flags.SetFlag("new-checkout", true);

    var checkout = new CheckoutService(flags); // TestFlagon is an IFlagon

    Assert.Equal("v2", checkout.Flow());
}
```

Put any flag of any type into any state in one line. Each call returns `Flags`, so they chain:

```csharp
flags.Flags
    .SetFlag("checkout-theme", "dark")                       // string, int and double too
    .SetFlag("layout", new Layout(Columns: 3))               // an object (or SetObjectFlag("layout", json))
    .SetFlag("page-size",                                    // named variants, as Define<T>
        new Dictionary<string, int> { ["small"] = 10, ["large"] = 50 }, defaultVariant: "small",
        targeting: """{"if":[{"==":[{"var":"plan"},"pro"]},"large",null]}""")
    .EnableFor("beta", "user-1", "user-2")                   // on only for these targeting keys
    .Disable("legacy-export");                               // also Enable, Archive, Remove, SetTargeting

flags.Context = new EvaluationContext("user-1");             // ambient context for calls that pass none
Assert.True(flags.IsEnabled("beta"));

flags.Flags.Reset();                                         // back to the seeded Define()s and an empty evaluation log
```

- Writes are upserts (a flag can be replaced by one of another type or state) and use the same write-time rules as
  `IFlagonAdmin`: an invalid key or variant set throws `FlagValidationException`, and a targeting rule that does not
  compile throws `FlagTargetingException`, from the call itself. Targeting, `fractional` rollouts and `TYPE_MISMATCH`
  behave exactly as in production.
- Every `Flags` write is visible to the very next evaluation (no refresh delay) and leaves no audit entry. Writes are
  synchronous and safe inside test frameworks' synchronization contexts.
- A key you never set returns the caller's default with reason `ERROR` and `FLAG_NOT_FOUND`, as in production.
- Pass your app's own configure delegate (`TestFlagon.Create(MyApp.ConfigureFlagon)`) to seed the flags it `Define`s
  and use its `ApplicationName`. Any storage choice in it (`UseSqlServerStorage`, ...) is ignored: no database is touched.
- Test-safe defaults: auto-create and usage tracking are off, and startup fails fast. Change them with the second
  argument, e.g. `TestFlagon.Create(testing: t => t.SeedDefinitions = false)` to start empty.
- `flags.Admin` is the `IFlagonAdmin` over the same store, for code under test that manages flags (segments included). Segments
  declared with `DefineSegment` are seeded like `Define`d flags, test flags may reference segments with `$ref`, and
  `Reset()` removes every segment and reseeds the declared ones.

`flags.Evaluations` records every evaluation, so a test can assert which flags the code evaluated, for whom, and what
it got. Each entry has the key, requested type, value, variant, reason, error, effective context (explicit or ambient),
source (`InProcess` or `Remote` for OFREP) and time. They are plain .NET types, so any assertion library works:

```csharp
new CheckoutService(flags).Flow(userId: "42");

var checkout = Assert.Single(flags.Evaluations.For("new-checkout"));
Assert.Equal("42", checkout.Context.TargetingKey);
Assert.Equal("on", checkout.Variant);
Assert.False(flags.Evaluations.WasEvaluated("legacy-checkout"));
```

The log is thread-safe and in evaluation order. `flags.Evaluations.Clear()` empties it, as `Reset()` does. Management
previews (draft evaluate) are not recorded. Turn recording off with `testing: t => t.RecordEvaluations = false`.

### Integration tests

For a running app (e.g. with `WebApplicationFactory`), call `AddFlagonTesting()` after the app's own `AddFlagon`. It
replaces the app's store with the in-memory test store, so its SQL Server or Postgres connection is never opened:

```csharp
public class AppFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder b) =>
        b.ConfigureTestServices(s => s.AddFlagonTesting());
}

// in a test
var flags = factory.Services.GetRequiredService<TestFlags>();
flags.SetFlag("new-checkout", true);
```

- The app's in-process evaluation, OFREP endpoints and management API see each `TestFlags` write on the next request.
- `flags.Evaluations` records the app's in-process evaluations and its OFREP requests (source `Remote`).
- The app's `Define`s, `ApplicationName`, ambient context (`AddFlagonAmbientContext()`), actor accessor and refresh
  interval are kept, so per-user targeting works through real requests.
- The same test-safe defaults apply; change them with `AddFlagonTesting(t => t.UsageTracking = true)`. Recorded usage
  is written when the host stops.
- It throws if `AddFlagon` hasn't been registered, and calling it twice is harmless.

## Roadmap

Flagon evaluates boolean, string, number and object flags, with flagd targeting rules, `fractional` rollouts and
N-variant percentage splits. The flagd document format already accommodates what comes next, in roughly this order:

1. Redis store, cache decorator and push-based invalidation
2. MongoDB, MySQL and SQLite stores
3. Scheduled changes and flag expiry
4. Kill-switch / prerequisite flags, approval workflows and webhooks
5. Roslyn analyzer with source-generated keys; flagd-format import/export (GitOps)
6. Direct-database clients for other languages; experimentation exposure events

## Contributing and releasing

Build and test with `dotnet test` (the PostgreSQL and SQL Server tests need Docker). Versions come from git tags via
MinVer; see [docs/releasing.md](https://github.com/jordanwallwork/flagon/blob/main/docs/releasing.md) for how packages
are published.

## Licence

[MIT](https://github.com/jordanwallwork/flagon/blob/main/LICENSE)
