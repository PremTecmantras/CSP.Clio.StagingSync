/*
    Run this in SSMS while connected to the affected EKPBI_New replica.
    It is safe for a BACPAC schema compatibility repair only: it creates a
    nullable column and refreshes dependent view metadata.  It does NOT
    populate CF_OsamaHasan values.  The normal matter-detail source/view
    mapping must be updated separately if reports need non-NULL values.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.VW_Matters_Detailed_Table', N'U') IS NULL
        THROW 51060, 'dbo.VW_Matters_Detailed_Table was not found as a table in the current database.', 1;

    IF COL_LENGTH(N'dbo.VW_Matters_Detailed_Table', N'CF_OsamaHasan') IS NULL
    BEGIN
        ALTER TABLE dbo.VW_Matters_Detailed_Table
            ADD CF_OsamaHasan nvarchar(max) NULL;
    END;

    IF OBJECT_ID(N'dbo.VW_Matters_Detailed', N'V') IS NULL
        THROW 51061, 'dbo.VW_Matters_Detailed was not found as a view in the current database.', 1;

    /* If the view uses SELECT *, this refresh adds the new table column to its metadata. */
    EXEC sys.sp_refreshview N'dbo.VW_Matters_Detailed';

    IF COL_LENGTH(N'dbo.VW_Matters_Detailed', N'CF_OsamaHasan') IS NULL
        THROW 51062, 'VW_Matters_Detailed has an explicit SELECT list that does not include CF_OsamaHasan. Add the column to that ALTER VIEW definition, then rerun this script.', 1;

    /* Refresh the views named in the BACPAC validation error. */
    IF OBJECT_ID(N'dbo.VW_Remuneration_Matter_Bill_Details', N'V') IS NOT NULL
        EXEC sys.sp_refreshview N'dbo.VW_Remuneration_Matter_Bill_Details';

    IF OBJECT_ID(N'dbo.vw_Bridge_BillPartner', N'V') IS NOT NULL
        EXEC sys.sp_refreshview N'dbo.vw_Bridge_BillPartner';

    IF OBJECT_ID(N'dbo.VW_Remuneration_Details_Internal_Use', N'V') IS NOT NULL
        EXEC sys.sp_refreshview N'dbo.VW_Remuneration_Details_Internal_Use';

    COMMIT TRANSACTION;

    SELECT
        N'PASS: CF_OsamaHasan is available on the matter detailed table and view.' AS Result,
        COL_LENGTH(N'dbo.VW_Matters_Detailed_Table', N'CF_OsamaHasan') AS TableColumnLength,
        COL_LENGTH(N'dbo.VW_Matters_Detailed', N'CF_OsamaHasan') AS ViewColumnLength;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
