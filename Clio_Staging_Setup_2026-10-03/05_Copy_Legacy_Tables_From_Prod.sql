/* Restores the staging TableInventory manifest and copies the 7 legacy Clio tables from EKPBI_New_Dev into Clio_Staging.
   In production these are one-time 2020 snapshots written by an older tool: they have no CompanyID
   and the current sync (CSP.Clio.Sync) never extracts or refreshes them. Staging therefore copies
   them as-is instead of calling the Clio API, so the 10% validation compares like with like.
     BankAccounts, CreditMemos, Currencies, CustomActions, DocumentCategories,
     ExpenseCategories, TextSnippets.
   control.TableInventory is staging bootstrap metadata, not a production source table. It is seededs
   here because 02_Staging_Test_Diagnostics.sql requires it to select the candidate raw tables.
   Reads EKPBI_New_Dev only. Keeps production's ID values (IDENTITY_INSERT).
   Safe to rerun: inserts only IDs that are missing in staging, never updates or deletes. */
USE [Clio_Staging];
SET NOCOUNT ON;
SET XACT_ABORT ON;
IF DB_ID(N'EKPBI_New_Dev') IS NULL THROW 51000,'EKPBI_New_Dev is not on this instance.',1;

DECLARE @tables TABLE (Seq int IDENTITY(1,1), TableName sysname);
INSERT @tables(TableName) VALUES
 (N'BankAccounts'),(N'CreditMemos'),(N'Currencies'),(N'CustomActions'),
 (N'DocumentCategories'),(N'ExpenseCategories'),(N'TextSnippets');

BEGIN TRY
BEGIN TRANSACTION;

/* Restore every missing manifest entry without replacing any existing category. */
INSERT control.TableInventory(TableName,Category)
SELECT v.TableName,v.Category
FROM (VALUES
(N'Contacts','CANDIDATE_RAW'),(N'CustomFields','CANDIDATE_RAW'),(N'Folders','CANDIDATE_RAW'),
(N'PracticeAreas','CANDIDATE_RAW'),(N'Relationships','CANDIDATE_RAW'),(N'TaskTypes','CANDIDATE_RAW'),
(N'Users','CANDIDATE_RAW'),(N'Activities','CANDIDATE_RAW'),(N'ContactNotes','CANDIDATE_RAW'),
(N'MatterNotes','CANDIDATE_RAW'),(N'Allocations','CANDIDATE_RAW'),(N'BankTransactions','CANDIDATE_RAW'),
(N'Bills','CANDIDATE_RAW'),(N'BillThemes','CANDIDATE_RAW'),(N'LineItems','CANDIDATE_RAW'),
(N'Matters','CANDIDATE_RAW'),(N'Tasks','CANDIDATE_RAW'),(N'ActivityDescriptions','CANDIDATE_RAW'),
(N'ActivityRates','CANDIDATE_RAW'),(N'BankAccounts','CANDIDATE_RAW'),(N'BankTransfers','CANDIDATE_RAW'),
(N'CalendarEntries','CANDIDATE_RAW'),(N'Calendars','CANDIDATE_RAW'),(N'Communications','CANDIDATE_RAW'),
(N'ConversationMessages','CANDIDATE_RAW'),(N'Conversations','CANDIDATE_RAW'),(N'CreditCards','CANDIDATE_RAW'),
(N'CreditMemos','CANDIDATE_RAW'),(N'Currencies','CANDIDATE_RAW'),(N'CustomActions','CANDIDATE_RAW'),
(N'CustomFieldSets','CANDIDATE_RAW'),(N'DocumentAutomations','CANDIDATE_RAW'),(N'DocumentCategories','CANDIDATE_RAW'),
(N'Documents','CANDIDATE_RAW'),(N'DocumentTemplates','CANDIDATE_RAW'),(N'ExpenseCategories','CANDIDATE_RAW'),
(N'Groups','CANDIDATE_RAW'),(N'Reminders','CANDIDATE_RAW'),(N'TableSettings','SUPPORT'),
(N'TaskCalendars','CANDIDATE_RAW'),(N'TaskTemplateLists','CANDIDATE_RAW'),(N'TaskTemplates','CANDIDATE_RAW'),
(N'TextSnippets','CANDIDATE_RAW'),(N'Timers','CANDIDATE_RAW'),(N'UserInvitations','CANDIDATE_RAW'),
(N'UserRoles','CANDIDATE_RAW'),(N'UserTypes','CANDIDATE_RAW'),(N'UtbmsCodes','CANDIDATE_RAW'),
(N'UtbmsSets','CANDIDATE_RAW'),(N'WebHooks','CANDIDATE_RAW'),(N'ActivityTypes','LOOKUP'),
(N'AppCurrencies','LOOKUP'),(N'BillKinds','LOOKUP'),(N'BillStateTypes','LOOKUP'),
(N'BillTypeNames','LOOKUP'),(N'BillTypes','LOOKUP'),(N'MatterStatusTypes','LOOKUP'),
(N'BillingMethods','LOOKUP'),(N'_EKPCompanies','CONFIG'),(N'ErrorInfo','SUPPORT'),(N'TempTables','SUPPORT')
) v(TableName,Category)
WHERE NOT EXISTS (SELECT 1 FROM control.TableInventory i WHERE i.TableName=v.TableName);

PRINT 'TableInventory: ' + CAST(@@ROWCOUNT AS varchar(20)) + ' missing row(s) restored';

DECLARE @i int = 1, @n int = (SELECT COUNT(*) FROM @tables), @t sysname, @cols nvarchar(max), @sql nvarchar(max);
WHILE @i <= @n
BEGIN
    SELECT @t = TableName FROM @tables WHERE Seq = @i;

    /* Columns present in BOTH databases (the bootstrap's schema-drift check makes them identical). */
    SELECT @cols = STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(s.name)), N',') WITHIN GROUP (ORDER BY s.column_id)
    FROM sys.columns s
    WHERE s.object_id = OBJECT_ID(N'dbo.' + QUOTENAME(@t))
      AND s.is_computed = 0
      AND EXISTS (SELECT 1 FROM EKPBI_New_Dev.sys.columns p
                  WHERE p.object_id = OBJECT_ID(N'EKPBI_New_Dev.dbo.' + QUOTENAME(@t))
                    AND p.name = s.name COLLATE DATABASE_DEFAULT);

    IF @cols IS NULL THROW 51003, 'A table was not found in Clio_Staging or EKPBI_New_Dev.', 1;

    SET @sql = N'SET IDENTITY_INSERT dbo.' + QUOTENAME(@t) + N' ON;
INSERT dbo.' + QUOTENAME(@t) + N' (' + @cols + N')
SELECT ' + @cols + N' FROM EKPBI_New_Dev.dbo.' + QUOTENAME(@t) + N' p
WHERE NOT EXISTS (SELECT 1 FROM dbo.' + QUOTENAME(@t) + N' d WHERE d.ID = p.ID);
PRINT ''' + @t + N': '' + CAST(@@ROWCOUNT AS varchar(20)) + '' row(s) copied'';
SET IDENTITY_INSERT dbo.' + QUOTENAME(@t) + N' OFF;';
    EXEC sys.sp_executesql @sql;

    SET @i += 1;
END

COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

/* Check: the staging manifest must contain every bootstrap entry; legacy-table counts should equal production. */
SELECT 'TableInventory' AS TableName, (SELECT COUNT(*) FROM control.TableInventory) AS Staging, 61 AS Production
UNION ALL SELECT 'BankAccounts',      (SELECT COUNT(*) FROM dbo.BankAccounts),      (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.BankAccounts)
UNION ALL SELECT 'CreditMemos',        (SELECT COUNT(*) FROM dbo.CreditMemos),        (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.CreditMemos)
UNION ALL SELECT 'Currencies',         (SELECT COUNT(*) FROM dbo.Currencies),         (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.Currencies)
UNION ALL SELECT 'CustomActions',      (SELECT COUNT(*) FROM dbo.CustomActions),      (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.CustomActions)
UNION ALL SELECT 'DocumentCategories', (SELECT COUNT(*) FROM dbo.DocumentCategories), (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.DocumentCategories)
UNION ALL SELECT 'ExpenseCategories',  (SELECT COUNT(*) FROM dbo.ExpenseCategories),  (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.ExpenseCategories)
UNION ALL SELECT 'TextSnippets',       (SELECT COUNT(*) FROM dbo.TextSnippets),       (SELECT COUNT(*) FROM EKPBI_New_Dev.dbo.TextSnippets);
