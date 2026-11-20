/*
  16_Release_Stale_Publish_Lease.sql

  Use ONLY after error 51048 "Another raw publication lease is already RUNNING" when no publish is really running.
  Cause: a publish process was ended in the middle of dbo.PublishApprovedClioStagingRun (supervisor stopped, console closed, machine restarted).
  SQL Server rolled the publish transaction back, so no data of that publish is in the target, but the lease row in Clio_Staging stays RUNNING
  and every later publish stops at control.BeginRawPublishLease.

  The script refuses to change anything while a publish session is alive (including one that is still rolling back).
  Run it in SSMS against the instance that holds Clio_Staging and EKPBI_New_Dev. Nothing outside the two audit/lease rows is touched.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

USE [Clio_Staging];
GO

PRINT '--- 1. The lease rows now ---';
SELECT LeaseName, PublishID, Status, StartedAtUTC, CompletedAtUTC, ErrorMessage FROM control.PublishLease;

PRINT '--- 2. Sessions that look like a publish (must be empty) ---';
SELECT s.session_id, s.status, s.program_name, s.login_name, s.last_request_start_time, r.command, r.percent_complete
FROM sys.dm_exec_sessions AS s
LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
WHERE s.session_id <> @@SPID
  AND (s.program_name LIKE N'CSP.Clio.StagingSync.Publish%' OR r.command LIKE N'%ROLLBACK%');

PRINT '--- 3. Holder of the publish application lock (must be empty) ---';
SELECT request_session_id, resource_description
FROM sys.dm_tran_locks
WHERE resource_type = N'APPLICATION' AND resource_description LIKE N'%ClioStagingToEKPBIPublish%';

IF EXISTS
(
    SELECT 1
    FROM sys.dm_exec_sessions AS s
    LEFT JOIN sys.dm_exec_requests AS r ON r.session_id = s.session_id
    WHERE s.session_id <> @@SPID
      AND (s.program_name LIKE N'CSP.Clio.StagingSync.Publish%' OR r.command LIKE N'%ROLLBACK%')
)
OR EXISTS
(
    SELECT 1 FROM sys.dm_tran_locks
    WHERE resource_type = N'APPLICATION' AND resource_description LIKE N'%ClioStagingToEKPBIPublish%'
)
    THROW 51900, 'A publish session is still alive or rolling back. Nothing was changed. Wait until it has ended, then run this script again.', 1;

IF EXISTS (SELECT 1 FROM Clio_Staging.control.SyncRun WHERE Status = 'RUNNING')
    THROW 51901, 'A staging extraction is RUNNING. Nothing was changed.', 1;

PRINT '--- 4. Releasing the stale lease and closing its audit row ---';
DECLARE @PublishID uniqueidentifier;
SELECT @PublishID = PublishID FROM control.PublishLease WHERE LeaseName = N'RAW_PUBLISH' AND Status = 'RUNNING';

IF @PublishID IS NULL
BEGIN
    PRINT 'No RUNNING lease: nothing to release.';
END
ELSE
BEGIN
    EXEC control.EndRawPublishLease
        @PublishID = @PublishID,
        @Status = 'FAILED',
        @ErrorMessage = N'Released manually: the publish process ended before it finished and its transaction was rolled back.';

    UPDATE [EKPBI_New_Dev].control.PublishRun
    SET Status = 'FAILED',
        CompletedAtUTC = SYSUTCDATETIME(),
        ErrorMessage = N'Released manually: the publish process ended before it finished and its transaction was rolled back.'
    WHERE PublishID = @PublishID AND Status = 'RUNNING';

    PRINT 'Released lease of publish ' + CONVERT(varchar(36), @PublishID) + '.';
END;

PRINT '--- 5. Result ---';
SELECT LeaseName, PublishID, Status, StartedAtUTC, CompletedAtUTC FROM control.PublishLease;
SELECT TOP (5) PublishID, SourceRunID, Status, StartedAtUTC, CompletedAtUTC FROM [EKPBI_New_Dev].control.PublishRun ORDER BY StartedAtUTC DESC;
GO
