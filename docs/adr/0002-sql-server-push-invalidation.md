# SQL Server push invalidation: feasible as an opt-in, polling stays the default

Status: proposed (#67, spec #38 §4). Decision pending: see [Options](#options).

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

### Permissions

`SqlDependency.Start(connectionString)` creates a queue, service and stored procedure per AppDomain. That needs
`CREATE PROCEDURE/QUEUE/SERVICE` plus more, and a plain data login fails with "CREATE PROCEDURE permission denied".
When a listener died mid-run, these `SqlQueryNotificationService-<guid>` objects were left behind as orphans.

The least-privilege setup that worked uses a pre-created queue and service:

```sql
CREATE QUEUE [flagon].[FlagonNotifications];
CREATE SERVICE [FlagonNotifications] ON QUEUE [flagon].[FlagonNotifications]
    ([http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification]);
-- app login (default schema = flagon):
GRANT SUBSCRIBE QUERY NOTIFICATIONS TO app;
GRANT RECEIVE ON [flagon].[FlagonNotifications] TO app;
GRANT RECEIVE ON QueryNotificationErrorsQueue TO app;
GRANT REFERENCES ON CONTRACT::[http://schemas.microsoft.com/SQL/Notifications/PostQueryNotification] TO app;
GRANT SEND ON SERVICE::[FlagonNotifications] TO app;
```

The client side uses `SqlDependency.Start(cs, "FlagonNotifications")` and `new SqlDependency(cmd,
"service=FlagonNotifications", 0)`. Two gotchas:

- `Start` brackets the queue name as one identifier, so the queue must be in the login's default schema and be passed
  unqualified.
- A plain `new SqlDependency(cmd)` does not bind to a custom-queue listener. It throws "SqlDependency.Start() must be
  called prior to execution". Explicit `service=` options are required.

When the app login owns the schema (the usual case when the app runs its own migrations), it already has these rights.

## Options

**A. `SqlDependency` on the counter row, opt-in (recommended if push is wanted).**
- A new migration creates the queue and service, guarded so it is a no-op where Service Broker DDL is unsupported
  (Azure SQL Database).
- `SqlServerFlagStore` implements `IFlagChangeNotifier`: start the listener, then subscribe, and resubscribe after every
  notification. `Info = Error` or `Invalid` means "resubscribe and refresh"; an exception on `Start` means "disconnected,
  polling covers".
- No write-path change: the counter update already exists.
- Cost: one migration, roughly a Postgres-listener-sized notifier, and the conformance cases. These already exist and map
  one-to-one onto the findings above.
- Off by default for SQL Server (for example `UseSqlServerStorage(…, push: true)`), because of the permission and
  platform caveats.

**B. Custom Service Broker queue written in the write transaction.** Same platforms as A, and a guaranteed message per
commit. But it means a write-path change, a reader that must `RECEIVE` and `END CONVERSATION`, and backlog if the reader
stops. A's single-row subscription already gives commit-time signals with natural coalescing. Not worth it.

**C. Polling only (status quo).** Works everywhere, with freshness bounded by `RefreshInterval` (default 10 s). This
remains the only option on Azure SQL Database, and the backstop everywhere (spec #38 §5).

## Recommendation

The original reasons for rejecting push mostly do not hold:

- "Needs DBA-enabled broker": new databases have it on.
- "Notifications are fragile": the listener self-heals, and errors map onto the existing reconnect path.
- "MAX(version) is invalid": the store does not use it.

What remains is the setup cost: the queue and service, explicit permissions for non-owner logins, and Azure SQL
Database falling back to polling.

Recommend A as an opt-in follow-up, if sub-`RefreshInterval` propagation on SQL Server matters to users. Otherwise C
stays acceptable: the seam is in place, and nothing here blocks adding A later.

Open before implementing A: test on Managed Instance and on RDS with a non-admin login.
