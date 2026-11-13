/*
  Reset ONLY dbo.Users in EKPBI_New_Dev from Prod (EKPBI_New).
  - Keeps a backup copy of the old Dev Users table (Users_bak_<date>).
  - Empties Dev Users and copies every row from Prod Users.
  - Touches no other table. Prod is only read.
  - All in one transaction: any error rolls everything back.
  Run in SSMS on the server (Dev and Prod must be on the same SQL Server instance).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

-- 1. Common, writable columns (same name in both, not computed / timestamp)
DECLARE @cols nvarchar(max);
SELECT @cols = STRING_AGG(CAST(QUOTENAME(d.name) AS nvarchar(max)), N', ')
FROM EKPBI_New_Dev.sys.columns d
JOIN EKPBI_New.sys.columns p ON p.name = d.name AND p.object_id = OBJECT_ID(N'EKPBI_New.dbo.Users')
WHERE d.object_id = OBJECT_ID(N'EKPBI_New_Dev.dbo.Users')
  AND d.is_computed = 0 AND p.is_computed = 0
  AND d.system_type_id <> 189;   -- timestamp/rowversion

IF @cols IS NULL THROW 50001, 'Users table not found in Dev or Prod.', 1;

DECLARE @hasIdentity bit = 0;
IF EXISTS (SELECT 1 FROM EKPBI_New_Dev.sys.columns
           WHERE object_id = OBJECT_ID(N'EKPBI_New_Dev.dbo.Users') AND is_identity = 1)
    SET @hasIdentity = 1;

DECLARE @bak sysname = N'Users_bak_' + CONVERT(char(8), GETDATE(), 112);
DECLARE @sql nvarchar(max);

DECLARE @devBefore int, @prodBefore int;
SELECT @devBefore = COUNT(*) FROM EKPBI_New_Dev.dbo.Users;
SELECT @prodBefore = COUNT(*) FROM EKPBI_New.dbo.Users;
PRINT 'Rows before: Dev=' + CONVERT(varchar(20), @devBefore) + '  Prod=' + CONVERT(varchar(20), @prodBefore);

BEGIN TRANSACTION;

-- 2. Backup of the current Dev Users (skipped if today's backup already exists)
DECLARE @bakFull nvarchar(300) = N'EKPBI_New_Dev.dbo.' + @bak;
IF OBJECT_ID(@bakFull) IS NULL
BEGIN
    SET @sql = N'SELECT * INTO EKPBI_New_Dev.dbo.' + QUOTENAME(@bak) + N' FROM EKPBI_New_Dev.dbo.Users;';
    EXEC (@sql);
    PRINT 'Backup created: ' + @bakFull;
END

-- 3. Empty Dev Users (DELETE, because TRUNCATE is refused when foreign keys point at the table)
DELETE FROM EKPBI_New_Dev.dbo.Users;

-- 4. Copy from Prod
SET @sql = N'';
IF @hasIdentity = 1 SET @sql += N'SET IDENTITY_INSERT EKPBI_New_Dev.dbo.Users ON; ';
SET @sql += N'INSERT INTO EKPBI_New_Dev.dbo.Users (' + @cols + N') SELECT ' + @cols + N' FROM EKPBI_New.dbo.Users; ';
IF @hasIdentity = 1 SET @sql += N'SET IDENTITY_INSERT EKPBI_New_Dev.dbo.Users OFF; ';
EXEC (@sql);

-- 5. Verify, then commit only when the counts match
DECLARE @d int, @p int;
SELECT @d = COUNT(*) FROM EKPBI_New_Dev.dbo.Users;
SELECT @p = COUNT(*) FROM EKPBI_New.dbo.Users;
IF @d <> @p
BEGIN
    ROLLBACK TRANSACTION;
    THROW 50002, 'Row counts differ after the copy. Rolled back, nothing changed.', 1;
END

COMMIT TRANSACTION;
PRINT 'Done. Dev Users = Prod Users = ' + CONVERT(varchar(20), @d) + ' rows.';
