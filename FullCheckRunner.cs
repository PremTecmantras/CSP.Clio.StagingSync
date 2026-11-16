using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using CSP.Clio.Sync.Data;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The scheduled publish cycle. It never polls Clio and never clears anything: the 5-minute incremental task and the delete task keep
    /// the raw tables current.
    ///
    /// Two-database mode (AppSetting LiveDatabaseName set): the incremental sync and the delete task write to Clio_Live all day.
    ///   1. checks that both tasks are alive (last incremental / last clean delete pass not older than the configured limits),
    ///   2. copies the raw tables from Clio_Live into Clio_Staging while holding Live exclusively (a few minutes), then lets Live go again,
    ///   3. records the Clio_Staging tables as an approved snapshot, runs the 10% comparison against the target database,
    ///   4. builds the reporting tables in Clio_Staging, 5. publishes to the target database (dry run without --confirm-publish).
    /// Steps 3 to 5 only touch Clio_Staging, so the incremental sync and the delete task are not held up by them.
    ///
    /// Single-database mode (LiveDatabaseName empty): everything lives in Clio_Staging, the cycle holds the database exclusively from start to
    /// end, and the delete reconciliation runs inside it (use --skip-deletes to leave it out).
    /// </summary>
    internal static class FullCheckRunner
    {
        const decimal MaximumDifferencePct = 10m;

        static int ReadInt(string name, int defaultValue)
        {
            int value;
            return int.TryParse(System.Configuration.ConfigurationManager.AppSettings[name], out value) && value >= 0 ? value : defaultValue;
        }

        public static async Task<int> RunAsync(StagingAudit audit, bool publish, bool reconcileDeletes = true)
        {
            var dual = PipelineRole.DualDatabaseMode;
            var wait = TimeSpan.FromMinutes(ReadInt("FullCheckLockWaitMinutes", 10));

            // One publish cycle at a time (in the Staging database).
            using (var pipelineLock = PipelineLock.TryAcquireWithWait(audit.ConnectionString, wait))
            {
                if (pipelineLock == null)
                {
                    Console.Error.WriteLine("FULL CHECK BLOCKED: another publish cycle is still running" + (dual ? string.Empty : ", or an incremental sync holds the database") +
                        " (waited " + (int)wait.TotalMinutes + " minute(s)). Wait for it to finish and run again.");
                    return 1;
                }

                // Single database: the cycle needs the whole database for itself, so the incremental sync and the delete task skip or wait.
                IDisposable wholeDatabase = null;
                if (!dual)
                {
                    wholeDatabase = PipelineLock.TryAcquireWithWait(audit.ConnectionString, wait, PipelineLock.LiveWrite, false);
                    if (wholeDatabase == null)
                    {
                        Console.Error.WriteLine("FULL CHECK BLOCKED: an incremental sync or a delete step holds the database (waited " + (int)wait.TotalMinutes + " minute(s)). Run again later.");
                        return 1;
                    }
                }

                using (wholeDatabase)
                {
                    audit.PreflightOrThrow(false, pipelineLockHeld: true);

                    string liveConnectionString;
                    try { liveConnectionString = dual ? PipelineRole.LiveConnectionString() : audit.ConnectionString; }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: " + ex.Message);
                        return 1;
                    }

                    // The cycle does not poll Clio itself, so first make sure the tasks that do are alive.
                    if (!CheckTasksAreAlive(liveConnectionString, dual)) return 1;

                    if (dual)
                    {
                        Console.WriteLine("Step 1/5: copying the raw tables from " + PipelineRole.LiveDatabaseName + " into Clio_Staging (Live is held for the copy only).");
                        if (!CopyLiveToStaging(audit, liveConnectionString)) return 1;
                    }
                    else if (reconcileDeletes)
                    {
                        // Deletions must reach the raw tables BEFORE the snapshot, so the reports and the target show them in this same run.
                        Console.WriteLine("Step 1/5: delete reconciliation (use --skip-deletes to leave it out): moving records deleted in Clio to the *_Deleted tables.");
                        try
                        {
                            var result = await new DeleteReconciler(audit.ConnectionString, true, false).RunAsync().ConfigureAwait(false);
                            if (result.Item2 != 0)
                            {
                                Console.Error.WriteLine("FULL CHECK STOPPED: delete reconciliation reported " + result.Item2 + " problem(s). Nothing was published; the target database keeps its last good data. Fix the problem, or run once with --skip-deletes.");
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
                    Console.WriteLine("Step 4/5: building the reporting tables in Clio_Staging (PopulateTables and the post-sync procedures).");
                    try { new ReportingBuilder(audit.ConnectionString).Build(); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: the reporting build failed: " + ex.Message);
                        return 1;
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

        /// <summary>Stops the cycle when the 5-minute incremental task or the delete task has not completed recently (a stopped task must not lead to a publish of old data).</summary>
        static bool CheckTasksAreAlive(string liveConnectionString, bool dual)
        {
            var maxIncrementalAge = ReadInt("FullCheckMaxIncrementalAgeMinutes", 30);
            if (maxIncrementalAge > 0)
            {
                var last = LiveToStagingCopier.LastCompletedIncrementalUtc(liveConnectionString);
                var age = last.HasValue ? (DateTime.UtcNow - last.Value).TotalMinutes : double.MaxValue;
                if (age > maxIncrementalAge)
                {
                    Console.Error.WriteLine("FULL CHECK STOPPED: the last completed incremental sync is " + (last.HasValue ? ((int)age + " minute(s) old") : "missing") +
                        " (limit " + maxIncrementalAge + " minutes, FullCheckMaxIncrementalAgeMinutes). Check that the 5-minute incremental task is running; nothing was published.");
                    return false;
                }
                Console.WriteLine("Last completed incremental sync: " + (int)age + " minute(s) ago (limit " + maxIncrementalAge + ").");
            }

            if (dual)
            {
                var maxDeleteAge = ReadInt("FullCheckMaxDeleteAgeMinutes", 240);
                if (maxDeleteAge > 0)
                {
                    var last = LiveToStagingCopier.LastCleanDeletePassUtc(liveConnectionString);
                    var age = last.HasValue ? (DateTime.UtcNow - last.Value).TotalMinutes : double.MaxValue;
                    if (age > maxDeleteAge)
                    {
                        Console.Error.WriteLine("FULL CHECK STOPPED: the last clean delete pass is " + (last.HasValue ? ((int)age + " minute(s) old") : "missing") +
                            " (limit " + maxDeleteAge + " minutes, FullCheckMaxDeleteAgeMinutes). Check that the delete task (--delete-sync) is running; nothing was published." +
                            " Set the limit to 0 only if deletions are intentionally not handled.");
                        return false;
                    }
                    Console.WriteLine("Last clean delete pass: " + (int)age + " minute(s) ago (limit " + maxDeleteAge + ").");
                }
            }
            return true;
        }

        /// <summary>Copies Live into Staging while holding Live exclusively; the lock is released as soon as the copy is done.</summary>
        static bool CopyLiveToStaging(StagingAudit audit, string liveConnectionString)
        {
            var wait = TimeSpan.FromMinutes(ReadInt("LiveCopyLockWaitMinutes", 15));
            using (var liveLock = PipelineLock.TryAcquireWithWait(liveConnectionString, wait, PipelineLock.LiveWrite, false))
            {
                if (liveLock == null)
                {
                    Console.Error.WriteLine("FULL CHECK STOPPED: " + PipelineRole.LiveDatabaseName + " is busy (an incremental run or a delete step did not finish within " +
                        (int)wait.TotalMinutes + " minute(s)). Nothing was published.");
                    return false;
                }
                try
                {
                    // The exclusive lock proves that no incremental run is alive, so a RUNNING row belongs to a process that died.
                    LiveToStagingCopier.AbandonStaleIncrementalRuns(liveConnectionString);
                    var latest = LiveToStagingCopier.LatestIncrementalStatus(liveConnectionString);
                    if (latest != null && latest != "COMPLETE")
                        Console.WriteLine("Note: the latest incremental run ended as " + latest + ". Its endpoints that failed are retried by the next run; the data already in Live is consistent.");
                    new LiveToStagingCopier(audit.ConnectionString).Run();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("FULL CHECK STOPPED: the copy from " + PipelineRole.LiveDatabaseName + " failed: " + ex.Message);
                    return false;
                }
            }
        }
    }
}
