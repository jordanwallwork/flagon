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
| `Flagon` | Core engine: `AddFlagon`, `IFlagon` (evaluation), `IFlagonAdmin` (management), in-memory store |
| `Flagon.Storage.Postgres` | `UsePostgresStorage(...)` |
| `Flagon.Storage.SqlServer` | `UseSqlServerStorage(...)` |
| `Flagon.AspNetCore` | OFREP endpoints, management HTTP API, ambient evaluation context, health check |
| `Flagon.Ui` | Embeddable management console: `MapFlagonUi(path)` |
| `Flagon.OpenFeature` | OpenFeature provider for the standard `IFeatureClient` API |
| `Flagon.Storage.Conformance` | xUnit conformance suite for writing your own `IFlagStore` provider |

## Install

```sh
dotnet add package Flagon
```

Add a storage provider and the ASP.NET Core packages as you need them:

```sh
dotnet add package Flagon.Storage.Postgres     # or Flagon.Storage.SqlServer
dotnet add package Flagon.AspNetCore
dotnet add package Flagon.Ui
dotnet add package Flagon.OpenFeature
```

## Quickstart

```csharp
using Flagon;

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
// details.Value, details.Variant ("on"/"off"), details.Reason (Static, Disabled, Archived, Error, ...),
// details.ErrorCode (None, FlagNotFound, ...)
```

Every evaluation method takes an optional `EvaluationContext` (targeting key plus attributes). When you don't pass one,
Flagon uses the ambient context: with `builder.Services.AddFlagonAmbientContext()` (Flagon.AspNetCore) that is the
current request's user. A flag's flagd `targeting` rule (JsonLogic plus `fractional`, `sem_ver`, `starts_with` and
`ends_with`) is evaluated against that context, compiled once when the snapshot loads.

The first time code evaluates an unknown key, Flagon creates the flag (in the background, off the evaluation path) with
the default the code supplied, marked `source: auto`. Archived keys are never re-created.

### Options

```csharp
builder.Services.AddFlagon(opts =>
{
    opts.ApplicationName = "checkout-api";            // default: the host's application name
    opts.RefreshInterval = TimeSpan.FromSeconds(10);  // how often to poll the store for changes
    opts.AutoCreate = true;                           // create unknown keys on first evaluation
    opts.AutoMigrate = true;                          // create/upgrade Flagon's tables on startup
    opts.UsageTracking = true;                        // record last-evaluated time per flag and app
    opts.FailFastOnStartup = false;                   // true: throw at startup if the store is unreachable

    // Flags as code: created on startup if missing; description and tags are kept in sync,
    // but the on/off state an admin has chosen is never overwritten.
    opts.Define("new-checkout", defaultValue: false, description: "New checkout flow", tags: ["checkout"]);
});
```

If the store is unavailable at startup, the app still boots with code defaults and keeps retrying; if it fails later,
Flagon keeps serving the last snapshot it loaded.

## Storage

Without a storage call Flagon uses an in-memory store, which is ideal for trying it out and for tests. For real use,
install a provider and pass the name of a connection string in `ConnectionStrings`, or a connection string itself. A
configured name wins; otherwise a value containing `=` is used as the connection string; anything else fails with a
clear error when the store is created. Both extensions live in the `Flagon` namespace.

```csharp
using Flagon;

// PostgreSQL (Flagon.Storage.Postgres)
builder.Services.AddFlagon(opts => opts.UsePostgresStorage("Flags"));                      // ConnectionStrings:Flags

// SQL Server (Flagon.Storage.SqlServer)
builder.Services.AddFlagon(opts => opts.UseSqlServerStorage("Flags", schema: "flagon"));
```

Flagon's tables live in their own schema (default `flagon`). Apps that point at the same database and schema share
flags; use different schemas to keep them apart.

Tables are created and upgraded on startup, serialised across instances by a database lock. To manage the schema
yourself, set `opts.AutoMigrate = false` and export the versioned scripts. Each migration runs in its own transaction
together with its `schema_version` row, so a run that fails part way leaves nothing behind and can simply be repeated:

```csharp
using Flagon.Storage.Postgres;
using Flagon.Storage.SqlServer;

string sqlServerScript = SqlServerMigrations.GenerateScript("flagon");           // idempotent, GO-separated
foreach (var script in PostgresFlagStore.GetMigrationScripts("flagon"))
{
    File.WriteAllText($"{script.Version:D4}_{script.Name}.sql", script.Sql);
}
```

To write your own provider, implement `Flagon.Storage.IFlagStore` and run the `Flagon.Storage.Conformance` suite
against it by deriving a test class from `FlagStoreConformanceTests`.

## Endpoints, UI and authorization

`Flagon.AspNetCore` and `Flagon.Ui` add HTTP endpoints. Each `Map*` call returns a convention builder, so
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

- **Management UI** (`MapFlagonUi(path)`, default `/flagon`): list, search and filter flags; toggle, edit, archive,
  restore and purge them; view audit history, last-evaluated time per app, and default-mismatch warnings. It is plain
  HTML/JavaScript embedded in the package and mounts a private instance of the management API for itself at
  `{path}/_api`, so it never collides with `MapFlagonApi()`.
- **Management API** (`MapFlagonApi(path)`, default `/flagon/api`): map it for your own tooling, with or without the UI
  (`GET/POST /flags`, `GET/PATCH/DELETE /flags/{key}`, `PUT /flags/{key}/enabled`, `POST /flags/{key}/archive|restore`,
  `GET /flags/{key}/audit`). Writes need the flag's current version (`If-Match` header or `version` field); a stale
  version gets `409 Conflict`.
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

## OpenFeature

`Flagon.OpenFeature` plugs Flagon into the [OpenFeature](https://openfeature.dev) .NET SDK, so your code can use the
vendor-neutral `IFeatureClient` (and any OpenFeature hooks you already have):

```csharp
using OpenFeature;
using OpenFeature.Hosting;

builder.Services.AddFlagon(opts => opts.UsePostgresStorage("Flags"));
builder.Services.AddOpenFeature(of => of.AddFlagon());

// ...
app.MapGet("/checkout", async (IFeatureClient features) =>
    await features.GetBooleanValueAsync("new-checkout", false) ? "New checkout" : "Old checkout");
```

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

The bulk endpoint (`POST /ofrep/v1/evaluate/flags`) supports `ETag`/`If-None-Match`, so polling clients only download
flags when something has changed.

## Observability

- Metrics and traces on a `Meter` and `ActivitySource` named `Flagon` (`FlagonDiagnostics.MeterName`), e.g. the
  `flagon.evaluations` counter tagged by flag key, variant and reason.
- Structured logs for refresh failures, migrations, auto-creation and version conflicts.

## Roadmap

Flagon's MVP evaluates boolean flags. The flagd document format already accommodates what comes next, in roughly this order:

1. Targeting rules and `fractional` percentage rollouts
2. String, number and JSON flags
3. Redis store, cache decorator and push-based invalidation
4. MongoDB, MySQL and SQLite stores
5. `Flagon.Testing` package
6. Segments; scheduled changes and flag expiry
7. Kill-switch / prerequisite flags, approval workflows and webhooks
8. Roslyn analyzer with source-generated keys; flagd-format import/export (GitOps)
9. Direct-database clients for other languages; experimentation exposure events

## Contributing and releasing

Build and test with `dotnet test` (the PostgreSQL and SQL Server tests need Docker). Versions come from git tags via
MinVer; see [docs/releasing.md](https://github.com/jordanwallwork/flagon/blob/main/docs/releasing.md) for how packages
are published.

## Licence

[MIT](https://github.com/jordanwallwork/flagon/blob/main/LICENSE)
