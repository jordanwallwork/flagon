-- Flagon schema v1. $schema$ is replaced with the configured schema name.

-- Single-row store-wide change sequence. Every flag write increments it inside the write's transaction, so versions
-- are assigned (and committed) in order and SELECT [value] is the store's change version.
CREATE TABLE [$schema$].[change_sequence] (
    [id] tinyint NOT NULL PRIMARY KEY CHECK ([id] = 1),
    [value] bigint NOT NULL
);
INSERT INTO [$schema$].[change_sequence] ([id], [value]) VALUES (1, 0);

-- One row per flag; the document is flagd JSON. Keys use a binary collation so they are case-sensitive.
CREATE TABLE [$schema$].[flags] (
    [flag_key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL PRIMARY KEY,
    [document] nvarchar(max) NOT NULL,
    [version] bigint NOT NULL,
    [archived] bit NOT NULL,
    [updated_at] datetimeoffset(7) NOT NULL
);

-- Append-only audit log; survives purges.
CREATE TABLE [$schema$].[audit] (
    [id] bigint IDENTITY(1, 1) NOT NULL PRIMARY KEY,
    [flag_key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [action] nvarchar(16) NOT NULL,
    [version] bigint NOT NULL,
    [timestamp] datetimeoffset(7) NOT NULL,
    [actor] nvarchar(256) NULL,
    [application_name] nvarchar(256) NULL,
    [before_document] nvarchar(max) NULL,
    [after_document] nvarchar(max) NULL
);
CREATE INDEX [IX_audit_flag_key] ON [$schema$].[audit] ([flag_key], [id]);

-- Last evaluation per (flag, application).
CREATE TABLE [$schema$].[usage] (
    [flag_key] nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [application_name] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    [last_evaluated_at] datetimeoffset(7) NOT NULL,
    [observed_default] nvarchar(max) NULL,
    PRIMARY KEY ([flag_key], [application_name])
);
