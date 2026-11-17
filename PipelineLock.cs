using System;
using System.Data;
using System.Data.SqlClient;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// One exclusive lock for everything that changes Clio_Staging (hourly incremental sync, full extraction, full check).
    /// It is a session applock held on a dedicated connection, so it is released automatically if the process dies
    /// and a crashed run can never leave the pipeline blocked.
    /// </summary>
    internal sealed class PipelineLock : IDisposable
    {
        const string Resource = "ClioStagingPipeline";
        readonly SqlConnection _connection;

        PipelineLock(SqlConnection connection) { _connection = connection; }

        /// <summary>Returns null when another pipeline run holds the lock.</summary>
        public static PipelineLock TryAcquire(string connectionString)
        {
            var connection = new SqlConnection(connectionString);
            try
            {
                connection.Open();
                using (var cmd = new SqlCommand("sp_getapplock", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@Resource", Resource);
                    cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
                    cmd.Parameters.AddWithValue("@LockOwner", "Session");
                    cmd.Parameters.AddWithValue("@LockTimeout", 0);
                    var result = new SqlParameter("@result", SqlDbType.Int) { Direction = ParameterDirection.ReturnValue };
                    cmd.Parameters.Add(result);
                    cmd.ExecuteNonQuery();
                    if ((int)result.Value >= 0)
                        return new PipelineLock(connection);
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
        /// holding the lock at the moment a scheduled full check starts must not make the full check give up).
        /// Returns null when the lock is still held after the wait.
        /// </summary>
        public static PipelineLock TryAcquireWithWait(string connectionString, TimeSpan maxWait)
        {
            var started = DateTime.UtcNow;
            var announced = false;
            while (true)
            {
                var acquired = TryAcquire(connectionString);
                if (acquired != null) return acquired;
                if (maxWait <= TimeSpan.Zero || DateTime.UtcNow - started >= maxWait) return null;
                if (!announced)
                {
                    Console.WriteLine("Another pipeline run (for example an incremental sync) holds the lock. Waiting up to " +
                        (int)maxWait.TotalMinutes + " minute(s) for it to finish...");
                    announced = true;
                }
                System.Threading.Thread.Sleep(TimeSpan.FromSeconds(15));
            }
        }

        public void Dispose()
        {
            try { _connection.Dispose(); } catch { }
        }
    }
}
