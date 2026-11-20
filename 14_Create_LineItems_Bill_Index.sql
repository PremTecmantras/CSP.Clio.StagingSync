/*
  14_Create_LineItems_Bill_Index.sql   (run in Clio_Staging)

  Why: the reporting build (PopulateTables, about 38 of the 42 minutes of a publish on the server) calls the scalar function
  dbo.Get_Bill_DiscountPerItem once per line item. The function runs
      SELECT count(*) FROM dbo.LineItems WHERE [Bill] = @billID AND [Activity] IS NOT NULL
  and dbo.LineItems only has its clustered primary key on ID, so every call scans the whole table
  (a live sample of the build showed ~294 million logical reads in one statement after 8 minutes).
  With this index each call is a few page reads.

  Safe to run twice. Reversible:  DROP INDEX IX_LineItems_Bill_Activity ON dbo.LineItems;
  Run it when no incremental pass, delete pass or publish is running (the table is locked for the few seconds the index needs).
  It only changes Clio_Staging; the index is not copied to the target database.
  ONLINE = ON is deliberately not used: LocalDB / Express do not support it, and the build takes seconds on ~160k rows.
*/
IF DB_NAME() <> N'Clio_Staging'
    THROW 51400, 'Run this script in the Clio_Staging database.', 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.LineItems') AND name = N'IX_LineItems_Bill_Activity')
    CREATE NONCLUSTERED INDEX IX_LineItems_Bill_Activity
    ON dbo.LineItems ([Bill]) INCLUDE ([Activity]);
GO

SELECT i.name AS IndexName, i.type_desc, i.is_disabled
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID(N'dbo.LineItems');
GO
