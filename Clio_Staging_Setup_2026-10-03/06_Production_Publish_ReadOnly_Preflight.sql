/*
    06_Production_Publish_ReadOnly_Preflight.sql

    Purpose
    -------
    Read-only discovery before building the production publication procedure.
    Run this in SSMS against the same SQL Server instance that contains
    EKPBI_New_Dev and Clio_Staging.

    It does NOT copy, delete, truncate, merge, or update any data.
*/

USE [EKPBI_New_Dev];
GO

SET NOCOUNT ON;
GO

IF DB_ID(N'Clio_Staging') IS NULL
    THROW 51020, 'Clio_Staging was not found on this SQL Server instance. Run this on the server containing both databases.', 1;
GO

DECLARE @EndpointTables TABLE
(
    TableName sysname NOT NULL PRIMARY KEY
);

INSERT INTO @EndpointTables (TableName)
VALUES
    (N'Activities'),
    (N'ActivityDescriptions'),
    (N'Allocations'),
    (N'BankTransactions'),
    (N'Bills'),
    (N'CalendarEntries'),
    (N'Calendars'),
    (N'Contacts'),
    (N'CustomFields'),
    (N'CustomFieldSets'),
    (N'Folders'),
    (N'LineItems'),
    (N'Matters'),
    (N'MatterNotes'),
    (N'PracticeAreas'),
    (N'Relationships'),
    (N'Tasks'),
    (N'TaskTypes'),
    (N'Users');

/* 1. Confirm the intended two-database boundary. */
SELECT
    d.name AS DatabaseName,
    d.compatibility_level AS CompatibilityLevel,
    d.state_desc AS DatabaseState,
    d.recovery_model_desc AS RecoveryModel
FROM sys.databases AS d
WHERE d.name IN (N'EKPBI_New_Dev', N'Clio_Staging')
ORDER BY d.name;

/* 2. Show whether every approved endpoint table exists on both sides. */
SELECT
    e.TableName,
    CASE WHEN st.object_id IS NULL THEN N'MISSING' ELSE N'PRESENT' END AS InClioStaging,
    CASE WHEN pt.object_id IS NULL THEN N'MISSING' ELSE N'PRESENT' END AS InEKPBINew,
    ISNULL(st.ColumnCount, 0) AS StagingColumns,
    ISNULL(pt.ColumnCount, 0) AS ProductionColumns
FROM @EndpointTables AS e
OUTER APPLY
(
    SELECT t.object_id, COUNT(c.column_id) AS ColumnCount
    FROM Clio_Staging.sys.tables AS t
    INNER JOIN Clio_Staging.sys.schemas AS s ON s.schema_id = t.schema_id
    LEFT JOIN Clio_Staging.sys.columns AS c ON c.object_id = t.object_id
    WHERE s.name = N'dbo'
      AND t.name = e.TableName
    GROUP BY t.object_id
) AS st
OUTER APPLY
(
    SELECT t.object_id, COUNT(c.column_id) AS ColumnCount
    FROM sys.tables AS t
    INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id
    LEFT JOIN sys.columns AS c ON c.object_id = t.object_id
    WHERE s.name = N'dbo'
      AND t.name = e.TableName
    GROUP BY t.object_id
) AS pt
ORDER BY e.TableName;

/* 3. Show column-level schema differences.  A publish routine must not assume schemas match. */
;WITH StagingColumns AS
(
    SELECT e.TableName, c.name AS ColumnName, ty.name AS DataType,
           c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale,
           c.is_nullable AS IsNullable
    FROM @EndpointTables AS e
    INNER JOIN Clio_Staging.sys.tables AS t ON t.name = e.TableName
    INNER JOIN Clio_Staging.sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
    INNER JOIN Clio_Staging.sys.columns AS c ON c.object_id = t.object_id
    INNER JOIN Clio_Staging.sys.types AS ty ON ty.user_type_id = c.user_type_id
),
ProductionColumns AS
(
    SELECT e.TableName, c.name AS ColumnName, ty.name AS DataType,
           c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale,
           c.is_nullable AS IsNullable
    FROM @EndpointTables AS e
    INNER JOIN sys.tables AS t ON t.name = e.TableName
    INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
    INNER JOIN sys.columns AS c ON c.object_id = t.object_id
    INNER JOIN sys.types AS ty ON ty.user_type_id = c.user_type_id
)
SELECT
    COALESCE(sc.TableName, pc.TableName) AS TableName,
    COALESCE(sc.ColumnName, pc.ColumnName) AS ColumnName,
    CASE
        WHEN sc.ColumnName IS NULL THEN N'MISSING_IN_STAGING'
        WHEN pc.ColumnName IS NULL THEN N'MISSING_IN_PRODUCTION'
        WHEN sc.DataType <> pc.DataType
          OR sc.MaxLength <> pc.MaxLength
          OR sc.[Precision] <> pc.[Precision]
          OR sc.Scale <> pc.Scale
          OR sc.IsNullable <> pc.IsNullable THEN N'DEFINITION_DIFFERENT'
        ELSE N'MATCH'
    END AS SchemaStatus,
    sc.DataType AS StagingDataType, sc.MaxLength AS StagingMaxLength,
    sc.[Precision] AS StagingPrecision, sc.Scale AS StagingScale,
    sc.IsNullable AS StagingNullable,
    pc.DataType AS ProductionDataType, pc.MaxLength AS ProductionMaxLength,
    pc.[Precision] AS ProductionPrecision, pc.Scale AS ProductionScale,
    pc.IsNullable AS ProductionNullable
FROM StagingColumns AS sc
FULL OUTER JOIN ProductionColumns AS pc
    ON pc.TableName = sc.TableName
   AND pc.ColumnName = sc.ColumnName
WHERE sc.ColumnName IS NULL
   OR pc.ColumnName IS NULL
   OR sc.DataType <> pc.DataType
   OR sc.MaxLength <> pc.MaxLength
   OR sc.[Precision] <> pc.[Precision]
   OR sc.Scale <> pc.Scale
   OR sc.IsNullable <> pc.IsNullable
ORDER BY COALESCE(sc.TableName, pc.TableName), COALESCE(sc.ColumnName, pc.ColumnName);

/* 4. Find production objects with recorded static dependencies on endpoint tables. */
SELECT DISTINCT
    e.TableName AS ReferencedRawTable,
    OBJECT_SCHEMA_NAME(o.object_id) AS ReferencingSchema,
    o.name AS ReferencingObject,
    o.type_desc AS ReferencingObjectType
FROM sys.sql_expression_dependencies AS d
INNER JOIN sys.objects AS o ON o.object_id = d.referencing_id
INNER JOIN @EndpointTables AS e ON e.TableName = d.referenced_entity_name
WHERE d.referenced_database_name IS NULL
  AND d.referenced_schema_name IN (N'dbo', NULL)
ORDER BY e.TableName, ReferencingObjectType, ReferencingSchema, ReferencingObject;

/* 5. Also find text references, including dynamic SQL that dependency metadata may not record. */
SELECT
    OBJECT_SCHEMA_NAME(o.object_id) AS ObjectSchema,
    o.name AS ObjectName,
    o.type_desc AS ObjectType,
    e.TableName AS MentionedRawTable
FROM sys.sql_modules AS m
INNER JOIN sys.objects AS o ON o.object_id = m.object_id
INNER JOIN @EndpointTables AS e ON m.definition LIKE N'%' + e.TableName + N'%'
ORDER BY ObjectType, ObjectSchema, ObjectName, MentionedRawTable;

/* 6. Display the known post-sync procedures for review before any publication is designed. */
SELECT
    SCHEMA_NAME(o.schema_id) AS ObjectSchema,
    o.name AS ObjectName,
    o.type_desc AS ObjectType,
    m.definition AS ObjectDefinition
FROM sys.objects AS o
LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
WHERE o.name IN
(
    N'SP_ClearData',
    N'SP_CleanData',
    N'SP_CustomExecAfterSync',
    N'SP_InsertIntoMattersDetailedCustomFields',
    N'SP_Sync_Matters_From_View',
    N'SP_UpdateNonBillableActivities',
    N'PopulateTables',
    N'SwapStagingToFinal'
)
ORDER BY ObjectName;

/* 7. List foreign keys that would constrain a raw-table replacement. */
SELECT
    e.TableName AS ReferencedRawTable,
    fk.name AS ForeignKeyName,
    SCHEMA_NAME(child.schema_id) AS ChildSchema,
    child.name AS ChildTable,
    SCHEMA_NAME(parent.schema_id) AS ParentSchema,
    parent.name AS ParentTable,
    fk.delete_referential_action_desc AS OnDeleteAction,
    fk.update_referential_action_desc AS OnUpdateAction
FROM sys.foreign_keys AS fk
INNER JOIN sys.tables AS child ON child.object_id = fk.parent_object_id
INNER JOIN sys.tables AS parent ON parent.object_id = fk.referenced_object_id
INNER JOIN @EndpointTables AS e ON e.TableName = parent.name
INNER JOIN sys.schemas AS ps ON ps.schema_id = parent.schema_id AND ps.name = N'dbo'
ORDER BY e.TableName, ChildSchema, ChildTable, ForeignKeyName;

/* 8. Identify the production key/index design for each endpoint table. */
SELECT
    e.TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique AS IsUnique,
    c.name AS ColumnName,
    ic.key_ordinal AS KeyOrdinal
FROM @EndpointTables AS e
INNER JOIN sys.tables AS t ON t.name = e.TableName
INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id AND s.name = N'dbo'
INNER JOIN sys.indexes AS i ON i.object_id = t.object_id
INNER JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
INNER JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
WHERE i.is_hypothetical = 0
  AND i.name IS NOT NULL
ORDER BY e.TableName, i.is_primary_key DESC, i.is_unique DESC, i.name, ic.key_ordinal;

SELECT
    N'READ_ONLY_PREFLIGHT_COMPLETE' AS Result,
    N'Review schema differences, dependencies, procedure definitions, foreign keys and keys before creating a publication procedure.' AS NextAction;
