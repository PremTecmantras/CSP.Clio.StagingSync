using System;
using System.Threading.Tasks;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The scheduled incremental task. One pass reads everything changed since the saved watermark into the raw tables.
    /// In the cycle the passes are repeated for a fixed time window (for example every 5 minutes for 55 minutes), then the process ends,
    /// so the publish can take the database for itself.
    /// </summary>
    internal static class IncrementalWindowRunner
    {
        /// <summary>
        /// One pass with its locks. Returns 0 when it finished or was skipped on purpose, 1 when a company or endpoint failed
        /// (the next pass retries from the saved watermark). Skipping is quiet because the next pass picks up everything.
        /// </summary>
        public static async Task<int> RunPassAsync(StagingAudit audit)
        {
            // Only one incremental pass at a time.
            using (var incrementalLock = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.IncrementalRun, false))
            {
                if (incrementalLock == null)
                {
                    Console.WriteLine("INCREMENTAL PASS SKIPPED: the previous incremental pass is still going. The next pass will pick up the changes.");
                    return 0;
                }
                // Shared: a delete pass may run at the same time; a publish cycle (exclusive) or the first load may not.
                using (var dataLock = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.DataWrite, true))
                {
                    if (dataLock == null)
                    {
                        Console.WriteLine("INCREMENTAL PASS SKIPPED: a publish cycle (or the first load) is using the database. The next pass will pick up the changes.");
                        return 0;
                    }
                    // Both locks are held, so a RUNNING row of an earlier pass belongs to a process that died; a full extraction must never overlap.
                    audit.PreflightOrThrow(false, pipelineLockHeld: true);
                    return await new IncrementalSyncRunner(audit.ConnectionString).RunAsync().ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Repeats the pass every <paramref name="intervalMinutes"/> (start to start) until the window of <paramref name="windowMinutes"/> is used up.
        /// The last pass starts at the end of the window, so a publish that follows sees the latest changes.
        /// A pass that runs longer than the interval is followed straight away by the next one (slots never pile up).
        /// Exit code: that of the last pass.
        /// </summary>
        public static async Task<int> RunWindowAsync(StagingAudit audit, int windowMinutes, int intervalMinutes)
        {
            if (windowMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(windowMinutes));
            if (intervalMinutes <= 0) throw new ArgumentOutOfRangeException(nameof(intervalMinutes));

            var window = TimeSpan.FromMinutes(windowMinutes);
            var interval = TimeSpan.FromMinutes(intervalMinutes);
            var started = DateTime.UtcNow;
            var lastExit = 0;
            var passes = 0;
            Console.WriteLine("Incremental window: " + windowMinutes + " minute(s), one pass every " + intervalMinutes + " minute(s).");

            for (var i = 0; ; i++)
            {
                var offset = TimeSpan.FromTicks(interval.Ticks * i);
                if (offset > window) break;

                var wait = started + offset - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait).ConfigureAwait(false);

                passes++;
                Console.WriteLine();
                Console.WriteLine("--- Incremental pass " + passes + " at " + (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss") + " of " + window.ToString(@"hh\:mm\:ss") + " ---");
                try { lastExit = await RunPassAsync(audit).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lastExit = 1;
                    Console.Error.WriteLine("INCREMENTAL PASS FAILED: " + ex.Message);
                }
            }

            Console.WriteLine();
            Console.WriteLine("Incremental window finished after " + (DateTime.UtcNow - started).ToString(@"hh\:mm\:ss") + ": " + passes + " pass(es), last exit code " + lastExit + ".");
            return lastExit;
        }
    }
}
