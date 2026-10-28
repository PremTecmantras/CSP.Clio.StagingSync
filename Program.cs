using CSP.Clio.API.Data;
using CSP.Clio.Sync.Data;
using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Collections.Generic;
using System.Diagnostics;
using System.Data.SqlClient;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;
using RestSharp;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Controlled staging and publishing variant of CSP.Clio.Sync.
    /// It uses the same Clio API calls, Save* mapping code and endpoint order as the production EXE, but:
    ///  - refuses to run unless the connection targets the configured staging database;
    ///  - always performs a FULL extraction into an emptied per-company buffer;
    ///  - records control.SyncRun / control.SyncEndpointResult (expected rows created at start);
    ///  - marks an endpoint COMPLETE only when API pagination and saving complete without an error,
    ///    and the committed SQL row count equals the API row count;
    ///  - never runs ConvertViewsToTable, PopulateTables, SyncMatters or the Dubai document download against the staging database;
    ///  - invokes dbo.PublishApprovedClioStagingRun only when the newly extracted RunID is EXTRACTED and validation passes;
    ///    the production publisher owns the approved reporting sequence;
    ///  - writes LastRefreshTime / LastSyncTime / FullSyncStart / FullSyncStop to _EKPCompanies like the production EXE,
    ///    but only for a company whose endpoints ALL completed (a failed endpoint never advances the cursor).
    /// Exit code 0 = configured pipeline completed, 1 = extraction/validation/publication failed, 2 = refused to start/configuration error.
    /// </summary>
    class Program
    {
        const string ErrorTag = "CSP.Clio.StagingSync - Company: {0} - Table: {1}";
        static bool _companyHadFailure;

        static async Task<int> Main(string[] args)
        {
            try
            {
                using (RunConsoleLogger.Start(HasArgument(args, "--incremental")))
                {
                    return await MainCore(args);
                }

            }
            catch (Exception ex)
            {
                // Logging setup itself can fail (for example, no write permission), so retain a console fallback.
                Console.Error.WriteLine("FATAL STARTUP ERROR: " + ex);
                return 1;
            }
        }

        static async Task<int> MainCore(string[] args)
        {
            Console.WriteLine("CSP Clio Staging Sync");
            // Normal scheduled/Visual Studio execution has no arguments.  These settings
            // make the pipeline behavior explicit in App.config rather than depending on
            // command-line flags which a scheduler may not provide.
            bool abandonStale = ReadBooleanSetting("AbandonPreviousRunningRun") || HasArgument(args, "--abandon-stale");
            bool publishFromConfiguration = ReadBooleanSetting("PublishAfterValidation");
            bool publishRequestedOnCommandLine = HasArgument(args, "--publish-after-validation");
            bool publishAfterValidation = publishFromConfiguration || publishRequestedOnCommandLine;
            bool incrementalRequested = HasArgument(args, "--incremental");

            if (publishRequestedOnCommandLine && !publishFromConfiguration && !HasArgument(args, "--confirm-publish"))
            {
                Console.Error.WriteLine("PUBLISH BLOCKED: --confirm-publish is required. No extraction or production change was started.");
                return 2;
            }

            if (HasArgument(args, "--validate"))
            {
                try
                {
                    var validator = PipelineValidation.CreateGuarded();
                    return validator.Validate(TryGetRunId(args), 10m);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Validation could not run: " + ex.Message);
                    return 2;
                }
            }

            if (HasArgument(args, "--status"))
            {
                try { return PipelineValidation.CreateGuarded().Status(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Status could not run: " + ex.Message);
                    return 2;
                }
            }

            if (HasArgument(args, "--preflight"))
            {
                try { return PipelineValidation.CreateGuarded().Preflight(); }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Preflight could not run: " + ex.Message);
                    return 2;
                }
            }

            if (HasArgument(args, "--help") || HasArgument(args, "-h") || HasArgument(args, "/?"))
            {
                PrintUsage();
                return 0;
            }

            StagingAudit audit;
            try
            {
                audit = StagingAudit.CreateGuarded();
                Console.WriteLine("Target: " + audit.Describe());
                // An incremental task must never abandon or overlap a full extraction.
                // It only updates changed staging rows and does not publish.
                audit.PreflightOrThrow(incrementalRequested ? false : abandonStale);
                if (incrementalRequested)
                {
                    if (!ReadBooleanSetting("EnableIncrementalSync"))
                    {
                        Console.Error.WriteLine("INCREMENTAL SYNC BLOCKED: set EnableIncrementalSync=true only after the incremental control script and first test are complete.");
                        return 2;
                    }
                    return await new IncrementalSyncRunner(audit.ConnectionString).RunAsync();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("PIPELINE PREFLIGHT FAILED: " + ex);
                return 2;
            }

            string abort = null;
            bool processed = false;
            try
            {
                processed = await RunAsync(audit);
            }
            catch (Exception ex)
            {
                abort = ex.GetType().Name + ": " + ex.Message;
                Console.Error.WriteLine(abort);
                try { new BIDataManager(false).LogException(ex, string.Format(ErrorTag, "-", "Run")); } catch { }
            }

            if (audit.RunID == Guid.Empty)
                return 1; // nothing was started, so there is no run to close

            bool ok;
            try { ok = audit.FinishRun(abort ?? (processed ? null : "No active companies were processed")); }
            catch (Exception ex) { Console.Error.WriteLine("Could not close the run: " + ex.Message); return 1; }
            if (!ok) return 1;

            try
            {
                Console.WriteLine("Extraction completed. Running the mandatory 10% staging/production validation.");
                var validationExitCode = PipelineValidation.CreateGuarded().Validate(audit.RunID, 10m);
                if (validationExitCode != 0)
                {
                    Console.Error.WriteLine("SYNC VALIDATION FAILED: this RunID must not be passed to PublishApprovedClioStagingRun.");
                    return 1;
                }
                Console.WriteLine("SYNC VALIDATION PASSED: this RunID is eligible for publication.");
                if (!publishAfterValidation) return 0;

                try
                {
                    ProductionPublisher.Publish(audit.RunID);
                    return 0;
                }
                catch (SqlException ex)
                {
                    ProductionPublisher.PrintSqlErrors(ex);
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("PUBLISH FAILED BEFORE/AFTER THE PROCEDURE: " + ex.GetType().Name + ": " + ex.Message);
                    return 2;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("SYNC VALIDATION COULD NOT RUN: " + ex.Message);
                return 2;
            }
        }

        static bool HasArgument(IEnumerable<string> args, string value)
        {
            return args.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        }

        static bool ReadBooleanSetting(string name)
        {
            var rawValue = ConfigurationManager.AppSettings[name];
            bool value;
            if (!bool.TryParse(rawValue, out value))
                throw new ConfigurationErrorsException("AppSetting '" + name + "' must be true or false.");
            return value;
        }

        static Guid? TryGetRunId(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--run-id", StringComparison.OrdinalIgnoreCase))
                {
                    Guid runId;
                    if (!Guid.TryParse(args[i + 1], out runId))
                        throw new ArgumentException("--run-id must be a valid GUID.");
                    return runId;
                }
            }
            return null;
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  CSP.Clio.StagingSync.exe                 Run the configured extract/validate/publish pipeline.");
            Console.WriteLine("  App.config: AbandonPreviousRunningRun and PublishAfterValidation control no-argument scheduler behavior.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --abandon-stale Explicitly close a confirmed-dead staging run, then extract.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --publish-after-validation --confirm-publish  Extract, validate, then publish the new RunID.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --validate [--run-id <GUID>]  Run read-only staging/production validation.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --preflight    Check raw schemas and pipeline readiness without changing data.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --status       Show recent staging and publication audit state.");
            Console.WriteLine("  CSP.Clio.StagingSync.exe --incremental  Poll configured changed records into staging only; does not publish or truncate.");
        }

        /// <returns>false if there was nothing to process.</returns>
        static async Task<bool> RunAsync(StagingAudit audit)
        {
            var runTimer = Stopwatch.StartNew();
            var dataManager = new BIDataManager(false);
            var companies = dataManager.GetClioConnectedCompanies();
            if (companies.Count == 0)
            {
                Console.WriteLine("No Active companies in staging dbo._EKPCompanies. Enable the agreed test companies first.");
                Console.WriteLine("Extraction elapsed: " + FormatElapsed(runTimer));
                return false;
            }

            //var existing = audit.BufferRowCount(StagingAudit.EndpointManifest.Select(e => e.Value).Distinct());
            //Console.WriteLine("Rows currently in the manifest tables (all companies): " + existing);

            audit.StartRun(companies.Select(c => c.ID));
            Console.WriteLine("RunID: " + audit.RunID + " - " + companies.Count + " company(ies) x " + StagingAudit.EndpointManifest.Count + " endpoints expected");

            var clearTimer = Stopwatch.StartNew();
            dataManager.ClearAllData();
            clearTimer.Stop();
            Console.WriteLine("SP_ClearAllData completed before full sync: " + FormatElapsed(clearTimer));

            foreach (var company in companies)
            {
                var companyTimer = Stopwatch.StartNew();
                try
                {
                    if (await IsTokenExpired(company.ClioToken))
                    {
                        Console.WriteLine(string.Format(Properties.Resources.CompanyTokenExpired, company.CompanyName));
                        var tokenClient = new CSP.Clio.API.ClioServiceClient($"Bearer {company.ClioToken}");
                        var newToken = await tokenClient.GetNewToken(company.ClioApiKey, company.ClioApiSecret, company.ClioRefreshToken);
                        company.ClioToken = newToken.access_token;
                        if (!string.IsNullOrWhiteSpace(newToken.refresh_token))
                            company.ClioRefreshToken = newToken.refresh_token;
                        dataManager.UpdateClioConnectedCompanies(company.ID, DateTime.Now, company.ClioToken);
                        Console.WriteLine(string.Format(Properties.Resources.UpdatedTokenForCompany, company.CompanyName));
                    }

                    Console.WriteLine();
                    Console.WriteLine(new string('-', 72));
                    Console.WriteLine(string.Format(Properties.Resources.SavingCompanyData, company.CompanyName));
                    Console.WriteLine(new string('-', 72));
                    var updateStartTime = DateTime.Now;
                    dataManager.SetFullSyncStart(company.ID);
                    _companyHadFailure = false;

                    // Full extraction: no lastRefreshTime filter, updateData=false (buffer was just emptied), all endpoints.
                    await ExtractCompany(audit, new BIDataManager(false), company.ID, company.CompanyName, $"Bearer {company.ClioToken}");

                    if (_companyHadFailure)
                    {
                        Console.Error.WriteLine("Company '" + company.CompanyName + "': at least one endpoint failed - sync times NOT updated.");
                    }
                    else
                    {
                        // Same values the production EXE records after a full sync.
                        dataManager.UpdateClioConnectedCompanies(company.ID, updateStartTime);                       // LastRefreshTime
                        dataManager.UpdateClioConnectedCompaniesLastSyncTime(company.ID, updateStartTime.AddMinutes(-5)); // LastSyncTime
                        dataManager.SetFullSyncStop(company.ID);                                                     // FullSyncStop
                    }
                }
                catch (Exception ex)
                {
                    // Endpoints of this company left PENDING/RUNNING are closed as FAILED by FinishRun.
                    Console.Error.WriteLine("Company '" + company.CompanyName + "' aborted: " + ex.Message);
                    dataManager.LogException(ex, string.Format(ErrorTag, company.CompanyName, "Company"));
                }
                finally
                {
                    Console.WriteLine();
                    Console.WriteLine("Company total: " + company.CompanyName + " | " + FormatElapsed(companyTimer));
                    Console.WriteLine(new string('-', 72));
                }
            }
            Console.WriteLine("Extraction elapsed: " + FormatElapsed(runTimer));
            return true;
        }

        static string FormatElapsed(Stopwatch timer)
        {
            return timer.Elapsed.TotalSeconds.ToString("F2") + "s";
        }

        static string FormatElapsed(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("F2") + "s";
        }

        internal static async Task<bool> IsTokenExpired(string bearerToken)
        {
            try
            {
                var client = new CSP.Clio.API.ClioServiceClient($"Bearer {bearerToken}");
                var result = await client.GetCustomFields();
                return result?.DataModel?.error?.Contains("invalid_token") == true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>Same endpoint order and Save* calls as ClioDataSyncManager in CSP.Clio.Sync (doFullSync = true).</summary>
        static async Task ExtractCompany(StagingAudit audit, BIDataManager dm, Guid id, string name, string bearerToken)
        {
            var pageClient = new ClioPageStreamClient();

            await Endpoint<PracticeAreaData>(audit, dm, id, name, bearerToken, pageClient, "PracticeAreas", "GetPracticeAreas", "practice_areas", d => { dm.SavePracticeAreas(d, id); return Task.CompletedTask; });
            await Endpoint<ActivityData>(audit, dm, id, name, bearerToken, pageClient, "Activities", "GetActivities", "activities", d => { dm.SaveActivity(d, id); return Task.CompletedTask; });
            await Endpoint<CalenderItem>(audit, dm, id, name, bearerToken, pageClient, "Calendars", "GetCalender", "calendars", d => { dm.SaveCalender(d, id); return Task.CompletedTask; });
            await Endpoint<CalenderEntry>(audit, dm, id, name, bearerToken, pageClient, "CalendarEntries", "GetCalenderEntries", "calendar_entries", d => { dm.SaveCalenderEntries(d, id); return Task.CompletedTask; });
            await Endpoint<RelationshipDataModel>(audit, dm, id, name, bearerToken, pageClient, "Relationships", "GetRelationships", "relationships", d => { dm.SaveRelationship(d, id); return Task.CompletedTask; });
            await Endpoint<TaskDataModel>(audit, dm, id, name, bearerToken, pageClient, "Tasks", "GetTasks", "tasks", d => SyncDataEntity(id, d, new SyncDataDelegate<TaskDataModel>(dm.SaveTasks)));
            await Endpoint<TaskTypeDataModel>(audit, dm, id, name, bearerToken, pageClient, "TaskTypes", "GetTaskTypes", "task_types", d => SyncDataEntity(id, d, new SyncDataDelegate<TaskTypeDataModel>(dm.SaveTaskTypes)));
            await Endpoint<MatterDataModel>(audit, dm, id, name, bearerToken, pageClient, "Matters", "GetMatters", "matters", d => SyncDataEntity(id, d, new SyncDataDelegate<MatterDataModel>(dm.SaveMatters)));
            await Endpoint<BillModelModel>(audit, dm, id, name, bearerToken, pageClient, "Bills", "GetBills", "bills", d => { dm.SaveBills(d, id); return Task.CompletedTask; });
            await Endpoint<BankTransactionModel>(audit, dm, id, name, bearerToken, pageClient, "BankTransactions", "GetBankTransactions", "bank_transactions", d => { dm.SaveBankTransactions(d, id); return Task.CompletedTask; });
            await Endpoint<CustomFieldData>(audit, dm, id, name, bearerToken, pageClient, "CustomFields", "GetCustomFields", "custom_fields", d => { dm.SaveCustomFields(d, id); return Task.CompletedTask; });
            await Endpoint<CustomFieldSets>(audit, dm, id, name, bearerToken, pageClient, "CustomFieldSets", "GetCustomFieldSets", "custom_field_sets", d => { dm.SaveCustomFieldSets(d, id); return Task.CompletedTask; });
            await Endpoint<FolderModel>(audit, dm, id, name, bearerToken, pageClient, "Folders", "GetFolders", "folders", d => { dm.SaveFolders(d, id); return Task.CompletedTask; });
            await Endpoint<LineItemModel>(audit, dm, id, name, bearerToken, pageClient, "LineItems", "GetLineItems", "line_items", d => { dm.SaveLineItems(d, id); return Task.CompletedTask; });
            await Endpoint<ContactDataModel>(audit, dm, id, name, bearerToken, pageClient, "Contacts", "GetContacts", "contacts", d => { dm.SaveContacts(d, id); return Task.CompletedTask; });
            await Endpoint<AllocationsModel>(audit, dm, id, name, bearerToken, pageClient, "Allocations", "GetAllocations", "allocations", d => { dm.SaveAllocations(d, id); return Task.CompletedTask; });
            await Endpoint<UserDataModel>(audit, dm, id, name, bearerToken, pageClient, "Users", "GetUsers", "users", d => { dm.SaveUsers(d, id); return Task.CompletedTask; });
            await Endpoint<ActivityDescriptionModel>(audit, dm, id, name, bearerToken, pageClient, "ActivityDescriptions", "GetActivityDescription", "activity_descriptions", d => SyncDataEntity(id, d, new SyncDataDelegate<ActivityDescriptionModel>(dm.SaveActivityDescription)));
            await Endpoint<NoteDataModel>(audit, dm, id, name, bearerToken, pageClient, "MatterNotes", "GetMatterNotes", "notes", d => { dm.SaveMatterNotes(d, id); return Task.CompletedTask; });
        }

        /// <summary>
        /// Fetch, save, verify, record. A failure here is recorded against the endpoint and does not stop
        /// the remaining endpoints (so one run shows every problem), but the run can then never become EXTRACTED.
        /// </summary>
        static async Task Endpoint<TItem>(
            StagingAudit audit, BIDataManager dm, Guid companyId, string companyName, string bearerToken,
            ClioPageStreamClient pageClient, string endpoint, string apiMethodName, string apiService,
            Func<List<TItem>, Task> save)
        {
            var endpointTimer = Stopwatch.StartNew();
            var apiMetrics = new ClioPageStreamMetrics();
            var verifyTimer = new Stopwatch();
            var mappingDuration = TimeSpan.Zero;
            var sqlWriteDuration = TimeSpan.Zero;
            long received = 0, written = 0;
            audit.EndpointStarted(companyId, endpoint);
            try
            {
                await pageClient.StreamAsync(apiMethodName, apiService, bearerToken, apiMetrics, async root =>
                {
                    var pageData = root.GetData().Cast<TItem>().ToList();
                    received += pageData.Count;
                    if (pageData.Count == 0) return;

                    var pageSaveTimer = Stopwatch.StartNew();
                    var sqlTimeBefore = dm.SqlWriteTime;
                    try
                    {
                        await save(pageData);
                    }
                    finally
                    {
                        pageSaveTimer.Stop();
                        var sqlElapsed = dm.SqlWriteTime - sqlTimeBefore;
                        if (sqlElapsed < TimeSpan.Zero) sqlElapsed = TimeSpan.Zero;
                        sqlWriteDuration += sqlElapsed;
                        mappingDuration += pageSaveTimer.Elapsed > sqlElapsed ? pageSaveTimer.Elapsed - sqlElapsed : TimeSpan.Zero;
                    }
                });

                if (!apiMetrics.PaginationComplete)
                    throw new InvalidOperationException("Clio pagination did not reach a verified final page.");

                var table = StagingAudit.EndpointManifest.First(e => e.Key == endpoint).Value;
                verifyTimer.Start();
                written = audit.CountCompanyRows(table, companyId);
                verifyTimer.Stop();

                if (written != received)
                    throw new InvalidOperationException("Row count mismatch: received " + received +
                        " but dbo." + table + " contains " + written + " rows for this company.");

                endpointTimer.Stop();
                var rowsPerMinute = endpointTimer.Elapsed.TotalMinutes > 0 ? received / endpointTimer.Elapsed.TotalMinutes : 0;
                Console.WriteLine();
                Console.WriteLine("  " + endpoint + " - " + companyName);
                Console.WriteLine("    Rows received: " + received);
                Console.WriteLine("    Time       API: " + FormatElapsed(apiMetrics.ApiDownloadTime)
                    + " | Mapping: " + FormatElapsed(mappingDuration)
                    + " | SQL write: " + FormatElapsed(sqlWriteDuration)
                    + " | Verify: " + FormatElapsed(verifyTimer));
                Console.WriteLine("    Requests   Pages: " + apiMetrics.PagesReceived
                    + " | Attempts: " + apiMetrics.RequestsAttempted
                    + " | Rate-limit retries: " + apiMetrics.RateLimitRetries
                    + " | Retry wait: " + FormatElapsed(apiMetrics.RateLimitWaitTime));
                Console.WriteLine("    Throughput " + rowsPerMinute.ToString("F0") + " rows/min"
                    + " | Endpoint total: " + FormatElapsed(endpointTimer));

                audit.EndpointFinished(companyId, endpoint, true, received, written, null);
            }
            catch (Exception ex)
            {
                endpointTimer.Stop();
                _companyHadFailure = true;
                Console.Error.WriteLine();
                Console.Error.WriteLine("  FAILED: " + endpoint + " - " + companyName);
                Console.Error.WriteLine("    Error      " + ex.Message);
                Console.Error.WriteLine("    Time       API: " + FormatElapsed(apiMetrics.ApiDownloadTime)
                    + " | Mapping: " + FormatElapsed(mappingDuration)
                    + " | SQL write: " + FormatElapsed(sqlWriteDuration)
                    + " | Verify: " + FormatElapsed(verifyTimer));
                Console.Error.WriteLine("    Requests   Pages: " + apiMetrics.PagesReceived
                    + " | Attempts: " + apiMetrics.RequestsAttempted
                    + " | Rate-limit retries: " + apiMetrics.RateLimitRetries
                    + " | Retry wait: " + FormatElapsed(apiMetrics.RateLimitWaitTime));
                Console.Error.WriteLine("    Endpoint total: " + FormatElapsed(endpointTimer));
                try { dm.LogException(ex, string.Format(ErrorTag, companyName, endpoint)); } catch { }
                audit.EndpointFinished(companyId, endpoint, false, received, written, ex.GetType().Name + ": " + ex.Message);
            }
        }

        public delegate void SyncDataDelegate<T>(List<T> data, Guid companyID);

        static async Task SyncDataEntity<T>(Guid companyID, List<T> data, SyncDataDelegate<T> syncDataDelegate)
        {
            if (data != null && data.Count > 0)
            {
                await PartitionAction(data, (items, id) => syncDataDelegate(items.ToList(), id), companyID, Properties.Settings.Default.DataSavePartitionSize);
            }
        }

        private static async Task PartitionAction<T, O>(List<T> list, Action<IEnumerable<T>, O> action, O value, int partitionSize = 50)
        {
            if (partitionSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(partitionSize), "Partition size must be greater than zero.");

            var partition = new List<T>();
            for (int i = 0; i < list.Count; i++)
            {
                partition.Add(list[i]);
                if (partition.Count >= partitionSize)
                {
                    action(partition, value);
                    partition.Clear();
                }
            }
            if (partition.Count > 0)
            {
                action(partition, value);
            }
            await Task.CompletedTask;
        }
    }

    internal sealed class ClioPageStreamMetrics
    {
        public TimeSpan ApiDownloadTime { get; set; }
        public TimeSpan RateLimitWaitTime { get; set; }
        public int PagesReceived { get; set; }
        public int RequestsAttempted { get; set; }
        public int RateLimitRetries { get; set; }
        public bool PaginationComplete { get; set; }
    }

    internal sealed class ClioPageStreamClient
    {
        const int MaximumRateLimitRetries = 5;
        readonly Uri _baseUri;

        public ClioPageStreamClient()
        {
            var configuredBaseUrl = ConfigurationManager.AppSettings["ClioApiBaseUrl"];
            if (string.IsNullOrWhiteSpace(configuredBaseUrl))
                configuredBaseUrl = "https://eu.app.clio.com/api/v4/";

            Uri baseUri;
            if (!Uri.TryCreate(configuredBaseUrl, UriKind.Absolute, out baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
                throw new ConfigurationErrorsException("ClioApiBaseUrl must be an absolute HTTPS URL.");

            var builder = new UriBuilder(baseUri);
            if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
                builder.Path += "/";
            _baseUri = builder.Uri;
        }

        public async Task StreamAsync(
            string apiMethodName,
            string apiService,
            string bearerToken,
            ClioPageStreamMetrics metrics,
            Func<CSP.Clio.API.Data.IDataRoot, Task> consumePage,
            string additionalQuery = null)
        {
            if (metrics == null) throw new ArgumentNullException(nameof(metrics));
            if (consumePage == null) throw new ArgumentNullException(nameof(consumePage));
            if (string.IsNullOrWhiteSpace(bearerToken)) throw new ArgumentException("A bearer token is required.", nameof(bearerToken));

            var fields = GetDefaultFields(apiMethodName);
            var pageType = GetPageType(apiMethodName);
            var nextUrl = BuildInitialUrl(apiService, fields, additionalQuery);
            var consecutiveRateLimitRetries = 0;
            var client = new RestClient(_baseUri.ToString());

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var request = new RestRequest(nextUrl, Method.Get);
                request.AddHeader("Authorization", bearerToken);

                RestResponse response;
                var requestTimer = Stopwatch.StartNew();
                metrics.RequestsAttempted++;
                try
                {
                    response = await client.ExecuteAsync(request).ConfigureAwait(false);
                }
                finally
                {
                    requestTimer.Stop();
                    metrics.ApiDownloadTime += requestTimer.Elapsed;
                }

                if (response == null)
                    throw new InvalidOperationException("Clio API returned no response for " + apiService + ".");

                if ((int)response.StatusCode == 429)
                {
                    if (consecutiveRateLimitRetries >= MaximumRateLimitRetries)
                        throw new InvalidOperationException("Clio API rate-limit retry limit reached for " + apiService + ".");

                    var wait = GetRetryDelay(response);
                    metrics.RateLimitRetries++;
                    var waitTimer = Stopwatch.StartNew();
                    await Task.Delay(wait).ConfigureAwait(false);
                    waitTimer.Stop();
                    metrics.RateLimitWaitTime += waitTimer.Elapsed;
                    consecutiveRateLimitRetries++;
                    continue;
                }

                if (!response.IsSuccessful)
                    throw new InvalidOperationException("Clio API request failed for " + apiService + " (HTTP " + (int)response.StatusCode + ", " + response.ResponseStatus + ").");

                var page = JsonConvert.DeserializeObject(response.Content, pageType) as CSP.Clio.API.Data.IDataRoot;
                if (page == null)
                    throw new InvalidOperationException("Clio API returned an empty or invalid page for " + apiService + ".");

                if (page.GetData() == null)
                    throw new InvalidOperationException("Clio API page had no data payload for " + apiService + ".");

                metrics.PagesReceived++;
                await consumePage(page).ConfigureAwait(false);

                consecutiveRateLimitRetries = 0;
                nextUrl = page.GetNextPageUrl();
                if (string.IsNullOrWhiteSpace(nextUrl))
                {
                    metrics.PaginationComplete = true;
                    break;
                }

                ValidateNextPageUrl(nextUrl);
            }
        }

        static string GetDefaultFields(string apiMethodName)
        {
            var method = typeof(CSP.Clio.API.ClioServiceClient).GetMethod(apiMethodName, BindingFlags.Instance | BindingFlags.Public);
            var parameter = method?.GetParameters().FirstOrDefault();
            var value = parameter?.DefaultValue as string;
            if (string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("No default field selection is available for Clio API method '" + apiMethodName + "'.");
            return value;
        }

        static Type GetPageType(string apiMethodName)
        {
            var method = typeof(CSP.Clio.API.ClioServiceClient).GetMethod(apiMethodName, BindingFlags.Instance | BindingFlags.Public);
            var methodReturnType = method?.ReturnType;
            if (methodReturnType == null || !methodReturnType.IsGenericType || methodReturnType.GetGenericTypeDefinition() != typeof(Task<>))
                throw new InvalidOperationException("Unable to determine the Clio page type for API method '" + apiMethodName + "'.");

            var operationType = methodReturnType.GetGenericArguments()[0];
            var pageType = operationType.GetProperty("DataModel")?.PropertyType;
            if (pageType == null || !typeof(CSP.Clio.API.Data.IDataRoot).IsAssignableFrom(pageType))
                throw new InvalidOperationException("Clio API method '" + apiMethodName + "' does not return a supported page model.");
            return pageType;
        }

        string BuildInitialUrl(string apiService, string fields, string additionalQuery)
        {
            if (string.IsNullOrWhiteSpace(apiService) || apiService.IndexOfAny(new[] { '?', '#', '\\' }) >= 0)
                throw new ArgumentException("Invalid Clio API service path.", nameof(apiService));

            if (!string.IsNullOrWhiteSpace(additionalQuery) &&
                (additionalQuery.IndexOf('#') >= 0 || additionalQuery.IndexOf('?') >= 0 || additionalQuery.IndexOf('\\') >= 0))
                throw new ArgumentException("Invalid additional Clio API query string.", nameof(additionalQuery));

            var url = _baseUri.AbsoluteUri + apiService.TrimStart('/') + "?fields=" + fields;
            if (!string.IsNullOrWhiteSpace(additionalQuery))
                url += "&" + additionalQuery.TrimStart('&');
            ValidateNextPageUrl(url);
            return url;
        }

        void ValidateNextPageUrl(string url)
        {
            Uri next;
            if (!Uri.TryCreate(url, UriKind.Absolute, out next) || next.Scheme != Uri.UriSchemeHttps ||
                !string.Equals(next.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase) || next.Port != _baseUri.Port ||
                !next.AbsolutePath.StartsWith(_baseUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Clio API returned a paging URL outside the configured HTTPS API path.");
        }

        static TimeSpan GetRetryDelay(RestResponse response)
        {
            var retryAfter = response.Headers?.FirstOrDefault(h => string.Equals(h.Name, "Retry-After", StringComparison.OrdinalIgnoreCase))?.Value?.ToString();
            int seconds;
            if (int.TryParse(retryAfter, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);

            var match = Regex.Match(response.Content ?? string.Empty, @"retry\s+in\s+(\d+)", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups[1].Value, out seconds) && seconds >= 0)
                return TimeSpan.FromSeconds(seconds);

            return TimeSpan.FromSeconds(60);
        }
    }
}
