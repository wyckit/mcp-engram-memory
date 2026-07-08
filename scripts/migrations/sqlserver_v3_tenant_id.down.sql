-- ============================================================================
-- Engram SQL Server storage — schema migration v3 -> v2 (REVERSE / DOWN)
-- Removes first-class tenant isolation.
--
-- Drops: idx_entries_tenant_ns_state, PK (tenant_id, ns, id),
--        DF_engram_entries_tenant, entries.tenant_id
-- Repks: PRIMARY KEY (ns, id)
--
-- +--------------------------------------------------------------------------+
-- |  DATA-LOSS / SAFETY WARNING                                               |
-- |  This reversal DROPS the tenant_id column. It is only lossless when every |
-- |  row is in the legacy '' tenant (single-tenant install). If more than one |
-- |  tenant exists, (ns, id) is no longer unique and re-adding the old PK     |
-- |  will FAIL — resolve/merge tenant data before running. The pre-flight     |
-- |  check below aborts rather than silently corrupt data.                    |
-- +--------------------------------------------------------------------------+
--
-- Usage (sqlcmd):
--   sqlcmd -S <server> -d <db> -v schema="dbo" -i sqlserver_v3_tenant_id.down.sql
-- ============================================================================
:setvar schema "dbo"
SET XACT_ABORT ON;
SET NOCOUNT ON;

-- Pre-flight: refuse to reverse if real (non-empty) tenants exist.
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'$(schema).entries') AND name = 'tenant_id')
   AND EXISTS (SELECT 1 FROM [$(schema)].entries WHERE tenant_id <> '')
BEGIN
    RAISERROR('Cannot reverse v3->v2: non-empty tenant_id values exist. Migrate/merge tenant data first.', 16, 1);
    RETURN;
END

BEGIN TRANSACTION;

-- 1. Drop the tenant-aware index.
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'idx_entries_tenant_ns_state'
           AND object_id = OBJECT_ID(N'$(schema).entries'))
    DROP INDEX idx_entries_tenant_ns_state ON [$(schema)].entries;

-- 2. Drop the tenant-rooted primary key.
IF EXISTS (SELECT 1 FROM sys.key_constraints
           WHERE name = 'PK_engram_entries'
             AND parent_object_id = OBJECT_ID(N'$(schema).entries'))
    ALTER TABLE [$(schema)].entries DROP CONSTRAINT PK_engram_entries;

-- 3. Drop the default constraint then the tenant column.
IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = 'DF_engram_entries_tenant'
           AND parent_object_id = OBJECT_ID(N'$(schema).entries'))
    ALTER TABLE [$(schema)].entries DROP CONSTRAINT DF_engram_entries_tenant;

IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID(N'$(schema).entries') AND name = 'tenant_id')
    ALTER TABLE [$(schema)].entries DROP COLUMN tenant_id;

-- 4. Restore the original (ns, id) primary key.
IF NOT EXISTS (SELECT 1 FROM sys.key_constraints
               WHERE name = 'PK_engram_entries'
                 AND parent_object_id = OBJECT_ID(N'$(schema).entries'))
    ALTER TABLE [$(schema)].entries
        ADD CONSTRAINT PK_engram_entries PRIMARY KEY (ns, id);

-- 5. Roll the schema version back to 2.
IF EXISTS (SELECT 1 FROM [$(schema)].schema_version)
    UPDATE [$(schema)].schema_version SET version = 2;

COMMIT TRANSACTION;
