using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using CSP.Clio.Sync.Data;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The publish cycle. It does NOT extract or clear anything: staging is kept current by the incremental task, and the records deleted in Clio
    /// are moved to the *_Deleted tables by the separate delete pass (CSP.Clio.StagingDelete.exe).
    ///
    /// Before it starts it makes sure that nothing else writes to Clio_Staging:
    ///   - it stops (exit code 3) when a delete pass is running, and it requires the latest delete pass to be COMPLETE and recent;
    ///   - it takes the whole database exclusively (ClioStagingData), so no incremental pass and no delete pass can run during the publish,
    ///     and neither of them can start until it is over.
    /// Then it catches staging up one last time, records the current staging tables as an approved snapshot, runs the 10% comparison
    /// (every endpoint COMPLETE and within the allowed shortfall against the target database), builds the reporting tables in staging, and only then
    /// publishes to the target database (EKPBI_New_Dev). Without --confirm-publish it stops after a publisher dry run.
    /// </summary>
    internal static class FullCheckRunner
    {
        const decimal MaximumDifferencePct = 10m;
        /// <summary>Exit code: the publish was not started on purpose (for example a delete pass is running). Nothing was changed.</summary>
        public const int StoppedExitCode = 3;

        static int ReadInt(string name, int defaultValue)
        {
            int value;
            return int.TryParse(System.Configuration.ConfigurationManager.AppSettings[name], out value) && value >= 0 ? value : defaultValue;
        }

        /// <param name="reconcileDeletes">Manual use only (--with-deletes): run the delete reconciliation inside this cycle instead of
        /// requiring a recent COMPLETE delete pass from the separate delete EXE.</param>
        public static async Task<int> RunAsync(StagingAudit audit, bool publish, bool reconcileDeletes = false)
        {
            var wait = TimeSpan.FromMinutes(ReadInt("FullCheckLockWaitMinutes", 10));

            // One publish cycle at a time.
            using (var pipelineLock = PipelineLock.TryAcquireWithWait(audit.ConnectionString, wait))
            {
                if (pipelineLock == null)
                {
                    Console.Error.WriteLine("FULL CHECK BLOCKED: another publish cycle (or the first load) is still running after waiting " + (int)wait.TotalMinutes + " minute(s). Wait for it to finish and run again.");
                    return 1;
                }

                // A delete pass in progress: stop, do not wait (the delete pass is allowed to finish and the next cycle publishes).
                using (var deleteProbe = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.DeleteRun, false))
                {
                    if (deleteProbe == null)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: a delete pass is running. Nothing was published; run again after it has finished.");
                        return StoppedExitCode;
                    }
                }

                // The whole database for this cycle: no incremental pass and no delete pass may run until it is over.
                using (var wholeDatabase = PipelineLock.TryAcquireWithWait(audit.ConnectionString, wait, PipelineLock.DataWrite, false))
                {
                    if (wholeDatabase == null)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: an incremental pass or a delete pass still uses the database after waiting " + (int)wait.TotalMinutes + " minute(s). Nothing was published.");
                        return StoppedExitCode;
                    }

                    if (!reconcileDeletes && !LatestDeletePassIsRecentAndComplete(audit.ConnectionString))
                        return StoppedExitCode;

                    // Fail in seconds, not after the whole reporting build, when the target database has an old publisher procedure.
                    var procedureProblem = ProductionPublisher.CheckProcedureIsCurrent();
                    if (procedureProblem != null)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: " + procedureProblem + " Nothing was changed.");
                        return StoppedExitCode;
                    }

                    audit.PreflightOrThrow(false, pipelineLockHeld: true);

                    Console.WriteLine("Step 1/5: final incremental catch-up of the raw tables (staging only).");
                    var catchUp = await new IncrementalSyncRunner(audit.ConnectionString).RunAsync();
                    if (catchUp != 0)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: the catch-up did not complete, so staging cannot be approved.");
                        return 1;
                    }

                    if (reconcileDeletes)
                    {
                        // Manual use: deletions must reach the raw tables BEFORE the snapshot, so the reports and the target show them in this same run.
                        Console.WriteLine("Step 1b/5: delete reconciliation (--with-deletes): moving records deleted in Clio to the *_Deleted tables.");
                        try
                        {
                            // The whole database is already held exclusively, so the delete step takes no lock of its own.
                            var result = await new DeleteReconciler(audit.ConnectionString, true, false).RunAsync().ConfigureAwait(false);
                            if (result.Item2 != 0)
                            {
                                Console.Error.WriteLine("FULL CHECK STOPPED: delete reconciliation reported " + result.Item2 + " problem(s). Nothing was published; the target database keeps its last good data.");
                                return 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("FULL CHECK STOPPED: delete reconciliation failed: " + ex.Message);
                            return 1;
                        }
                    }

                    Console.WriteLine("Step 2/5: recording the approved staging snapshot.");
                    var companyIds = new BIDataManager(false).GetClioConnectedCompanies().Select(c => c.ID).ToList();
                    if (companyIds.Count == 0)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: no active Clio companies are configured in staging.");
                        return 1;
                    }
                    var runId = audit.RecordSnapshotRun(companyIds);
                    Console.WriteLine("Snapshot RunID: " + runId);

                    Console.WriteLine("Step 3/5: 10% comparison and endpoint status check.");
                    var validation = PipelineValidation.CreateGuarded().Validate(runId, MaximumDifferencePct);
                    if (validation != 0)
                    {
                        Console.Error.WriteLine("FULL CHECK FAILED: this snapshot must not be published.");
                        return 1;
                    }

                    // Only after the data passed: build the reporting tables in staging from exactly this snapshot.
                    // StagingReportingBuild: auto (default) = skip when the publisher procedure rebuilds the reporting tables in the target itself (building them
                    // here as well would be the same work twice); true = always build in staging; false = never.
                    var buildSetting = (System.Configuration.ConfigurationManager.AppSettings["StagingReportingBuild"] ?? "auto").Trim().ToLowerInvariant();
                    var buildInStaging = buildSetting == "true" || (buildSetting != "false" && !ProductionPublisher.TargetRebuildsReportingItself());
                    if (!buildInStaging)
                    {
                        Console.WriteLine("Step 4/5: skipped. The publisher procedure rebuilds the reporting tables in the target database itself (PopulateTables), so building them in Clio_Staging too would do the same work twice. Set StagingReportingBuild=true to build them in staging anyway.");
                    }
                    else
                    {
                        Console.WriteLine("Step 4/5: building the reporting tables in Clio_Staging (PopulateTables and the post-sync procedures).");
                        try { new ReportingBuilder(audit.ConnectionString).Build(); }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("FULL CHECK STOPPED: the reporting build failed: " + ex.Message);
                            return 1;
                        }
                    }

                    Console.WriteLine("Step 5/5: " + (publish ? "publishing to the target database." : "publisher dry run (add --confirm-publish to publish)."));
                    try
                    {
                        if (publish) ProductionPublisher.Publish(runId);
                        else ProductionPublisher.DryRun(runId);
                        return 0;
                    }
                    catch (SqlException ex)
                    {
                        ProductionPublisher.PrintSqlErrors(ex);
                        return 1;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("PUBLISH STEP FAILED: " + ex.GetType().Name + ": " + ex.Message);
                        return 2;
                    }
                }
            }
        }

        /// <summary>
        /// The deletions of Clio must have reached the raw tables before a snapshot is approved. The latest delete pass (control.DeleteRun) has to be
        /// COMPLETE (all endpoints, no problems) and not older than FullCheckMaxDeleteAgeMinutes (0 = do not check age; deletions then are not enforced at all).
        /// </summary>
        static bool LatestDeletePassIsRecentAndComplete(string connectionString)
        {
            var maxAge = ReadInt("FullCheckMaxDeleteAgeMinutes", 240);
            Tuple<string, DateTime, DateTime?> latest;
            try { latest = DeleteReconciler.LatestPass(connectionString); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FULL CHECK STOPPED: control.DeleteRun could not be read: " + ex.Message);
                return false;
            }

            if (latest == null)
            {
                Console.Error.WriteLine("FULL CHECK STOPPED: no delete pass has been recorded yet (control.DeleteRun). Run the delete EXE first, or use --with-deletes once. Nothing was published.");
                return false;
            }
            if (latest.Item1 != "COMPLETE")
            {
                Console.Error.WriteLine("FULL CHECK STOPPED: the latest delete pass is " + latest.Item1 + ", not COMPLETE. Nothing was published; the target keeps its last good data.");
                return false;
            }
            var finished = DateTime.SpecifyKind(latest.Item3 ?? latest.Item2, DateTimeKind.Utc);
            var age = (DateTime.UtcNow - finished).TotalMinutes;
            if (maxAge > 0 && age > maxAge)
            {
                Console.Error.WriteLine("FULL CHECK STOPPED: the latest COMPLETE delete pass is " + (int)age + " minute(s) old (limit " + maxAge +
                    " minutes, FullCheckMaxDeleteAgeMinutes). Check that the delete EXE is running; nothing was published.");
                return false;
            }
            Console.WriteLine("Latest delete pass: COMPLETE, " + (int)age + " minute(s) ago (limit " + (maxAge > 0 ? maxAge.ToString() : "none") + ").");
            return true;
        }
    }
}
