/* Imports ONLY what the active reporting views need from the main production database (EKPBI_New) into Clio_Staging,
   so PopulateTables and the post-sync procedures can run in staging.
   What is copied is computed, not listed by hand:
     start    = views in CSPViewsToTable with Active = 1 and ConvertToTable = 1
                + the post-sync procedures (@RootProcs below)
     closure  = everything those objects read or call (views, functions, procedures, tables)
     copied   = the closure minus what staging already has and minus the *_Table reporting tables
                (PopulateTables builds those) and the 19 raw endpoint tables (the EXE extracts those).
   Reads EKPBI_New only. Existing staging tables are left alone; existing code is left alone except the
   three stub procedures production must replace (PopulateTables, SP_CleanData, SP_CustomExecAfterSync).
   DEFAULT IS DRY RUN: set @DryRun = 0 to apply. Safe to rerun. */
USE [Clio_Staging];
SET NOCOUNT ON;
/* Every production view/procedure/function was created with both ON, and an object keeps the settings it was created with.
   PopulateMatterNotesView fails with QUOTED_IDENTIFIER OFF (sqlcmd's default). These apply when the batch is parsed, so keep them here. */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @SourceDb sysname = N'EKPBI_New';
DECLARE @DryRun   bit     = 1;   -- 1 = only report what would be copied

IF DB_ID(@SourceDb) IS NULL THROW 51100, 'Source database is not on this instance.', 1;
IF DB_NAME() <> N'Clio_Staging' THROW 51101, 'Run this script in Clio_Staging.', 1;

DECLARE @sql nvarchar(max);

/* Procedures the old app ran after a sync (BIDataManager.ConvertViewsToTable / SyncMatters / CleanData). */
CREATE TABLE #rootProcs (Name sysname COLLATE DATABASE_DEFAULT PRIMARY KEY);
INSERT #rootProcs VALUES
 (N'PopulateTables'), (N'SP_CustomExecAfterSync'), (N'SP_InsertIntoMattersDetailedCustomFields'),
 (N'PopulateMatterNotesView'), (N'SP_UpdateNonBillableActivities'), (N'UpdateExpectedHoursDaily'),
 (N'SP_AddUserGroupReportTo'), (N'SP_CleanData'),
 -- PopulateTables / SP_InsertIntoMattersDetailedCustomFields call these by string, so the dependency scan cannot see them
 (N'GenerateViewToTableScript'), (N'GenerateViewToTableInsertScript'), (N'SwapStagingToFinal'),
 (N'CleanupOldSwapBackups'), (N'SP_InsertCustomFieldToMattersDetailed');

/* ---------- 1. Closure of everything the active views and post-sync procedures use ---------- */
CREATE TABLE #need (Id int PRIMARY KEY);
CREATE TABLE #needObj (Name sysname COLLATE DATABASE_DEFAULT, Type char(2) COLLATE DATABASE_DEFAULT, Defn nvarchar(max) NULL,
                       Qi bit NULL, An bit NULL);   -- QUOTED_IDENTIFIER / ANSI_NULLS the object was created with in production

SET @sql = N'USE ' + QUOTENAME(@SourceDb) + N';
INSERT #need SELECT o.object_id FROM dbo.CSPViewsToTable c
  JOIN sys.objects o ON o.name = c.ViewName AND o.type = ''V'' WHERE c.Active = 1 AND c.ConvertToTable = 1;
INSERT #need SELECT o.object_id FROM sys.objects o JOIN #rootProcs r ON r.Name = o.name COLLATE DATABASE_DEFAULT
  WHERE o.type = ''P'' AND o.object_id NOT IN (SELECT Id FROM #need);
/* Procedures build SQL as strings that call functions (for example GetMatterCustomFieldValue_New), which the dependency
   scan cannot see, so every production function is a root. They are small and have no side effects. */
INSERT #need SELECT o.object_id FROM sys.objects o WHERE o.type IN (''FN'', ''IF'', ''TF'') AND o.is_ms_shipped = 0
  AND o.object_id NOT IN (SELECT Id FROM #need);
WHILE 1 = 1
BEGIN
    INSERT #need SELECT DISTINCT d.referenced_id FROM sys.sql_expression_dependencies d JOIN #need n ON n.Id = d.referencing_id
    WHERE d.referenced_id IS NOT NULL AND d.referenced_database_name IS NULL AND d.referenced_id NOT IN (SELECT Id FROM #need);
    IF @@ROWCOUNT = 0 BREAK;
END;
INSERT #needObj(Name, Type, Defn, Qi, An)
SELECT o.name, o.type, m.definition, m.uses_quoted_identifier, m.uses_ansi_nulls
FROM #need n JOIN sys.objects o ON o.object_id = n.Id
JOIN sys.schemas s ON s.schema_id = o.schema_id AND s.name = N''dbo''
LEFT JOIN sys.sql_modules m ON m.object_id = o.object_id
WHERE o.is_ms_shipped = 0;';
EXEC sys.sp_executesql @sql;

/* ---------- 2. Tables ---------- */
CREATE TABLE #result (Item sysname COLLATE DATABASE_DEFAULT, Action varchar(30), SourceRows bigint NULL, StagingRows bigint NULL);

DECLARE @t sysname, @action varchar(30), @srcRows bigint, @stgRows bigint;
DECLARE tc CURSOR LOCAL FAST_FORWARD FOR
    SELECT Name FROM #needObj
    WHERE Type = 'U' AND Name NOT LIKE '%[_]Table' AND Name NOT LIKE '%[_]Staging'
      AND Name NOT IN (SELECT TableName FROM control.TableInventory WHERE Category IN ('CANDIDATE_RAW', 'LOOKUP', 'CONFIG', 'SUPPORT'))
    ORDER BY Name;
OPEN tc;
FETCH NEXT FROM tc INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'SELECT @r = COUNT_BIG(*) FROM ' + QUOTENAME(@SourceDb) + N'.dbo.' + QUOTENAME(@t);
    EXEC sys.sp_executesql @sql, N'@r bigint OUTPUT', @r = @srcRows OUTPUT;
    SET @stgRows = NULL;

    IF OBJECT_ID(N'dbo.' + QUOTENAME(@t), 'U') IS NULL
    BEGIN
        SET @action = CASE WHEN @DryRun = 1 THEN 'WOULD CREATE' ELSE 'CREATED' END;
        IF @DryRun = 0
        BEGIN
            SET @sql = N'SELECT * INTO dbo.' + QUOTENAME(@t) + N' FROM ' + QUOTENAME(@SourceDb) + N'.dbo.' + QUOTENAME(@t);
            EXEC sys.sp_executesql @sql;
        END;
    END
    ELSE
        SET @action = 'EXISTS - SKIPPED';

    IF OBJECT_ID(N'dbo.' + QUOTENAME(@t), 'U') IS NOT NULL
    BEGIN
        SET @sql = N'SELECT @r = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t);
        EXEC sys.sp_executesql @sql, N'@r bigint OUTPUT', @r = @stgRows OUTPUT;
    END;

    INSERT #result VALUES (@t, @action, @srcRows, @stgRows);
    FETCH NEXT FROM tc INTO @t;
END;
CLOSE tc;
DEALLOCATE tc;

/* The *_Table reporting tables are built by PopulateTables from the views, but other views read them too.
   Create them EMPTY (structure only) so those views can compile; PopulateTables fills them on its first run. */
DECLARE sc CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM #needObj WHERE Type = 'U' AND Name LIKE '%[_]Table' ORDER BY Name;
OPEN sc;
FETCH NEXT FROM sc INTO @t;
WHILE @@FETCH_STATUS = 0
BEGIN
    IF OBJECT_ID(N'dbo.' + QUOTENAME(@t), 'U') IS NULL
    BEGIN
        IF @DryRun = 0
        BEGIN
            SET @sql = N'SELECT TOP (0) * INTO dbo.' + QUOTENAME(@t) + N' FROM ' + QUOTENAME(@SourceDb) + N'.dbo.' + QUOTENAME(@t);
            EXEC sys.sp_executesql @sql;
        END;
        INSERT #result VALUES (@t, CASE WHEN @DryRun = 1 THEN 'WOULD CREATE EMPTY' ELSE 'CREATED EMPTY' END, NULL, 0);
    END;
    FETCH NEXT FROM sc INTO @t;
END;
CLOSE sc;
DEALLOCATE sc;

/* ---------- 3. Functions, procedures, views (dependency-ordered with retries) ---------- */
CREATE TABLE #obj (Seq int IDENTITY(1,1), ObjName sysname COLLATE DATABASE_DEFAULT, ObjType char(2), TypeRank int,
                   Defn nvarchar(max), Qi bit, An bit, Status varchar(12), Err nvarchar(4000) NULL);
INSERT #obj(ObjName, ObjType, TypeRank, Defn, Qi, An, Status)
SELECT Name, Type, CASE WHEN Type IN ('FN', 'IF', 'TF') THEN 1 WHEN Type = 'P' THEN 2 ELSE 3 END, Defn, Qi, An, 'PENDING'
FROM #needObj WHERE Type IN ('V', 'P', 'FN', 'IF', 'TF') AND Defn IS NOT NULL AND Name NOT LIKE 'Publish%';

/* Staging holds stubs of these three; production's real versions replace them. Any other existing object is kept. */
UPDATE o SET Status = 'EXISTS'
FROM #obj o
WHERE o.ObjName NOT IN (N'PopulateTables', N'SP_CleanData', N'SP_CustomExecAfterSync')
  /* An object created with the wrong QUOTED_IDENTIFIER / ANSI_NULLS setting is not "existing": it is recreated.
     (PopulateMatterNotesView, for example, uses XML .value() and fails when created with QUOTED_IDENTIFIER OFF.) */
  AND EXISTS (SELECT 1 FROM sys.objects x JOIN sys.sql_modules sm ON sm.object_id = x.object_id
              WHERE x.name COLLATE DATABASE_DEFAULT = o.ObjName
                AND x.schema_id = SCHEMA_ID(N'dbo') AND x.type COLLATE DATABASE_DEFAULT = o.ObjType
                AND sm.uses_quoted_identifier = o.Qi AND sm.uses_ansi_nulls = o.An);

IF @DryRun = 0
BEGIN
    DECLARE @pass int = 1, @progress int = 1, @id int, @name sysname, @type char(2), @defn nvarchar(max);

    WHILE @progress > 0 AND @pass <= 15
    BEGIN
        SET @progress = 0;
        DECLARE c CURSOR LOCAL FAST_FORWARD FOR
            SELECT Seq FROM #obj WHERE Status IN ('PENDING', 'RETRY') ORDER BY TypeRank, Seq;
        OPEN c;
        FETCH NEXT FROM c INTO @id;
        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @name = ObjName, @type = ObjType, @defn = Defn FROM #obj WHERE Seq = @id;
            BEGIN TRY
                IF EXISTS (SELECT 1 FROM sys.objects x WHERE x.name = @name AND x.schema_id = SCHEMA_ID(N'dbo') AND x.type = @type)
                BEGIN
                    SET @sql = N'DROP ' + CASE WHEN @type = 'V' THEN N'VIEW' WHEN @type = 'P' THEN N'PROCEDURE' ELSE N'FUNCTION' END
                             + N' dbo.' + QUOTENAME(@name);
                    EXEC sys.sp_executesql @sql;
                END;
                EXEC sys.sp_executesql @defn;
                UPDATE #obj SET Status = 'CREATED', Err = NULL WHERE Seq = @id;
                SET @progress += 1;
            END TRY
            BEGIN CATCH
                UPDATE #obj SET Status = 'RETRY', Err = ERROR_MESSAGE() WHERE Seq = @id;
            END CATCH;
            FETCH NEXT FROM c INTO @id;
        END;
        CLOSE c;
        DEALLOCATE c;
        SET @pass += 1;
    END;

    UPDATE #obj SET Status = 'FAILED' WHERE Status IN ('PENDING', 'RETRY');
END
ELSE
    UPDATE #obj SET Status = 'WOULD CREATE' WHERE Status = 'PENDING';

/* ---------- 4. Report ---------- */
SELECT 'TABLES' AS Section, Item, Action, SourceRows, StagingRows,
       CASE WHEN StagingRows IS NOT NULL AND StagingRows <> SourceRows THEN 'row count differs from production' END AS Note
FROM #result ORDER BY Action DESC, Item;

SELECT 'CODE' AS Section,
       CASE ObjType WHEN 'V' THEN 'View' WHEN 'P' THEN 'Procedure' ELSE 'Function' END AS ObjectKind,
       Status, COUNT(*) AS Objects
FROM #obj GROUP BY ObjType, Status ORDER BY ObjType, Status;

SELECT 'CODE FAILED' AS Section, ObjName, ObjType, Err FROM #obj WHERE Status = 'FAILED' ORDER BY ObjName;

/* Every active view that PopulateTables converts must exist in staging (or be created by this run). */
IF OBJECT_ID(N'dbo.CSPViewsToTable', 'U') IS NOT NULL
    SELECT 'ACTIVE VIEW CHECK' AS Section, v.ViewName,
           CASE WHEN OBJECT_ID(N'dbo.' + QUOTENAME(v.ViewName), 'V') IS NOT NULL THEN 'present'
                WHEN EXISTS (SELECT 1 FROM #obj o WHERE o.ObjName = v.ViewName AND o.ObjType = 'V' AND o.Status = 'WOULD CREATE') THEN 'would be created'
                ELSE 'MISSING' END AS ViewStatus
    FROM dbo.CSPViewsToTable v
    WHERE v.Active = 1 AND v.ConvertToTable = 1
    ORDER BY ViewStatus DESC, v.ExecutionOrder;

DROP TABLE #result, #obj, #needObj, #need, #rootProcs;
