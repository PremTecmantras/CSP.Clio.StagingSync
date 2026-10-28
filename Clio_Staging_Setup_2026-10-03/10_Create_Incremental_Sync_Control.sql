/*
    Incremental polling control objects for Clio_Staging.

    Run once in Clio_Staging as the DBA before enabling
    CSP.Clio.StagingSync.exe --incremental.

    This script creates audit/watermark tables only. It does not change raw
    Clio data and it does not enable the feature in App.config.
*/
USE [Clio_Staging];
GO

IF SCHEMA_ID(N'control') IS NULL
    EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
GO

IF OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NULL
BEGIN
    CREATE TABLE control.IncrementalSyncRun
    (
        IncrementalRunID uniqueidentifier NOT NULL CONSTRAINT PK_IncrementalSyncRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_IncrementalSyncRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL,
        ErrorMessage nvarchar(3500) NULL,
        CONSTRAINT CK_IncrementalSyncRun_Status CHECK (Status IN ('RUNNING','COMPLETE','FAILED','ABANDONED'))
    );
END
GO

IF OBJECT_ID(N'control.IncrementalSyncResult', N'U') IS NULL
BEGIN
    CREATE TABLE control.IncrementalSyncResult
    (
        IncrementalResultID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_IncrementalSyncResult PRIMARY KEY,
        IncrementalRunID uniqueidentifier NOT NULL,
        CompanyID uniqueidentifier NOT NULL,
        Endpoint sysname NOT NULL,
        WatermarkFromUTC datetime2(7) NOT NULL,
        WatermarkToUTC datetime2(7) NULL,
        RowsReceived bigint NOT NULL CONSTRAINT DF_IncrementalSyncResult_RowsReceived DEFAULT 0,
        Status varchar(20) NOT NULL,
        ErrorMessage nvarchar(3500) NULL,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_IncrementalSyncResult_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        CONSTRAINT FK_IncrementalSyncResult_Run FOREIGN KEY (IncrementalRunID)
            REFERENCES control.IncrementalSyncRun(IncrementalRunID),
        CONSTRAINT UQ_IncrementalSyncResult_RunCompanyEndpoint UNIQUE (IncrementalRunID, CompanyID, Endpoint),
        CONSTRAINT CK_IncrementalSyncResult_Status CHECK (Status IN ('RUNNING','COMPLETE','FAILED','SKIPPED'))
    );
END
GO

IF OBJECT_ID(N'control.IncrementalWatermark', N'U') IS NULL
BEGIN
    CREATE TABLE control.IncrementalWatermark
    (
        CompanyID uniqueidentifier NOT NULL,
        Endpoint sysname NOT NULL,
        LastSuccessfulToUTC datetime2(7) NOT NULL,
        LastIncrementalRunID uniqueidentifier NULL,
        UpdatedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_IncrementalWatermark_UpdatedAtUTC DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_IncrementalWatermark PRIMARY KEY (CompanyID, Endpoint)
    );
END
GO

/*
    Safety policy: physical deletions are not inferred from an updated_since
    poll. The daily full extraction remains the authoritative reconciliation.
    Suspected deletions may be recorded here later by an ID-only reconciliation
    job, then confirmed through an individual API GET before any SQL row changes.
*/
IF OBJECT_ID(N'control.IncrementalDeletionCandidate', N'U') IS NULL
BEGIN
    CREATE TABLE control.IncrementalDeletionCandidate
    (
        DeletionCandidateID bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_IncrementalDeletionCandidate PRIMARY KEY,
        CompanyID uniqueidentifier NOT NULL,
        Endpoint sysname NOT NULL,
        ClioInternalID bigint NOT NULL,
        FirstSuspectedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_IncrementalDeletionCandidate_FirstSuspectedAtUTC DEFAULT SYSUTCDATETIME(),
        LastCheckedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL CONSTRAINT DF_IncrementalDeletionCandidate_Status DEFAULT 'SUSPECTED',
        ConfirmationDetail nvarchar(3500) NULL,
        CONSTRAINT UQ_IncrementalDeletionCandidate UNIQUE (CompanyID, Endpoint, ClioInternalID),
        CONSTRAINT CK_IncrementalDeletionCandidate_Status CHECK (Status IN ('SUSPECTED','CONFIRMED_DELETED','NOT_DELETED','RETRY'))
    );
END
GO

IF NOT EXISTS
(
    SELECT 1
    FROM sys.indexes
    WHERE object_id = OBJECT_ID(N'control.IncrementalSyncResult')
      AND name = N'IX_IncrementalSyncResult_Run_Status'
)
    CREATE INDEX IX_IncrementalSyncResult_Run_Status
        ON control.IncrementalSyncResult(IncrementalRunID, Status);
GO
