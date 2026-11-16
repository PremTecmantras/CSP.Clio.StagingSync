using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// First step of the publish cycle in two-database mode. Replaces the raw tables (and the company list) in Clio_Staging with an exact
    /// copy of Clio_Live, so the snapshot, the 10% check, the reporting build and the publish work on a frozen copy while the
    /// incremental sync and the delete task keep working on Clio_Live.
    ///
    /// The caller holds the exclusive ClioLiveWrite lock on Clio_Live, so no incremental run or delete archive step can change Live while
    /// it is read. Each table is copied in its own transaction (TRUNCATE + INSERT ... SELECT, identity values preserved) and the row
    /// counts of both sides are compared. The copy writes a marker row to control.IncrementalSyncRun in Clio_Staging (RUNNING, then
    /// COMPLETE or FAILED) so the publisher's existing checks (51049 / 51050) see an unfinished or failed copy and refuse to publish.
    /// </summary>
    internal sealed class LiveToStagingCopier
    {
        static readonly Regex SafeName = new Regex("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

        /// <summary>The 19 raw endpoint tables plus the company list (it carries the Active flags the checks use).</summary>
        public static readonly string[] CopyTables =
            StagingAudit.EndpointManifest.Select(e => e.Value).Concat(new[] { "_EKPCompanies" }).ToArray();

        readonly string _stagingConnectionString;
        readonly string _liveDatabase;
        readonly int _parallelism;
        readonly int _timeoutSeconds;
        readonly object _consoleLock = new object();

        public LiveToStagingCopier(string stagingConnectionString)
        {
            _stagingConnectionString = stagingConnectionString ?? throw new ArgumentNullException(nameof(stagingConnectionString));
            _liveDatabase = PipelineRole.LiveDatabaseName;
            if (_liveDatabase == null || !SafeName.IsMatch(_liveDatabase))
                throw new ConfigurationErrorsException("AppSetting 'LiveDatabaseName' is missing or contains characters other than letters, digits and underscore.");
            int parallelism;
            _parallelism = int.TryParse(ConfigurationManager.AppSettings["LiveCopyParallelism"], out parallelism) && parallelism > 0 ? Math.Min(parallelism, 8) : 4;
            int minutes;
            _timeoutSeconds = (int.TryParse(ConfigurationManager.AppSettings["LiveCopyTimeoutMinutes"], out minutes) && minutes > 0 ? minutes : 60) * 60;
        }

        // ---- read-only helpers on the Live database (used by the cycle's freshness guards) ----

        public static DateTime? LastCompletedIncrementalUtc(string liveConnectionString)
        {
            return ScalarDate(liveConnectionString, "SELECT MAX(CompletedAtUTC) FROM control.IncrementalSyncRun WHERE Status = 'COMPLETE'");
        }

        public static string LatestIncrementalStatus(string liveConnectionString)
        {
            using (var c = new SqlConnection(liveConnectionString))
            using (var cmd = new SqlCommand("SELECT TOP (1) Status FROM control.IncrementalSyncRun ORDER BY StartedAtUTC DESC", c))
            {
                c.Open();
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? null : Convert.ToString(value);
            }
        }

        /// <summary>Closes RUNNING incremental rows of a process that died. Only call while holding the exclusive ClioLiveWrite lock (no incremental run can be alive then).</summary>
        public static void AbandonStaleIncrementalRuns(string liveConnectionString)
        {
            using (var c = new SqlConnection(liveConnectionString))
            using (var cmd = new SqlCommand(@"UPDATE control.IncrementalSyncResult SET Status='FAILED', CompletedAtUTC=SYSUTCDATETIME(),
                    ErrorMessage=N'Abandoned: the process ended before this endpoint finished.' WHERE Status='RUNNING';
                UPDATE control.IncrementalSyncRun SET Status='ABANDONED', CompletedAtUTC=SYSUTCDATETIME(),
                    ErrorMessage=N'Abandoned: the process ended before the run finished.' WHERE Status='RUNNING';", c))
            {
                c.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Completion time of the last delete pass that finished without any problem (null when there is none).</summary>
        public static DateTime? LastCleanDeletePassUtc(string liveConnectionString)
        {
            return ScalarDate(liveConnectionString,
                "IF OBJECT_ID(N'control.DeleteRun', N'U') IS NULL SELECT CAST(NULL AS datetime2) ELSE SELECT MAX(CompletedAtUTC) FROM control.DeleteRun WHERE Status = 'COMPLETE'");
        }

        static DateTime? ScalarDate(string connectionString, string sql)
        {
            using (var c = new SqlConnection(connectionString))
            using (var cmd = new SqlCommand(sql, c))
            {
                c.Open();
                var value = cmd.ExecuteScalar();
                return value == null || value == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(value);
            }
        }

        // ---- the copy ----

        public void Run()
        {
            var total = Stopwatch.StartNew();
            EnsureAuditTables();
            var copyId = Guid.NewGuid();
            StartRun(copyId);
            Console.WriteLine("Copying the raw tables from " + _liveDatabase + " into Clio_Staging (" + CopyTables.Length + " tables, up to " + _parallelism + " at a time).");

            var results = new ConcurrentBag<Tuple<string, long, double>>();
            try
            {
                var errors = new ConcurrentQueue<Exception>();
                // Largest tables first so the slowest one starts immediately.
                var ordered = CopyTables.OrderByDescending(t => EstimatedWeight(t)).ToList();
                System.Threading.Tasks.Parallel.ForEach(ordered, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = _parallelism }, table =>
                {
                    try { results.Add(CopyTable(table, copyId)); }
                    catch (Exception ex) { errors.Enqueue(new InvalidOperationException("copy of " + table + " failed: " + ex.Message, ex)); }
                });
                if (!errors.IsEmpty)
                    throw new InvalidOperationException(string.Join(" | ", errors.Select(e => e.Message)));

                var rows = results.Sum(r => r.Item2);
                FinishRun(copyId, true, rows, null);
                Console.WriteLine("Copy from " + _liveDatabase + " complete: " + rows.ToString("N0") + " rows in " + total.Elapsed.ToString(@"mm\:ss") + ".");
            }
            catch (Exception ex)
            {
                try { FinishRun(copyId, false, results.Sum(r => r.Item2), ex.Message); } catch { }
                throw;
            }
        }

        static long EstimatedWeight(string table)
        {
            switch (table)
            {
                case "Activities": return 4;
                case "LineItems": return 3;
                case "BankTransactions": case "Bills": case "Matters": case "Allocations": case "Folders": return 2;
                default: return 1;
            }
        }

        Tuple<string, long, double> CopyTable(string table, Guid copyId)
        {
            var watch = Stopwatch.StartNew();
            var stagingColumns = Columns(table, null);
            var liveColumns = Columns(table, _liveDatabase);
            var missingInLive = stagingColumns.Except(liveColumns, StringComparer.OrdinalIgnoreCase).ToList();
            var extraInLive = liveColumns.Except(stagingColumns, StringComparer.OrdinalIgnoreCase).ToList();
            if (missingInLive.Count > 0 || extraInLive.Count > 0)
                throw new InvalidOperationException("the column lists of dbo." + table + " differ between Clio_Staging and " + _liveDatabase +
                    " (only in Staging: " + string.Join(",", missingInLive) + "; only in Live: " + string.Join(",", extraInLive) + "). Make both tables identical first.");

            var columnList = string.Join(",", stagingColumns.Select(n => "[" + n.Replace("]", "]]") + "]"));
            var live = "[" + _liveDatabase + "].dbo.[" + table + "]";
            var local = "dbo.[" + table + "]";

            using (var c = new SqlConnection(_stagingConnectionString))
            {
                c.Open();
                var identity = Convert.ToInt32(Scalar(c, null, "SELECT CAST(OBJECTPROPERTY(OBJECT_ID(N'dbo.[" + table + "]'), 'TableHasIdentity') AS int)")) == 1;
                using (var tx = c.BeginTransaction())
                {
                    try
                    {
                        try { Exec(c, tx, "TRUNCATE TABLE " + local + ";"); }
                        catch (SqlException ex) when (ex.Number == 4712)
                        {
                            // Referenced by a foreign key: TRUNCATE is not allowed, so delete instead.
                            Exec(c, tx, "DELETE FROM " + local + ";");
                        }
                        Exec(c, tx, (identity ? "SET IDENTITY_INSERT " + local + " ON; " : string.Empty) +
                            "INSERT INTO " + local + " WITH (TABLOCK) (" + columnList + ") SELECT " + columnList + " FROM " + live + "; " +
                            (identity ? "SET IDENTITY_INSERT " + local + " OFF;" : string.Empty));

                        var copied = Convert.ToInt64(Scalar(c, tx, "SELECT COUNT_BIG(*) FROM " + local));
                        var source = Convert.ToInt64(Scalar(c, tx, "SELECT COUNT_BIG(*) FROM " + live));
                        if (copied != source)
                            throw new InvalidOperationException("row counts differ after the copy of " + table + " (Live " + source + ", Staging " + copied + ")");
                        Exec(c, tx, "INSERT control.LiveCopyTable (CopyRunID, TableName, RowsCopied, Seconds) VALUES (@r, @t, @n, @s)",
                            new SqlParameter("@r", copyId), new SqlParameter("@t", table), new SqlParameter("@n", copied),
                            new SqlParameter("@s", Math.Round(watch.Elapsed.TotalSeconds, 2)));
                        tx.Commit();
                        lock (_consoleLock)
                            Console.WriteLine("  " + table.PadRight(22) + copied.ToString("N0").PadLeft(12) + " rows  " + watch.Elapsed.ToString(@"mm\:ss"));
                        return Tuple.Create(table, copied, watch.Elapsed.TotalSeconds);
                    }
                    catch { try { tx.Rollback(); } catch { } throw; }
                }
            }
        }

        /// <summary>Column names of dbo.[table] (writable columns only), in table order. liveDatabase null = the Staging database.</summary>
        List<string> Columns(string table, string liveDatabase)
        {
            var prefix = liveDatabase == null ? string.Empty : "[" + liveDatabase + "].";
            using (var c = new SqlConnection(_stagingConnectionString))
            using (var cmd = new SqlCommand("SELECT c.name FROM " + prefix + "sys.columns c WHERE c.object_id = OBJECT_ID(N'" + prefix + "dbo.[" + table + "]') " +
                "AND c.is_computed = 0 AND c.system_type_id <> 189 ORDER BY c.column_id", c))
            {
                c.Open();
                var names = new List<string>();
                using (var r = cmd.ExecuteReader())
                    while (r.Read()) names.Add(r.GetString(0));
                if (names.Count == 0)
                    throw new InvalidOperationException("table dbo." + table + " was not found in " + (liveDatabase ?? "Clio_Staging"));
                return names;
            }
        }

        void EnsureAuditTables()
        {
            using (var c = new SqlConnection(_stagingConnectionString))
            {
                c.Open();
                Exec(c, null, @"
IF SCHEMA_ID(N'control') IS NULL EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
IF OBJECT_ID(N'control.LiveCopyRun', N'U') IS NULL
    CREATE TABLE control.LiveCopyRun
    (
        CopyRunID uniqueidentifier NOT NULL CONSTRAINT PK_LiveCopyRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_LiveCopyRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        CompletedAtUTC datetime2(7) NULL,
        Status varchar(20) NOT NULL,
        RowsCopied bigint NULL,
        ErrorMessage nvarchar(3500) NULL
    );
IF OBJECT_ID(N'control.LiveCopyTable', N'U') IS NULL
    CREATE TABLE control.LiveCopyTable
    (
        CopyRunID uniqueidentifier NOT NULL,
        TableName sysname NOT NULL,
        RowsCopied bigint NOT NULL,
        Seconds decimal(9,2) NOT NULL,
        CONSTRAINT PK_LiveCopyTable PRIMARY KEY (CopyRunID, TableName)
    );");
            }
        }

        void StartRun(Guid copyId)
        {
            using (var c = new SqlConnection(_stagingConnectionString))
            {
                c.Open();
                Exec(c, null, "INSERT control.LiveCopyRun (CopyRunID, Status) VALUES (@r, 'RUNNING');", new SqlParameter("@r", copyId));
                // Marker for the publisher's checks 51049 / 51050: the raw tables of Clio_Staging are being replaced right now.
                Exec(c, null, "INSERT control.IncrementalSyncRun (IncrementalRunID, Status, ErrorMessage) VALUES (@r, 'RUNNING', N'Copy from " + _liveDatabase + "');",
                    new SqlParameter("@r", copyId));
            }
        }

        void FinishRun(Guid copyId, bool ok, long rows, string error)
        {
            using (var c = new SqlConnection(_stagingConnectionString))
            {
                c.Open();
                var message = error == null ? null : StagingAudit.Truncate(error, 3500);
                Exec(c, null, "UPDATE control.LiveCopyRun SET Status = @s, CompletedAtUTC = SYSUTCDATETIME(), RowsCopied = @n, ErrorMessage = @e WHERE CopyRunID = @r",
                    new SqlParameter("@s", ok ? "COMPLETE" : "FAILED"), new SqlParameter("@n", rows),
                    new SqlParameter("@e", (object)message ?? DBNull.Value), new SqlParameter("@r", copyId));
                Exec(c, null, "UPDATE control.IncrementalSyncRun SET Status = @s, CompletedAtUTC = SYSUTCDATETIME(), ErrorMessage = @e WHERE IncrementalRunID = @r",
                    new SqlParameter("@s", ok ? "COMPLETE" : "FAILED"),
                    new SqlParameter("@e", ok ? (object)("Copy from " + _liveDatabase) : (object)message ?? DBNull.Value), new SqlParameter("@r", copyId));
            }
        }

        int Exec(SqlConnection c, SqlTransaction tx, string sql, params SqlParameter[] parameters)
        {
            using (var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = _timeoutSeconds })
            {
                if (parameters.Length > 0) cmd.Parameters.AddRange(parameters);
                return cmd.ExecuteNonQuery();
            }
        }

        object Scalar(SqlConnection c, SqlTransaction tx, string sql)
        {
            using (var cmd = new SqlCommand(sql, c, tx) { CommandTimeout = _timeoutSeconds })
                return cmd.ExecuteScalar();
        }
    }
}
