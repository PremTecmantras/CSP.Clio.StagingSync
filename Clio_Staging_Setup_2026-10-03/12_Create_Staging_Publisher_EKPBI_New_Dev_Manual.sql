/*
  12_Create_Staging_Publisher_EKPBI_New_Dev_Manual.sql   (SQLCMD Mode is NOT required)

  Replaces dbo.PublishApprovedClioStagingRun in EKPBI_New_Dev for the new pipeline where Clio_Staging builds the
  reporting tables itself (incremental sync + PopulateTables). The procedure no longer rebuilds anything in
  EKPBI_New_Dev. It copies, from Clio_Staging, inside ONE transaction:
     1. the 19 raw endpoint tables (per company, as before),
     2. the reporting tables PopulateTables built (one *_Table per active CSPViewsToTable row),
     3. the tables the post-sync procedures write: Matters_Detailed_Custom_Fields, Expected_Hours_Daily, UsersGroupReportTo,
     4. the freshness metadata (CSPViewsToTable.LastDataUpdate/RecordsCount, TempTables.LastDateUpdated).
  The checks 51031-51043 of script 07 are unchanged. The caller (the staging EXE) holds the pipeline lock while it records
  the approved snapshot and publishes it, so the staging buffers cannot change in between.

  Prerequisite: script 07 has been deployed (control tables and lease procedures exist). Fixed targets: Clio_Staging -> EKPBI_New_Dev.
  Do not run this file against production. DEFAULT BEHAVIOUR IS A DRY RUN (@DryRun = 1).
*/

USE [EKPBI_New_Dev];
GO

IF DB_ID(N'Clio_Staging') IS NULL
    THROW 51030, 'Clio_Staging must be on the same SQL Server instance as EKPBI_New_Dev.', 1;
GO

IF OBJECT_ID(N'control.PublishRun', N'U') IS NULL OR OBJECT_ID(N'control.PublishTableAudit', N'U') IS NULL
    THROW 51055, 'Run 07_Create_Production_Publisher_EKPBI_New_Dev_Manual.sql first: the control tables do not exist.', 1;
GO

CREATE OR ALTER PROCEDURE dbo.PublishApprovedClioStagingRun
    @RunID uniqueidentifier,
    @DryRun bit = 1,
    @PublishReporting bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @PublishID uniqueidentifier = NEWID();
    DECLARE @LockResult int;
    DECLARE @ErrorMessage nvarchar(4000);
    DECLARE @TableName sysname;
    DECLARE @Endpoint sysname;
    DECLARE @Columns nvarchar(max);
    DECLARE @SourceColumns nvarchar(max);
    DECLARE @Sql nvarchar(max);
    DECLARE @RowsDeleted bigint;
    DECLARE @RowsInserted bigint;
    DECLARE @SchemaDiff int;
    DECLARE @StagingRows bigint;
    DECLARE @HasIdentity bit;

    CREATE TABLE #TableMap
    (
        Endpoint sysname NOT NULL PRIMARY KEY,
        TableName sysname NOT NULL UNIQUE,
        SortOrder int NOT NULL UNIQUE
    );

    INSERT #TableMap (Endpoint, TableName, SortOrder)
    VALUES
        (N'Users',                N'Users',                10),
        (N'PracticeAreas',        N'PracticeAreas',        20),
        (N'ActivityDescriptions', N'ActivityDescriptions', 30),
        (N'CustomFieldSets',      N'CustomFieldSets',      40),
        (N'CustomFields',         N'CustomFields',         50),
        (N'Contacts',             N'Contacts',             60),
        (N'Matters',              N'Matters',              70),
        (N'Folders',              N'Folders',              80),
        (N'Calendars',            N'Calendars',            90),
        (N'CalendarEntries',      N'CalendarEntries',     100),
        (N'TaskTypes',            N'TaskTypes',           110),
        (N'Tasks',                N'Tasks',               120),
        (N'Relationships',        N'Relationships',       130),
        (N'Bills',                N'Bills',               140),
        (N'Allocations',          N'Allocations',         150),
        (N'BankTransactions',     N'BankTransactions',    160),
        (N'LineItems',            N'LineItems',           170),
        (N'Activities',           N'Activities',          180),
        (N'MatterNotes',          N'MatterNotes',         190);

    CREATE TABLE #RunCompanies (CompanyID uniqueidentifier NOT NULL PRIMARY KEY);

    CREATE TABLE #SourceValidation
    (
        TableName sysname NOT NULL,
        CompanyID uniqueidentifier NOT NULL,
        AuditRowsWritten bigint NOT NULL,
        SourceRows bigint NOT NULL,
        PRIMARY KEY (TableName, CompanyID)
    );

    CREATE TABLE #PublishPlan
    (
        TableName sysname NOT NULL PRIMARY KEY,
        SourceRows bigint NOT NULL,
        TargetRowsToDelete bigint NOT NULL
    );

    /* Reporting tables built in staging: one *_Table per active view, plus the tables the post-sync procedures write. */
    CREATE TABLE #ReportingMap
    (
        TableName sysname NOT NULL PRIMARY KEY,
        SortOrder int NOT NULL,
        Recreatable bit NOT NULL DEFAULT 0   -- 1 = columns depend on the data (pivot), so a changed structure is recreated
    );

    CREATE TABLE #ReportingPlan
    (
        TableName sysname NOT NULL PRIMARY KEY,
        StagingRows bigint NOT NULL,
        TargetRowsToReplace bigint NOT NULL
    );

    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'ClioStagingToEKPBIPublish',
        @LockMode = N'Exclusive',
        @LockOwner = N'Session',
        @LockTimeout = 0,
        @DbPrincipal = N'public';

    IF @LockResult < 0
        THROW 51031, 'Another publication is already running. No changes were made.', 1;

    BEGIN TRY
        IF EXISTS (SELECT 1 FROM [Clio_Staging].control.SyncRun WHERE Status = 'RUNNING')
            THROW 51032, 'A staging extraction is RUNNING. Wait for it to finish before publishing.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [Clio_Staging].control.SyncRun
            WHERE RunID = @RunID AND Status = 'EXTRACTED' AND Mode = 'FULL'
        )
            THROW 51033, 'RunID is not an approved FULL snapshot record.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [Clio_Staging].control.SyncRun AS later
            INNER JOIN [Clio_Staging].control.SyncRun AS approved ON approved.RunID = @RunID
            WHERE later.RunID <> @RunID
              AND later.StartedAtUTC > approved.StartedAtUTC
        )
            THROW 51034, 'A later staging snapshot record exists. Publish only the latest one.', 1;

        INSERT #RunCompanies (CompanyID)
        SELECT DISTINCT CompanyID
        FROM [Clio_Staging].control.SyncEndpointResult
        WHERE RunID = @RunID;

        IF NOT EXISTS (SELECT 1 FROM #RunCompanies)
            THROW 51035, 'The staging run has no companies.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [Clio_Staging].control.SyncEndpointResult AS e
            LEFT JOIN #TableMap AS m ON m.Endpoint = e.Endpoint
            WHERE e.RunID = @RunID AND m.Endpoint IS NULL
        )
            THROW 51036, 'The staging run contains an endpoint outside the approved 19-table publish allowlist.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM #RunCompanies AS c
            CROSS JOIN #TableMap AS m
            LEFT JOIN [Clio_Staging].control.SyncEndpointResult AS e
                ON e.RunID = @RunID
               AND e.CompanyID = c.CompanyID
               AND e.Endpoint = m.Endpoint
            WHERE e.RunID IS NULL
               OR e.Status <> 'COMPLETE'
               OR e.RowsWritten IS NULL
        )
            THROW 51037, 'The staging run does not have a COMPLETE result with a recorded SQL row count for every approved endpoint and company.', 1;

        /* ---- Reporting layer readiness (new). Staging must have finished building it from the current raw data. ---- */
        IF @PublishReporting = 1
        BEGIN
            IF EXISTS (SELECT 1 FROM [Clio_Staging].control.IncrementalSyncRun WHERE Status = 'RUNNING')
                THROW 51049, 'A staging incremental sync is RUNNING. Wait for it to finish before publishing.', 1;

            IF (SELECT TOP 1 Status FROM [Clio_Staging].control.IncrementalSyncRun ORDER BY StartedAtUTC DESC) NOT IN ('COMPLETE')
                THROW 51050, 'The latest staging incremental run did not complete, so the staging raw tables may be partly updated. Run the incremental sync again first.', 1;

            INSERT #ReportingMap (TableName, SortOrder, Recreatable)
            SELECT v.ViewName + N'_Table', v.ExecutionOrder,
                   CASE WHEN v.ViewName = N'VW_Matter_Notes_Data' THEN 1 ELSE 0 END
            FROM [Clio_Staging].dbo.CSPViewsToTable AS v
            WHERE v.Active = 1 AND v.ConvertToTable = 1;

            IF NOT EXISTS (SELECT 1 FROM #ReportingMap)
                THROW 51038, 'No active reporting-table configuration exists in Clio_Staging.dbo.CSPViewsToTable.', 1;

            IF EXISTS
            (
                SELECT 1
                FROM [Clio_Staging].dbo.CSPViewsToTable AS v
                WHERE v.Active = 1 AND v.ConvertToTable = 1 AND v.LastDataUpdate IS NULL
            )
                THROW 51051, 'One or more active reporting tables were never built in Clio_Staging (CSPViewsToTable.LastDataUpdate is NULL).', 1;

            /* The reporting tables are built by --full-check AFTER the snapshot is recorded (the 5-minute incremental task only updates
               raw tables). A view built before the snapshot reflects older raw data, so it must not be published.
               LastDataUpdate is server local time, CompletedAtUTC is UTC, hence the offset conversion. */
            IF EXISTS
            (
                SELECT 1
                FROM [Clio_Staging].dbo.CSPViewsToTable AS v
                CROSS JOIN (SELECT CompletedAtUTC FROM [Clio_Staging].control.SyncRun WHERE RunID = @RunID) AS s
                WHERE v.Active = 1 AND v.ConvertToTable = 1
                  AND v.LastDataUpdate < DATEADD(MINUTE, DATEDIFF(MINUTE, SYSUTCDATETIME(), SYSDATETIME()), s.CompletedAtUTC)
            )
                THROW 51058, 'The staging reporting tables were built before the approved snapshot was recorded. Run the reporting build after the snapshot (--full-check does this).', 1;

            INSERT #ReportingMap (TableName, SortOrder)
            VALUES (N'Matters_Detailed_Custom_Fields', 100001), (N'Expected_Hours_Daily', 100002), (N'UsersGroupReportTo', 100003);

            IF EXISTS
            (
                SELECT 1 FROM #ReportingMap AS r
                WHERE OBJECT_ID(N'[Clio_Staging].dbo.' + QUOTENAME(r.TableName), N'U') IS NULL
            )
                THROW 51052, 'A reporting table that must be published is missing from Clio_Staging.', 1;
        END;

        /* Validate table existence, column definitions, and the explicit-ID strategy for the raw tables. */
        IF EXISTS
        (
            SELECT 1
            FROM #TableMap AS m
            LEFT JOIN sys.tables AS pt ON pt.name = m.TableName
            LEFT JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id AND ps.name = N'dbo'
            LEFT JOIN [Clio_Staging].sys.tables AS st ON st.name = m.TableName
            LEFT JOIN [Clio_Staging].sys.schemas AS ss ON ss.schema_id = st.schema_id AND ss.name = N'dbo'
            WHERE pt.object_id IS NULL OR ps.schema_id IS NULL
               OR st.object_id IS NULL OR ss.schema_id IS NULL
        )
            THROW 51039, 'One or more approved raw tables are missing from the configured production or staging database.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM #TableMap AS m
            INNER JOIN sys.tables AS pt ON pt.name = m.TableName
            INNER JOIN sys.schemas AS ps ON ps.schema_id = pt.schema_id AND ps.name = N'dbo'
            INNER JOIN [Clio_Staging].sys.tables AS st ON st.name = m.TableName
            INNER JOIN [Clio_Staging].sys.schemas AS ss ON ss.schema_id = st.schema_id AND ss.name = N'dbo'
            WHERE EXISTS
                  (
                      SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
                      FROM sys.columns AS c
                      INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                      WHERE c.object_id = pt.object_id
                      EXCEPT
                      SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
                      FROM [Clio_Staging].sys.columns AS c
                      INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id
                      WHERE c.object_id = st.object_id
                  )
               OR EXISTS
                  (
                      SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
                      FROM [Clio_Staging].sys.columns AS c
                      INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id
                      WHERE c.object_id = st.object_id
                      EXCEPT
                      SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity
                      FROM sys.columns AS c
                      INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                      WHERE c.object_id = pt.object_id
                  )
        )
            THROW 51040, 'An approved raw-table schema differs between the configured staging and production database.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM #TableMap AS m
            INNER JOIN sys.tables AS t ON t.name = m.TableName
            INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
            LEFT JOIN sys.columns AS idc ON idc.object_id = t.object_id AND idc.name = N'ID'
            LEFT JOIN sys.columns AS cc ON cc.object_id = t.object_id AND cc.name = N'CompanyID'
            LEFT JOIN sys.columns AS cic ON cic.object_id = t.object_id AND cic.name = N'Clio_Internal_ID'
            WHERE idc.column_id IS NULL OR idc.is_identity <> 1
               OR cc.column_id IS NULL OR cic.column_id IS NULL
        )
            THROW 51041, 'The expected ID identity, CompanyID, or Clio_Internal_ID column is missing.', 1;

        DECLARE source_validation_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT Endpoint, TableName FROM #TableMap ORDER BY SortOrder;

        OPEN source_validation_cursor;
        FETCH NEXT FROM source_validation_cursor INTO @Endpoint, @TableName;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Sql = N'
                INSERT #SourceValidation (TableName, CompanyID, AuditRowsWritten, SourceRows)
                SELECT @TableName, e.CompanyID, CONVERT(bigint, e.RowsWritten), COUNT_BIG(s.ID)
                FROM [Clio_Staging].control.SyncEndpointResult AS e
                INNER JOIN #RunCompanies AS rc ON rc.CompanyID = e.CompanyID
                LEFT JOIN [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N' AS s
                    ON s.CompanyID = e.CompanyID
                WHERE e.RunID = @RunID AND e.Endpoint = @Endpoint
                GROUP BY e.CompanyID, e.RowsWritten;';

            EXEC sys.sp_executesql @Sql,
                N'@RunID uniqueidentifier, @Endpoint sysname, @TableName sysname',
                @RunID = @RunID, @Endpoint = @Endpoint, @TableName = @TableName;

            FETCH NEXT FROM source_validation_cursor INTO @Endpoint, @TableName;
        END;

        CLOSE source_validation_cursor;
        DEALLOCATE source_validation_cursor;

        IF EXISTS (SELECT 1 FROM #SourceValidation WHERE AuditRowsWritten <> SourceRows)
            THROW 51042, 'Current staging rows no longer match the approved snapshot audit. The staging tables changed after the snapshot was recorded.', 1;

        DECLARE plan_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT TableName FROM #TableMap ORDER BY SortOrder;

        OPEN plan_cursor;
        FETCH NEXT FROM plan_cursor INTO @TableName;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SET @Sql = N'
                INSERT #PublishPlan (TableName, SourceRows, TargetRowsToDelete)
                SELECT @TableName,
                       (SELECT COUNT_BIG(*) FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N' AS s INNER JOIN #RunCompanies AS rc ON rc.CompanyID = s.CompanyID),
                       (SELECT COUNT_BIG(*) FROM dbo.' + QUOTENAME(@TableName) + N' AS p INNER JOIN #RunCompanies AS rc ON rc.CompanyID = p.CompanyID);';

            EXEC sys.sp_executesql @Sql, N'@TableName sysname', @TableName = @TableName;
            FETCH NEXT FROM plan_cursor INTO @TableName;
        END;

        CLOSE plan_cursor;
        DEALLOCATE plan_cursor;

        /* Reporting plan and safety checks: never replace a populated Dev table with an empty or differently shaped one. */
        IF @PublishReporting = 1
        BEGIN
            DECLARE reporting_plan_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT TableName FROM #ReportingMap ORDER BY SortOrder;

            OPEN reporting_plan_cursor;
            FETCH NEXT FROM reporting_plan_cursor INTO @TableName;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                SET @StagingRows = 0;
                SET @RowsDeleted = 0;
                SET @Sql = N'SELECT @s = COUNT_BIG(*) FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N';';
                EXEC sys.sp_executesql @Sql, N'@s bigint OUTPUT', @s = @StagingRows OUTPUT;

                IF OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') IS NOT NULL
                BEGIN
                    SET @Sql = N'SELECT @d = COUNT_BIG(*) FROM dbo.' + QUOTENAME(@TableName) + N';';
                    EXEC sys.sp_executesql @Sql, N'@d bigint OUTPUT', @d = @RowsDeleted OUTPUT;

                    SET @Sql = N'
                        SELECT @n = COUNT(*) FROM
                        (
                            (SELECT c.name, ty.name AS tname, c.max_length, c.precision, c.scale, c.is_nullable
                             FROM sys.columns AS c INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                             WHERE c.object_id = OBJECT_ID(N''dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''') AND c.is_computed = 0
                             EXCEPT
                             SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
                             FROM [Clio_Staging].sys.columns AS c INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id
                             WHERE c.object_id = OBJECT_ID(N''Clio_Staging.dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''') AND c.is_computed = 0)
                            UNION ALL
                            (SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
                             FROM [Clio_Staging].sys.columns AS c INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id
                             WHERE c.object_id = OBJECT_ID(N''Clio_Staging.dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''') AND c.is_computed = 0
                             EXCEPT
                             SELECT c.name, ty.name, c.max_length, c.precision, c.scale, c.is_nullable
                             FROM sys.columns AS c INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
                             WHERE c.object_id = OBJECT_ID(N''dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''') AND c.is_computed = 0)
                        ) AS d;';
                    EXEC sys.sp_executesql @Sql, N'@n int OUTPUT', @n = @SchemaDiff OUTPUT;

                    IF @SchemaDiff > 0 AND NOT EXISTS (SELECT 1 FROM #ReportingMap WHERE TableName = @TableName AND Recreatable = 1)
                    BEGIN
                        SET @ErrorMessage = N'The structure of reporting table ' + @TableName
                            + N' differs between Clio_Staging and EKPBI_New_Dev. Align the view definitions first.';
                        THROW 51053, @ErrorMessage, 1;
                    END;
                END;

                IF @StagingRows = 0 AND @RowsDeleted > 0
                BEGIN
                    SET @ErrorMessage = N'Reporting table ' + @TableName + N' is empty in Clio_Staging but populated in EKPBI_New_Dev. Publication stopped to avoid wiping it.';
                    THROW 51056, @ErrorMessage, 1;
                END;

                INSERT #ReportingPlan (TableName, StagingRows, TargetRowsToReplace) VALUES (@TableName, @StagingRows, @RowsDeleted);
                FETCH NEXT FROM reporting_plan_cursor INTO @TableName;
            END;

            CLOSE reporting_plan_cursor;
            DEALLOCATE reporting_plan_cursor;
        END;

        IF @DryRun = 1
        BEGIN
            SELECT @RunID AS SourceRunID, CAST(1 AS bit) AS DryRun,
                   (SELECT COUNT(*) FROM #RunCompanies) AS CompanyCount,
                   (SELECT SUM(SourceRows) FROM #PublishPlan) AS TotalRawSourceRows,
                   (SELECT SUM(TargetRowsToDelete) FROM #PublishPlan) AS TotalRawTargetRowsToReplace,
                   (SELECT COUNT(*) FROM #ReportingPlan) AS ReportingTables,
                   (SELECT SUM(StagingRows) FROM #ReportingPlan) AS TotalReportingStagingRows,
                   @PublishReporting AS WillPublishReporting;
            SELECT TableName, SourceRows, TargetRowsToDelete FROM #PublishPlan ORDER BY TableName;
            SELECT TableName, StagingRows, TargetRowsToReplace FROM #ReportingPlan ORDER BY TableName;
            SELECT TableName, CompanyID, AuditRowsWritten, SourceRows
            FROM #SourceValidation ORDER BY TableName, CompanyID;

            EXEC sys.sp_releaseapplock
                @Resource = N'ClioStagingToEKPBIPublish', @LockOwner = N'Session', @DbPrincipal = N'public';
            RETURN;
        END;

        /* Atomically prevents a new staging extraction from starting. */
        EXEC [Clio_Staging].control.BeginRawPublishLease @PublishID = @PublishID;

        INSERT control.PublishRun (PublishID, SourceRunID, Status, DryRun)
        VALUES (@PublishID, @RunID, 'RUNNING', 0);

        BEGIN TRANSACTION;

        /* ---- 1. Raw endpoint tables (unchanged from script 07) ---- */
        DECLARE publish_cursor CURSOR LOCAL FAST_FORWARD FOR
            SELECT TableName FROM #TableMap ORDER BY SortOrder;

        OPEN publish_cursor;
        FETCH NEXT FROM publish_cursor INTO @TableName;

        WHILE @@FETCH_STATUS = 0
        BEGIN
            SELECT @Columns = STUFF
            (
                (
                    SELECT N',' + QUOTENAME(c.name)
                    FROM sys.tables AS t
                    INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
                    INNER JOIN sys.columns AS c ON c.object_id = t.object_id
                    WHERE t.name = @TableName
                      AND c.is_computed = 0
                      AND c.system_type_id <> 189 /* rowversion/timestamp */
                    ORDER BY c.column_id
                    FOR XML PATH(N''), TYPE
                ).value(N'.', N'nvarchar(max)'), 1, 1, N''
            );

            SELECT @SourceColumns = STUFF
            (
                (
                    SELECT N',s.' + QUOTENAME(c.name)
                    FROM sys.tables AS t
                    INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
                    INNER JOIN sys.columns AS c ON c.object_id = t.object_id
                    WHERE t.name = @TableName
                      AND c.is_computed = 0
                      AND c.system_type_id <> 189
                    ORDER BY c.column_id
                    FOR XML PATH(N''), TYPE
                ).value(N'.', N'nvarchar(max)'), 1, 1, N''
            );

            SET @Sql = N'
                DELETE p
                FROM dbo.' + QUOTENAME(@TableName) + N' AS p
                INNER JOIN #RunCompanies AS rc ON rc.CompanyID = p.CompanyID;
                SET @RowsDeletedOut = @@ROWCOUNT;

                BEGIN TRY
                    SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' ON;
                    INSERT dbo.' + QUOTENAME(@TableName) + N' (' + @Columns + N')
                    SELECT ' + @SourceColumns + N'
                    FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N' AS s
                    INNER JOIN #RunCompanies AS rc ON rc.CompanyID = s.CompanyID;
                    SET @RowsInsertedOut = @@ROWCOUNT;
                    SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' OFF;
                END TRY
                BEGIN CATCH
                    BEGIN TRY
                        SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' OFF;
                    END TRY
                    BEGIN CATCH
                    END CATCH;
                    THROW;
                END CATCH;';

            SET @RowsDeleted = 0;
            SET @RowsInserted = 0;
            EXEC sys.sp_executesql @Sql,
                N'@RowsDeletedOut bigint OUTPUT, @RowsInsertedOut bigint OUTPUT',
                @RowsDeletedOut = @RowsDeleted OUTPUT,
                @RowsInsertedOut = @RowsInserted OUTPUT;

            IF @RowsInserted <> (SELECT SourceRows FROM #PublishPlan WHERE TableName = @TableName)
                THROW 51043, 'A raw table copy wrote an unexpected number of rows.', 1;

            INSERT control.PublishTableAudit (PublishID, TableName, SourceRows, TargetRowsDeleted, TargetRowsInserted)
            VALUES (@PublishID, @TableName,
                    (SELECT SourceRows FROM #PublishPlan WHERE TableName = @TableName),
                    @RowsDeleted, @RowsInserted);

            FETCH NEXT FROM publish_cursor INTO @TableName;
        END;

        CLOSE publish_cursor;
        DEALLOCATE publish_cursor;

        /* ---- 2. Reporting tables built by Clio_Staging (replaces the old rebuild inside this database) ---- */
        IF @PublishReporting = 1
        BEGIN
            DECLARE reporting_cursor CURSOR LOCAL FAST_FORWARD FOR
                SELECT r.TableName, r.Recreatable FROM #ReportingMap AS r ORDER BY r.SortOrder;

            DECLARE @Recreatable bit;
            OPEN reporting_cursor;
            FETCH NEXT FROM reporting_cursor INTO @TableName, @Recreatable;

            WHILE @@FETCH_STATUS = 0
            BEGIN
                /* A data-dependent structure (pivot columns) is recreated when it changed; other tables were already checked. */
                IF OBJECT_ID(N'dbo.' + QUOTENAME(@TableName), N'U') IS NULL
                BEGIN
                    SET @Sql = N'SELECT * INTO dbo.' + QUOTENAME(@TableName) + N' FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N' WHERE 1 = 0;';
                    EXEC sys.sp_executesql @Sql;
                END
                ELSE IF @Recreatable = 1
                BEGIN
                    SET @Sql = N'
                        SELECT @n = COUNT(*) FROM
                        (
                            (SELECT c.name, ty.name AS tname, c.max_length, c.is_nullable FROM sys.columns AS c
                             INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N''dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''')
                             EXCEPT
                             SELECT c.name, ty.name, c.max_length, c.is_nullable FROM [Clio_Staging].sys.columns AS c
                             INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N''Clio_Staging.dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N'''))
                            UNION ALL
                            (SELECT c.name, ty.name, c.max_length, c.is_nullable FROM [Clio_Staging].sys.columns AS c
                             INNER JOIN [Clio_Staging].sys.types AS ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N''Clio_Staging.dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N''')
                             EXCEPT
                             SELECT c.name, ty.name, c.max_length, c.is_nullable FROM sys.columns AS c
                             INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID(N''dbo.' + REPLACE(QUOTENAME(@TableName), N'''', N'''''') + N'''))
                        ) AS d;';
                    EXEC sys.sp_executesql @Sql, N'@n int OUTPUT', @n = @SchemaDiff OUTPUT;

                    IF @SchemaDiff > 0
                    BEGIN
                        SET @Sql = N'DROP TABLE dbo.' + QUOTENAME(@TableName) + N'; SELECT * INTO dbo.' + QUOTENAME(@TableName)
                                 + N' FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N' WHERE 1 = 0;';
                        EXEC sys.sp_executesql @Sql;
                    END;
                END;

                SELECT @Columns = STUFF
                (
                    (
                        SELECT N',' + QUOTENAME(c.name)
                        FROM sys.columns AS c
                        WHERE c.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@TableName))
                          AND c.is_computed = 0
                          AND c.system_type_id <> 189
                        ORDER BY c.column_id
                        FOR XML PATH(N''), TYPE
                    ).value(N'.', N'nvarchar(max)'), 1, 1, N''
                );

                SET @HasIdentity = CAST(OBJECTPROPERTY(OBJECT_ID(N'dbo.' + QUOTENAME(@TableName)), N'TableHasIdentity') AS bit);

                SET @Sql = N'
                    DELETE FROM dbo.' + QUOTENAME(@TableName) + N';
                    SET @RowsDeletedOut = @@ROWCOUNT;
                    BEGIN TRY
                        ' + CASE WHEN @HasIdentity = 1 THEN N'SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' ON;' ELSE N'' END + N'
                        INSERT dbo.' + QUOTENAME(@TableName) + N' (' + @Columns + N')
                        SELECT ' + @Columns + N' FROM [Clio_Staging].dbo.' + QUOTENAME(@TableName) + N';
                        SET @RowsInsertedOut = @@ROWCOUNT;
                        ' + CASE WHEN @HasIdentity = 1 THEN N'SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' OFF;' ELSE N'' END + N'
                    END TRY
                    BEGIN CATCH
                        ' + CASE WHEN @HasIdentity = 1
                                 THEN N'BEGIN TRY SET IDENTITY_INSERT dbo.' + QUOTENAME(@TableName) + N' OFF; END TRY BEGIN CATCH END CATCH;'
                                 ELSE N'' END + N'
                        THROW;
                    END CATCH;';

                SET @RowsDeleted = 0;
                SET @RowsInserted = 0;
                EXEC sys.sp_executesql @Sql,
                    N'@RowsDeletedOut bigint OUTPUT, @RowsInsertedOut bigint OUTPUT',
                    @RowsDeletedOut = @RowsDeleted OUTPUT,
                    @RowsInsertedOut = @RowsInserted OUTPUT;

                IF @RowsInserted <> (SELECT StagingRows FROM #ReportingPlan WHERE TableName = @TableName)
                    THROW 51057, 'A reporting table copy wrote an unexpected number of rows.', 1;

                INSERT control.PublishTableAudit (PublishID, TableName, SourceRows, TargetRowsDeleted, TargetRowsInserted)
                VALUES (@PublishID, @TableName, @RowsInserted, @RowsDeleted, @RowsInserted);

                FETCH NEXT FROM reporting_cursor INTO @TableName, @Recreatable;
            END;

            CLOSE reporting_cursor;
            DEALLOCATE reporting_cursor;

            /* The pivot view over MatterNotes is a real view, not a table: recreate it from the freshly published raw notes. */
            EXEC dbo.PopulateMatterNotesView;
            IF OBJECT_ID(N'dbo.VW_Matter_Notes_Data', N'V') IS NULL
                THROW 51054, 'PopulateMatterNotesView did not recreate dbo.VW_Matter_Notes_Data.', 1;

            /* Freshness metadata that reports and dashboards read. */
            UPDATE d
            SET d.LastDataUpdate = s.LastDataUpdate,
                d.DataTablesLastChanged = s.DataTablesLastChanged,
                d.ClioDataLastDateUpdated = s.ClioDataLastDateUpdated,
                d.RecordsCount = s.RecordsCount
            FROM dbo.CSPViewsToTable AS d
            INNER JOIN [Clio_Staging].dbo.CSPViewsToTable AS s ON s.ViewName = d.ViewName
            WHERE s.Active = 1 AND s.ConvertToTable = 1;

            UPDATE d
            SET d.LastDateUpdated = s.LastDateUpdated
            FROM dbo.TempTables AS d
            INNER JOIN [Clio_Staging].dbo.TempTables AS s ON s.ID = d.ID;
        END;

        UPDATE control.PublishRun
        SET Status = 'COMPLETE', CompletedAtUTC = SYSUTCDATETIME()
        WHERE PublishID = @PublishID;

        EXEC [Clio_Staging].control.EndRawPublishLease
            @PublishID = @PublishID, @Status = 'COMPLETE';

        COMMIT TRANSACTION;

        SELECT @PublishID AS PublishID, @RunID AS SourceRunID, N'COMPLETE' AS Status;
        SELECT TableName, SourceRows, TargetRowsDeleted, TargetRowsInserted
        FROM control.PublishTableAudit
        WHERE PublishID = @PublishID
        ORDER BY TableName;

        EXEC sys.sp_releaseapplock
            @Resource = N'ClioStagingToEKPBIPublish', @LockOwner = N'Session', @DbPrincipal = N'public';
    END TRY
    BEGIN CATCH
        SET @ErrorMessage = ERROR_MESSAGE();

        IF XACT_STATE() <> 0
            ROLLBACK TRANSACTION;

        IF @DryRun = 0
        BEGIN
            UPDATE control.PublishRun
            SET Status = 'FAILED', CompletedAtUTC = SYSUTCDATETIME(), ErrorMessage = @ErrorMessage
            WHERE PublishID = @PublishID;

            EXEC [Clio_Staging].control.EndRawPublishLease
                @PublishID = @PublishID, @Status = 'FAILED', @ErrorMessage = @ErrorMessage;
        END;

        EXEC sys.sp_releaseapplock
            @Resource = N'ClioStagingToEKPBIPublish', @LockOwner = N'Session', @DbPrincipal = N'public';

        THROW;
    END CATCH;
END;
GO

/*
    Dry run (no data changes), using the approved snapshot RunID recorded by the staging EXE:

    EXEC dbo.PublishApprovedClioStagingRun @RunID = '<RunID>', @DryRun = 1, @PublishReporting = 1;

    Real publication, only after the dry run is approved and a backup of EKPBI_New_Dev exists:

    EXEC dbo.PublishApprovedClioStagingRun @RunID = '<RunID>', @DryRun = 0, @PublishReporting = 1;
*/
