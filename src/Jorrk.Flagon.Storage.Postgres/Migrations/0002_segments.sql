-- Flagon PostgreSQL schema, version 2: segments.
-- Segment writes take their version from the same change_sequence as flags; GetChangeVersion = MAX(version) over
-- flags, purges, segments and segment_purges. Additive only: apps on version 1 ignore these tables.

CREATE TABLE IF NOT EXISTS "__schema__".segments (
    key         text COLLATE "C" NOT NULL PRIMARY KEY,
    document    text             NOT NULL,
    version     bigint           NOT NULL,
    archived    boolean          NOT NULL DEFAULT false,
    updated_at  timestamptz      NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_segments_version ON "__schema__".segments (version);

-- One row per segment purge so that deletes are observed through the change version.
CREATE TABLE IF NOT EXISTS "__schema__".segment_purges (
    version    bigint           NOT NULL PRIMARY KEY,
    key        text COLLATE "C" NOT NULL,
    purged_at  timestamptz      NOT NULL DEFAULT now()
);

-- Kept apart from audit so flag audit queries are untouched; survives purges.
CREATE TABLE IF NOT EXISTS "__schema__".segment_audit (
    id                bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    key               text COLLATE "C" NOT NULL,
    action            text             NOT NULL,
    version           bigint           NOT NULL,
    "timestamp"       timestamptz      NOT NULL,
    actor             text             NULL,
    application_name  text             NULL,
    before_document   text             NULL,
    after_document    text             NULL
);

CREATE INDEX IF NOT EXISTS ix_segment_audit_key ON "__schema__".segment_audit (key, id);
