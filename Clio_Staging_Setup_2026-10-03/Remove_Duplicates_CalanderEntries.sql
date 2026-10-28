GO
 
SET XACT_ABORT ON;
 
BEGIN TRANSACTION;
 
;WITH RankedEntries AS
(
    SELECT
        ID,
        ROW_NUMBER() OVER
        (
            PARTITION BY ETag
            ORDER BY ID DESC
        ) AS RowNum
    FROM dbo.CalendarEntries
    WHERE ETag IS NOT NULL
)
DELETE FROM dbo.CalendarEntries
WHERE ID IN
(
    SELECT ID
    FROM RankedEntries
    WHERE RowNum > 1
);
 
DECLARE @DeletedRows INT = @@ROWCOUNT;
 
SELECT @DeletedRows AS DeletedRecords;
 
-- Verify the deleted count before choosing:
-- COMMIT TRANSACTION;   -- Keep the deletion
-- ROLLBACK TRANSACTION; -- Undo the deletion
COMMIT TRANSACTION;