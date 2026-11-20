using CSP.Clio.Sync.Data;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Task = System.Threading.Tasks.Task;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Finds records that were deleted in Clio. An updated_since poll can never see a deletion, so this lists every ID Clio
    /// still returns and compares it with the raw table. A record is only treated as deleted when ALL of these hold:
    ///   - the full ID listing finished without error (verified last page),
    ///   - the listing is not empty while the table has rows,
    ///   - the number of missing IDs is within DeleteReconcileMaxPercent of the company's rows,
    ///   - an individual GET of that record returns HTTP 404.
    /// A deleted record is MOVED to dbo.[Table]_Deleted (soft delete: kept with the time and run), so the reporting views,
    /// which read the raw tables, no longer see it. Nothing is changed unless apply is true.
    /// </summary>
    internal sealed class DeleteReconciler
    {
        sealed class Target
        {
            public string Endpoint, Table, ApiMethod, Service;
        }

        static readonly Target[] Targets =
        {
            new Target { Endpoint = "Activities",           Table = "Activities",           ApiMethod = "GetActivities",          Service = "activities" },
            new Target { Endpoint = "ActivityDescriptions", Table = "ActivityDescriptions", ApiMethod = "GetActivityDescription", Service = "activity_descriptions" },
            new Target { Endpoint = "Allocations",          Table = "Allocations",          ApiMethod = "GetAllocations",         Service = "allocations" },
            new Target { Endpoint = "BankTransactions",     Table = "BankTransactions",     ApiMethod = "GetBankTransactions",    Service = "bank_transactions" },
            new Target { Endpoint = "Bills",                Table = "Bills",                ApiMethod = "GetBills",               Service = "bills" },
            new Target { Endpoint = "CalendarEntries",      Table = "CalendarEntries",      ApiMethod = "GetCalenderEntries",     Service = "calendar_entries" },
            new Target { Endpoint = "Calendars",            Table = "Calendars",            ApiMethod = "GetCalender",            Service = "calendars" },
            new Target { Endpoint = "Contacts",             Table = "Contacts",             ApiMethod = "GetContacts",            Service = "contacts" },
            new Target { Endpoint = "CustomFields",         Table = "CustomFields",         ApiMethod = "GetCustomFields",        Service = "custom_fields" },
            new Target { Endpoint = "CustomFieldSets",      Table = "CustomFieldSets",      ApiMethod = "GetCustomFieldSets",     Service = "custom_field_sets" },
            new Target { Endpoint = "Folders",              Table = "Folders",              ApiMethod = "GetFolders",             Service = "folders" },
            new Target { Endpoint = "LineItems",            Table = "LineItems",            ApiMethod = "GetLineItems",           Service = "line_items" },
            new Target { Endpoint = "MatterNotes",          Table = "MatterNotes",          ApiMethod = "GetMatterNotes",         Service = "notes" },
            new Target { Endpoint = "Matters",              Table = "Matters",              ApiMethod = "GetMatters",             Service = "matters" },
            new Target { Endpoint = "PracticeAreas",        Table = "PracticeAreas",        ApiMethod = "GetPracticeAreas",       Service = "practice_areas" },
            new Target { Endpoint = "Relationships",        Table = "Relationships",        ApiMethod = "GetRelationships",       Service = "relationships" },
            new Target { Endpoint = "Tasks",                Table = "Tasks",                ApiMethod = "GetTasks",               Service = "tasks" },
            new Target { Endpoint = "TaskTypes",            Table = "TaskTypes",            ApiMethod = "GetTaskTypes",           Service = "task_types" },
            new Target { Endpoint = "Users",                Table = "Users",                ApiMethod = "GetUsers",               Service = "users" },
        };

        /// <summary>
        /// The delete pass (--reconcile-deletes in the main EXE, and the separate CSP.Clio.StagingDelete.exe). Only one pass runs at a time
        /// (ClioDeleteRun). The whole pass holds the SHARED ClioStagingData lock: it runs beside the incremental passes (also shared), but a
        /// publish cycle (exclusive) can never start while it runs, and a pass never starts while a publish cycle runs - it stops at once.
        /// Every applied pass is recorded in control.DeleteRun (COMPLETE only when all endpoints were checked without problems).
        /// </summary>
        public static async Task<int> RunCommandAsync(StagingAudit audit, bool apply, IEnumerable<string> only = null)
        {
            using (var passLock = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.DeleteRun, false))
            {
                if (passLock == null)
                {
                    Console.WriteLine("DELETE PASS SKIPPED: another delete pass is still running.");
                    return 0;
                }
                using (var dataLock = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.DataWrite, true))
                {
                    if (dataLock == null)
                    {
                        Console.WriteLine("DELETE PASS STOPPED: a publish cycle (or the first load) is running. Nothing was changed; the next pass will run after it.");
                        return 0;
                    }
                    audit.PreflightForDelete();

                    var runId = Guid.NewGuid();
                    if (apply)
                    {
                        EnsureDeleteRunTable(audit.ConnectionString);
                        StartDeleteRun(audit.ConnectionString, runId);
                    }

                    Tuple<long, int> result;
                    DeleteReconciler reconciler;
                    try
                    {
                        // The whole pass already holds the shared lock, so the archive steps do not take it again.
                        reconciler = new DeleteReconciler(audit.ConnectionString, apply, false, only);
                        result = await reconciler.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        if (apply) FinishDeleteRun(audit.ConnectionString, runId, "FAILED", 0, 0, ex.GetType().Name + ": " + ex.Message);
                        throw;
                    }

                    if (apply)
                        // A pass limited to some endpoints (--only / DeleteReconcileEndpoints) is recorded as PARTIAL and never counts as a clean pass.
                        FinishDeleteRun(audit.ConnectionString, runId, result.Item2 != 0 ? "PROBLEMS" : reconciler.IsPartial ? "PARTIAL" : "COMPLETE", result.Item1, result.Item2,
                            result.Item2 == 0 ? null : result.Item2 + " endpoint(s) reported a problem; see the run log.");
                    if (result.Item1 > 0)
                        Console.WriteLine("Records were moved to the *_Deleted tables. The next publish cycle copies them to the reporting database.");
                    return result.Item2 == 0 ? 0 : 1;
                }
            }
        }

        /// <summary>
        /// Status of the latest applied delete pass (null when no pass was ever recorded). The publish cycle requires COMPLETE.
        /// </summary>
        public static Tuple<string, DateTime, DateTime?> LatestPass(string connectionString)
        {
            using (var c = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(@"IF OBJECT_ID(N'control.DeleteRun', N'U') IS NULL SELECT CAST(NULL AS varchar(20)), CAST(NULL AS datetime2), CAST(NULL AS datetime2) WHERE 1 = 0
ELSE SELECT TOP (1) Status, StartedAtUTC, CompletedAtUTC FROM control.DeleteRun ORDER BY StartedAtUTC DESC", c))
            {
                c.Open();
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    return Tuple.Create(r.GetString(0), r.GetDateTime(1), r.IsDBNull(2) ? (DateTime?)null : r.GetDateTime(2));
                }
            }
        }

        static void EnsureDeleteRunTable(string connectionString)
        {
            using (var c = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(@"
IF SCHEMA_ID(N'control') IS NULL EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
IF OBJECT_ID(N'control.DeleteRun', N'U') IS NULL
    CREATE TABLE control.DeleteRun
    (
        DeleteRunID uniqueidentifier NOT NULL CONSTRAINT PK_DeleteRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_DeleteRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL,
        RowsMoved bigint NULL,
        Problems int NULL,
        ErrorMessage nvarchar(3500) NULL,
        CONSTRAINT CK_DeleteRun_Status CHECK (Status IN ('RUNNING','COMPLETE','PARTIAL','PROBLEMS','FAILED','ABANDONED'))
    );", c))
            {
                c.Open();
                cmd.ExecuteNonQuery();
            }
        }

        static void StartDeleteRun(string connectionString, Guid runId)
        {
            using (var c = new SqlConnection(connectionString))
            {
                c.Open();
                // The ClioDeleteRun lock is held, so any RUNNING row belongs to a pass whose process died.
                using (var abandon = new SqlCommand("UPDATE control.DeleteRun SET Status = 'ABANDONED', CompletedAtUTC = SYSUTCDATETIME(), ErrorMessage = N'Abandoned: the process ended before the pass finished.' WHERE Status = 'RUNNING'", c))
                    abandon.ExecuteNonQuery();
                using (var cmd = new SqlCommand("INSERT control.DeleteRun (DeleteRunID, Status) VALUES (@r, 'RUNNING')", c))
                {
                    cmd.Parameters.AddWithValue("@r", runId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        static void FinishDeleteRun(string connectionString, Guid runId, string status, long moved, int problems, string error)
        {
            using (var c = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand("UPDATE control.DeleteRun SET Status = @s, CompletedAtUTC = SYSUTCDATETIME(), RowsMoved = @m, Problems = @p, ErrorMessage = @e WHERE DeleteRunID = @r", c))
            {
                cmd.Parameters.AddWithValue("@s", status);
                cmd.Parameters.AddWithValue("@m", moved);
                cmd.Parameters.AddWithValue("@p", problems);
                cmd.Parameters.AddWithValue("@e", (object)StagingAudit.Truncate(error, 3500) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@r", runId);
                c.Open();
                cmd.ExecuteNonQuery();
            }
        }

        readonly string _connectionString;
        readonly bool _apply;
        readonly decimal _maxPercent;
        readonly int _maxConfirmations;
        readonly int _smallTableAllowance;
        readonly HashSet<string> _listingAuthoritative;
        readonly Guid _reconcileRunId = Guid.NewGuid();
        readonly bool _useChunkLocks;
        readonly int _progressSeconds;
        readonly bool _useOwnToken;
        readonly TimeSpan _lockWait;
        readonly HashSet<string> _only;

        /// <summary>True when only some endpoints are reconciled.</summary>
        public bool IsPartial { get { return _only.Count > 0 && _only.Count < Targets.Length; } }

        /// <param name="useChunkLocks">True for the background delete task (each archive step takes the shared ClioDataWrite lock itself).
        /// False only when the caller already holds the exclusive lock for the whole run.</param>
        /// <param name="only">Endpoint names to reconcile (default: all, or AppSetting DeleteReconcileEndpoints).</param>
        public DeleteReconciler(string connectionString, bool apply, bool useChunkLocks = true, IEnumerable<string> only = null)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _apply = apply;
            _useChunkLocks = useChunkLocks;
            _useOwnToken = !string.Equals(ConfigurationManager.AppSettings["DeleteUsesOwnClioToken"], "false", StringComparison.OrdinalIgnoreCase);
            int progressSeconds;
            _progressSeconds = int.TryParse(ConfigurationManager.AppSettings["DeleteProgressSeconds"], out progressSeconds) && progressSeconds >= 0 ? progressSeconds : 60;
            int waitMinutes;
            _lockWait = TimeSpan.FromMinutes(int.TryParse(ConfigurationManager.AppSettings["DeleteLockWaitMinutes"], out waitMinutes) && waitMinutes >= 0 ? waitMinutes : 30);
            var wanted = only != null
                ? only.ToList()
                : (ConfigurationManager.AppSettings["DeleteReconcileEndpoints"] ?? string.Empty)
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            _only = new HashSet<string>(wanted, StringComparer.OrdinalIgnoreCase);
            var unknown = _only.Where(n => !Targets.Any(t => string.Equals(t.Endpoint, n, StringComparison.OrdinalIgnoreCase))).ToList();
            if (unknown.Count > 0)
                throw new ConfigurationErrorsException("Unknown endpoint(s) for the delete reconciliation: " + string.Join(", ", unknown) +
                    ". Allowed: " + string.Join(", ", Targets.Select(t => t.Endpoint)) + ".");
            decimal pct;
            _maxPercent = decimal.TryParse(ConfigurationManager.AppSettings["DeleteReconcileMaxPercent"], NumberStyles.Number, CultureInfo.InvariantCulture, out pct) && pct > 0 ? pct : 5m;
            int max;
            _maxConfirmations = int.TryParse(ConfigurationManager.AppSettings["DeleteReconcileMaxConfirmations"], out max) && max > 0 ? max : 2000;
            _listingAuthoritative = new HashSet<string>(
                (ConfigurationManager.AppSettings["DeleteReconcileListingAuthoritative"] ?? "Users")
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()), StringComparer.OrdinalIgnoreCase);
            int allowance;
            _smallTableAllowance = int.TryParse(ConfigurationManager.AppSettings["DeleteReconcileSmallTableAllowance"], out allowance) && allowance >= 0 ? allowance : 25;
        }

        /// <summary>Returns the number of records moved to the *_Deleted tables (0 in report-only mode) and the number of problems.</summary>
        public async Task<Tuple<long, int>> RunAsync()
        {
            Console.WriteLine("Delete reconciliation " + (_apply ? "(APPLY)" : "(REPORT ONLY - nothing will be changed; add --apply to move confirmed deletions)") +
                ". Safety limit: " + _maxPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% of a company's rows per endpoint.");
            Console.WriteLine("  Endpoint             Company     In table   In Clio   Missing   Result");
            Console.WriteLine("  (In table = rows in " + StagingAudit.RequiredDatabase + "; In Clio = rows Clio returns now; Missing = rows we hold that Clio no longer has. Missing 0 is correct.)");
            long moved = 0;
            long checkedCount = 0;
            var problems = 0;
            var companies = new BIDataManager(false).GetClioConnectedCompanies();

            // Clio limits the requests per token (about 50 pages a minute), and every company has its own token, so the companies are
            // listed in parallel: the pass then takes about as long as the largest company instead of the sum of all of them.
            int parallelCompanies;
            if (!int.TryParse(ConfigurationManager.AppSettings["DeleteMaxParallelCompanies"], out parallelCompanies) || parallelCompanies < 1) parallelCompanies = 4;
            Console.WriteLine("  Companies in parallel: " + Math.Min(parallelCompanies, Math.Max(1, companies.Count)));

            using (var gate = new System.Threading.SemaphoreSlim(parallelCompanies))
            {
                var work = companies.Select(async company =>
                {
                    await gate.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        // An Entity Framework context is not thread-safe: each company uses its own data manager.
                        var dataManager = new BIDataManager(false);
                        string bearer;
                        try
                        {
                            bearer = _useOwnToken
                                ? await ClioTokens.GetOwnBearerTokenAsync(dataManager, company).ConfigureAwait(false)
                                : await ClioTokens.GetBearerTokenAsync(dataManager, company).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            System.Threading.Interlocked.Increment(ref problems);
                            Console.Error.WriteLine("  RECONCILE SKIPPED " + company.CompanyName + ": no Clio token (" + ex.Message + ")");
                            return;
                        }

                        var pageClient = new ClioPageStreamClient();
                        var companyTargets = Targets.Where(t => _only.Count == 0 || _only.Contains(t.Endpoint)).ToList();
                        var companyDone = 0;
                        var companyClock = System.Diagnostics.Stopwatch.StartNew();
                        foreach (var target in companyTargets)
                        {
                            try
                            {
                                long movedHere;
                                try { movedHere = await ReconcileAsync(pageClient, company.ID, company.CompanyName, bearer, target).ConfigureAwait(false); }
                                catch (Exception first) when (first.Message.IndexOf("HTTP 401", StringComparison.Ordinal) >= 0)
                                {
                                    // The token was ended (another process refreshed it): take the current one, or refresh once, and read this endpoint again.
                                    Console.WriteLine("  " + company.CompanyName + ": Clio answered 401 for " + target.Endpoint + "; getting a current token and trying once more.");
                                    bearer = await ClioTokens.RecoverAfterUnauthorizedAsync(dataManager, company, bearer).ConfigureAwait(false);
                                    movedHere = await ReconcileAsync(pageClient, company.ID, company.CompanyName, bearer, target).ConfigureAwait(false);
                                }
                                System.Threading.Interlocked.Add(ref moved, movedHere);
                                System.Threading.Interlocked.Increment(ref checkedCount);
                                companyDone++;
                            }
                            catch (Exception ex)
                            {
                                System.Threading.Interlocked.Increment(ref problems);
                                Console.Error.WriteLine("  RECONCILE FAILED " + target.Endpoint + " - " + company.CompanyName + ": " + ex.Message + " (no rows were changed for this endpoint)");
                            }
                        }
                        // The companies run side by side, so the last line printed is not the end of the pass: say when one company is through.
                        Console.WriteLine("  COMPANY " + company.CompanyName + " FINISHED: " + companyDone + " of " + companyTargets.Count + " endpoints checked in " +
                            companyClock.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture) + ".");
                    }
                    finally { gate.Release(); }
                }).ToList();
                await Task.WhenAll(work).ConfigureAwait(false);
            }

            var expected = companies.Count * Targets.Count(t => _only.Count == 0 || _only.Contains(t.Endpoint));
            Console.WriteLine("Delete reconciliation finished: " + moved + " record(s) moved to *_Deleted, " + problems + " problem(s). " +
                "Checked " + System.Threading.Interlocked.Read(ref checkedCount) + " of " + expected + " endpoint/company combination(s).");
            return Tuple.Create(moved, problems);
        }

        async Task<long> ReconcileAsync(ClioPageStreamClient pageClient, Guid companyId, string companyName, string bearer, Target target)
        {
            var inTable = ReadTableIds(target, companyId);
            var inClio = new HashSet<long>();
            var metrics = new ClioPageStreamMetrics();
            var started = DateTime.Now;
            Console.WriteLine("  ... reading the " + target.Endpoint + " ID list from Clio for " + companyName + " (" + inTable.Count + " rows in table; large tables can take several minutes)");
            var progressClock = System.Diagnostics.Stopwatch.StartNew();
            var lastProgress = TimeSpan.Zero;
            await pageClient.StreamIdsAsync(target.ApiMethod, target.Service, bearer, metrics, ids =>
            {
                foreach (var id in ids) inClio.Add(id);

                // A list of tens of thousands of IDs takes many minutes and would print nothing until it is complete, so you could not tell
                // "still reading" from "stuck". Say so every DeleteProgressSeconds (and whether Clio's rate limit made us wait).
                if (_progressSeconds > 0 && progressClock.Elapsed - lastProgress >= TimeSpan.FromSeconds(_progressSeconds))
                {
                    lastProgress = progressClock.Elapsed;
                    var waits = metrics.RateLimitRetries;
                    Console.WriteLine("  ... " + target.Endpoint + " / " + companyName + ": " + inClio.Count.ToString("N0", CultureInfo.InvariantCulture) + " of about " +
                        inTable.Count.ToString("N0", CultureInfo.InvariantCulture) + " IDs read after " + progressClock.Elapsed.ToString(@"mm\:ss", CultureInfo.InvariantCulture) +
                        (waits > 0 ? "; waited for Clio's rate limit " + waits + "x (" + (int)metrics.RateLimitWaitTime.TotalSeconds + " s)" : string.Empty));
                }
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            Console.WriteLine("  ... " + target.Endpoint + " / " + companyName + " list read in " + (DateTime.Now - started).ToString(@"mm\:ss"));

            if (!metrics.PaginationComplete)
                throw new InvalidOperationException("the Clio ID listing did not reach a verified last page");
            if (inClio.Count == 0 && inTable.Count > 0)
                throw new InvalidOperationException("Clio returned no IDs although the table has " + inTable.Count + " rows; treated as an API problem, not as deletions");

            var missing = inTable.Where(id => !inClio.Contains(id)).OrderBy(id => id).ToList();
            var pct = inTable.Count == 0 ? 0m : 100m * missing.Count / inTable.Count;
            Console.WriteLine("  " + target.Endpoint.PadRight(21) + companyName.PadRight(8) + inTable.Count.ToString().PadLeft(10) + inClio.Count.ToString().PadLeft(10) +
                missing.Count.ToString().PadLeft(10) + "   " + (missing.Count == 0 ? "OK" : missing.Count + " possibly deleted in Clio - checking one by one"));
            if (missing.Count == 0) return 0;

            if (pct > _maxPercent && missing.Count > _smallTableAllowance)
                throw new InvalidOperationException(missing.Count + " records (" + pct.ToString("0.0", CultureInfo.InvariantCulture) + "%) are missing in Clio, above the " +
                    _maxPercent.ToString("0.##", CultureInfo.InvariantCulture) + "% safety limit; sample IDs: " + string.Join(",", missing.Take(10)));
            if (missing.Count > _maxConfirmations)
                throw new InvalidOperationException(missing.Count + " records need individual confirmation, above DeleteReconcileMaxConfirmations (" + _maxConfirmations + ")");

            // Individual confirmation: only HTTP 404 proves a deletion. Any other outcome leaves the record alone.
            // Exception: endpoints whose complete ID listing is the source of truth (the old full sync reloaded them from the
            // listing, so Clio's list can omit a user that a direct lookup still returns). All listing safeguards above still apply.
            var confirmed = new List<long>();
            if (_listingAuthoritative.Contains(target.Endpoint))
            {
                foreach (var id in missing)
                {
                    RecordCandidate(companyId, target.Endpoint, id, "CONFIRMED_DELETED", "Not in Clio's complete listing (listing is authoritative for this endpoint).");
                    confirmed.Add(id);
                }
            }
            else
            foreach (var id in missing)
            {
                try
                {
                    var exists = await pageClient.RecordExistsAsync(target.Service, id, bearer).ConfigureAwait(false);
                    RecordCandidate(companyId, target.Endpoint, id, exists ? "NOT_DELETED" : "CONFIRMED_DELETED");
                    if (!exists) confirmed.Add(id);
                }
                catch (Exception ex)
                {
                    RecordCandidate(companyId, target.Endpoint, id, "RETRY", ex.Message);
                }
            }
            Console.WriteLine("    confirmed deleted in Clio (HTTP 404): " + confirmed.Count + " of " + missing.Count +
                (confirmed.Count > 0 ? "  IDs: " + string.Join(",", confirmed.Take(10)) + (confirmed.Count > 10 ? ",..." : string.Empty) : string.Empty));

            if (confirmed.Count == 0 || !_apply) return 0;
            return ArchiveWithLock(target, companyId, confirmed);
        }

        /// <summary>Moves the rows while holding the shared ClioDataWrite lock: it runs beside the incremental sync, but never while the publish cycle copies this database.</summary>
        long ArchiveWithLock(Target target, Guid companyId, List<long> ids)
        {
            if (!_useChunkLocks) return ArchiveAndRemove(target, companyId, ids);
            using (var writeLock = PipelineLock.TryAcquireWithWait(_connectionString, _lockWait, PipelineLock.DataWrite, true))
            {
                if (writeLock == null)
                    throw new InvalidOperationException("the database is locked by a publish cycle (waited " + (int)_lockWait.TotalMinutes +
                        " minute(s)); these " + ids.Count + " deletion(s) will be retried by the next pass");
                return ArchiveAndRemove(target, companyId, ids);
            }
        }

        HashSet<long> ReadTableIds(Target target, Guid companyId)
        {
            var ids = new HashSet<long>();
            using (var c = Open())
            using (var cmd = new SqlCommand("SELECT CAST(Clio_Internal_ID AS bigint) FROM dbo." + Quote(target.Table) + " WHERE CompanyID=@c AND Clio_Internal_ID IS NOT NULL", c) { CommandTimeout = 600 })
            {
                cmd.Parameters.AddWithValue("@c", companyId);
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) ids.Add(r.GetInt64(0));
            }
            return ids;
        }

        /// <summary>Moves the rows to dbo.[Table]_Deleted (created on first use, same columns plus DeletedAtUTC / DeletedByRunID) and removes them from the raw table, atomically.</summary>
        long ArchiveAndRemove(Target target, Guid companyId, List<long> ids)
        {
            var table = Quote(target.Table);
            var archive = Quote(target.Table + "_Deleted");
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    Exec(c, tx, "IF OBJECT_ID(N'dbo." + target.Table + "_Deleted', N'U') IS NULL BEGIN " +
                        "SELECT TOP (0) * INTO dbo." + archive + " FROM dbo." + table + "; " +
                        "ALTER TABLE dbo." + archive + " ADD DeletedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_" + target.Table + "_Deleted_At DEFAULT SYSUTCDATETIME(), DeletedByRunID uniqueidentifier NULL; END");

                    Exec(c, tx, "CREATE TABLE #ids (Id bigint NOT NULL PRIMARY KEY)");
                    var dt = new DataTable();
                    dt.Columns.Add("Id", typeof(long));
                    foreach (var id in ids) dt.Rows.Add(id);
                    using (var bulk = new SqlBulkCopy(c, SqlBulkCopyOptions.Default, tx) { DestinationTableName = "#ids" })
                        bulk.WriteToServer(dt);

                    var columns = (string)Scalar(c, tx, @"SELECT STRING_AGG(CONVERT(nvarchar(max), QUOTENAME(name)), N',') WITHIN GROUP (ORDER BY column_id)
                        FROM sys.columns WHERE object_id = OBJECT_ID(@t) AND is_computed = 0 AND system_type_id <> 189",
                        new SqlParameter("@t", "dbo." + target.Table));

                    // A record can only be archived once: replace an earlier archived copy of the same ID.
                    Exec(c, tx, "DELETE a FROM dbo." + archive + " a WHERE EXISTS (SELECT 1 FROM #ids i WHERE i.Id = a.Clio_Internal_ID AND a.CompanyID = @c)", new SqlParameter("@c", companyId));
                    var hasIdentity = Convert.ToInt32(Scalar(c, tx, "SELECT CAST(OBJECTPROPERTY(OBJECT_ID(@t), 'TableHasIdentity') AS int)", new SqlParameter("@t", "dbo." + target.Table + "_Deleted"))) == 1;
                    Exec(c, tx, (hasIdentity ? "SET IDENTITY_INSERT dbo." + archive + " ON; " : string.Empty) +
                        "INSERT dbo." + archive + " (" + columns + ", DeletedByRunID) SELECT " + columns + ", @run FROM dbo." + table +
                        " WHERE CompanyID = @c AND Clio_Internal_ID IN (SELECT Id FROM #ids); " +
                        (hasIdentity ? "SET IDENTITY_INSERT dbo." + archive + " OFF;" : string.Empty),
                        new SqlParameter("@c", companyId), new SqlParameter("@run", _reconcileRunId));
                    var removed = Exec(c, tx, "DELETE FROM dbo." + table + " WHERE CompanyID = @c AND Clio_Internal_ID IN (SELECT Id FROM #ids)", new SqlParameter("@c", companyId));
                    if (removed != ids.Count)
                        throw new InvalidOperationException("expected to move " + ids.Count + " rows but moved " + removed + "; nothing was changed");
                    tx.Commit();
                    Console.WriteLine("    moved " + removed + " record(s) to " + target.Table + "_Deleted.");
                    return removed;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        void RecordCandidate(Guid companyId, string endpoint, long id, string status, string detail = null)
        {
            using (var c = Open())
            using (var cmd = new SqlCommand(@"MERGE control.IncrementalDeletionCandidate AS t
                USING (SELECT @c AS CompanyID, @e AS Endpoint, @i AS ClioInternalID) AS s
                ON t.CompanyID = s.CompanyID AND t.Endpoint = s.Endpoint AND t.ClioInternalID = s.ClioInternalID
                WHEN MATCHED THEN UPDATE SET Status = @s, LastCheckedAtUTC = SYSUTCDATETIME(), ConfirmationDetail = @d
                WHEN NOT MATCHED THEN INSERT (CompanyID, Endpoint, ClioInternalID, Status, LastCheckedAtUTC, ConfirmationDetail) VALUES (@c, @e, @i, @s, SYSUTCDATETIME(), @d);", c))
            {
                cmd.Parameters.AddWithValue("@c", companyId);
                cmd.Parameters.AddWithValue("@e", endpoint);
                cmd.Parameters.AddWithValue("@i", id);
                cmd.Parameters.AddWithValue("@s", status);
                cmd.Parameters.AddWithValue("@d", (object)StagingAudit.Truncate(detail, 3500) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        static string Quote(string identifier)
        {
            if (!Targets.Any(t => t.Table == identifier || t.Table + "_Deleted" == identifier))
                throw new ArgumentException("Table not in the reconciliation list: " + identifier);
            return "[" + identifier + "]";
        }

        SqlConnection Open()
        {
            var connection = new SqlConnection(_connectionString);
            connection.Open();
            return connection;
        }

        static int Exec(SqlConnection c, SqlTransaction tx, string sql, params SqlParameter[] parameters)
        {
            using (var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 1800 })
            {
                if (parameters.Length > 0) cmd.Parameters.AddRange(parameters);
                return cmd.ExecuteNonQuery();
            }
        }

        static object Scalar(SqlConnection c, SqlTransaction tx, string sql, params SqlParameter[] parameters)
        {
            using (var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = 1800 })
            {
                if (parameters.Length > 0) cmd.Parameters.AddRange(parameters);
                return cmd.ExecuteScalar();
            }
        }
    }
}
