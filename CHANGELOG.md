# Changelog

Notable changes to the Flagon packages. All packages share one version (see [docs/releasing.md](docs/releasing.md)).

## Unreleased

### Added

- **Segments** (#39): named, reusable targeting conditions that flags reference with flagd's `{"$ref": "<segment-key>"}`,
  inlined before compilation exactly as flagd inlines a flag set's `$evaluators`. Evaluation and the hot path are
  unchanged: a flag using a segment compiles to the same rule as its inlined equivalent.
  - Model: `SegmentDocument`, `SegmentRecord`, `CreateSegmentRequest`, `UpdateSegmentMetadataRequest`,
    `SegmentReference`, `SegmentJson`, and the exceptions `SegmentNotFoundException`, `SegmentAlreadyExistsException`,
    `SegmentVersionConflictException`, `SegmentInUseException`, `SegmentArchivedException` and `SegmentRuleException`.
  - Storage: the optional `ISegmentStore` capability (with `FlagStoreContents` and `SegmentAuditEntry`), implemented by
    the in-memory, PostgreSQL and SQL Server stores. Segment writes share the flags' change sequence (and push), and the
    snapshot loads flags and segments in one consistent read.
  - Management: segment CRUD, archive/restore/purge, audit and usage on `IFlagonAdmin`; `/segments` routes on the
    management API (`501` when the store has no segments); `segments` and `segmentTargeting` on flag responses and a
    `segment` filter on `GET /flags`.
  - Console: Segments pages (list, key-list or rule editor, test against a context, used by, audit), a Segment mode on
    the targeting card, an "Insert segment reference" picker in the JSON editor, and a segment badge and filter on the
    flag list.
  - Code: `FlagonOptions.DefineSegment`, seeded before flags; `Define` targeting may use `$ref`.
  - Testing: `TestFlagon` supports segments through `Admin` and `DefineSegment`; the conformance suite gains segment
    cases (skipped for stores without `ISegmentStore`).

### Changed

- **Breaking:** `IFlagonAdmin` gains the segment members (`SupportsSegments`, `ListSegmentsAsync`, …). Custom
  implementations of the interface must add them (pre-1.0).
- A flag rule referencing a segment that does not exist is rejected with `FlagTargetingException` (operator `$ref`,
  message `segment '<key>' does not exist`). With a store that does not support segments, `$ref` is still rejected, now
  saying so.
- A `Define`d flag referencing a segment that does not exist, or a `DefineSegment` whose rule does not compile, fails
  startup whatever `FailFastOnStartup` says (like a `Define` rule that does not compile).
- The SQL Server store now takes a write's version before deleting a purged flag's rows (every write increments the
  change counter before changing a row), which the consistent load relies on.

### Migrations

- PostgreSQL and SQL Server schema version 2 (`0002_segments`) adds the `segments` and `segment_audit` tables (and, for
  PostgreSQL, `segment_purges`). It only adds tables, so it is safe for a rolling deploy, but upgrade every app before
  anyone writes a `$ref`: an app on the previous release serves `PARSE_ERROR` for a flag that uses one. With
  `AutoMigrate` off, run the new script before deploying (`SqlServerMigrations.GenerateScript`,
  `PostgresFlagStore.GetMigrationScripts`).
