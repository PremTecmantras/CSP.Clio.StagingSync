/*
  15_Drop_Reverted_Clio_Live_Objects.sql

  Removes what the reverted "Clio_Live" design (commit 1bff6da, script 13_Create_Clio_Live.sql) left in the SQL Server instance.

  FOUND (created by the reverted code):
    in Clio_Staging  control.LiveCopyRun, control.LiveCopyTable      audit tables of the Live -> Staging copy (LiveToStagingCopier)
    in Clio_Staging  rows in control.IncrementalSyncRun              one "Copy from Clio_Live" marker row per copy (same id as control.LiveCopyRun.CopyRunID)
    the database    Clio_Live                                        a backup/restore copy of Clio_Staging made by script 13  (only dropped when you switch it on, step 4)
    a file          Clio_Live_seed_from_Clio_Staging.bak             left behind by script 13; this script only shows where it is, delete it yourself

  NOT TOUCHED (still used by the current single-database design - do not drop):
    control.DeleteRun, control.CycleRun, control.IncrementalSyncRun / Result / Watermark / DeletionCandidate, dbo.<Table>_Deleted,
    control.SyncRun, control.SyncEndpointResult, control.PublishRun and the rest of Clio_Staging.

  Run in SSMS on the SQL Server instance that holds Clio_Staging (on your PC: (localdb)\MSSQLLocalDB, and on the server), with a login that may
  drop tables (and, for step 4, databases). Stop the cycle task and any running EXE first. Safe to run twice: everything is checked before it is dropped.
  Steps 1 and 2 only read; step 3 deletes/drops; step 4 does nothing unless @DropLiveDatabase is set to 1.
*/
USE Clio_Staging;
GO
SET NOCOUNT ON;

------------------------------------------------------------------------------------------------------------------
-- 1. What exists now (read only)
------------------------------------------------------------------------------------------------------------------
PRINT '--- 1. What exists now ---';

SELECT N'Database Clio_Live' AS Item, CASE WHEN DB_ID(N'Clio_Live') IS NULL THEN N'not present' ELSE N'present' END AS State
UNION ALL SELECT N'control.LiveCopyRun',   CASE WHEN OBJECT_ID(N'control.LiveCopyRun',   N'U') IS NULL THEN N'not present' ELSE N'present' END
UNION ALL SELECT N'control.LiveCopyTable', CASE WHEN OBJECT_ID(N'control.LiveCopyTable', N'U') IS NULL THEN N'not present' ELSE N'present' END;

IF OBJECT_ID(N'control.LiveCopyRun', N'U') IS NOT NULL
BEGIN
    EXEC (N'SELECT N''control.LiveCopyRun'' AS TableName, COUNT(*) AS Rows_, MIN(StartedAtUTC) AS FirstUTC, MAX(StartedAtUTC) AS LastUTC FROM control.LiveCopyRun;');
    IF OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NOT NULL
        EXEC (N'SELECT COUNT(*) AS MarkerRows_in_IncrementalSyncRun_to_delete FROM control.IncrementalSyncRun WHERE IncrementalRunID IN (SELECT CopyRunID FROM control.LiveCopyRun);');
END;
IF OBJECT_ID(N'control.LiveCopyTable', N'U') IS NOT NULL
    EXEC (N'SELECT N''control.LiveCopyTable'' AS TableName, COUNT(*) AS Rows_ FROM control.LiveCopyTable;');

-- The backup file that script 13 left behind (delete it yourself when the database is gone)
SELECT TOP (3) mf.physical_device_name AS LeftoverBackupFile, bs.backup_finish_date
FROM msdb.dbo.backupset bs
JOIN msdb.dbo.backupmediafamily mf ON mf.media_set_id = bs.media_set_id
WHERE bs.database_name = N'Clio_Staging' AND mf.physical_device_name LIKE N'%Clio_Live_seed_from_%'
ORDER BY bs.backup_finish_date DESC;
GO

------------------------------------------------------------------------------------------------------------------
-- 2. Safety check: this must be Clio_Staging, and the tables to drop must not be referenced by anything else
------------------------------------------------------------------------------------------------------------------
SET NOCOUNT ON;
IF DB_NAME() <> N'Clio_Staging'
    THROW 51500, 'Run this script in the Clio_Staging database.', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys fk
           WHERE fk.referenced_object_id IN (OBJECT_ID(N'control.LiveCopyRun'), OBJECT_ID(N'control.LiveCopyTable')))
    THROW 51501, 'A foreign key points to a LiveCopy table. Nothing was dropped; check what references it.', 1;
PRINT '--- 2. Safety check passed ---';
GO

------------------------------------------------------------------------------------------------------------------
-- 3. Remove the leftovers in Clio_Staging
------------------------------------------------------------------------------------------------------------------
SET NOCOUNT ON;
DECLARE @deleted int = 0;

-- 3a. The marker rows the copier wrote into control.IncrementalSyncRun (they have the same id as a LiveCopyRun row; they have no result rows).
--     They look like "incremental runs" (some COMPLETE), which is wrong, so remove them before the table that identifies them is dropped.
IF OBJECT_ID(N'control.LiveCopyRun', N'U') IS NOT NULL AND OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NOT NULL
BEGIN
    EXEC (N'DELETE FROM control.IncrementalSyncRun WHERE IncrementalRunID IN (SELECT CopyRunID FROM control.LiveCopyRun);');
    SET @deleted = @@ROWCOUNT;
END;
PRINT CONCAT(N'3a. marker rows removed from control.IncrementalSyncRun: ', @deleted);

-- 3b. The two audit tables
DROP TABLE IF EXISTS control.LiveCopyTable;
DROP TABLE IF EXISTS control.LiveCopyRun;
PRINT N'3b. control.LiveCopyTable and control.LiveCopyRun dropped (or they were not there).';
GO

------------------------------------------------------------------------------------------------------------------
-- 4. OPTIONAL: drop the whole database Clio_Live (the copy made by script 13). OFF by default.
--    Set @DropLiveDatabase = 1 only when you are sure nothing uses it any more: every table in it is a copy of Clio_Staging made
--    when script 13 ran, plus control.DeleteRun; the current design does not read it. This cannot be undone (restore from a backup).
------------------------------------------------------------------------------------------------------------------
SET NOCOUNT ON;
DECLARE @DropLiveDatabase bit = 0;       -- <<< change to 1 to drop Clio_Live

IF DB_ID(N'Clio_Live') IS NULL
    PRINT N'4. Database Clio_Live does not exist: nothing to drop.';
ELSE IF @DropLiveDatabase = 0
    PRINT N'4. Database Clio_Live still exists. Left alone (set @DropLiveDatabase = 1 above to drop it).';
ELSE IF DB_ID(N'Clio_Live') = DB_ID(N'Clio_Staging') OR DB_ID(N'Clio_Live') IN (DB_ID(N'EKPBI_New_Dev'), DB_ID(N'EKPBI_New'))
    THROW 51502, 'Refusing: Clio_Live resolves to a database that must not be dropped.', 1;
ELSE
BEGIN
    ALTER DATABASE [Clio_Live] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;   -- ends any connection still using it
    DROP DATABASE [Clio_Live];
    PRINT N'4. Database Clio_Live dropped. Delete the leftover .bak file listed in step 1 by hand.';
END;
GO

PRINT '--- Done. Check the result with step 1 again. ---';
