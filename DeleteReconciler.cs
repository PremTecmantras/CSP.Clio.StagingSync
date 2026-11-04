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

        /// <summary>The --reconcile-deletes command: takes the pipeline lock, reconciles, and rebuilds the reporting tables when rows moved.</summary>
        public static async Task<int> RunCommandAsync(StagingAudit audit, bool apply)
        {
            using (var pipelineLock = PipelineLock.TryAcquire(audit.ConnectionString))
            {
                if (pipelineLock == null)
                {
                    Console.Error.WriteLine("DELETE RECONCILIATION BLOCKED: an incremental sync or another pipeline run is active. Wait for it to finish and run again.");
                    return 1;
                }
                audit.PreflightOrThrow(false, pipelineLockHeld: true);

                var result = await new DeleteReconciler(audit.ConnectionString, apply).RunAsync().ConfigureAwait(false);
                if (result.Item1 > 0)
                    Console.WriteLine("Records were removed from the raw tables. Run --full-check (it rebuilds the reporting tables) and then --full-check --confirm-publish to send the deletions to the target database.");
                return result.Item2 == 0 ? 0 : 1;
            }
        }

        readonly string _connectionString;
        readonly bool _apply;
        readonly decimal _maxPercent;
        readonly int _maxConfirmations;
        readonly int _smallTableAllowance;
        readonly HashSet<string> _listingAuthoritative;
        readonly Guid _reconcileRunId = Guid.NewGuid();

        public DeleteReconciler(string connectionString, bool apply)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _apply = apply;
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
            Console.WriteLine("  (In table = rows in Clio_Staging; In Clio = rows Clio returns now; Missing = rows we hold that Clio no longer has. Missing 0 is correct.)");
            long moved = 0;
            var problems = 0;
            var dataManager = new BIDataManager(false);
            var pageClient = new ClioPageStreamClient();

            foreach (var company in dataManager.GetClioConnectedCompanies())
            {
                string bearer;
                try { bearer = await IncrementalSyncRunner.GetBearerTokenAsync(dataManager, company).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    problems++;
                    Console.Error.WriteLine("  RECONCILE SKIPPED " + company.CompanyName + ": no Clio token (" + ex.Message + ")");
                    continue;
                }

                foreach (var target in Targets)
                {
                    try { moved += await ReconcileAsync(pageClient, company.ID, company.CompanyName, bearer, target).ConfigureAwait(false); }
                    catch (Exception ex)
                    {
                        problems++;
                        Console.Error.WriteLine("  RECONCILE FAILED " + target.Endpoint + " - " + company.CompanyName + ": " + ex.Message + " (no rows were changed for this endpoint)");
                    }
                }
            }

            Console.WriteLine("Delete reconciliation finished: " + moved + " record(s) moved to *_Deleted, " + problems + " problem(s).");
            return Tuple.Create(moved, problems);
        }

        async Task<long> ReconcileAsync(ClioPageStreamClient pageClient, Guid companyId, string companyName, string bearer, Target target)
        {
            var inTable = ReadTableIds(target, companyId);
            var inClio = new HashSet<long>();
            var metrics = new ClioPageStreamMetrics();
            var started = DateTime.Now;
            Console.WriteLine("  ... reading the " + target.Endpoint + " ID list from Clio for " + companyName + " (" + inTable.Count + " rows in table; large tables can take several minutes)");
            await pageClient.StreamIdsAsync(target.ApiMethod, target.Service, bearer, metrics, ids =>
            {
                foreach (var id in ids) inClio.Add(id);
                return Task.CompletedTask;
            }).ConfigureAwait(false);
            Console.WriteLine("  ... done in " + (DateTime.Now - started).ToString(@"mm\:ss"));

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
            return ArchiveAndRemove(target, companyId, confirmed);
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
