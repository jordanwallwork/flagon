# SQL Server push invalidation: opt-in via SqlDependency, polling stays the default

Status: accepted (#67, spec #38 §4). Implemented in #72.

Spec #38 adds an optional `IFlagChangeNotifier` seam so stores can push "the version moved" signals. Postgres
does this with `LISTEN`/`NOTIFY` (spec #38 §3). This ADR asks whether `SqlServerFlagStore` should do the same, and how.

The first version of this ADR answered from documentation alone and recommended polling. A follow-up spike ran
`SqlDependency` against a real server. Several premises turned out wrong. Push via query notifications works well on
every platform that has Service Broker, and degrades to polling everywhere else.

## Spike setup

- SQL Server 2022 RTM-CU14 Developer on Linux (`mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04`, the image the
  test suite uses), `Microsoft.Data.SqlClient` 6.1.4, .NET 10.
- Writes went through the real `SqlServerFlagStore`, after `ApplyMigrationsAsync`.
- Subscriptions watched `SELECT [value] FROM [flagon].[change_sequence] WHERE [id] = 1`, the query
  `GetChangeVersionAsync` already runs. `NextVersionAsync` increments that row in every version-moving transaction.
- The harness was a throwaway console app, not committed. Its output is summarised below.

## Findings

### Query shape

| Query | Result |
|---|---|
| `SELECT [value] FROM [flagon].[change_sequence] WHERE [id] = 1` | Valid. Subscription holds until a change. |
| `SELECT MAX([version]) FROM [flagon].[flags]` | Rejected immediately: `Subscribe / Invalid / Statement`. |
| `SELECT * FROM [flagon].[change_sequence] WHERE [id] = 1` | Rejected immediately: `Subscribe / Invalid / Statement`. |

The store already reads the single-row counter, not `MAX(version)`, so no query change is needed. Table names must be
two-part ([Working with Query Notifications](https://learn.microsoft.com/en-us/sql/relational-databases/native-client/features/working-with-query-notifications)).

### Which writes signal

| Write | Version | Notification |
|---|---|---|
| create, update, archive, restore, purge | moves | `Change / Update / Data` |
| rejected: existing key, version conflict | unchanged | none |
| usage upsert, `LoadAll` | unchanged | none |
| raw SQL insert into `flags` without bumping the counter | unchanged | none (polling does not see it either) |
| raw SQL `UPDATE change_sequence` | moves | `Change / Update / Data` |

This matches the conformance contract (spec #38 §6) exactly. The notification fires at commit, and any writer that bumps
the counter is pushed, including other app instances.

### Latency and bursts

- Commit to `OnChange`, 30 writes: p50 2.5 ms, p95 6.6 ms, max 9.8 ms.
- 50 concurrent writes with a "notify, resubscribe, read" loop: 8 notifications, and the loop read the final version
  (v85) 465 ms after the burst started. Notifications coalesce naturally, because a subscription fires once and the next
  read picks up everything committed meanwhile. This is the convergence the conformance suite asserts.

### Resilience

- Killing the `SqlDependency` listener session (`KILL`): the armed subscription still fired, and new subscriptions
  worked. `SqlClient` re-establishes its `WAITFOR (RECEIVE …)` listener by itself.
- Database `OFFLINE` / `ONLINE` while subscribed: `Change / Error / Client`. The notifier treats this as "resubscribe and
  refresh", the same as a Postgres reconnect.
- `READ_COMMITTED_SNAPSHOT ON`: subscriptions remain valid and fire. Only `SNAPSHOT` and `READ UNCOMMITTED` are excluded.

### Broker availability

- `CREATE DATABASE` produced a database with Service Broker **enabled**, although `model` has it off. Broker is off only
  on restored or attached databases, which need `ALTER DATABASE … SET ENABLE_BROKER` (exclusive access).
- With broker off, `SqlDependency.Start` throws `InvalidOperationException` ("Service Broker … is not enabled") at
  once. The notifier can catch this, report `IsConnected = false`, and leave polling in charge.

| Platform | Service Broker / query notifications | Source |
|---|---|---|
| On-premises SQL Server | Yes. On by default for new databases. | spike |
| Azure SQL Managed Instance | Yes. Always on; `ENABLE_BROKER` not supported. | [features comparison](https://learn.microsoft.com/en-us/azure/azure-sql/database/features-comparison) (2026-09-30) |
| Azure SQL Database (all tiers) | **No** for both | same page: "Query Notifications: No", "Service Broker: No" |
| Amazon RDS for SQL Server | Broker supported, endpoints not | [AWS feature list](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/SQLServer.Concepts.General.FeatureNonSupport.html). Not tested. |

### Multiple instances and permissions (spike 2)

A second spike ran several app processes against one database.

- **A shared, pre-provisioned queue does not work.** Two processes listening on one queue each received the other's
  notification, which `SqlDependency` reports as `Change / Error / Client`, and then no further notifications arrived.
  A Service Broker queue hands each message to one reader, so a shared queue cannot fan out.
- **The default per-process queue works.** `SqlDependency.Start(connectionString)` creates a queue, service and activation
  procedure for each process. Two processes each received all three of three changes.
- **Crashed processes clean up.** After `kill -9`, the per-process queue was removed by its activation procedure within
  about two minutes.
- **Permissions: `db_owner`.** The per-process objects need more than `CREATE PROCEDURE/QUEUE/SERVICE`. With data roles,
  `db_ddladmin`, `RECEIVE` on `QueryNotificationErrorsQueue`, `REFERENCES` on the contract, `VIEW DEFINITION`, `EXECUTE`
  and `IMPERSONATE` on `dbo` granted one by one, `Start` still failed on its next step. `db_owner` worked. Apps that run
  Flagon's migrations already have DDL rights, and usually `db_owner`.
- **`QUOTED_IDENTIFIER`.** While a subscription exists on `change_sequence`, writes to it from a session with
  `QUOTED_IDENTIFIER OFF` (for example `sqlcmd` without `-I`) fail with error 1934. SqlClient sets it on, so Flagon's own
  writes are unaffected.

## Options

**A. `SqlDependency` on the counter row, per-process queue, opt-in (chosen).**
- `UseSqlServerStorage(..., push: true)` builds `SqlServerPushFlagStore`, which implements `IFlagChangeNotifier`. It
  subscribes, re-subscribes after every notification, and signals when the counter moved since its last read, which
  closes the gap between a notification and the re-subscribe. The 30-second subscription timeout renews it periodically.
- Any other notification, or a failed `Start` or subscribe, counts as a disconnect. It reconnects with the shared backoff,
  then signals a resubscribe.
- No write-path change and no migration.
- Off by default because of the `db_owner` and Service Broker requirements.

**B. Custom Service Broker queue written in the write transaction.** Same platforms as A, and a guaranteed message per
commit. A single shared queue cannot fan out to several instances (spike 2), so it would also need a queue per instance. But it means a write-path change, a reader that must `RECEIVE` and `END CONVERSATION`, and backlog if the reader
stops. A's single-row subscription already gives commit-time signals with natural coalescing. Not worth it.

**C. Polling only (status quo).** Works everywhere, with freshness bounded by `RefreshInterval` (default 10 s). This
remains the only option on Azure SQL Database, and the backstop everywhere (spec #38 §5).

## Decision

Implement A as an opt-in (#72). Polling stays the default, and the only option on Azure SQL Database.

The original reasons for rejecting push mostly did not hold:

- "Needs DBA-enabled broker": new databases have it on.
- "Notifications are fragile": the listener self-heals, and errors map onto the reconnect path.
- "MAX(version) is invalid": the store does not use it.

What remains is the `db_owner` requirement, the `QUOTED_IDENTIFIER` caveat for hand-run SQL, and Azure SQL Database
falling back to polling. These are documented in the README and are why push is opt-in.

Still untested: Managed Instance, and RDS.
