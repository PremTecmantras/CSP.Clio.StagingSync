using System;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;
using CSP.Clio.Sync.Data;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The manual morning/night run. It does NOT extract or clear anything: staging is kept current by the hourly
    /// incremental task. It takes the pipeline lock, catches staging up one last time, records the current staging tables
    /// as an approved snapshot, runs the 10% comparison (every endpoint COMPLETE and within the allowed shortfall against
    /// the target database), and only then publishes. Without --confirm-publish it stops after a publisher dry run.
    /// </summary>
    internal static class FullCheckRunner
    {
        const decimal MaximumDifferencePct = 10m;

        public static async Task<int> RunAsync(StagingAudit audit, bool publish)
        {
            using (var pipelineLock = PipelineLock.TryAcquire(audit.ConnectionString))
            {
                if (pipelineLock == null)
                {
                    Console.Error.WriteLine("FULL CHECK BLOCKED: an incremental sync or another full check is running. Wait for it to finish and run again.");
                    return 1;
                }

                audit.PreflightOrThrow(false, pipelineLockHeld: true);

                Console.WriteLine("Step 1/5: final incremental catch-up of the raw tables (staging only).");
                var catchUp = await new IncrementalSyncRunner(audit.ConnectionString).RunAsync();
                if (catchUp != 0)
                {
                    Console.Error.WriteLine("FULL CHECK STOPPED: the catch-up did not complete, so staging cannot be approved.");
                    return 1;
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
}
