using System;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Rebuilds the reporting tables inside Clio_Staging after the raw tables changed, in the same order the old
    /// CSP.Clio.Sync used (BIDataManager.ConvertViewsToTable + SyncMatters + CleanData). The views are only converted
    /// where CSPViewsToTable has Active = 1 and ConvertToTable = 1; PopulateTables decides that itself.
    /// </summary>
    internal sealed class ReportingBuilder
    {
        readonly string _connectionString;
        readonly int _timeoutSeconds;

        public ReportingBuilder(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            var raw = ConfigurationManager.AppSettings["ReportingBuildTimeoutMinutes"];
            int minutes;
            _timeoutSeconds = int.TryParse(raw, out minutes) && minutes > 0 ? minutes * 60 : 90 * 60;
        }

        public void Build()
        {
            var total = Stopwatch.StartNew();
            var startedAt = ServerNow();
            Console.WriteLine("Reporting build started in Clio_Staging.");

            Step("SP_CustomExecAfterSync", "EXEC dbo.SP_CustomExecAfterSync;");
            // The matters/contacts/users tables must be current before the custom-field rows are derived from them.
            Step("PopulateTables VW_Users", PopulateOne("VW_Users"));
            Step("PopulateTables VW_Contacts", PopulateOne("VW_Contacts"));
            Step("PopulateTables VW_Matters", PopulateOne("VW_Matters"));
            Step("SP_InsertIntoMattersDetailedCustomFields", "EXEC dbo.SP_InsertIntoMattersDetailedCustomFields;");
            Step("PopulateMatterNotesView", "EXEC dbo.PopulateMatterNotesView;");
            Step("SP_UpdateNonBillableActivities", "EXEC dbo.SP_UpdateNonBillableActivities;");
            Step("PopulateTables (all active views)", "EXEC dbo.PopulateTables;");
            Step("UpdateExpectedHoursDaily", "EXEC dbo.UpdateExpectedHoursDaily;");
            Step("SP_UpdateNonBillableActivities", "EXEC dbo.SP_UpdateNonBillableActivities;");
            Step("SP_AddUserGroupReportTo", "EXEC dbo.SP_AddUserGroupReportTo;");
            Step("SP_CleanData", "EXEC dbo.SP_CleanData;");

            Console.WriteLine("Reporting build complete in " + total.Elapsed.ToString(@"hh\:mm\:ss") + ".");
            ReportLoggedErrors(startedAt);
        }

        DateTime ServerNow()
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand("SELECT GETDATE()", connection))
            {
                connection.Open();
                return (DateTime)command.ExecuteScalar();
            }
        }

        /// <summary>Several procedures catch their own errors and only write them to dbo.ErrorInfo, so surface them.</summary>
        void ReportLoggedErrors(DateTime since)
        {
            using (var connection = new SqlConnection(_connectionString))
            using (var command = new SqlCommand(
                "SELECT TOP 10 ExtraInfo, LEFT(CAST([Message] AS nvarchar(max)), 300) FROM dbo.ErrorInfo WHERE DateCreated >= @since ORDER BY DateCreated", connection))
            {
                command.Parameters.AddWithValue("@since", since);
                connection.Open();
                using (var reader = command.ExecuteReader())
                {
                    var any = false;
                    while (reader.Read())
                    {
                        if (!any) Console.Error.WriteLine("WARNING: errors were logged to dbo.ErrorInfo during the reporting build:");
                        any = true;
                        Console.Error.WriteLine("  [" + reader.GetValue(0) + "] " + reader.GetValue(1));
                    }
                }
            }
        }

        static string PopulateOne(string view)
        {
            return "EXEC dbo.PopulateTables @updateAll = 0, @specificViewName = N'" + view + "', @EverySync = 0;";
        }

        void Step(string name, string sql)
        {
            var watch = Stopwatch.StartNew();
            try
            {
                using (var connection = new SqlConnection(_connectionString))
                using (var command = new SqlCommand(sql, connection) { CommandTimeout = _timeoutSeconds })
                {
                    connection.Open();
                    command.ExecuteNonQuery();
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Reporting build failed at '" + name + "' after " + watch.Elapsed.ToString(@"hh\:mm\:ss") + ": " + ex.Message, ex);
            }
            Console.WriteLine("  " + name + " - " + watch.Elapsed.ToString(@"hh\:mm\:ss"));
        }
    }
}
