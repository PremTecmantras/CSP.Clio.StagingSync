/* READ-ONLY diagnostics after staging test; not a publish validator.
   No concurrent ingestion while comparing.
   PASS when absolute row-count difference is <= 10% of production count.
   FAIL when difference is > 10%.
   Counts alone do not prove API completeness or record-level equality. */

USE [Clio_Staging];
SET NOCOUNT ON;

IF OBJECT_ID('tempdb..#Counts') IS NOT NULL
    DROP TABLE #Counts;

IF OBJECT_ID(N'control.BootstrapInfo', N'U') IS NULL
    THROW 51000, 'Bootstrap is incomplete.', 1;

SELECT * FROM control.BootstrapInfo;

SELECT
    ID, CompanyName, Active, LastRefreshTime, LastSyncTime
FROM dbo._EKPCompanies;

SELECT * FROM control.SyncRun
ORDER BY StartedAtUTC DESC;

SELECT * FROM control.SyncEndpointResult
ORDER BY RunID, CompanyID, Endpoint;

SELECT TOP (100)
    ID, [Type], [Message], ExtraInfo, DateCreated
FROM dbo.ErrorInfo
ORDER BY ID DESC;

CREATE TABLE #Counts
(
    TableName sysname NOT NULL,
    CompanyID uniqueidentifier NULL,
    StageRows bigint NOT NULL,
    ProductionRows bigint NOT NULL
);

DECLARE @t sysname, @sql nvarchar(max);

DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT TableName
    FROM control.TableInventory
    WHERE Category = 'CANDIDATE_RAW'
    ORDER BY TableName;

OPEN c;
FETCH NEXT FROM c INTO @t;

WHILE @@FETCH_STATUS = 0
BEGIN
    IF COL_LENGTH(N'dbo.' + QUOTENAME(@t), N'CompanyID') IS NOT NULL
    BEGIN
        SET @sql = N'
            ;WITH s AS
            (
                SELECT CompanyID, COUNT_BIG(*) AS n
                FROM dbo.' + QUOTENAME(@t) + N'
                GROUP BY CompanyID
            ),
            p AS
            (
                SELECT CompanyID, COUNT_BIG(*) AS n
                FROM EKPBI_New_Dev.dbo.' + QUOTENAME(@t) + N'
                GROUP BY CompanyID
            )
            INSERT INTO #Counts
                (TableName, CompanyID, StageRows, ProductionRows)
            SELECT
                @table,
                COALESCE(s.CompanyID, p.CompanyID),
                ISNULL(s.n, 0),
                ISNULL(p.n, 0)
            FROM s
            FULL OUTER JOIN p
                ON s.CompanyID = p.CompanyID
                OR (s.CompanyID IS NULL AND p.CompanyID IS NULL);';
    END
    ELSE
    BEGIN
        SET @sql = N'
            INSERT INTO #Counts
                (TableName, CompanyID, StageRows, ProductionRows)
            SELECT
                @table,
                NULL,
                (SELECT COUNT_BIG(*) FROM dbo.' + QUOTENAME(@t) + N'),
                (SELECT COUNT_BIG(*) FROM EKPBI_New_Dev.dbo.' + QUOTENAME(@t) + N');';
    END;

    EXEC sys.sp_executesql
        @sql,
        N'@table sysname',
        @table = @t;

    FETCH NEXT FROM c INTO @t;
END;

CLOSE c;
DEALLOCATE c;

;WITH Comparison AS
(
    SELECT
        c.TableName,
        c.CompanyID,
        e.CompanyName,
        c.StageRows,
        c.ProductionRows,
        c.StageRows - c.ProductionRows AS Difference,
        ABS(c.StageRows - c.ProductionRows) AS AbsoluteDifference,
        CASE
            WHEN c.ProductionRows = 0 AND c.StageRows = 0
                THEN CAST(0 AS decimal(18,2))
            WHEN c.ProductionRows = 0
                THEN NULL
            ELSE CONVERT(
                decimal(18,2),
                100.0 * ABS(c.StageRows - c.ProductionRows)
                / NULLIF(c.ProductionRows, 0)
            )
        END AS ChangePct
    FROM #Counts AS c
    LEFT JOIN dbo._EKPCompanies AS e
        ON e.ID = c.CompanyID
),
Validation AS
(
    SELECT *,
        CASE
            WHEN ProductionRows = 0 AND StageRows = 0
                THEN 'PASS'
            WHEN ProductionRows = 0 AND StageRows > 0
                THEN 'FAIL'
            WHEN ChangePct <= 10.00
                THEN 'PASS'
            ELSE 'FAIL'
        END AS ValidationStatus
    FROM Comparison
)
SELECT
    TableName,
    CompanyID,
    CompanyName,
    StageRows,
    ProductionRows,
    Difference,
    ChangePct,
    CASE
        WHEN ProductionRows = 0 AND StageRows = 0
            THEN 'Both staging and production contain zero rows.'
        WHEN ProductionRows = 0 AND StageRows > 0
            THEN 'Production has zero rows, but staging contains rows.'
        WHEN Difference = 0
            THEN 'Row counts match exactly.'
        WHEN ChangePct <= 10.00
            THEN 'Row-count difference is within the 10% threshold.'
        ELSE 'Row-count difference exceeds the 10% threshold.'
    END AS ValidationResult,
    ValidationStatus
FROM Validation
ORDER BY TableName, CompanyName;

SELECT
    CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM #Counts
            WHERE
                (ProductionRows = 0 AND StageRows > 0)
                OR
                (
                    ProductionRows > 0
                    AND ABS(StageRows - ProductionRows) * 100.0
                        / NULLIF(ProductionRows, 0) > 10.0
                )
        )
        THEN 'FAIL'
        ELSE 'PASS'
    END AS OverallValidationStatus,
    CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM #Counts
            WHERE
                (ProductionRows = 0 AND StageRows > 0)
                OR
                (
                    ProductionRows > 0
                    AND ABS(StageRows - ProductionRows) * 100.0
                        / NULLIF(ProductionRows, 0) > 10.0
                )
        )
        THEN 'One or more table/company row counts differ by more than 10%.'
        ELSE 'All table/company row counts are within the 10% threshold.'
    END AS OverallValidationResult;

SELECT
    N'DIAGNOSTICS ONLY: row-count validation does not prove record-level equality, API completeness, or production publish readiness. Production publishing is not installed.'
    AS Result;