using System;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The production-write portion of the all-in-one pipeline.  It is called only
    /// after PipelineValidation has passed for the newly extracted RunID.
    /// </summary>
    internal static class ProductionPublisher
    {
        const string ConnectionName = "ProductionSql";
        const string ProcedureName = "dbo.PublishApprovedClioStagingRun";

        public static void Publish(Guid runId)
        {
            using (var connection = OpenGuarded())
            {
                if (!ProcedureExists(connection))
                    throw new InvalidOperationException("Required production procedure " + ProcedureName + " is missing.");

                Console.WriteLine("Starting " + ProcedureName + " for RunID " + runId + ".");
                using (var command = new SqlCommand(ProcedureName, connection))
                {
                    command.CommandType = CommandType.StoredProcedure;
                    // The publisher now only copies tables (no reporting rebuild), but it is one transaction over ~1.5M rows.
                    var timeoutMinutes = 0;
                    int.TryParse(ConfigurationManager.AppSettings["PublishTimeoutMinutes"], out timeoutMinutes);
                    command.CommandTimeout = timeoutMinutes > 0 ? timeoutMinutes * 60 : 3600;
                    command.Parameters.Add("@RunID", SqlDbType.UniqueIdentifier).Value = runId;
                    command.Parameters.Add("@DryRun", SqlDbType.Bit).Value = false;
                    command.Parameters.Add("@RebuildReporting", SqlDbType.Bit).Value = true;
                    command.ExecuteNonQuery();
                }

                using (var command = new SqlCommand(@"SELECT TOP (1) PublishID, Status, ErrorMessage
FROM control.PublishRun WHERE SourceRunID=@r ORDER BY StartedAtUTC DESC", connection))
                {
                    command.Parameters.Add("@r", SqlDbType.UniqueIdentifier).Value = runId;
                    using (var reader = command.ExecuteReader())
                    {
                        if (!reader.Read())
                            throw new InvalidOperationException("Publisher returned without creating a control.PublishRun audit row.");

                        var status = reader.IsDBNull(1) ? null : reader.GetString(1);
                        var error = reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture);
                        Console.WriteLine("Production PublishID: " + reader.GetGuid(0) + "; status: " + (status ?? "NULL"));
                        if (!string.IsNullOrWhiteSpace(error)) Console.WriteLine("Publisher audit error: " + error);
                        if (!string.Equals(status, "COMPLETE", StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Publisher did not finish COMPLETE; final status is " + (status ?? "NULL") + ".");
                    }
                }
            }
            Console.WriteLine("PUBLISH COMPLETE: production raw data and requested reporting rebuild completed.");
        }

        /// <summary>Runs the publisher with @DryRun = 1: all checks and the plan are produced, nothing is changed.</summary>
        public static void DryRun(Guid runId)
        {
            using (var connection = OpenGuarded())
            {
                if (!ProcedureExists(connection))
                    throw new InvalidOperationException("Required production procedure " + ProcedureName + " is missing.");

                Console.WriteLine("Dry run of " + ProcedureName + " for RunID " + runId + " (no data will change).");
                using (var command = new SqlCommand(ProcedureName, connection) { CommandType = CommandType.StoredProcedure, CommandTimeout = 1800 })
                {
                    command.Parameters.Add("@RunID", SqlDbType.UniqueIdentifier).Value = runId;
                    command.Parameters.Add("@DryRun", SqlDbType.Bit).Value = true;
                    command.Parameters.Add("@RebuildReporting", SqlDbType.Bit).Value = true;
                    using (var reader = command.ExecuteReader())
                    {
                        var resultSet = 0;
                        do
                        {
                            resultSet++;
                            if (resultSet > 3) continue; // summary, raw plan and reporting plan; the per-company audit rows are noise
                            while (reader.Read())
                            {
                                var parts = new string[reader.FieldCount];
                                for (var i = 0; i < reader.FieldCount; i++)
                                    parts[i] = reader.GetName(i) + "=" + Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                                Console.WriteLine("  " + string.Join(" | ", parts));
                            }
                        } while (reader.NextResult());
                    }
                }
            }
            Console.WriteLine("PUBLISH DRY RUN COMPLETE: all publisher checks passed and no data was changed.");
        }

        public static void PrintSqlErrors(SqlException exception)
        {
            Console.Error.WriteLine("PUBLISH FAILED: SQL Server rejected " + ProcedureName + ".");
            foreach (SqlError error in exception.Errors)
            {
                Console.Error.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "SQL {0}, severity {1}, state {2}, procedure {3}, line {4}: {5}",
                    error.Number, error.Class, error.State,
                    string.IsNullOrWhiteSpace(error.Procedure) ? "(ad hoc)" : error.Procedure,
                    error.LineNumber, error.Message));
            }
        }

        static SqlConnection OpenGuarded()
        {
            var setting = ConfigurationManager.ConnectionStrings[ConnectionName];
            if (setting == null)
                throw new InvalidOperationException("Connection string '" + ConnectionName + "' is missing.");
            var builder = new SqlConnectionStringBuilder(setting.ConnectionString);
            if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
                throw new InvalidOperationException("SAFETY STOP: ProductionSql has no database name in its connection string.");
            builder.ApplicationName = "CSP.Clio.StagingSync.Publish";
            var connection = new SqlConnection(builder.ConnectionString);
            connection.Open();
            return connection;
        }

        /// <summary>
        /// Called at the START of the publish cycle, before the catch-up and the 20-40 minute reporting build: the target database must hold the current
        /// version of the publisher procedure (script 12: it has the parameter @PublishReporting; script 07 alone creates an older version without it).
        /// Without this check a missing script 12 is only found at the very last step, after the whole build. Returns null when all is well,
        /// otherwise the message to show (nothing is changed either way).
        /// </summary>
        public static string CheckProcedureIsCurrent()
        {
            try
            {
                using (var connection = OpenGuarded())
                {
                    if (!ProcedureExists(connection))
                        return "The procedure " + ProcedureName + " does not exist in " + connection.Database + ". Run 07_Create_Production_Publisher_EKPBI_New_Dev_Manual.sql and then 12_Create_Staging_Publisher_EKPBI_New_Dev_Manual.sql in " + connection.Database + ".";

                    //using (var command = new SqlCommand("SELECT COUNT(*) FROM sys.parameters WHERE object_id = OBJECT_ID(N'dbo.PublishApprovedClioStagingRun', N'P') AND name = N'@PublishReporting'", connection))
                    //{
                    //    if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                    //        return "The procedure " + ProcedureName + " in " + connection.Database + " is an older version (it has no @PublishReporting parameter). " +
                    //            "Run 12_Create_Staging_Publisher_EKPBI_New_Dev_Manual.sql in " + connection.Database + " (it is CREATE OR ALTER, so it is safe to run again), then start the publish again.";
                    //}
                    return null;
                }
            }
            catch (Exception ex)
            {
                return "The publisher procedure in the target database could not be checked: " + ex.Message;
            }
        }

        /// <summary>
        /// True when the publisher procedure in the target database rebuilds the reporting tables ITSELF (its text calls PopulateTables), as the
        /// "rebuild in target" version does. The reporting tables built in Clio_Staging are then never used by the publish, so building them there is
        /// the same 20+ minutes of work done twice. False (the "copy the staging reporting tables" version) or unknown: staging must build them.
        /// </summary>
        public static bool TargetRebuildsReportingItself()
        {
            try
            {
                using (var connection = OpenGuarded())
                using (var command = new SqlCommand("SELECT OBJECT_DEFINITION(OBJECT_ID(N'dbo.PublishApprovedClioStagingRun', N'P'))", connection))
                {
                    var text = command.ExecuteScalar() as string;
                    if (text == null) return false;
                    // Comments are removed first: the "copy from staging" version only MENTIONS PopulateTables in its header, it never executes it.
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
                    text = System.Text.RegularExpressions.Regex.Replace(text, @"--[^\r\n]*", " ");
                    return System.Text.RegularExpressions.Regex.IsMatch(text, @"\bEXEC(UTE)?\s+(\[?dbo\]?\.)?\[?PopulateTables\]?\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
            }
            catch { return false; }
        }

        static bool ProcedureExists(SqlConnection connection)
        {
            using (var command = new SqlCommand("SELECT OBJECT_ID(N'dbo.PublishApprovedClioStagingRun', N'P')", connection))
            {
                var value = command.ExecuteScalar();
                return value != null && value != DBNull.Value;
            }
        }
    }
}
