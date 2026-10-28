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
                    command.CommandTimeout = 3600;
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
