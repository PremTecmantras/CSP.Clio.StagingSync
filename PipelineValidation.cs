using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Data.Entity.Core.EntityClient;
using System.Globalization;
using System.Linq;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Read-only replacement for the routine checks in 02_Staging_Test_Diagnostics.sql.
    /// It deliberately cannot publish, clear, or modify staging/production data.
    /// </summary>
    internal sealed class PipelineValidation
    {
        const string StagingConnectionName = "EKPBIEntities";
        const string ProductionConnectionName = "ProductionSql";
        const decimal DefaultMaximumDifferencePct = 10m;

        readonly string _stagingConnectionString;
        readonly string _productionConnectionString;

        PipelineValidation(string stagingConnectionString, string productionConnectionString)
        {
            _stagingConnectionString = stagingConnectionString;
            _productionConnectionString = productionConnectionString;
        }

        public static PipelineValidation CreateGuarded()
        {
            var staging = ConfigurationManager.ConnectionStrings[StagingConnectionName]
                ?? throw new InvalidOperationException("Connection string '" + StagingConnectionName + "' is missing.");
            var production = ConfigurationManager.ConnectionStrings[ProductionConnectionName]
                ?? throw new InvalidOperationException("Connection string '" + ProductionConnectionName + "' is missing.");
            var requiredComparisonDatabase = ConfigurationManager.AppSettings["ComparisonDatabaseName"];
            if (string.IsNullOrWhiteSpace(requiredComparisonDatabase))
                throw new ConfigurationErrorsException("AppSetting 'ComparisonDatabaseName' is missing or empty.");
            requiredComparisonDatabase = requiredComparisonDatabase.Trim();

            var stagingSql = new SqlConnectionStringBuilder(new EntityConnectionStringBuilder(staging.ConnectionString).ProviderConnectionString);
            var productionSql = new SqlConnectionStringBuilder(production.ConnectionString);

            if (!string.Equals(stagingSql.InitialCatalog, StagingAudit.RequiredDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Validation requires " + StagingAudit.RequiredDatabase + " as the staging database.");
            if (string.IsNullOrWhiteSpace(productionSql.InitialCatalog))
                throw new InvalidOperationException("SAFETY STOP: ProductionSql has no database name in its connection string.");
            if (!string.Equals(productionSql.InitialCatalog, requiredComparisonDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Validation is restricted to database '" + requiredComparisonDatabase + "'; ProductionSql points to '" + productionSql.InitialCatalog + "'.");
            if (string.Equals(stagingSql.InitialCatalog, productionSql.InitialCatalog, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAFETY STOP: ProductionSql and EKPBIEntities point to the same database '" + productionSql.InitialCatalog + "'.");
            if (!string.Equals(stagingSql.DataSource, productionSql.DataSource, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Staging and production must be on the same SQL Server instance for the current validation design.");

            stagingSql.ApplicationName = "CSP.Clio.StagingSync.Validate";
            productionSql.ApplicationName = "CSP.Clio.StagingSync.Validate";
            return new PipelineValidation(stagingSql.ConnectionString, productionSql.ConnectionString);
        }

        public int Validate(Guid? requestedRunId, decimal maximumDifferencePct)
        {
            if (maximumDifferencePct < 0 || maximumDifferencePct > 100)
                throw new ArgumentOutOfRangeException(nameof(maximumDifferencePct), "Threshold must be between 0 and 100.");

            var runId = requestedRunId ?? LatestExtractedRunId();
            Console.WriteLine("Validating staging RunID: " + runId);
            var failures = new List<string>();

            using (var staging = Open(_stagingConnectionString))
            using (var production = Open(_productionConnectionString))
            {
                if (TableExists(production, "control", "PublishRun"))
                {
                    var completedPublications = ScalarLong(production,
                        "SELECT COUNT_BIG(*) FROM control.PublishRun WHERE SourceRunID=@r AND Status='COMPLETE'",
                        new SqlParameter("@r", runId));
                    if (completedPublications > 0)
                    {
                        failures.Add("This RunID already has a COMPLETE production publication. Do not republish it; extract a new RunID for the next cycle.");
                    }
                }

                var runStatus = ScalarString(staging,
                    "SELECT Status FROM control.SyncRun WHERE RunID=@r", new SqlParameter("@r", runId));
                if (!string.Equals(runStatus, "EXTRACTED", StringComparison.OrdinalIgnoreCase))
                    failures.Add("Run status is '" + (runStatus ?? "MISSING") + "', not EXTRACTED.");

                var activeCompanyIds = ActiveCompanyIds(staging);
                if (activeCompanyIds.Count == 0)
                    failures.Add("No active staging companies are configured for comparison.");

                var expected = ScalarLong(staging,
                    "SELECT COUNT_BIG(*) FROM control.SyncEndpointResult e INNER JOIN dbo._EKPCompanies c ON c.ID=e.CompanyID WHERE e.RunID=@r AND c.Active=1", new SqlParameter("@r", runId));
                var complete = ScalarLong(staging,
                    "SELECT COUNT_BIG(*) FROM control.SyncEndpointResult e INNER JOIN dbo._EKPCompanies c ON c.ID=e.CompanyID WHERE e.RunID=@r AND c.Active=1 AND e.Status='COMPLETE'", new SqlParameter("@r", runId));
                Console.WriteLine("Endpoint audit: " + complete + " COMPLETE of " + expected + " expected.");
                var requiredExpected = activeCompanyIds.Count * StagingAudit.EndpointManifest.Count;
                if (expected != requiredExpected || complete != expected)
                    failures.Add("Not every expected endpoint/company audit record is COMPLETE.");

                foreach (var mapping in StagingAudit.EndpointManifest)
                {
                    RequireRawTable(staging, mapping.Value, "staging");
                    RequireRawTable(production, mapping.Value, "production");

                    using (var companies = new SqlCommand(@"SELECT DISTINCT CompanyID, RowsWritten
FROM control.SyncEndpointResult
WHERE RunID=@r AND Endpoint=@e
ORDER BY CompanyID", staging))
                    {
                        companies.Parameters.AddWithValue("@r", runId);
                        companies.Parameters.AddWithValue("@e", mapping.Key);
                        using (var reader = companies.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                var companyId = reader.GetGuid(0);
                                if (!activeCompanyIds.Contains(companyId))
                                    continue;
                                var auditedRows = reader.IsDBNull(1) ? -1 : reader.GetInt64(1);
                                var stageRows = CountCompanyRows(staging, mapping.Value, companyId);
                                var productionRows = CountCompanyRows(production, mapping.Value, companyId);

                                if (auditedRows != stageRows)
                                {
                                    failures.Add(mapping.Key + " / " + companyId + ": staging rows " + stageRows +
                                        " do not match audit RowsWritten " + auditedRows + ".");
                                }

                                var differencePct = PercentageDifference(stageRows, productionRows);
                                var result = differencePct <= maximumDifferencePct ? "PASS" : "REVIEW";
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "{0,-22} {1} | stage {2,8} | production {3,8} | difference {4,7:0.00}% | {5}",
                                    mapping.Key, companyId, stageRows, productionRows, differencePct, result));

                                if (differencePct > maximumDifferencePct)
                                {
                                    failures.Add(mapping.Key + " / " + companyId + ": count difference " +
                                        differencePct.ToString("0.00", CultureInfo.InvariantCulture) + "% exceeds " +
                                        maximumDifferencePct.ToString("0.00", CultureInfo.InvariantCulture) + "%.");
                                }
                            }
                        }
                    }
                }
            }

            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("VALIDATION PASSED: Run " + runId + " is ready for a separately authorised publication dry-run.");
                return 0;
            }

            Console.Error.WriteLine("VALIDATION FAILED: " + failures.Count + " issue(s).");
            foreach (var failure in failures)
                Console.Error.WriteLine(" - " + failure);
            return 1;
        }

        public int Status()
        {
            using (var staging = Open(_stagingConnectionString))
            using (var production = Open(_productionConnectionString))
            {
                Console.WriteLine("Latest staging runs:");
                PrintRows(staging, @"SELECT TOP (5) RunID, Status, Mode, StartedAtUTC, CompletedAtUTC, ErrorMessage
FROM control.SyncRun ORDER BY StartedAtUTC DESC");

                Console.WriteLine("Latest production publications:");
                if (TableExists(production, "control", "PublishRun"))
                    PrintRows(production, @"SELECT TOP (5) PublishID, SourceRunID, Status, DryRun, StartedAtUTC, CompletedAtUTC, ErrorMessage
FROM control.PublishRun ORDER BY StartedAtUTC DESC");
                else
                    Console.WriteLine("Publisher objects are not deployed.");
            }
            return 0;
        }

        /// <summary>
        /// Checks the database prerequisites which used to be verified manually by
        /// the production read-only preflight script.  It never changes either database.
        /// </summary>
        public int Preflight()
        {
            var failures = new List<string>();
            using (var staging = Open(_stagingConnectionString))
            using (var production = Open(_productionConnectionString))
            {
                Console.WriteLine("Read-only pipeline preflight");

                RequireTable(staging, "control", "BootstrapInfo", "staging", failures);
                RequireTable(staging, "control", "SyncRun", "staging", failures);
                RequireTable(staging, "control", "SyncEndpointResult", "staging", failures);
                RequireTable(staging, "dbo", "_EKPCompanies", "staging", failures);
                RequireTable(production, "dbo", "_EKPCompanies", "production", failures);
                RequireTable(production, "dbo", "CSPViewsToTable", "production", failures);

                var runningSyncs = ScalarLong(staging,
                    "SELECT COUNT_BIG(*) FROM control.SyncRun WHERE Status='RUNNING'");
                Console.WriteLine("Staging runs currently RUNNING: " + runningSyncs);
                if (runningSyncs != 0)
                    failures.Add("A staging extraction is already RUNNING. Do not start another extraction.");

                if (TableExists(staging, "control", "PublishLease"))
                {
                    var runningPublications = ScalarLong(staging,
                        "SELECT COUNT_BIG(*) FROM control.PublishLease WHERE Status='RUNNING'");
                    Console.WriteLine("Publication leases currently RUNNING: " + runningPublications);
                    if (runningPublications != 0)
                        failures.Add("A production publication lease is RUNNING. Resolve it before a new publication.");
                }
                else
                {
                    Console.WriteLine("Publication lease table is not deployed (publication is not enabled by this EXE).");
                }

                if (TableExists(production, "dbo", "CSPViewsToTable"))
                {
                    var activeTransformations = ScalarLong(production,
                        "SELECT COUNT_BIG(*) FROM dbo.CSPViewsToTable WHERE ISNULL(Active, 0)=1");
                    Console.WriteLine("Active reporting transformations: " + activeTransformations);
                    if (activeTransformations == 0)
                        failures.Add("No active CSPViewsToTable transformations were found in production.");
                }

                foreach (var tableName in StagingAudit.EndpointManifest.Select(x => x.Value).Distinct())
                {
                    if (!TableExists(staging, "dbo", tableName))
                    {
                        failures.Add("Staging raw table dbo." + tableName + " is missing.");
                        continue;
                    }
                    if (!TableExists(production, "dbo", tableName))
                    {
                        failures.Add("Production raw table dbo." + tableName + " is missing.");
                        continue;
                    }

                    var stagingColumns = ColumnSignatures(staging, tableName);
                    var productionColumns = ColumnSignatures(production, tableName);
                    if (!stagingColumns.SequenceEqual(productionColumns, StringComparer.OrdinalIgnoreCase))
                        failures.Add("Raw table schema differs for dbo." + tableName + ". Review before publication.");
                }
            }

            Console.WriteLine();
            if (failures.Count == 0)
            {
                Console.WriteLine("PREFLIGHT PASSED: databases and raw-table schemas are ready for the next authorised pipeline step.");
                return 0;
            }

            Console.Error.WriteLine("PREFLIGHT FAILED: " + failures.Count + " issue(s).");
            foreach (var failure in failures)
                Console.Error.WriteLine(" - " + failure);
            return 1;
        }

        Guid LatestExtractedRunId()
        {
            using (var staging = Open(_stagingConnectionString))
            {
                var result = Scalar(staging, "SELECT TOP (1) RunID FROM control.SyncRun WHERE Status='EXTRACTED' ORDER BY CompletedAtUTC DESC");
                if (result == null || result == DBNull.Value)
                    throw new InvalidOperationException("No EXTRACTED staging RunID exists. Run extraction first.");
                return (Guid)result;
            }
        }

        static SqlConnection Open(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            connection.Open();
            return connection;
        }

        static long CountCompanyRows(SqlConnection connection, string tableName, Guid companyId)
        {
            using (var command = new SqlCommand("SELECT COUNT_BIG(*) FROM dbo." + Quote(tableName) + " WHERE CompanyID=@c", connection))
            {
                command.Parameters.AddWithValue("@c", companyId);
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        static HashSet<Guid> ActiveCompanyIds(SqlConnection connection)
        {
            var result = new HashSet<Guid>();
            using (var command = new SqlCommand("SELECT ID FROM dbo._EKPCompanies WHERE Active=1", connection))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) result.Add(reader.GetGuid(0));
            return result;
        }

        static decimal PercentageDifference(long stageRows, long productionRows)
        {
            if (productionRows == 0)
                return stageRows == 0 ? 0m : 100m;

            if (stageRows >= productionRows)
                return 0m;

            return Math.Abs((decimal)stageRows - productionRows) * 100m / productionRows;
        }

        static void RequireRawTable(SqlConnection connection, string tableName, string databaseLabel)
        {
            if (!TableExists(connection, "dbo", tableName))
                throw new InvalidOperationException("Approved table dbo." + tableName + " is missing from " + databaseLabel + ".");
        }

        static void RequireTable(SqlConnection connection, string schemaName, string tableName, string databaseLabel, IList<string> failures)
        {
            if (!TableExists(connection, schemaName, tableName))
                failures.Add("Required " + databaseLabel + " object " + schemaName + "." + tableName + " is missing.");
        }

        static List<string> ColumnSignatures(SqlConnection connection, string tableName)
        {
            var signatures = new List<string>();
            const string sql = @"SELECT c.column_id, c.name, ty.name AS TypeName, c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity
FROM sys.columns c
INNER JOIN sys.tables t ON t.object_id=c.object_id
INNER JOIN sys.schemas s ON s.schema_id=t.schema_id
INNER JOIN sys.types ty ON ty.user_type_id=c.user_type_id
WHERE s.name='dbo' AND t.name=@t
ORDER BY c.column_id";
            using (var command = new SqlCommand(sql, connection))
            {
                command.Parameters.AddWithValue("@t", tableName);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        signatures.Add(string.Join("|", Enumerable.Range(1, reader.FieldCount - 1)
                            .Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
                    }
                }
            }
            return signatures;
        }

        static bool TableExists(SqlConnection connection, string schemaName, string tableName)
        {
            return Convert.ToInt32(Scalar(connection,
                "SELECT COUNT(*) FROM sys.tables t INNER JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE s.name=@s AND t.name=@t",
                new SqlParameter("@s", schemaName), new SqlParameter("@t", tableName))) > 0;
        }

        static object Scalar(SqlConnection connection, string sql, params SqlParameter[] parameters)
        {
            using (var command = new SqlCommand(sql, connection) { CommandTimeout = 600 })
            {
                if (parameters != null && parameters.Length > 0) command.Parameters.AddRange(parameters);
                return command.ExecuteScalar();
            }
        }

        static long ScalarLong(SqlConnection connection, string sql, params SqlParameter[] parameters)
        {
            var value = Scalar(connection, sql, parameters);
            return value == null || value == DBNull.Value ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        static string ScalarString(SqlConnection connection, string sql, params SqlParameter[] parameters)
        {
            var value = Scalar(connection, sql, parameters);
            return value == null || value == DBNull.Value ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        static string Quote(string tableName)
        {
            if (!StagingAudit.EndpointManifest.Any(x => string.Equals(x.Value, tableName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Table is not in the approved endpoint manifest: " + tableName);
            return "[" + tableName.Replace("]", "]]" ) + "]";
        }

        static void PrintRows(SqlConnection connection, string sql)
        {
            using (var command = new SqlCommand(sql, connection))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var values = new string[reader.FieldCount];
                    for (var i = 0; i < reader.FieldCount; i++)
                        values[i] = reader.IsDBNull(i) ? "NULL" : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                    Console.WriteLine(string.Join(" | ", values));
                }
            }
        }
    }
}
