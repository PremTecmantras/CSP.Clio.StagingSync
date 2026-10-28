/*
	Clears control/audit history in Clio_Staging only.
	This deletes all SyncRun and SyncEndpointResult history and clears the
	staging PublishLease row. It does not delete control.BootstrapInfo because
	that table contains configuration, not run history.

	Do not run while a staging extraction or publication is active.
*/
USE [Clio_Staging];
GO

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'Clio_Staging'
	THROW 51060, 'Safety stop: this script must run only in Clio_Staging.', 1;

IF OBJECT_ID(N'control.SyncRun', N'U') IS NULL
   OR OBJECT_ID(N'control.SyncEndpointResult', N'U') IS NULL
	THROW 51061, 'Required staging control tables are missing. No data was changed.', 1;

IF EXISTS (SELECT 1 FROM control.SyncRun WHERE Status = 'RUNNING')
	THROW 51062, 'A staging sync is RUNNING. Stop and wait for it to finish before clearing control history.', 1;

IF OBJECT_ID(N'control.PublishLease', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM control.PublishLease WHERE Status = 'RUNNING')
	THROW 51063, 'A publication lease is RUNNING. Wait for publication to finish before clearing control history.', 1;

BEGIN TRANSACTION;

DECLARE @EndpointResultsDeleted bigint = 0;
DECLARE @SyncRunsDeleted bigint = 0;
DECLARE @PublishLeasesDeleted bigint = 0;

DELETE FROM control.SyncEndpointResult;
SET @EndpointResultsDeleted = @@ROWCOUNT;

DELETE FROM control.SyncRun;
SET @SyncRunsDeleted = @@ROWCOUNT;

IF OBJECT_ID(N'control.PublishLease', N'U') IS NOT NULL
BEGIN
	DELETE FROM control.PublishLease;
	SET @PublishLeasesDeleted = @@ROWCOUNT;
END;

COMMIT TRANSACTION;

SELECT @EndpointResultsDeleted AS SyncEndpointResultsDeleted,
	   @SyncRunsDeleted AS SyncRunsDeleted,
	   @PublishLeasesDeleted AS PublishLeasesDeleted;
GO