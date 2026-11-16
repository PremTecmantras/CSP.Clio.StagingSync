using System;
using System.Configuration;
using System.Data.Entity.Core.EntityClient;
using System.Data.SqlClient;
using System.Reflection;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Two-database mode. When the AppSetting 'LiveDatabaseName' is set, the work is split over two databases:
    ///   Clio_Live     (role Live)    - the 5-minute incremental sync, the first load and the background delete task write here, all day;
    ///   Clio_Staging  (role Staging) - the publish cycle (--full-check) copies the raw tables from Live, then snapshots, checks (10%),
    ///                                  builds the reporting tables and publishes to the target database. Nothing else writes to it.
    /// When 'LiveDatabaseName' is empty the EXE behaves as before (everything runs in Clio_Staging).
    /// All data access (EF context and plain ADO.NET) reads the connection string 'EKPBIEntities'. A process that works on the Live
    /// database therefore swaps that one connection string for 'EKPBIEntitiesLive' before anything else runs, and then verifies by
    /// asking the server which database it is really talking to.
    /// </summary>
    internal static class PipelineRole
    {
        const string EfConnectionName = "EKPBIEntities";
        const string LiveEfConnectionName = "EKPBIEntitiesLive";

        public static bool IsLive { get; private set; }

        public static string LiveDatabaseName
        {
            get
            {
                var value = ConfigurationManager.AppSettings["LiveDatabaseName"];
                return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            }
        }

        public static bool DualDatabaseMode { get { return LiveDatabaseName != null; } }

        public static string StagingDatabaseName
        {
            get
            {
                var value = ConfigurationManager.AppSettings["StagingDatabaseName"];
                if (string.IsNullOrWhiteSpace(value))
                    throw new ConfigurationErrorsException("AppSetting 'StagingDatabaseName' is missing or empty.");
                return value.Trim();
            }
        }

        /// <summary>The database this process is allowed to write to (Live in a Live process, otherwise Staging).</summary>
        public static string RequiredDatabase
        {
            get { return IsLive ? LiveDatabaseName : StagingDatabaseName; }
        }

        /// <summary>Which commands work on the Live database (only used in two-database mode).</summary>
        public static bool CommandUsesLive(string[] args)
        {
            Func<string, bool> has = name => Array.Exists(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            if (has("--full-check") || has("--validate") || has("--status") || has("--preflight") || has("--help") || has("-h") || has("/?"))
                return false;
            // --incremental, --reconcile-deletes, --delete-sync and the no-argument first load all write to Live.
            return true;
        }

        /// <summary>Points this process at the Live database. A no-op when two-database mode is off or already switched.</summary>
        public static void UseLiveDatabase()
        {
            if (!DualDatabaseMode || IsLive) return;

            var live = ConfigurationManager.ConnectionStrings[LiveEfConnectionName]
                ?? throw new ConfigurationErrorsException("LiveDatabaseName is set, but connection string '" + LiveEfConnectionName + "' is missing from the config file.");
            var liveSql = new SqlConnectionStringBuilder(new EntityConnectionStringBuilder(live.ConnectionString).ProviderConnectionString);
            if (!string.Equals(liveSql.InitialCatalog, LiveDatabaseName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAFETY STOP: '" + LiveEfConnectionName + "' points at '" + liveSql.InitialCatalog + "', but LiveDatabaseName is '" + LiveDatabaseName + "'.");
            if (string.Equals(LiveDatabaseName, StagingDatabaseName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAFETY STOP: LiveDatabaseName and StagingDatabaseName are the same database.");

            var setting = ConfigurationManager.ConnectionStrings[EfConnectionName]
                ?? throw new ConfigurationErrorsException("Connection string '" + EfConnectionName + "' is missing from the config file.");
            // ConnectionStringSettings are read-only after the config is loaded; unlock this one entry so every later
            // "name=EKPBIEntities" lookup (the EF context) and every ADO.NET string built from it targets the Live database.
            var readOnlyField = typeof(ConfigurationElement).GetField("_bReadOnly", BindingFlags.Instance | BindingFlags.NonPublic);
            if (readOnlyField == null)
                throw new InvalidOperationException("Cannot switch the connection string to the Live database on this runtime.");
            readOnlyField.SetValue(setting, false);
            setting.ConnectionString = live.ConnectionString;
            IsLive = true;
        }

        /// <summary>Asks the server which database the EF context really uses, so a wrong switch can never write to the wrong database.</summary>
        public static void VerifyEfTarget()
        {
            var expected = RequiredDatabase;
            using (var context = new CSP.Clio.Sync.Data.BIDataManager(false).CreateContext())
            {
                var actual = System.Linq.Enumerable.FirstOrDefault(context.Database.SqlQuery<string>("SELECT DB_NAME()"));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("SAFETY STOP: the data context is connected to '" + actual + "', expected '" + expected + "'. Nothing was changed.");
            }
        }

        /// <summary>Plain ADO.NET connection string of the Live database (used by the publish cycle, which itself runs against Staging).</summary>
        public static string LiveConnectionString()
        {
            var live = ConfigurationManager.ConnectionStrings[LiveEfConnectionName]
                ?? throw new ConfigurationErrorsException("Connection string '" + LiveEfConnectionName + "' is missing from the config file.");
            var sql = new SqlConnectionStringBuilder(new EntityConnectionStringBuilder(live.ConnectionString).ProviderConnectionString);
            if (!string.Equals(sql.InitialCatalog, LiveDatabaseName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAFETY STOP: '" + LiveEfConnectionName + "' points at '" + sql.InitialCatalog + "', but LiveDatabaseName is '" + LiveDatabaseName + "'.");
            sql.ApplicationName = "CSP.Clio.StagingSync.Live";
            return sql.ConnectionString;
        }
    }
}
