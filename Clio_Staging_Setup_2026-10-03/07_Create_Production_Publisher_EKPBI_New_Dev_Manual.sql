/*
  Manual SSMS deployment script. SQLCMD Mode is NOT required.
  Fixed targets: Clio_Staging -> EKPBI_New_Dev.
  Do not run this file against production. For production, create a separately reviewed script with EKPBI_New as the target.
*/

/*
    07_Create_Production_Publisher.sql

    Deploys the controlled, full-snapshot publisher in the configured production database.

    This script CREATES control tables and dbo.PublishApprovedClioStagingRun,
    but it does not publish any data by itself.

    The procedure defaults to @DryRun = 1.  The first real publication must
    be explicitly run with @DryRun = 0 after the dry-run output is approved.
    This manual copy is fixed to Clio_Staging and EKPBI_New_Dev. SQLCMD Mode is not required.
*/


USE [Clio_Staging];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

IF DB_ID(N'EKPBI_New_Dev') IS NULL
    THROW 51030, 'The configured production database must be on the same SQL Server instance as the configured staging database.', 1;
GO

IF SCHEMA_ID(N'control') IS NULL
    EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
GO

/*
    This lease coordinates a staging run with a production publication.
    The staging EXE checks it before beginning a new full extraction.
*/
IF OBJECT_ID(N'control.PublishLease', N'U') IS NULL
BEGIN
    CREATE TABLE control.PublishLease
    (
        LeaseName       sysname NOT NULL CONSTRAINT PK_control_PublishLease PRIMARY KEY,
        PublishID       uniqueidentifier NULL,
        Status          varchar(20) NOT NULL,
        StartedAtUTC    datetime2(7) NULL,
        CompletedAtUTC  datetime2(7) NULL,
        ErrorMessage    nvarchar(4000) NULL
    );
END;
GO

CREATE OR ALTER PROCEDURE control.BeginRawPublishLease
    @PublishID uniqueidentifier
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    BEGIN TRANSACTION;

    DECLARE @LockResult int;
    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'ClioStagingSyncOrPublish',
        @LockMode = N'Exclusive',
        @LockOwner = N'Transaction',
        @LockTimeout = 0,
        @DbPrincipal = N'public';

    IF @LockResult < 0
        THROW 51046, 'A staging start/publication coordination lock is unavailable.', 1;

    IF EXISTS (SELECT 1 FROM control.SyncRun WHERE Status = 'RUNNING')
        THROW 51047, 'A staging extraction is RUNNING.', 1;

    IF EXISTS
    (
        SELECT 1 FROM control.PublishLease
        WHERE LeaseName = N'RAW_PUBLISH' AND Status = 'RUNNING'
    )
        THROW 51048, 'Another raw publication lease is already RUNNING.', 1;

    MERGE control.PublishLease AS target
    USING (SELECT N'RAW_PUBLISH' AS LeaseName) AS source
       ON target.LeaseName = source.LeaseName
    WHEN MATCHED THEN
        UPDATE SET PublishID = @PublishID, Status = 'RUNNING',
                   StartedAtUTC = SYSUTCDATETIME(), CompletedAtUTC = NULL, ErrorMessage = NULL
    WHEN NOT MATCHED THEN
        INSERT (LeaseName, PublishID, Status, StartedAtUTC)
        VALUES (N'RAW_PUBLISH', @PublishID, 'RUNNING', SYSUTCDATETIME());

    COMMIT TRANSACTION;
END;
GO

CREATE OR ALTER PROCEDURE control.EndRawPublishLease
    @PublishID uniqueidentifier,
    @Status varchar(20),
    @ErrorMessage nvarchar(4000) = NULL
AS
BEGIN
    SET NOCOUNT ON;

    UPDATE control.PublishLease
    SET Status = @Status,
        CompletedAtUTC = SYSUTCDATETIME(),
        ErrorMessage = @ErrorMessage
    WHERE LeaseName = N'RAW_PUBLISH'
      AND PublishID = @PublishID;
END;
GO

USE [EKPBI_New_Dev];
GO

IF SCHEMA_ID(N'control') IS NULL
    EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'control.PublishRun', N'U') IS NULL
BEGIN
    CREATE TABLE control.PublishRun
    (
        PublishID       uniqueidentifier NOT NULL CONSTRAINT PK_control_PublishRun PRIMARY KEY,
        SourceRunID     uniqueidentifier NOT NULL,
        Status          varchar(20) NOT NULL,
        DryRun          bit NOT NULL,
        StartedAtUTC    datetime2(7) NOT NULL CONSTRAINT DF_control_PublishRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC  datetime2(7) NULL,
        PublishedBy     sysname NOT NULL CONSTRAINT DF_control_PublishRun_PublishedBy DEFAULT SUSER_SNAME(),
        ErrorMessage    nvarchar(4000) NULL
    );

    CREATE INDEX IX_control_PublishRun_SourceRunID ON control.PublishRun(SourceRunID, StartedAtUTC DESC);
END;
GO

IF OBJECT_ID(N'control.PublishTableAudit', N'U') IS NULL
BEGIN
    CREATE TABLE control.PublishTableAudit
    (
        PublishID       uniqueidentifier NOT NULL,
        TableName       sysname NOT NULL,
        SourceRows      bigint NOT NULL,
        TargetRowsDeleted bigint NOT NULL,
        TargetRowsInserted bigint NOT NULL,
        CONSTRAINT PK_control_PublishTableAudit PRIMARY KEY (PublishID, TableName),
        CONSTRAINT FK_control_PublishTableAudit_PublishRun
            FOREIGN KEY (PublishID) REFERENCES control.PublishRun(PublishID)
    );
END;
GO

CREATE OR ALTER PROCEDURE dbo.PublishApprovedClioStagingRun
    @RunID uniqueidentifier,
    @DryRun bit = 1,
    @RebuildReporting bit = 1
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    /*
        This is deliberately a full-snapshot publisher.  It publishes only
        companies covered by the approved staging run and only the 19 tables
        below.  It never publishes legacy-only TableInventory candidates.
    */
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
    DECLARE @StepStarted datetime2(7);

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

    CREATE TABLE #RunCompanies
    (
        CompanyID uniqueidentifier NOT NULL PRIMARY KEY
    );

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

    EXEC @LockResult = sys.sp_getapplock
        @Resource = N'ClioStagingToEKPBIPublish',
        @LockMode = N'Exclusive',
        @LockOwner = N'Session',
        @LockTimeout = 0,
        @DbPrincipal = N'public';

    IF @LockResult < 0
        THROW 51031, 'Another publication is already running. No changes were made.', 1;

    BEGIN TRY
        /* A staging extraction started after this run can have cleared its buffers. */
        IF EXISTS (SELECT 1 FROM [Clio_Staging].control.SyncRun WHERE Status = 'RUNNING')
            THROW 51032, 'A staging extraction is RUNNING. Wait for it to finish before publishing.', 1;

        IF NOT EXISTS
        (
            SELECT 1
            FROM [Clio_Staging].control.SyncRun
            WHERE RunID = @RunID AND Status = 'EXTRACTED' AND Mode = 'FULL'
        )
            THROW 51033, 'RunID is not a completed FULL extraction.', 1;

        IF EXISTS
        (
            SELECT 1
            FROM [Clio_Staging].control.SyncRun AS later
            INNER JOIN [Clio_Staging].control.SyncRun AS approved ON approved.RunID = @RunID
            WHERE later.RunID <> @RunID
              AND later.StartedAtUTC > approved.StartedAtUTC
        )
            THROW 51034, 'A later staging run exists. Publish only the latest started extraction to avoid using overwritten buffers.', 1;

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

        IF NOT EXISTS
        (
            SELECT 1
            FROM dbo.CSPViewsToTable
            WHERE Active = 1 AND ConvertToTable = 1
        )
            THROW 51038, 'No active reporting-table configuration exists in dbo.CSPViewsToTable.', 1;

        /* Validate table existence, column definitions, and the explicit-ID strategy. */
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

        /* The current raw tables intentionally use Clio IDs as explicit identity values. */
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

        IF EXISTS
        (
            SELECT 1 FROM #SourceValidation WHERE AuditRowsWritten <> SourceRows
        )
            THROW 51042, 'Current staging rows no longer match the approved run audit. The buffers changed after extraction.', 1;

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

        IF @DryRun = 1
        BEGIN
            SELECT @RunID AS SourceRunID, CAST(1 AS bit) AS DryRun,
                   (SELECT COUNT(*) FROM #RunCompanies) AS CompanyCount,
                   (SELECT SUM(SourceRows) FROM #PublishPlan) AS TotalSourceRows,
                   (SELECT SUM(TargetRowsToDelete) FROM #PublishPlan) AS TotalProductionRowsToReplace,
                   @RebuildReporting AS WillRebuildReporting;
            SELECT TableName, SourceRows, TargetRowsToDelete FROM #PublishPlan ORDER BY TableName;
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

        /*
           Match the production full-sync reporting sequence in the same transaction
           as the approved 19-table raw publication.  The order is intentionally the
           only for the five internal CSP.Clio.Sync SyncMatters PreSyncViews.
           Do not use PopulateTables with a NULL view name here: that rebuilds every
           active CSPViewsToTable row and duplicates these five view rebuilds.
           MatterSyncJob-only views and all external matters/fees/expenses/documents
           API pushes are intentionally outside this pipeline.
        */
        SET @StepStarted = SYSDATETIME();
        EXEC dbo.SP_CustomExecAfterSync;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.Bills AS b
            INNER JOIN dbo._EKPCompanies AS c ON c.ID = b.CompanyID
            INNER JOIN #RunCompanies AS rc ON rc.CompanyID = b.CompanyID
            WHERE b.Currency = 0 AND ISNULL(c.MainCurrencyID, 0) <> 0
        )
            THROW 51044, 'SP_CustomExecAfterSync did not populate Currency for one or more published bills.', 1;

        EXEC dbo.SP_InsertIntoMattersDetailedCustomFields;

        EXEC dbo.PopulateMatterNotesView;

        SET @StepStarted = SYSDATETIME();
        EXEC dbo.SP_UpdateNonBillableActivities;

        IF EXISTS
        (
            SELECT 1
            FROM dbo.Activities AS a
            INNER JOIN dbo.VW_Matters AS m ON m.InternalID = a.Matter
            INNER JOIN #RunCompanies AS rc ON rc.CompanyID = a.CompanyID
            WHERE m.Billable = 0 AND a.Non_Billable = 0
        )
            THROW 51045, 'SP_UpdateNonBillableActivities did not update one or more published activities.', 1;

        IF @RebuildReporting = 1
        BEGIN
            EXEC dbo.UpdateExpectedHoursDaily;

            /* Keep the second update from the existing full-sync reporting sequence. */
            EXEC dbo.SP_UpdateNonBillableActivities;

            EXEC dbo.SP_AddUserGroupReportTo;

            /* Minimum local reporting allow-list: same five internal CSP.Clio.Sync PreSyncViews. */
            EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'VW_Users', @EverySync = 0;
            EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'VW_Contacts', @EverySync = 0;
            EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'VW_Folders', @EverySync = 0;
            EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'VW_PracticeAreas', @EverySync = 0;
            EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'VW_Matters', @EverySync = 0;
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
    First run — validation only, no raw/reporting data changes:

    EXEC dbo.PublishApprovedClioStagingRun
        @RunID = '55045B39-80F0-4D2E-A114-07CA08B59C04',
        @DryRun = 1,
        @RebuildReporting = 1;

    After reviewing the dry-run output and taking a database backup, the
    explicitly approved production run would be:

    EXEC dbo.PublishApprovedClioStagingRun
        @RunID = '55045B39-80F0-4D2E-A114-07CA08B59C04',
        @DryRun = 0,
        @RebuildReporting = 1;
*/

