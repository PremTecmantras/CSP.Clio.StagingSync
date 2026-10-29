using CSP.Clio.API.Data;
using CSP.Clio.Sync.Data;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Polls changed records into the existing staging raw tables without clearing them.
    /// This mode never calls the publisher. The daily FULL run remains the authoritative
    /// reconciliation and publication path, including removal of records no longer returned
    /// by Clio.
    /// </summary>
    internal sealed class IncrementalSyncRunner
    {
        readonly string _connectionString;
        readonly int _overlapMinutes;
        readonly int _initialLookbackHours;
        readonly HashSet<string> _enabledEndpoints;

        public IncrementalSyncRunner(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _overlapMinutes = ReadPositiveInt("IncrementalOverlapMinutes", 10);
            _initialLookbackHours = ReadPositiveInt("IncrementalInitialLookbackHours", 24);
            _enabledEndpoints = new HashSet<string>(ReadEndpoints(), StringComparer.OrdinalIgnoreCase);
        }

        public async Task<int> RunAsync()
        {
            EnsureControlSchema();
            EnsureNoFullRunIsActive();
            EnsureNoIncrementalRunIsActive();

            var runId = Guid.NewGuid();
            StartRun(runId);
            try
            {
                var allSucceeded = true;
                var dataManager = new BIDataManager(false);
                var companies = dataManager.GetClioConnectedCompanies();

                if (companies.Count == 0)
                {
                    FinishRun(runId, false, "No active Clio companies are configured in staging.");
                    Console.Error.WriteLine("INCREMENTAL SYNC FAILED: no active Clio companies are configured.");
                    return 1;
                }

                Console.WriteLine("Incremental RunID: " + runId);
                Console.WriteLine("Incremental endpoints: " + string.Join(", ", _enabledEndpoints.OrderBy(x => x)));

                foreach (var company in companies)
                {
                    try
                    {
                        var bearerToken = await GetBearerTokenAsync(dataManager, company).ConfigureAwait(false);
                        var pageClient = new ClioPageStreamClient();
                        var savingDataManager = new BIDataManager(true);

                        await RunConfiguredEndpoints(runId, company.ID, company.CompanyName, bearerToken, pageClient, savingDataManager)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        allSucceeded = false;
                        Console.Error.WriteLine("INCREMENTAL COMPANY FAILED: " + company.CompanyName + " - " + ex.Message);
                        try { dataManager.LogException(ex, "CSP.Clio.StagingSync - Incremental company: " + company.CompanyName); } catch { }
                    }
                }

                var failedResults = CountFailedResults(runId);
                allSucceeded = allSucceeded && failedResults == 0;
                FinishRun(runId, allSucceeded, allSucceeded ? null : "One or more company/endpoint incremental polls failed.");
                Console.WriteLine(allSucceeded
                    ? "INCREMENTAL SYNC COMPLETE: staging rows were updated. No production publication was attempted."
                    : "INCREMENTAL SYNC FAILED: review control.IncrementalSyncResult and the run log. No production publication was attempted.");
                return allSucceeded ? 0 : 1;
            }
            catch (Exception ex)
            {
                try { FinishRun(runId, false, ex.GetType().Name + ": " + ex.Message); } catch { }
                throw;
            }
        }

        async Task RunConfiguredEndpoints(Guid runId, Guid companyId, string companyName, string bearerToken,
            ClioPageStreamClient pageClient, BIDataManager dataManager)
        {
            if (IsEnabled("PracticeAreas"))
                await RunEndpoint<PracticeAreaData>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "PracticeAreas", "GetPracticeAreas", "practice_areas", x => dataManager.SavePracticeAreas(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Activities"))
                await RunEndpoint<ActivityData>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Activities", "GetActivities", "activities", x => dataManager.SaveActivity(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Calendars"))
                await RunEndpoint<CalenderItem>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Calendars", "GetCalender", "calendars", x => dataManager.SaveCalender(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("CalendarEntries"))
                await RunEndpoint<CalenderEntry>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "CalendarEntries", "GetCalenderEntries", "calendar_entries", x => dataManager.SaveCalenderEntries(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Relationships"))
                await RunEndpoint<RelationshipDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Relationships", "GetRelationships", "relationships", x => dataManager.SaveRelationship(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Tasks"))
                await RunEndpoint<TaskDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Tasks", "GetTasks", "tasks", x => dataManager.SaveTasks(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("TaskTypes"))
                await RunEndpoint<TaskTypeDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "TaskTypes", "GetTaskTypes", "task_types", x => dataManager.SaveTaskTypes(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Matters"))
                await RunEndpoint<MatterDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Matters", "GetMatters", "matters", x => dataManager.SaveMatters(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Bills"))
                await RunEndpoint<BillModelModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Bills", "GetBills", "bills", x => dataManager.SaveBills(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("BankTransactions"))
                await RunEndpoint<BankTransactionModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "BankTransactions", "GetBankTransactions", "bank_transactions", x => dataManager.SaveBankTransactions(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("CustomFields"))
                await RunEndpoint<CustomFieldData>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "CustomFields", "GetCustomFields", "custom_fields", x => dataManager.SaveCustomFields(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("CustomFieldSets"))
                await RunEndpoint<CustomFieldSets>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "CustomFieldSets", "GetCustomFieldSets", "custom_field_sets", x => dataManager.SaveCustomFieldSets(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Folders"))
                await RunEndpoint<FolderModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Folders", "GetFolders", "folders", x => dataManager.SaveFolders(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("LineItems"))
                await RunEndpoint<LineItemModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "LineItems", "GetLineItems", "line_items", x => dataManager.SaveLineItems(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Contacts"))
                await RunEndpoint<ContactDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Contacts", "GetContacts", "contacts", x => dataManager.SaveContacts(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Allocations"))
                await RunEndpoint<AllocationsModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Allocations", "GetAllocations", "allocations", x => dataManager.SaveAllocations(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("Users"))
                await RunEndpoint<UserDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "Users", "GetUsers", "users", x => dataManager.SaveUsers(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("ActivityDescriptions"))
                await RunEndpoint<ActivityDescriptionModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "ActivityDescriptions", "GetActivityDescription", "activity_descriptions", x => dataManager.SaveActivityDescription(x, companyId)).ConfigureAwait(false);
            if (IsEnabled("MatterNotes"))
                await RunEndpoint<NoteDataModel>(runId, companyId, companyName, bearerToken, pageClient, dataManager,
                    "MatterNotes", "GetMatterNotes", "notes", x => dataManager.SaveMatterNotes(x, companyId)).ConfigureAwait(false);
        }

        async Task RunEndpoint<T>(Guid runId, Guid companyId, string companyName, string bearerToken,
            ClioPageStreamClient pageClient, BIDataManager dataManager, string endpoint, string apiMethod,
            string service, Action<List<T>> save)
        {
            var fromUtc = GetWatermark(companyId, endpoint).AddMinutes(-_overlapMinutes);
            var toUtc = DateTime.UtcNow;
            long rows = 0;
            StartResult(runId, companyId, endpoint, fromUtc);

            try
            {
                var query = "updated_since=" + Uri.EscapeDataString(fromUtc.ToString("o", CultureInfo.InvariantCulture));
                var metrics = new ClioPageStreamMetrics();
                await pageClient.StreamAsync(apiMethod, service, bearerToken, metrics, root =>
                {
                    var data = root.GetData().Cast<T>().ToList();
                    rows += data.Count;
                    if (data.Count > 0) save(data);
                    return Task.CompletedTask;
                }, query).ConfigureAwait(false);

                if (!metrics.PaginationComplete)
                    throw new InvalidOperationException("Clio pagination did not reach a verified final page.");

                FinishResult(runId, companyId, endpoint, true, rows, toUtc, null);
                UpsertWatermark(companyId, endpoint, toUtc, runId);
                Console.WriteLine("  Incremental " + endpoint + " - " + companyName + ": " + rows + " changed row(s).");
            }
            catch (Exception ex)
            {
                FinishResult(runId, companyId, endpoint, false, rows, null, ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine("  INCREMENTAL FAILED " + endpoint + " - " + companyName + ": " + ex.Message);
            }
        }

        async Task<string> GetBearerTokenAsync(BIDataManager dataManager, C_EKPCompanies company)
        {
            if (!await Program.IsTokenExpired(company.ClioToken).ConfigureAwait(false))
                return "Bearer " + company.ClioToken;

            var tokenClient = new CSP.Clio.API.ClioServiceClient("Bearer " + company.ClioToken);
            var token = await tokenClient.GetNewToken(company.ClioApiKey, company.ClioApiSecret, company.ClioRefreshToken)
                .ConfigureAwait(false);
            if (token == null || string.IsNullOrWhiteSpace(token.access_token))
                throw new InvalidOperationException("Clio OAuth token refresh returned no access token.");

            company.ClioToken = token.access_token;
            dataManager.UpdateClioConnectedCompanies(company.ID, DateTime.UtcNow, company.ClioToken);
            return "Bearer " + company.ClioToken;
        }

        bool IsEnabled(string endpoint) { return _enabledEndpoints.Contains(endpoint); }

        IEnumerable<string> ReadEndpoints()
        {
            var raw = ConfigurationManager.AppSettings["IncrementalEndpoints"];
            if (string.IsNullOrWhiteSpace(raw))
                throw new ConfigurationErrorsException("AppSetting 'IncrementalEndpoints' is missing or empty.");

            var allowed = new HashSet<string>(new[]
            {
                "PracticeAreas", "Activities", "Calendars", "CalendarEntries", "Relationships",
                "Tasks", "TaskTypes", "Matters", "Bills", "BankTransactions", "CustomFields",
                "CustomFieldSets", "Folders", "LineItems", "Contacts", "Allocations", "Users",
                "ActivityDescriptions", "MatterNotes"
            }, StringComparer.OrdinalIgnoreCase);
            var endpoints = raw.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (endpoints.Count == 0 || endpoints.Any(x => !allowed.Contains(x)))
                throw new ConfigurationErrorsException("IncrementalEndpoints may only contain: " + string.Join(", ", allowed.OrderBy(x => x)) + ".");
            return endpoints;
        }

        static int ReadPositiveInt(string name, int defaultValue)
        {
            var raw = ConfigurationManager.AppSettings[name];
            int value;
            if (string.IsNullOrWhiteSpace(raw)) return defaultValue;
            if (!int.TryParse(raw, out value) || value <= 0)
                throw new ConfigurationErrorsException("AppSetting '" + name + "' must be a positive integer.");
            return value;
        }

        SqlConnection Open()
        {
            var connection = new SqlConnection(_connectionString);
            connection.Open();
            return connection;
        }

        void EnsureControlSchema()
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(@"IF OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NULL
                THROW 51070, 'Incremental control tables are missing. Run 10_Create_Incremental_Sync_Control.sql in Clio_Staging first.', 1;", c))
                cmd.ExecuteNonQuery();
        }

        void EnsureNoFullRunIsActive()
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("IF EXISTS (SELECT 1 FROM control.SyncRun WHERE Status='RUNNING') THROW 51071, 'A full staging sync is RUNNING. Incremental sync will not overlap it.', 1;", c))
                cmd.ExecuteNonQuery();
        }

        void EnsureNoIncrementalRunIsActive()
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("IF EXISTS (SELECT 1 FROM control.IncrementalSyncRun WHERE Status='RUNNING') THROW 51072, 'A previous incremental sync is still RUNNING. Investigate it before starting another incremental task.', 1;", c))
                cmd.ExecuteNonQuery();
        }

        void StartRun(Guid runId)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("INSERT control.IncrementalSyncRun(IncrementalRunID, Status) VALUES(@r, 'RUNNING')", c))
            {
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.ExecuteNonQuery();
            }
        }

        void FinishRun(Guid runId, bool ok, string error)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("UPDATE control.IncrementalSyncRun SET Status=@s, CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=@e WHERE IncrementalRunID=@r", c))
            {
                cmd.Parameters.AddWithValue("@s", ok ? "COMPLETE" : "FAILED");
                cmd.Parameters.AddWithValue("@e", (object)Truncate(error, 3500) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.ExecuteNonQuery();
            }
        }

        DateTime GetWatermark(Guid companyId, string endpoint)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("SELECT LastSuccessfulToUTC FROM control.IncrementalWatermark WHERE CompanyID=@c AND Endpoint=@e", c))
            {
                cmd.Parameters.AddWithValue("@c", companyId);
                cmd.Parameters.AddWithValue("@e", endpoint);
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? DateTime.UtcNow.AddHours(-_initialLookbackHours) : Convert.ToDateTime(value).ToUniversalTime();
            }
        }

        void StartResult(Guid runId, Guid companyId, string endpoint, DateTime fromUtc)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(@"INSERT control.IncrementalSyncResult(IncrementalRunID, CompanyID, Endpoint, WatermarkFromUTC, Status)
                VALUES(@r, @c, @e, @w, 'RUNNING')", c))
            {
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.Parameters.AddWithValue("@c", companyId);
                cmd.Parameters.AddWithValue("@e", endpoint);
                cmd.Parameters.AddWithValue("@w", fromUtc);
                cmd.ExecuteNonQuery();
            }
        }

        void FinishResult(Guid runId, Guid companyId, string endpoint, bool ok, long rows, DateTime? toUtc, string error)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(@"UPDATE control.IncrementalSyncResult SET Status=@s, RowsReceived=@rows,
                WatermarkToUTC=@to, ErrorMessage=@error, CompletedAtUTC=SYSUTCDATETIME()
                WHERE IncrementalRunID=@r AND CompanyID=@c AND Endpoint=@e", c))
            {
                cmd.Parameters.AddWithValue("@s", ok ? "COMPLETE" : "FAILED");
                cmd.Parameters.AddWithValue("@rows", rows);
                cmd.Parameters.AddWithValue("@to", (object)toUtc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@error", (object)Truncate(error, 3500) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.Parameters.AddWithValue("@c", companyId);
                cmd.Parameters.AddWithValue("@e", endpoint);
                cmd.ExecuteNonQuery();
            }
        }

        void UpsertWatermark(Guid companyId, string endpoint, DateTime toUtc, Guid runId)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(@"MERGE control.IncrementalWatermark AS target
                USING (SELECT @c AS CompanyID, @e AS Endpoint) AS source
                ON target.CompanyID=source.CompanyID AND target.Endpoint=source.Endpoint
                WHEN MATCHED THEN UPDATE SET LastSuccessfulToUTC=@to, LastIncrementalRunID=@r, UpdatedAtUTC=SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT(CompanyID, Endpoint, LastSuccessfulToUTC, LastIncrementalRunID)
                    VALUES(@c, @e, @to, @r);", c))
            {
                cmd.Parameters.AddWithValue("@c", companyId);
                cmd.Parameters.AddWithValue("@e", endpoint);
                cmd.Parameters.AddWithValue("@to", toUtc);
                cmd.Parameters.AddWithValue("@r", runId);
                cmd.ExecuteNonQuery();
            }
        }

        int CountFailedResults(Guid runId)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand("SELECT COUNT(*) FROM control.IncrementalSyncResult WHERE IncrementalRunID=@r AND Status <> 'COMPLETE'", c))
            {
                cmd.Parameters.AddWithValue("@r", runId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        static string Truncate(string value, int length)
        {
            return string.IsNullOrEmpty(value) || value.Length <= length ? value : value.Substring(0, length);
        }
    }
}
