using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.Entity.Core.EntityClient;
using System.Data.SqlClient;
using System.Linq;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Writes the control.SyncRun / control.SyncEndpointResult audit contract defined by
    /// 01_Create_Clio_Staging.sql, and owns the staging-only safety checks.
    /// Uses plain ADO.NET against the same server/database as the EF context.
    /// </summary>
    internal sealed class StagingAudit
    {
        public static string RequiredDatabase
        {
            get
            {
                var databaseName = ConfigurationManager.AppSettings["StagingDatabaseName"];
                if (string.IsNullOrWhiteSpace(databaseName))
                    throw new ConfigurationErrorsException("AppSetting 'StagingDatabaseName' is missing or empty.");
                return databaseName.Trim();
            }
        }
        const string ConnectionName = "EKPBIEntities";

        /// <summary>Endpoint name -> destination raw table. This is the manifest checked at run start.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> EndpointManifest = new List<KeyValuePair<string, string>>
        {
            new KeyValuePair<string,string>("PracticeAreas",       "PracticeAreas"),
            new KeyValuePair<string,string>("Activities",          "Activities"),
            new KeyValuePair<string,string>("Calendars",           "Calendars"),
            new KeyValuePair<string,string>("CalendarEntries",     "CalendarEntries"),
            new KeyValuePair<string,string>("Relationships",       "Relationships"),
            new KeyValuePair<string,string>("Tasks",               "Tasks"),
            new KeyValuePair<string,string>("TaskTypes",           "TaskTypes"),
            new KeyValuePair<string,string>("Matters",             "Matters"),
            new KeyValuePair<string,string>("Bills",               "Bills"),
            new KeyValuePair<string,string>("BankTransactions",    "BankTransactions"),
            new KeyValuePair<string,string>("CustomFields",        "CustomFields"),
            new KeyValuePair<string,string>("CustomFieldSets",     "CustomFieldSets"),
            new KeyValuePair<string,string>("Folders",             "Folders"),
            new KeyValuePair<string,string>("LineItems",           "LineItems"),
            new KeyValuePair<string,string>("Contacts",            "Contacts"),
            new KeyValuePair<string,string>("Allocations",         "Allocations"),
            new KeyValuePair<string,string>("Users",               "Users"),
            new KeyValuePair<string,string>("ActivityDescriptions","ActivityDescriptions"),
            new KeyValuePair<string,string>("MatterNotes",         "MatterNotes"),
        };

        readonly string _connectionString;
        public Guid RunID { get; private set; }
        internal string ConnectionString { get { return _connectionString; } }

        StagingAudit(string connectionString) { _connectionString = connectionString; }

        /// <summary>
        /// Builds the audit helper and refuses to continue unless the target matches the configured staging database.
        /// </summary>
        public static StagingAudit CreateGuarded()
        {
            var cs = ConfigurationManager.ConnectionStrings[ConnectionName]
                ?? throw new InvalidOperationException("Connection string '" + ConnectionName + "' is missing from the config file.");
            var sql = new SqlConnectionStringBuilder(new EntityConnectionStringBuilder(cs.ConnectionString).ProviderConnectionString);
            var requiredDatabase = RequiredDatabase;

            if (!string.Equals(sql.InitialCatalog, requiredDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "SAFETY STOP: this EXE only runs against database '" + requiredDatabase + "', but the config points at '" +
                    sql.InitialCatalog + "'. Nothing was changed.");
            if (sql.IntegratedSecurity == false && string.IsNullOrEmpty(sql.UserID))
                throw new InvalidOperationException("Connection string has neither integrated security nor a SQL login.");
            if (string.Equals(sql.UserID, "cspsync", StringComparison.OrdinalIgnoreCase) || string.Equals(sql.UserID, "sa", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAFETY STOP: do not run the staging EXE with the production 'cspsync' or 'sa' login. Use the dedicated clio_stage_loader login.");

            sql.ApplicationName = "CSP.Clio.StagingSync";
            return new StagingAudit(sql.ConnectionString);
        }

        public string Describe()
        {
            var b = new SqlConnectionStringBuilder(_connectionString);
            return b.DataSource + " / " + b.InitialCatalog + " as " + (b.IntegratedSecurity ? "Windows auth" : b.UserID);
        }

        SqlConnection Open()
        {
            var c = new SqlConnection(_connectionString);
            c.Open();
            return c;
        }

        static int Exec(SqlConnection c, string sql, params SqlParameter[] p)
        {
            using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 600 })
            {
                if (p != null && p.Length > 0) cmd.Parameters.AddRange(p);
                return cmd.ExecuteNonQuery();
            }
        }

        static object Scalar(SqlConnection c, string sql, params SqlParameter[] p)
        {
            using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 600 })
            {
                if (p != null && p.Length > 0) cmd.Parameters.AddRange(p);
                return cmd.ExecuteScalar();
            }
        }

        /// <summary>Checks the bootstrap info and that no run is already RUNNING.</summary>
        public void PreflightOrThrow(bool abandonStale)
        {
            using (var c = Open())
            {
                var db = (string)Scalar(c, "SELECT DB_NAME()");
                if (!string.Equals(db, RequiredDatabase, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SAFETY STOP: connected database is '" + db + "'.");

                var pub = Scalar(c, "SELECT PublishEnabled FROM control.BootstrapInfo WHERE ID=1");
                if (pub == null) throw new InvalidOperationException("control.BootstrapInfo is empty - was 01_Create_Clio_Staging.sql run?");

                var incrementalRunning = Convert.ToInt32(Scalar(c, @"
SELECT CASE WHEN OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NULL THEN 0
            WHEN EXISTS (SELECT 1 FROM control.IncrementalSyncRun WHERE Status='RUNNING') THEN 1
            ELSE 0 END"));
                if (incrementalRunning > 0)
                    throw new InvalidOperationException("An incremental staging sync is RUNNING. Do not start or abandon a full extraction until it has completed or has been investigated.");

                var running = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM control.SyncRun WHERE Status='RUNNING'"));
                if (running > 0)
                {
                    if (!abandonStale)
                        throw new InvalidOperationException(
                            "A previous run is still marked RUNNING in control.SyncRun. If it is dead, re-run with --abandon-stale to close it explicitly. " +
                            "If it is alive this is a concurrent start and must not proceed.");
                    Exec(c, "UPDATE control.SyncRun SET Status='ABANDONED', CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=N'Closed by --abandon-stale at next start' WHERE Status='RUNNING'");
                    Exec(c, "UPDATE e SET Status='FAILED', CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=N'Run abandoned' FROM control.SyncEndpointResult e JOIN control.SyncRun r ON r.RunID=e.RunID WHERE r.Status='ABANDONED' AND e.Status IN ('PENDING','RUNNING')");
                    Console.WriteLine("Closed stale RUNNING run(s) as ABANDONED.");
                }
            }
        }

        /// <summary>Counts rows in the staging buffer for the given tables (all companies). Used to show the buffer is empty at start.</summary>
        public long BufferRowCount(IEnumerable<string> tables)
        {
            long total = 0;
            using (var c = Open())
                foreach (var t in tables)
                    total += Convert.ToInt64(Scalar(c, "SELECT COUNT_BIG(*) FROM dbo." + Quote(t)));
            return total;
        }

        public void StartRun(IEnumerable<Guid> companyIds)
        {
            // Do not expose a RunID until the guarded transaction has actually created
            // its control rows. This prevents a failed startup guard from being closed
            // later as a misleading "0 of 0 endpoint results" failed run.
            var newRunId = Guid.NewGuid();
            using (var c = Open())
            using (var tx = c.BeginTransaction())
            {
                try
                {
                    // Coordinate with the production publisher.  The lease table is created by
                    // 07_Create_Production_Publisher.sql; the OBJECT_ID check preserves compatibility
                    // until that deployment has been applied.
                    Exec2(c, tx, @"
DECLARE @LockResult int;
EXEC @LockResult = sys.sp_getapplock
    @Resource = N'ClioStagingSyncOrPublish',
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 0,
    @DbPrincipal = N'public';
IF @LockResult < 0
    THROW 51049, 'A staging start/publication coordination lock is unavailable.', 1;
IF OBJECT_ID(N'control.PublishLease', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM control.PublishLease WHERE LeaseName=N'RAW_PUBLISH' AND Status='RUNNING')
    THROW 51050, 'A production publication is running. Do not start a staging extraction until it completes.', 1;
IF OBJECT_ID(N'control.IncrementalSyncRun', N'U') IS NOT NULL
   AND EXISTS (SELECT 1 FROM control.IncrementalSyncRun WHERE Status='RUNNING')
    THROW 51051, 'An incremental staging sync is running. Do not clear the staging buffer until it completes.', 1;");

                    Exec2(c, tx, "INSERT control.SyncRun(RunID,Status,Mode) VALUES(@r,'RUNNING','FULL')", new SqlParameter("@r", newRunId));
                    foreach (var company in companyIds)
                        foreach (var ep in EndpointManifest)
                            Exec2(c, tx, "INSERT control.SyncEndpointResult(RunID,CompanyID,Endpoint,Status) VALUES(@r,@c,@e,'PENDING')",
                                new SqlParameter("@r", newRunId), new SqlParameter("@c", company), new SqlParameter("@e", ep.Key));
                    tx.Commit();
                    RunID = newRunId;
                }
                catch { tx.Rollback(); throw; }
            }
        }

        static void Exec2(SqlConnection c, SqlTransaction tx, string sql, params SqlParameter[] p)
        {
            using (var cmd = new SqlCommand(sql, c, tx))
            {
                cmd.Parameters.AddRange(p);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Deletes every row of this company from every manifest table. The baseline SP_ClearData only covers
        /// 14 tables, so this closes the gap for the endpoints this EXE actually writes.
        /// </summary>
        public void ResetCompanyBuffer(Guid companyId)
        {
            using (var c = Open())
                foreach (var ep in EndpointManifest)
                {
                    if (!HasCompanyColumn(c, ep.Value))
                        throw new InvalidOperationException("Table dbo." + ep.Value + " has no CompanyID column; cannot scope the reset/verification.");
                    Exec(c, "DELETE FROM dbo." + Quote(ep.Value) + " WHERE CompanyID=@c", new SqlParameter("@c", companyId));
                }
        }

        public long CountCompanyRows(string table, Guid companyId)
        {
            using (var c = Open())
                return Convert.ToInt64(Scalar(c, "SELECT COUNT_BIG(*) FROM dbo." + Quote(table) + " WHERE CompanyID=@c", new SqlParameter("@c", companyId)));
        }

        static bool HasCompanyColumn(SqlConnection c, string table)
        {
            return Convert.ToInt32(Scalar(c,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@t AND COLUMN_NAME='CompanyID'",
                new SqlParameter("@t", table))) > 0;
        }

        static string Quote(string identifier)
        {
            if (!EndpointManifest.Any(e => e.Value == identifier))
                throw new ArgumentException("Table not in manifest: " + identifier);
            return "[" + identifier + "]";
        }

        public void EndpointStarted(Guid companyId, string endpoint)
        {
            using (var c = Open())
                Exec(c, "UPDATE control.SyncEndpointResult SET Status='RUNNING', StartedAtUTC=SYSUTCDATETIME() WHERE RunID=@r AND CompanyID=@c AND Endpoint=@e",
                    new SqlParameter("@r", RunID), new SqlParameter("@c", companyId), new SqlParameter("@e", endpoint));
        }

        public void EndpointFinished(Guid companyId, string endpoint, bool ok,  long rowsReceived, long rowsWritten, string error)
        {
            using (var c = Open())
                Exec(c, @"UPDATE control.SyncEndpointResult
                          SET Status=@s, RowsReceived=@rr, RowsWritten=@rw, CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=@m
                          WHERE RunID=@r AND CompanyID=@c AND Endpoint=@e",
                    new SqlParameter("@s", ok ? "COMPLETE" : "FAILED"),
                   // new SqlParameter("@pg", pagesReceived),
                    new SqlParameter("@rr", rowsReceived), new SqlParameter("@rw", rowsWritten),
                   // new SqlParameter("@pc", paginationComplete), // true only when the API client read the last page (no "next" link) without an error
                    new SqlParameter("@m", (object)Truncate(error, 3500) ?? DBNull.Value),
                    new SqlParameter("@r", RunID), new SqlParameter("@c", companyId), new SqlParameter("@e", endpoint));
        }

        /// <summary>Marks anything not COMPLETE as FAILED and closes the run. Returns true only if every expected row is COMPLETE.</summary>
        public bool FinishRun(string abortMessage)
        {
            using (var c = Open())
            {
                Exec(c, "UPDATE control.SyncEndpointResult SET Status='FAILED', CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=ISNULL(ErrorMessage,N'Not completed: company aborted or run ended early') WHERE RunID=@r AND Status IN ('PENDING','RUNNING')",
                    new SqlParameter("@r", RunID));
                var expected = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM control.SyncEndpointResult WHERE RunID=@r", new SqlParameter("@r", RunID)));
                var complete = Convert.ToInt32(Scalar(c, "SELECT COUNT(*) FROM control.SyncEndpointResult WHERE RunID=@r AND Status='COMPLETE'", new SqlParameter("@r", RunID)));
                bool ok = abortMessage == null && expected > 0 && expected == complete;
                Exec(c, "UPDATE control.SyncRun SET Status=@s, CompletedAtUTC=SYSUTCDATETIME(), ErrorMessage=@m WHERE RunID=@r",
                    new SqlParameter("@s", ok ? "EXTRACTED" : "FAILED"),
                    new SqlParameter("@m", ok ? (object)DBNull.Value : (object)(abortMessage ?? (complete + " of " + expected + " endpoint results COMPLETE"))),
                    new SqlParameter("@r", RunID));
                Console.WriteLine("Run " + RunID + ": " + complete + " of " + expected + " endpoint results COMPLETE -> " + (ok ? "EXTRACTED" : "FAILED"));
                return ok;
            }
        }

        public static string Truncate(string s, int max)
        {
            if (s == null) return null;
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
