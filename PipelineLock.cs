using System;
using System.Data;
using System.Data.SqlClient;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Session applocks held on a dedicated connection, so a lock is released automatically if the process dies and a crashed
    /// run can never leave the pipeline blocked. Application locks belong to the database of the connection (Clio_Staging).
    ///
    /// Locks used:
    ///   ClioStagingPipeline   Exclusive   one publish cycle (--full-check) at a time
    ///   ClioStagingData       Shared      held by an incremental pass and by a whole delete pass: both write the raw tables and may run together
    ///                         Exclusive   held by the publish cycle for its whole run (snapshot, check, reporting build, publish) and by the
    ///                                     first load. So a publish never starts while a delete pass or an incremental pass runs, and
    ///                                     neither of them starts while a publish runs.
    ///   ClioIncrementalRun    Exclusive   only one incremental pass at a time
    ///   ClioDeleteRun         Exclusive   only one delete pass at a time
    ///   ClioCycleSupervisor   Exclusive   only one --cycle supervisor at a time
    /// Shared holders can run together; an Exclusive holder excludes every other holder.
    /// </summary>
    internal sealed class PipelineLock : IDisposable
    {
        public const string Pipeline = "ClioStagingPipeline";
        public const string DataWrite = "ClioStagingData";
        public const string IncrementalRun = "ClioIncrementalRun";
        public const string DeleteRun = "ClioDeleteRun";
        public const string Supervisor = "ClioCycleSupervisor";

        readonly SqlConnection _connection;
        readonly string _resource;

        PipelineLock(SqlConnection connection, string resource) { _connection = connection; _resource = resource; }

        /// <summary>Exclusive pipeline lock. Returns null when another pipeline run holds it.</summary>
        public static PipelineLock TryAcquire(string connectionString)
        {
            return TryAcquire(connectionString, Pipeline, false);
        }

        /// <summary>Returns null when the lock cannot be taken right now (held in a conflicting mode).</summary>
        public static PipelineLock TryAcquire(string connectionString, string resource, bool shared)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                connection.Open();
                using (var cmd = new SqlCommand("sp_getapplock", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@Resource", resource);
                    cmd.Parameters.AddWithValue("@LockMode", shared ? "Shared" : "Exclusive");
                    cmd.Parameters.AddWithValue("@LockOwner", "Session");
                    cmd.Parameters.AddWithValue("@LockTimeout", 0);
                    var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
                    cmd.Parameters.Add(result);
                    cmd.ExecuteNonQuery();
                    if ((int)result.Value >= 0)
                        return new PipelineLock(connection, resource);
                }
            }
            catch
            {
                connection.Dispose();
                throw;
            }
            connection.Dispose();
            return null;
        }

        /// <summary>
        /// Like TryAcquire, but keeps trying every few seconds for up to <paramref name="maxWait"/> (a short incremental run
        /// holding a lock at the moment a scheduled job starts must not make that job give up).
        /// Returns null when the lock is still held after the wait.
        /// </summary>
        public static PipelineLock TryAcquireWithWait(string connectionString, TimeSpan maxWait)
        {
            return TryAcquireWithWait(connectionString, maxWait, Pipeline, false);
        }

        public static PipelineLock TryAcquireWithWait(string connectionString, TimeSpan maxWait, string resource, bool shared)
        {
            var started = DateTime.UtcNow;
            var announced = false;
            while (true)
            {
                var acquired = TryAcquire(connectionString, resource, shared);
                if (acquired != null) return acquired;
                if (maxWait <= TimeSpan.Zero || DateTime.UtcNow - started >= maxWait) return null;
                if (!announced)
                {
                    Console.WriteLine("Another pipeline run holds the lock '" + resource + "'. Waiting up to " +
                        Math.Max(1, (int)maxWait.TotalMinutes) + " minute(s) for it to finish...");
                    announced = true;
                }
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(10));
            }
        }

        /// <summary>
        /// Takes several locks exclusively, all or none (used by the first load, which needs the whole database for itself).
        /// Returns null when any of them is held; otherwise an object whose Dispose releases all of them.
        /// </summary>
        public static IDisposable TryAcquireExclusiveSet(string connectionString, params string[] resources)
        {
            var taken = new System.Collections.Generic.List<PipelineLock>();
            foreach (var resource in resources)
            {
                var acquired = TryAcquire(connectionString, resource, false);
                if (acquired == null)
                {
                    foreach (var l in taken) l.Dispose();
                    return null;
                }
                taken.Add(acquired);
            }
            return new LockSet(taken);
        }

        sealed class LockSet : IDisposable
        {
            readonly System.Collections.Generic.List<PipelineLock> _locks;
            public LockSet(System.Collections.Generic.List<PipelineLock> locks) { _locks = locks; }
            public void Dispose() { foreach (var l in _locks) l.Dispose(); }
        }

        public void Dispose()
        {
            // The connection returns to the pool when disposed and a pooled session keeps its session applocks until it is reused,
            // so release the lock explicitly. Otherwise a lock that is given up early (the Live copy step) would stay held.
            try
            {
                using (var cmd = new SqlCommand("sp_releaseapplock", _connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@Resource", _resource);
                    cmd.Parameters.AddWithValue("@LockOwner", "Session");
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
            try { _connection.Dispose(); } catch { }
        }
    }
}
