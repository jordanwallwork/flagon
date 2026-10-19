-- Flagon schema v2: segments. $schema$ is replaced with the configured schema name.
-- Segment writes increment the same [change_sequence] counter as flag writes (so push needs no change). Additive only:
-- apps on v1 ignore these tables.

-- One row per segment; the document is {"rule": ..., "metadata": {...}}. Keys use a binary collation so they are case-sensitive.
CREATE TABLE [$schema$].[segments] (
    [segment_key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    [document] nvarchar(max) NOT NULL,
    [version] bigint NOT NULL,
    [archived] bit NOT NULL,
    [updated_at] datetimeoffset(7) NOT NULL
);
CREATE INDEX [IX_segments_version] ON [$schema$].[segments] ([version]);

-- Append-only segment audit log, apart from flag audit; survives purges.
CREATE TABLE [$schema$].[segment_audit] (
    [id] bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    [segment_key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [action] nvarchar(16) NOT NULL,
    [version] bigint NOT NULL,
    [timestamp] datetimeoffset(7) NOT NULL,
    [actor] nvarchar(256) NULL,
    [application_name] nvarchar(256) NULL,
    [before_document] nvarchar(max) NULL,
    [after_document] nvarchar(max) NULL
);
CREATE INDEX [IX_segment_audit_segment_key] ON [$schema$].[segment_audit] ([segment_key], [id]);
