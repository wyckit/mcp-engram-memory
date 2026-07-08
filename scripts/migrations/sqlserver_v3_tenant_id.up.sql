-- ============================================================================
-- Engram SQL Server storage — schema migration v2 -> v3 (FORWARD / UP)
-- First-class tenant isolation.
--
-- Adds:  entries.tenant_id NVARCHAR(64) NOT NULL DEFAULT ''
-- Repks: PRIMARY KEY (ns, id) -> PRIMARY KEY (tenant_id, ns, id)
-- Index: idx_entries_tenant_ns_state (tenant_id, ns, lifecycle_state)
--
-- This mirrors SqlServerStorageProvider.MigrateToV3 for out-of-band / DBA use.
-- Every step is guarded so the script is idempotent (safe to re-run).
--
-- Usage (sqlcmd):
--   sqlcmd -S <server> -d <db> -v schema="dbo" -i sqlserver_v3_tenant_id.up.sql
-- ============================================================================
:setvar schema "dbo"
SET XACT_ABORT ON;
SET NOCOUNT ON;

BEGIN TRANSACTION;

-- 1. Add the tenant column, defaulting existing rows to the legacy '' tenant.
IF NOT EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'$(schema).entries') AND name = 'tenant_id')
    ALTER TABLE [$(schema)].entries
        ADD tenant_id NVARCHAR(64) NOT NULL
            CONSTRAINT DF_engram_entries_tenant DEFAULT('');

-- 2. Re-root the primary key onto (tenant_id, ns, id).
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = 'PK_engram_entries'
             AND parent_object_id = OBJECT_ID(N'$(schema).entries'))
    ALTER TABLE [$(schema)].entries DROP CONSTRAINT PK_engram_entries;

IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE name = 'PK_engram_entries'
                 AND parent_object_id = OBJECT_ID(N'$(schema).entries'))
    ALTER TABLE [$(schema)].entries
        ADD CONSTRAINT PK_engram_entries PRIMARY KEY (tenant_id, ns, id);

-- 3. Tenant-aware covering index for tenant-scoped queries.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_entries_tenant_ns_state'
               AND object_id = OBJECT_ID(N'$(schema).entries'))
    CREATE INDEX idx_entries_tenant_ns_state
        ON [$(schema)].entries(tenant_id, ns, lifecycle_state);

-- 4. Record the new schema version.
IF EXISTS (SELECT 1 FROM [$(schema)].schema_version)
    UPDATE [$(schema)].schema_version SET version = 3;
ELSE
    INSERT INTO [$(schema)].schema_version (version) VALUES (3);

COMMIT TRANSACTION;
