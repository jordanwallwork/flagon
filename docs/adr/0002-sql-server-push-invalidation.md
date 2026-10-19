# SQL Server push invalidation: stay on polling

Status: proposed (#67, spec #38 §4)

Spec #38 adds an optional `IFlagChangeNotifier` seam so stores can push "the version moved" signals. Postgres
does this with `LISTEN`/`NOTIFY` (spec #38 §3). This ADR asks whether `SqlServerFlagStore` should do the same, and how.
Answer: not now. SQL Server stays on polling. Push stays available for a later spike if a deployment needs
sub-`RefreshInterval` propagation on platforms where it works.

## Query shape: `MAX(version)` is not allowed

The change-version read `MAX(version)` over `flags` and `purges` cannot be used for query notifications. The
restriction list says a statement "must not use any of the following aggregate functions: AVG, COUNT(\*), MAX, MIN,
STDEV, STDEVP, VAR, or VARP", and also forbids `TOP`, `DISTINCT`, subqueries, derived tables, unions and system views
([Creating a Query for Notification](https://learn.microsoft.com/en-us/previous-versions/sql/sql-server-2008-r2/ms181122(v=sql.105))).

The nearest valid shape is the query the store already runs:

```sql
SELECT [value] FROM [schema].[change_sequence] WHERE [id] = 1
```

(`SqlServerFlagStore.GetChangeVersionAsync`.) It names its columns, uses a two-part name on a base table, and has no
aggregate. `NextVersionAsync` increments this row in every version-moving transaction (flag writes, purges and their
audit rows), so one counter covers both tables. A notification on that row fires at commit.

Caveats:

- The restriction list comes from an archived page (SQL Server 2008 R2, marked retired). I could not retrieve the current
  Microsoft Learn version. Re-check it on a real server before any implementation.
- A query-notification statement must not run under SNAPSHOT or READ UNCOMMITTED. The store's writes and the counter read
  use READ COMMITTED, so they are compatible.
- Notifications are one-shot: "Notifications are sent only once. For continuous notification of data change, a new
  subscription must be created by re-executing the query after each notification is processed"
  ([Working with Query Notifications](https://learn.microsoft.com/en-us/sql/relational-databases/native-client/features/working-with-query-notifications)).

## Availability

| Platform | Service Broker | Query notifications / `SqlDependency` | Notes |
|---|---|---|---|
| On-premises SQL Server | Per database: `ALTER DATABASE ... SET ENABLE_BROKER`. DBA step. | Yes, if broker is enabled | Caller needs `SUBSCRIBE QUERY NOTIFICATIONS` on the database. `Start` needs `CREATE QUEUE` and `CREATE SERVICE` unless a queue is passed in ([enabling](https://learn.microsoft.com/en-us/dotnet/framework/data/adonet/sql/enabling-query-notifications), [Start](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqldependency.start?view=sqlclient-dotnet-7.1)). |
| Azure SQL Database (all tiers) | No | No | The feature comparison lists both as "No" for Azure SQL Database with no tier split ([features comparison](https://learn.microsoft.com/en-us/azure/azure-sql/database/features-comparison)). |
| Azure SQL Managed Instance | Yes. Enabled by default and cannot be disabled. `ENABLE_BROKER` and `DISABLE_BROKER` are not supported. | Yes ([features comparison](https://learn.microsoft.com/en-us/azure/azure-sql/database/features-comparison)) | Cross-instance broker is in public preview, port 4022 only, dialog security not supported ([MI T-SQL differences](https://learn.microsoft.com/en-us/azure/azure-sql/managed-instance/transact-sql-tsql-differences-sql-server), [Service Broker](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-service-broker?view=sql-server-ver17)). |
| Amazon RDS for SQL Server | Supported. Service Broker endpoints are not ([AWS feature list](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/SQLServer.Concepts.General.FeatureNonSupport.html)). Enabling is per database; AWS re:Post also mentions Availability Group limits ([re:Post](https://repost.aws/knowledge-center/rds-sql-server-enable-service-broker), search summary only, page returned 403). | Not verified | Needs a test instance before any claim. |

## Operational cost

**A. `SqlDependency` / query notifications.** DBA: enable broker on each on-premises database and grant
`SUBSCRIBE QUERY NOTIFICATIONS`. Pre-create the queue so `Start` does not create objects in the Flagon database. Schema:
no change to Flagon's tables. Connection behaviour: a listener per AppDomain, one-shot subscriptions that must be renewed
after every notification. Microsoft says `SqlDependency` was designed for ASP.NET and middle-tier services, not for large
numbers of clients ([SqlDependency](https://learn.microsoft.com/en-us/dotnet/api/microsoft.data.sqlclient.sqldependency?view=sqlclient-dotnet-7.1)).
Failure modes: notifications missed while disconnected (polling covers this). If broker is off or SET options are wrong,
the notification fires immediately, so a naive loop can refresh continuously. Subscriptions carry a timeout, so each one
must be renewed. Not available on Azure SQL Database.

**B. Service Broker queue written in the write transaction.** DBA: enable broker everywhere. Schema: a new migration adds
queue, service and message-type objects. A reader must `RECEIVE` each message and `END CONVERSATION`
([Service Broker](https://learn.microsoft.com/en-us/sql/database-engine/configure-windows/sql-server-service-broker?view=sql-server-ver17)).
Strength: the message commits or rolls back with the flag write. Failure modes: queue backlog if the reader stops, and
an extra component to monitor. Not available on Azure SQL Database. This is the most work for the least coverage.

**C. Polling only (status quo).** DBA: none. Works on all four platforms and is already implemented. Freshness is bounded
by `RefreshInterval` (default 10 s). The cost is one primary-key read per interval per instance.

## Decision

Stay on polling for SQL Server. Do not implement `IFlagChangeNotifier` for `SqlServerFlagStore`.

1. Azure SQL Database has neither mechanism. Push would be a per-platform fork, and polling must stay as the backstop
   regardless (spec #38 §5).
2. Where push works (on-premises, Managed Instance), it needs DBA-enabled broker, per-database permissions, one-shot
   renewal and a listener per AppDomain. That is the complexity the notifier seam is meant to isolate.
3. Polling costs one indexed single-row read per interval. The latency it adds is bounded and documented.

## Follow-up

An implementation issue is not warranted. Spec #38 §4 asked for a spike. This research answers the query-shape and
availability questions. A spike is warranted only if a deployment needs push on on-premises SQL Server or Managed
Instance, and RDS remains unverified. The draft below is not opened.

**Draft spike title:** Spike: verify SqlDependency push on the change_sequence counter (on-prem, Managed Instance, RDS)

**Draft spike body:**

Context: ADR 0002 (docs/adr/0002-sql-server-push-invalidation.md) recommends polling. Only proceed if a deployment needs
push on on-premises SQL Server or Managed Instance.

Questions:

1. On SQL Server 2022 and a Managed Instance, does `SqlDependency` fire on `SELECT [value] FROM [schema].[change_sequence]
   WHERE [id] = 1` after a flag write and after a purge, and not after a rejected write?
2. Does the current Microsoft Learn restriction list match the archived 2008 R2 page cited in ADR 0002?
3. Does RDS for SQL Server allow broker enablement and `SqlDependency` for a non-admin login?
4. What is the commit-to-notification latency, and does it survive a dropped connection?

Done when: each question has a recorded answer with a test log and a link to the source. No production code.
