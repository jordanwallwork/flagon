-- Flagon PostgreSQL schema, version 1.
-- Every flag write takes its version from change_sequence; GetChangeVersion = MAX(version) over flags and purges.

CREATE SEQUENCE IF NOT EXISTS "__schema__".change_sequence AS bigint;

CREATE TABLE IF NOT EXISTS "__schema__".flags (
    key         text COLLATE "C" NOT NULL PRIMARY KEY,
    document    text             NOT NULL,
    version     bigint           NOT NULL,
    archived    boolean          NOT NULL DEFAULT false,
    updated_at  timestamptz      NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_flags_version ON "__schema__".flags (version);

-- One row per purge so that deletes are observed through the change version.
CREATE TABLE IF NOT EXISTS "__schema__".purges (
    version    bigint           NOT NULL PRIMARY KEY,
    key        text COLLATE "C" NOT NULL,
    purged_at  timestamptz      NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS "__schema__".audit (
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

CREATE INDEX IF NOT EXISTS ix_audit_key ON "__schema__".audit (key, id);

CREATE TABLE IF NOT EXISTS "__schema__".usage (
    key                text COLLATE "C" NOT NULL,
    application_name   text COLLATE "C" NOT NULL,
    last_evaluated_at  timestamptz      NOT NULL,
    observed_default   text             NULL,
    PRIMARY KEY (key, application_name)
);
