/*
    One-time upgrade for an existing Clio_Staging database.

    The original SP_ClearAllData omitted CalendarEntries even though it is one
    of the 19 active extraction endpoints. Without this change, a new full
    sync can retain calendar entries from a prior run.

    Run in SSMS while connected to Clio_Staging. This only changes the local
    reset procedure; it does not clear data until the next EXE full-sync run.
*/
USE [Clio_Staging];
GO

ALTER PROCEDURE dbo.SP_ClearAllData
AS
BEGIN
    SET NOCOUNT ON;

    TRUNCATE TABLE dbo.Bills;
    TRUNCATE TABLE dbo.Allocations;
    TRUNCATE TABLE dbo.Activities;
    TRUNCATE TABLE dbo.ActivityDescriptions;
    TRUNCATE TABLE dbo.Matters;
    TRUNCATE TABLE dbo.Contacts;
    TRUNCATE TABLE dbo.Users;
    TRUNCATE TABLE dbo.PracticeAreas;
    TRUNCATE TABLE dbo.CustomFields;
    TRUNCATE TABLE dbo.CustomFieldSets;
    TRUNCATE TABLE dbo.LineItems;
    TRUNCATE TABLE dbo.Folders;
    TRUNCATE TABLE dbo.BankTransactions;
    TRUNCATE TABLE dbo.Calendars;
    TRUNCATE TABLE dbo.CalendarEntries;
    TRUNCATE TABLE dbo.Relationships;
    TRUNCATE TABLE dbo.Tasks;
    TRUNCATE TABLE dbo.TaskTypes;
    TRUNCATE TABLE dbo.MatterNotes;

    UPDATE dbo._EKPCompanies
    SET LastRefreshTime = NULL,
        LastSyncTime = NULL;
END;
GO
