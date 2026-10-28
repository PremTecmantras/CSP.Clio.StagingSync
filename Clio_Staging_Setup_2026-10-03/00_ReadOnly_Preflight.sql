/* READ ONLY. Run in SSMS on the instance hosting EKPBI_New_Dev.
   No tokens, passwords or API secrets are selected. Save all result sets.
   Baseline: scripts(3).sql, 2026-09-27. No production changes. */
USE [EKPBI_New_Dev];
SET NOCOUNT ON;
SELECT @@SERVERNAME AS ServerName, SERVERPROPERTY('ProductVersion') AS ProductVersion,
       SERVERPROPERTY('Edition') AS Edition;
SELECT name, state_desc, compatibility_level, collation_name, recovery_model_desc,
       is_read_committed_snapshot_on, is_trustworthy_on, is_db_chaining_on
FROM sys.databases WHERE name IN (N'EKPBI_New_Dev',N'Clio_Staging');
SELECT name, type_desc, size/128.0 AS AllocatedMB, physical_name FROM sys.database_files;
SELECT ID,CompanyName,Active,LastRefreshTime,LastSyncTime,FullSyncStart,FullSyncStop
FROM dbo._EKPCompanies ORDER BY CompanyName;
SELECT s.name AS SchemaName,t.name AS TableName,SUM(p.rows) AS ApproxRows
FROM sys.tables t JOIN sys.schemas s ON t.schema_id=s.schema_id
LEFT JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1)
GROUP BY s.name,t.name ORDER BY s.name,t.name;
SELECT SCHEMA_NAME(o.schema_id) AS SchemaName,o.name, m.definition
FROM sys.objects o JOIN sys.sql_modules m ON m.object_id=o.object_id
WHERE o.name IN (N'SP_ClearAllData',N'SP_ClearData',N'SP_CustomExecAfterSync',
N'PopulateTables',N'SwapStagingToFinal',N'SP_CleanData',N'SP_SyncAllData',N'Start_Sync',N'Stop_Sync');
SELECT OBJECT_SCHEMA_NAME(referencing_id) AS SchemaName,
       OBJECT_NAME(referencing_id) AS ObjectName, referenced_server_name,
       referenced_database_name,referenced_schema_name,referenced_entity_name
FROM sys.sql_expression_dependencies
WHERE referenced_database_name IS NOT NULL OR referenced_server_name IS NOT NULL;
SELECT name,base_object_name FROM sys.synonyms;
SELECT OBJECT_SCHEMA_NAME(parent_id) AS SchemaName,OBJECT_NAME(parent_id) AS TableName,
       name AS TriggerName,is_disabled FROM sys.triggers WHERE parent_class=1;
/* Job commands are intentionally excluded because they can contain credentials. */
BEGIN TRY
 SELECT j.name AS JobName,j.enabled,s.step_id,s.step_name,s.subsystem,s.database_name
 FROM msdb.dbo.sysjobs j JOIN msdb.dbo.sysjobsteps s ON j.job_id=s.job_id
 WHERE s.database_name=N'EKPBI_New_Dev' OR j.name LIKE N'%Sync%'
    OR j.name LIKE N'%Populate%' OR j.name LIKE N'%ConvertViews%';
END TRY
BEGIN CATCH
 SELECT N'Job inventory unavailable to this login; DBA must inspect it.' AS Note;
END CATCH;
