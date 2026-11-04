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

        public void Dispose()
        {
            try { _connection.Dispose(); } catch { }
        }
    }
}
