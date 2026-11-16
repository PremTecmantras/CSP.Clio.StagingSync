/*
    13_Create_Clio_Live.sql  -  two-database mode

    Creates the database that the 5-minute incremental sync, the first load and the background delete task write to (Clio_Live),
    and adds the small control tables the new commands need. Clio_Staging stays what it is: the frozen copy that the publish cycle
    (--full-check) fills from Clio_Live, checks, builds the reports in and publishes from.

    How Clio_Live is created: an exact copy of Clio_Staging (COPY_ONLY backup + restore under the new name). That brings the raw tables,
    the incremental watermarks, the *_Deleted archives, the company list with its Clio tokens and every index and constraint, so the
    incremental sync continues exactly where it stopped. Nothing is changed in Clio_Staging except two new audit tables.

    Run in SSMS on the SQL Server instance of Clio_Staging, as a login with sysadmin (or db_owner + backup/restore rights).
    It is safe to run again: when Clio_Live already exists, only the missing control tables are added.

    Needs disk space for one backup file and one more copy of Clio_Staging (the data files, about as large as Clio_Staging).
    The backup file is NOT deleted by this script: delete it when the script has finished.

    After it ran:
      - App.config: LiveDatabaseName = Clio_Live and connection string EKPBIEntitiesLive (Initial Catalog = Clio_Live) -> already in the shipped config.
      - Stop the old 5-minute task and the publish task before running this script, so that Clio_Staging does not change while it is copied.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @SourceDb     sysname       = N'Clio_Staging';
DECLARE @LiveDb       sysname       = N'Clio_Live';
DECLARE @BackupFolder nvarchar(260) = NULL;     -- NULL = the instance default backup folder, else the folder of Clio_Staging's data file
DECLARE @LiveDataFolder nvarchar(260) = NULL;   -- folder for the data AND log file of Clio_Live (it must exist), e.g. N'E:\SQLData'. NULL = same folder as Clio_Staging's files

IF DB_ID(@SourceDb) IS NULL THROW 50001, 'The source database (Clio_Staging) does not exist on this instance.', 1;
IF @LiveDb = @SourceDb      THROW 50002, 'The Live database must have a different name than the source database.', 1;

------------------------------------------------------------------------------------------------------------------
-- 1. Create Clio_Live from Clio_Staging (only when it does not exist yet)
------------------------------------------------------------------------------------------------------------
IF DB_ID(@LiveDb) IS NULL
BEGIN
    DECLARE @sql nvarchar(max);
    DECLARE @firstFile nvarchar(260) = (SELECT TOP (1) physical_name FROM sys.master_files WHERE database_id = DB_ID(@SourceDb) AND type = 0 ORDER BY file_id);
    DECLARE @dataFolder nvarchar(260) = LEFT(@firstFile, LEN(@firstFile) - CHARINDEX(N'\', REVERSE(@firstFile)));

    IF @BackupFolder IS NULL
        SET @BackupFolder = COALESCE(CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(260)), @dataFolder);
    DECLARE @bak nvarchar(520) = @BackupFolder + N'\' + @LiveDb + N'_seed_from_' + @SourceDb + N'.bak';

    PRINT CONCAT(N'Backing up ', @SourceDb, N' to ', @bak, N' ...');
    SET @sql = N'BACKUP DATABASE ' + QUOTENAME(@SourceDb) + N' TO DISK = N''' + REPLACE(@bak, N'''', N'''''') + N''' WITH COPY_ONLY, INIT, STATS = 10;';
    EXEC (@sql);

    -- One MOVE per file: the folder of the source file (or @LiveDataFolder), the file name with the database name replaced.
    DECLARE @move nvarchar(max) = N'';
    SELECT @move += N', MOVE N''' + REPLACE(f.name, N'''', N'''''') + N''' TO N'''
        + REPLACE(COALESCE(@LiveDataFolder, p.dir) + N'\'
            + CASE WHEN p.fileName LIKE N'%' + @SourceDb + N'%' THEN REPLACE(p.fileName, @SourceDb, @LiveDb) ELSE @LiveDb + N'_' + p.fileName END, N'''', N'''''') + N''''
    FROM sys.master_files AS f
    CROSS APPLY (SELECT LEFT(f.physical_name, LEN(f.physical_name) - CHARINDEX(N'\', REVERSE(f.physical_name))) AS dir,
                        REVERSE(LEFT(REVERSE(f.physical_name), CHARINDEX(N'\', REVERSE(f.physical_name)) - 1)) AS fileName) AS p
    WHERE f.database_id = DB_ID(@SourceDb);

    PRINT CONCAT(N'Restoring as ', @LiveDb, N' ...');
    SET @sql = N'RESTORE DATABASE ' + QUOTENAME(@LiveDb) + N' FROM DISK = N''' + REPLACE(@bak, N'''', N'''''') + N''' WITH RECOVERY' + @move + N', STATS = 10;';
    EXEC (@sql);

    PRINT CONCAT(N'Backup file left in place (delete it when you are done): ', @bak);
END
ELSE
    PRINT CONCAT(N'Database ', @LiveDb, N' already exists - only the control tables are checked.');

------------------------------------------------------------------------------------------------------------------
-- 2. Recovery model: the incremental sync writes all day, the log must not grow without limit
------------------------------------------------------------------------------------------------------------------
DECLARE @alter nvarchar(max) = N'ALTER DATABASE ' + QUOTENAME(@LiveDb) + N' SET RECOVERY SIMPLE;';
EXEC (@alter);

------------------------------------------------------------------------------------------------------------------
-- 3. Control table of the background delete task (in Clio_Live)
------------------------------------------------------------------------------------------------------------------
DECLARE @liveDdl nvarchar(max) = N'USE ' + QUOTENAME(@LiveDb) + N';
IF SCHEMA_ID(N''control'') IS NULL EXEC(N''CREATE SCHEMA control AUTHORIZATION dbo;'');
IF OBJECT_ID(N''control.DeleteRun'', N''U'') IS NULL
    CREATE TABLE control.DeleteRun
    (
        DeleteRunID uniqueidentifier NOT NULL CONSTRAINT PK_DeleteRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_DeleteRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL,
        RowsMoved bigint NULL,
        Problems int NULL,
        ErrorMessage nvarchar(3500) NULL,
        CONSTRAINT CK_DeleteRun_Status CHECK (Status IN (''RUNNING'',''COMPLETE'',''PARTIAL'',''PROBLEMS'',''FAILED'',''ABANDONED''))
    );';
EXEC (@liveDdl);

------------------------------------------------------------------------------------------------------------------
-- 4. Audit tables of the Live -> Staging copy (in Clio_Staging)
------------------------------------------------------------------------------------------------------------------
DECLARE @stagingDdl nvarchar(max) = N'USE ' + QUOTENAME(@SourceDb) + N';
IF SCHEMA_ID(N''control'') IS NULL EXEC(N''CREATE SCHEMA control AUTHORIZATION dbo;'');
IF OBJECT_ID(N''control.LiveCopyRun'', N''U'') IS NULL
    CREATE TABLE control.LiveCopyRun
    (
        CopyRunID uniqueidentifier NOT NULL CONSTRAINT PK_LiveCopyRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_LiveCopyRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL,
        RowsCopied bigint NULL,
        ErrorMessage nvarchar(3500) NULL
    );
IF OBJECT_ID(N''control.LiveCopyTable'', N''U'') IS NULL
    CREATE TABLE control.LiveCopyTable
    (
        CopyRunID uniqueidentifier NOT NULL,
        TableName sysname NOT NULL,
        RowsCopied bigint NOT NULL,
        Seconds decimal(9,2) NOT NULL,
        CONSTRAINT PK_LiveCopyTable PRIMARY KEY (CopyRunID, TableName)
    );';
EXEC (@stagingDdl);

------------------------------------------------------------------------------------------------------------------
-- 5. Report: both databases must hold the same raw rows right after creation
------------------------------------------------------------------------------------------------------------------
DECLARE @report nvarchar(max) = N'';
SELECT @report += N'SELECT N''' + t.name + N''' AS TableName, (SELECT COUNT_BIG(*) FROM ' + QUOTENAME(@SourceDb) + N'.dbo.' + QUOTENAME(t.name)
    + N') AS StagingRows, (SELECT COUNT_BIG(*) FROM ' + QUOTENAME(@LiveDb) + N'.dbo.' + QUOTENAME(t.name) + N') AS LiveRows UNION ALL '
FROM (VALUES (N'Activities'),(N'ActivityDescriptions'),(N'Allocations'),(N'BankTransactions'),(N'Bills'),(N'CalendarEntries'),(N'Calendars'),(N'Contacts'),
             (N'CustomFields'),(N'CustomFieldSets'),(N'Folders'),(N'LineItems'),(N'MatterNotes'),(N'Matters'),(N'PracticeAreas'),(N'Relationships'),
             (N'Tasks'),(N'TaskTypes'),(N'Users'),(N'_EKPCompanies')) AS t(name);
SET @report = LEFT(@report, LEN(@report) - LEN(N' UNION ALL ')) + N' ORDER BY 1;';
EXEC (@report);

PRINT N'Done. Next: set LiveDatabaseName / EKPBIEntitiesLive in the EXE config, then start the incremental and delete tasks (Setup_Scheduled_Tasks.ps1).';
