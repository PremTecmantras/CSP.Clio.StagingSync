/*
  Read-only diagnostics for day-first date text in raw tables (see StagingSync culture fix).
  Run in Clio_Staging and in EKPBI_New_Dev (and EKPBI_New to see the clean baseline).
  Raw columns are text; reporting views CAST them under us_english, so dd-MM-yyyy values either
  fail (day > 12) or silently swap day and month (day <= 12).
*/

------------------------------------------------------------------
-- 1. Per table/column: how many rows are day-first, fail, or silently swap
------------------------------------------------------------------
SET NOCOUNT ON;
DECLARE @tables TABLE (t sysname, c sysname);
INSERT @tables VALUES
 ('Activities','Created_At'),('Activities','Updated_At'),
 ('LineItems','Created_At'),('LineItems','Updated_At'),
 ('Bills','Created_At'),('Bills','Updated_At'),
 ('Allocations','Created_At'),('Allocations','Updated_At'),('Allocations','Voided_At'),
 ('Folders','Created_At'),('Folders','Updated_At'),
 ('Contacts','Created_At'),('Contacts','Updated_At'),
 ('Relationships','Created_At'),('Relationships','Updated_At'),
 ('Matters','Created_At'),('Matters','Updated_At'),
 ('Tasks','Created_At'),('Tasks','Updated_At'),('Tasks','Completed_At'),
 ('Users','Created_At'),('Users','Updated_At'),
 ('MatterNotes','Created_At'),('MatterNotes','Updated_At'),
 ('ActivityDescriptions','Created_At'),('ActivityDescriptions','Updated_At'),
 ('PracticeAreas','Created_At'),('PracticeAreas','Updated_At');

DECLARE @sql nvarchar(max) = N'';
SELECT @sql += N'SELECT ''' + t + N'.' + c + N''' AS TableColumn, COUNT(*) AS TotalRows,
  SUM(CASE WHEN v LIKE ''[0-3][0-9]-[0-1][0-9]-[0-9][0-9][0-9][0-9]%'' THEN 1 ELSE 0 END) AS DayFirstRows,
  SUM(CASE WHEN v LIKE ''[0-3][0-9]-[0-1][0-9]-[0-9][0-9][0-9][0-9]%'' AND TRY_CONVERT(datetime, v) IS NULL THEN 1 ELSE 0 END) AS FailsCast,
  SUM(CASE WHEN v LIKE ''[0-3][0-9]-[0-1][0-9]-[0-9][0-9][0-9][0-9]%'' AND TRY_CONVERT(datetime, v) IS NOT NULL THEN 1 ELSE 0 END) AS SilentlySwapped
 FROM (SELECT CAST(' + QUOTENAME(c) + N' AS varchar(60)) AS v FROM dbo.' + QUOTENAME(t) + N') s UNION ALL '
FROM @tables WHERE OBJECT_ID('dbo.' + t) IS NOT NULL;
SET @sql = LEFT(@sql, LEN(@sql) - 10) + N' ORDER BY 3 DESC';
EXEC (@sql);

------------------------------------------------------------------
-- 2. Rows that FAIL the cast (these break VW_Clients / PopulateTables). Example: Contacts.
--    Change the table and column to inspect others.
------------------------------------------------------------------
SELECT TOP 50 ID, CompanyID,
       CAST(Created_At AS varchar(60)) AS RawCreated_At,
       CAST(Updated_At AS varchar(60)) AS RawUpdated_At
FROM dbo.Contacts
WHERE TRY_CONVERT(datetime, CAST(Created_At AS varchar(60))) IS NULL
   OR TRY_CONVERT(datetime, CAST(Updated_At AS varchar(60))) IS NULL;

------------------------------------------------------------------
-- 3. Rows that SILENTLY swap day and month: raw text vs what the views read vs the true date.
--    Example: Allocations (drives AllocationDate in the collection reports).
------------------------------------------------------------------
SELECT TOP 100 ID, CompanyID,
       CAST(Created_At AS varchar(60))                                                   AS RawText,
       TRY_CONVERT(datetime, CAST(Created_At AS varchar(60)))                            AS WhatViewsRead,
       TRY_CONVERT(datetime, REPLACE(CAST(Created_At AS varchar(60)), '.', ':'), 105)    AS TrueDate
FROM dbo.Allocations
WHERE CAST(Created_At AS varchar(60)) LIKE '[0-3][0-9]-[0-1][0-9]-[0-9][0-9][0-9][0-9]%'
  AND TRY_CONVERT(datetime, CAST(Created_At AS varchar(60))) IS NOT NULL
  AND TRY_CONVERT(datetime, CAST(Created_At AS varchar(60)))
      <> TRY_CONVERT(datetime, REPLACE(CAST(Created_At AS varchar(60)), '.', ':'), 105);

------------------------------------------------------------------
-- 4. Reporting tables that depend on the affected columns (rebuilt by PopulateTables)
------------------------------------------------------------------
SELECT v.name AS ViewName
FROM sys.views v
INNER JOIN sys.sql_modules m ON m.object_id = v.object_id
WHERE v.name IN (SELECT ViewName FROM dbo.CSPViewsToTable WHERE Active = 1 AND ConvertToTable = 1)
  AND (m.definition LIKE '%Created_At%' OR m.definition LIKE '%Updated_At%'
    OR m.definition LIKE '%Voided_At%'  OR m.definition LIKE '%Completed_At%')
ORDER BY v.name;
