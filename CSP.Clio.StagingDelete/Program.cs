using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// CSP.Clio.StagingDelete.exe: ONE delete pass on Clio_Staging, always applied (unless --report-only).
    ///  - Stops at once (exit 0, nothing changed) when a publish cycle holds the database, and a publish cycle never starts while this runs
    ///    (the pass holds the shared ClioStagingData lock; see PipelineLock).
    ///  - Lists the IDs of every company in parallel, confirms each missing record with HTTP 404, moves the confirmed ones to the *_Deleted tables
    ///    and records the pass in control.DeleteRun: COMPLETE only when all endpoints were checked without problems.
    /// Exit code 0 = finished or skipped on purpose, 1 = the pass had problems or failed, 2 = configuration / preflight error.
    /// The logic is the shared DeleteReconciler of CSP.Clio.StagingSync (linked source, not a copy).
    /// </summary>
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Same culture pin as CSP.Clio.StagingSync.exe: dates are stored as text through DateTime.ToString().
            var storageCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = storageCulture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = storageCulture;
            System.Threading.Thread.CurrentThread.CurrentCulture = storageCulture;
            System.Threading.Thread.CurrentThread.CurrentUICulture = storageCulture;

            try
            {
                if (HasArgument(args, "--help") || HasArgument(args, "-h") || HasArgument(args, "/?"))
                {
                    PrintUsage();
                    return 0;
                }
                using (RunConsoleLogger.StartFor(args, "delete-last-run"))
                {
                    return await RunAsync(args);
                }
            }
            catch (Exception ex)
            {
                // Logging setup itself can fail (for example, no write permission), so keep a console fallback.
                Console.Error.WriteLine("FATAL ERROR: " + ex);
                return 1;
            }
        }

        static async Task<int> RunAsync(string[] args)
        {
            Console.WriteLine("CSP Clio Staging Delete");

            StagingAudit audit;
            try
            {
                audit = StagingAudit.CreateGuarded();
                Console.WriteLine("Target: " + audit.Describe());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DELETE PREFLIGHT FAILED: " + ex.Message);
                return 2;
            }

            var apply = !HasArgument(args, "--report-only");
            try
            {
                return await DeleteReconciler.RunCommandAsync(audit, apply, TryGetList(args, "--only"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("DELETE PASS FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return 1;
            }
        }

        static bool HasArgument(IEnumerable<string> args, string value)
        {
            return args.Any(a => string.Equals(a, value, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads "--name a,b,c" from the arguments (null when the argument is absent).</summary>
        static List<string> TryGetList(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
            return null;
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage:");
            Console.WriteLine("  CSP.Clio.StagingDelete.exe                      One delete pass on Clio_Staging: records deleted in Clio are moved to the *_Deleted tables.");
            Console.WriteLine("  CSP.Clio.StagingDelete.exe --report-only        Only report what would be moved (nothing is changed, nothing is recorded in control.DeleteRun).");
            Console.WriteLine("  CSP.Clio.StagingDelete.exe --only Users,Tasks   Limit the pass to some endpoints (recorded as PARTIAL, never counts as a clean pass).");
            Console.WriteLine("  Normally it is started by CSP.Clio.StagingSync.exe --cycle. It stops at once while a publish cycle runs.");
        }
    }
}
