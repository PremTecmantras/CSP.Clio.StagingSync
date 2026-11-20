using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// The single scheduled task (--cycle). It repeats this cycle on its own:
    ///
    ///   1. starts the incremental task (a time window of passes, for example every 5 minutes for 55 minutes)
    ///      and, at the same time, the delete EXE (one pass; one retry when it fails and enough of the window is left);
    ///   2. when the incremental task has ended and the delete pass is COMPLETE (control.DeleteRun),
    ///      starts the publish (--full-check --confirm-publish): snapshot, 10% check, reporting build, publish to the target database;
    ///   3. waits a short gap and starts the next cycle.
    ///
    /// Every part is a separate process (the same EXE for incremental and publish, CSP.Clio.StagingDelete.exe for the delete pass), so they have
    /// their own logs and no shared state. Overlaps are impossible: the delete pass and the incremental passes hold the shared
    /// ClioStagingData lock, the publish holds it exclusively (see PipelineLock). The supervisor itself holds only ClioCycleSupervisor,
    /// so a second --cycle task exits at once. Child processes are put in a job object: if the supervisor is stopped or killed they end too.
    /// </summary>
    internal static class CycleRunner
    {
        sealed class Settings
        {
            public int WindowMinutes, IntervalMinutes, DeleteRetryMinWindowMinutes, DeleteWatchdogMinutes, PublishWatchdogMinutes, GapMinutes;
            public int IncrementalWatchdogSlackMinutes, ShortCycleMinutes, ShortCycleWaitMinutes;
            public int HeartbeatMinutes, LogRetentionDays;
            public bool PublishEnabled;
            /// <summary>false = the cycle does not start the delete EXE (it runs as its own scheduled job); the publish then needs a recent COMPLETE pass of that job.</summary>
            public bool RunsDelete;
            /// <summary>What of the child processes' output goes into the cycle log: "important" (everything except routine lines), "all", or "none".</summary>
            public string ChildOutput;
            public string DeleteExeName;

            /// <summary>Every timing of the cycle comes from App.config; these are only the fallbacks for a missing or invalid value.</summary>
            public static Settings Read()
            {
                return new Settings
                {
                    WindowMinutes = ReadInt("CycleIncrementalWindowMinutes", 55, 1),
                    IntervalMinutes = ReadInt("CycleIncrementalIntervalMinutes", 5, 1),
                    DeleteRetryMinWindowMinutes = ReadInt("CycleDeleteRetryMinWindowMinutes", 25, 0),
                    DeleteWatchdogMinutes = ReadInt("CycleDeleteWatchdogMinutes", 90, 1),
                    PublishWatchdogMinutes = ReadInt("CyclePublishWatchdogMinutes", 180, 1),
                    IncrementalWatchdogSlackMinutes = ReadInt("CycleIncrementalWatchdogSlackMinutes", 30, 0),
                    ShortCycleMinutes = ReadInt("CycleShortCycleMinutes", 2, 0),
                    ShortCycleWaitMinutes = ReadInt("CycleShortCycleWaitMinutes", 5, 1),
                    GapMinutes = ReadInt("CycleGapMinutes", 2, 1),
                    HeartbeatMinutes = ReadInt("CycleHeartbeatMinutes", 5, 0),
                    LogRetentionDays = ReadInt("LogRetentionDays", 30, 0),
                    ChildOutput = ReadChildOutput(),
                    PublishEnabled = !string.Equals(ConfigurationManager.AppSettings["CyclePublishEnabled"], "false", StringComparison.OrdinalIgnoreCase),
                    RunsDelete = !string.Equals(ConfigurationManager.AppSettings["CycleRunsDelete"], "false", StringComparison.OrdinalIgnoreCase),
                    DeleteExeName = string.IsNullOrWhiteSpace(ConfigurationManager.AppSettings["CycleDeleteExeName"])
                        ? "CSP.Clio.StagingDelete.exe" : ConfigurationManager.AppSettings["CycleDeleteExeName"].Trim()
                };
            }

            static int ReadInt(string name, int defaultValue, int minimum)
            {
                int value;
                return int.TryParse(ConfigurationManager.AppSettings[name], out value) && value >= minimum ? value : defaultValue;
            }

            static string ReadChildOutput()
            {
                var raw = (ConfigurationManager.AppSettings["CycleChildOutputInLog"] ?? string.Empty).Trim().ToLowerInvariant();
                return raw == "all" || raw == "none" ? raw : "important";
            }
        }

        sealed class Child
        {
            public string Name;
            public Process Process;
            public Task<int> Exit;
            public DateTime StartedUtc = DateTime.UtcNow;
            public volatile string LastLine = string.Empty;
            public DateTime LastLineUtc = DateTime.UtcNow;
            readonly Queue<string> _tail = new Queue<string>();

            public void Remember(string line)
            {
                LastLine = line;
                LastLineUtc = DateTime.UtcNow;
                lock (_tail) { _tail.Enqueue(line); while (_tail.Count > 12) _tail.Dequeue(); }
            }

            public string Tail() { lock (_tail) { return string.Join(" | ", _tail); } }
        }

        /// <summary>The parts that run right now (for the status lines in the log).</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Child> Active = new System.Collections.Concurrent.ConcurrentDictionary<int, Child>();
        static string _childOutput = "important";

        /// <summary>Durations of the parts of one cycle, for the summary line.</summary>
        sealed class CycleTimes
        {
            public DateTime CycleStarted = DateTime.Now;
            public DateTime? IncrementalStarted, DeleteStarted, PublishStarted;
            public TimeSpan? Incremental, Delete, Publish;
            public int? IncrementalExit, PublishExit;
            public string DeleteResult;
            public string CycleFolderPath;

            static string Time(DateTime? t) { return t.HasValue ? t.Value.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "-"; }
            static string Span(TimeSpan? t) { return t.HasValue ? t.Value.ToString(@"hh\:mm\:ss", System.Globalization.CultureInfo.InvariantCulture) : "-"; }
            static string ExitText(int? exit) { return !exit.HasValue ? "not run" : exit.Value == 0 ? "OK" : "FAILED (exit " + exit.Value + ")"; }

            /// <summary>What an outcome means, in plain words.</summary>
            static string Meaning(string outcome)
            {
                switch (outcome)
                {
                    case "PUBLISHED": return "The data was published to the target database.";
                    case "PUBLISH_FAILED": return "The publish failed. The target database keeps its last good data. See the publish log of this cycle.";
                    case "PUBLISH_SKIPPED_DELETE_NOT_COMPLETE": return "Nothing was published: the delete pass did not finish COMPLETE. The target keeps its last good data; the next cycle tries again.";
                    case "PUBLISH_DISABLED": return "Nothing was published: CyclePublishEnabled is false in App.config.";
                    case "CANCELLED": return "The cycle was stopped before it finished.";
                    default: return string.Empty;
                }
            }

            /// <summary>The table written at the end of a cycle.</summary>
            public string Describe(string outcome, TimeSpan total)
            {
                var text = new System.Text.StringBuilder();
                text.AppendLine(Rule);
                text.AppendLine("CYCLE SUMMARY   outcome: " + outcome);
                var meaning = Meaning(outcome);
                if (meaning.Length > 0) text.AppendLine("  " + meaning);
                text.AppendLine();
                text.AppendLine("  Part          Started    Duration   Result");
                text.AppendLine("  Incremental   " + Time(IncrementalStarted).PadRight(10) + " " + Span(Incremental).PadRight(10) + " " + ExitText(IncrementalExit));
                text.AppendLine("  Delete        " + Time(DeleteStarted).PadRight(10) + " " + Span(Delete).PadRight(10) + " " + (DeleteResult ?? "not run"));
                text.AppendLine("  Publish       " + Time(PublishStarted).PadRight(10) + " " + Span(Publish).PadRight(10) + " " + ExitText(PublishExit));
                text.AppendLine("  Whole cycle   " + Time(CycleStarted).PadRight(10) + " " + Span(total));
                if (!string.IsNullOrEmpty(CycleFolderPath)) text.AppendLine("  Complete logs of the parts: " + CycleFolderPath);
                text.Append(Rule);
                return text.ToString();
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        public static async Task<int> RunAsync(StagingAudit audit, bool once)
        {
            using (var supervisorLock = PipelineLock.TryAcquire(audit.ConnectionString, PipelineLock.Supervisor, false))
            {
                if (supervisorLock == null)
                {
                    Log("supervisor", "Not started: another cycle supervisor is already running.");
                    return 0;
                }

                var settings = Settings.Read();
                _childOutput = settings.ChildOutput;
                _logPath = NewLogPath();
                var stopFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cycle.stop");
                try { if (File.Exists(stopFile)) File.Delete(stopFile); } catch { }
                CleanOldLogs(settings);

                var cts = new CancellationTokenSource();
                var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                ConsoleCancelEventHandler onCancel = (o, e) => { e.Cancel = true; cts.Cancel(); };
                Console.CancelKeyPress += onCancel;
                var job = ChildJob.TryCreate();
                if (job == null) Log("WARNING", "No job object: child processes are not ended automatically if this supervisor is killed.");

                LogRaw(string.Empty);
                Log("supervisor", "STARTED" + (once ? " (one cycle only)" : string.Empty) + "\n" +
                    "Settings (minutes, from App.config):\n" +
                    "  incremental   window " + settings.WindowMinutes + ", a pass every " + settings.IntervalMinutes + ", watchdog = window + " + settings.IncrementalWatchdogSlackMinutes + "\n" +
                    "  delete        runs in parallel; a failed pass is retried once if " + settings.DeleteRetryMinWindowMinutes + " of the window are left; watchdog " + settings.DeleteWatchdogMinutes + "\n" +
                    "  publish       " + (settings.PublishEnabled ? "ON" : "OFF (CyclePublishEnabled=false)") + "; watchdog " + settings.PublishWatchdogMinutes + "\n" +
                    "  between cycles  " + settings.GapMinutes + " (a cycle shorter than " + settings.ShortCycleMinutes + " waits " + settings.ShortCycleWaitMinutes + ")\n" +
                    "Logging:\n" +
                    "  this file: steps, a status line " + (settings.HeartbeatMinutes > 0 ? "every " + settings.HeartbeatMinutes + " minute(s)" : "(off)") +
                    ", output of the parts (" + settings.ChildOutput + "), a summary per cycle\n" +
                    "  complete log of every part of a cycle: " + LogPaths.CyclesRoot + "\\<cycle start>\\ (incremental.txt, delete.txt, publish.txt)\n" +
                    "  " + (settings.LogRetentionDays > 0 ? "logs older than " + settings.LogRetentionDays + " days are deleted" : "logs are never deleted") + "\n" +
                    "To stop after the current cycle: create the file " + stopFile);
                var heartbeat = settings.HeartbeatMinutes > 0 ? HeartbeatAsync(settings.HeartbeatMinutes, heartbeatCts.Token) : Task.CompletedTask;
                try
                {
                    var cycleNumber = 0;
                    while (!cts.IsCancellationRequested)
                    {
                        if (cycleNumber++ > 0) _logPath = NewLogPath();   // the first cycle continues in the file of the supervisor start
                        var cycleStarted = DateTime.UtcNow;
                        string outcome;
                        try { outcome = await RunCycleAsync(audit, settings, job, cts.Token).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            outcome = "ERROR";
                            Log("PROBLEM", "The cycle stopped with an error: " + ex.GetType().Name + ": " + ex.Message);
                        }
                        Log("cycle", "Cycle ended: " + outcome + " (after " + (DateTime.UtcNow - cycleStarted).ToString(@"hh\:mm\:ss") + ").");
                        CleanOldLogs(settings);

                        if (once || cts.IsCancellationRequested) break;
                        if (File.Exists(stopFile)) { Log("supervisor", "Stop file found: the supervisor ends after this cycle."); break; }

                        // A cycle that ends within CycleShortCycleMinutes did nothing useful (for example a missing EXE or a database problem).
                        // Wait CycleShortCycleWaitMinutes then, so the log and the Clio API are not hammered.
                        var gap = DateTime.UtcNow - cycleStarted < TimeSpan.FromMinutes(settings.ShortCycleMinutes)
                            ? TimeSpan.FromMinutes(Math.Max(settings.ShortCycleWaitMinutes, settings.GapMinutes))
                            : TimeSpan.FromMinutes(settings.GapMinutes);
                        try { await Task.Delay(gap, cts.Token).ConfigureAwait(false); }
                        catch (TaskCanceledException) { break; }
                    }
                }
                finally
                {
                    Console.CancelKeyPress -= onCancel;
                    heartbeatCts.Cancel();
                    try { await heartbeat.ConfigureAwait(false); } catch { }
                    if (cts.IsCancellationRequested) Log("supervisor", "Stopped (Ctrl+C): running parts are ended.");
                    if (job != null) job.Dispose();   // ends any child that is still running
                }
                Log("supervisor", "ENDED.");
                return 0;
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        static async Task<string> RunCycleAsync(StagingAudit audit, Settings s, ChildJob job, CancellationToken token)
        {
            var cs = audit.ConnectionString;
            var cycleId = Guid.NewGuid();
            var cycleStart = DateTime.UtcNow;
            var windowEnd = cycleStart.AddMinutes(s.WindowMinutes);
            var self = Assembly.GetExecutingAssembly().Location;
            var deleteExe = Path.Combine(Path.GetDirectoryName(self), s.DeleteExeName);
            var times = new CycleTimes();
            var cycleClock = Stopwatch.StartNew();
            // The one folder of this cycle: each part writes its complete log here itself (it is told the file with --log-file).
            var cycleFolder = times.CycleStarted.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            times.CycleFolderPath = LogPaths.CycleFolder(cycleFolder);
            try { Directory.CreateDirectory(times.CycleFolderPath); }
            catch (Exception ex) { Log("WARNING", "The log folder of this cycle could not be created (" + ex.Message + "); the parts then log only to the console."); }
            Func<string, string, string> done = (outcome, notes) =>
            {
                LogRaw(times.Describe(outcome, cycleClock.Elapsed));
                return Finish(cs, cycleId, outcome, notes);
            };

            LogRaw(string.Empty);
            LogRaw(DoubleRule);
            LogRaw("CYCLE   started " + times.CycleStarted.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "     id " + cycleId.ToString().Substring(0, 8));
            LogRaw(DoubleRule);
            Log("cycle", "Plan: incremental window of " + s.WindowMinutes + " min (a pass every " + s.IntervalMinutes + " min) and the delete pass in parallel.\n" +
                "When both are finished and the delete pass is COMPLETE, the publish starts" + (s.PublishEnabled ? "." : " (switched OFF in App.config)."));
            Db(() =>
            {
                EnsureCycleTable(cs);
                Exec(cs, "INSERT control.CycleRun (CycleID, Outcome) VALUES (@id, 'RUNNING')", new SqlParameter("@id", cycleId));
            });

            times.IncrementalStarted = DateTime.Now;
            var incremental = StartChild("incremental", self, "--incremental --window-minutes " + s.WindowMinutes + " --interval-minutes " + s.IntervalMinutes, job,
                Path.Combine(times.CycleFolderPath, "incremental.txt"));
            Task<bool> deleteTask;
            if (s.RunsDelete)
                deleteTask = RunDeleteAsync(cs, s, deleteExe, windowEnd, job, token, times, cycleFolder);
            else
            {
                // The delete EXE is its own scheduled job. The publish (--full-check) itself requires a COMPLETE pass of it that is not older than
                // FullCheckMaxDeleteAgeMinutes and stops when a pass is running at that moment.
                Log("cycle", "CycleRunsDelete=false: the delete pass is not started here (it runs as its own job). The publish needs its latest COMPLETE pass to be recent.");
                times.DeleteResult = "separate job";
                deleteTask = Task.FromResult(true);
            }

            // The window plus some slack for the last pass; after that the incremental process is ended (a hung pass must not block the cycle).
            var incrementalExit = await WaitAsync(incremental, TimeSpan.FromMinutes(s.WindowMinutes + s.IncrementalWatchdogSlackMinutes), token).ConfigureAwait(false);
            times.Incremental = cycleClock.Elapsed;
            times.IncrementalExit = incrementalExit;
            if (incrementalExit == 0) Log("incremental", "FINISHED after " + cycleClock.Elapsed.ToString(@"hh\:mm\:ss") + " (exit code 0).");
            else Log("PROBLEM", "incremental ended with exit code " + incrementalExit + " after " + cycleClock.Elapsed.ToString(@"hh\:mm\:ss") + ".\nLast output: " + incremental.Tail());
            Db(() => Exec(cs, "UPDATE control.CycleRun SET IncrementalEndedAtUTC = SYSUTCDATETIME(), IncrementalExitCode = @c WHERE CycleID = @id",
                new SqlParameter("@c", incrementalExit), new SqlParameter("@id", cycleId)));

            // Publish waits here until the delete pass is finished (status based). It never overlaps with it.
            var deleteOk = await deleteTask.ConfigureAwait(false);
            times.DeleteResult = !s.RunsDelete ? "separate job" : (deleteOk ? "COMPLETE" : "NOT COMPLETE");
            Db(() => Exec(cs, "UPDATE control.CycleRun SET DeleteStatus = @d, DeleteEndedAtUTC = SYSUTCDATETIME() WHERE CycleID = @id",
                new SqlParameter("@d", !s.RunsDelete ? "SEPARATE_JOB" : (deleteOk ? "COMPLETE" : "NOT_COMPLETE")), new SqlParameter("@id", cycleId)));

            if (token.IsCancellationRequested) return done("CANCELLED", null);
            if (!deleteOk)
            {
                Log("PROBLEM", "No publish in this cycle: the delete pass did not finish COMPLETE (the target keeps its last good data).");
                return done("PUBLISH_SKIPPED_DELETE_NOT_COMPLETE",
                    "The delete pass did not finish COMPLETE, so nothing was published this cycle (the target keeps its last good data).");
            }
            if (!s.PublishEnabled)
            {
                Log("cycle", "No publish in this cycle: CyclePublishEnabled is false.");
                return done("PUBLISH_DISABLED", "CyclePublishEnabled is false.");
            }

            Log("cycle", "Incremental window over and delete pass COMPLETE: the publish starts now.");
            Db(() => Exec(cs, "UPDATE control.CycleRun SET PublishStartedAtUTC = SYSUTCDATETIME() WHERE CycleID = @id", new SqlParameter("@id", cycleId)));
            var publishClock = Stopwatch.StartNew();
            times.PublishStarted = DateTime.Now;
            var publish = StartChild("publish", self, "--full-check --confirm-publish", job, Path.Combine(times.CycleFolderPath, "publish.txt"));
            var publishExit = await WaitAsync(publish, TimeSpan.FromMinutes(s.PublishWatchdogMinutes), token).ConfigureAwait(false);
            times.Publish = publishClock.Elapsed;
            times.PublishExit = publishExit;
            if (publishExit == 0) Log("publish", "FINISHED after " + publishClock.Elapsed.ToString(@"hh\:mm\:ss") + " (exit code 0).");
            else Log("PROBLEM", "publish ended with exit code " + publishExit + " after " + publishClock.Elapsed.ToString(@"hh\:mm\:ss") + ".\nLast output: " + publish.Tail());
            Db(() => Exec(cs, "UPDATE control.CycleRun SET PublishEndedAtUTC = SYSUTCDATETIME(), PublishExitCode = @c WHERE CycleID = @id",
                new SqlParameter("@c", publishExit), new SqlParameter("@id", cycleId)));

            if (token.IsCancellationRequested) return done("CANCELLED", null);
            if (publishExit == 0) return done("PUBLISHED", null);
            return done("PUBLISH_FAILED", "Exit code " + publishExit + ": " + publish.Tail());
        }

        /// <summary>
        /// Runs the delete EXE and checks control.DeleteRun for a COMPLETE row written by this attempt. When the pass failed and enough of the
        /// incremental window is left, it is started once more.
        /// </summary>
        static async Task<bool> RunDeleteAsync(string cs, Settings s, string deleteExe, DateTime windowEnd, ChildJob job, CancellationToken token,
            CycleTimes times, string cycleFolder)
        {
            if (!File.Exists(deleteExe))
            {
                Log("PROBLEM", "The delete EXE was not found: " + deleteExe + "\nNothing can be published in this cycle. Copy CSP.Clio.StagingDelete.exe next to this EXE.");
                return false;
            }

            var deleteClock = Stopwatch.StartNew();
            try { return await RunDeleteAttemptsAsync(cs, s, deleteExe, windowEnd, job, token, cycleFolder, times).ConfigureAwait(false); }
            finally { times.Delete = deleteClock.Elapsed; }
        }

        static async Task<bool> RunDeleteAttemptsAsync(string cs, Settings s, string deleteExe, DateTime windowEnd, ChildJob job, CancellationToken token, string cycleFolder,
            CycleTimes times)
        {
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                if (attempt == 2)
                {
                    var remaining = windowEnd - DateTime.UtcNow;
                    if (remaining < TimeSpan.FromMinutes(s.DeleteRetryMinWindowMinutes))
                    {
                        Log("PROBLEM", "delete retry not started: only " + Math.Max(0, (int)remaining.TotalMinutes) + " minute(s) of the window are left (it needs " + s.DeleteRetryMinWindowMinutes + ").");
                        return false;
                    }
                    Log("delete", "Starting the delete pass once more (" + (int)remaining.TotalMinutes + " minute(s) of the window are left).");
                }

                // A pass of this attempt is a row newer than the last one recorded before the attempt started (compared by the database's own
                // timestamps, so the clock of this process does not matter).
                DateTime? lastBefore = null;
                try { var before = DeleteReconciler.LatestPass(cs); if (before != null) lastBefore = before.Item2; }
                catch (Exception ex) { Log("WARNING", "Could not read control.DeleteRun: " + ex.Message); }

                var attemptStart = DateTime.UtcNow;
                if (attempt == 1 && times != null && !times.DeleteStarted.HasValue) times.DeleteStarted = DateTime.Now;
                var child = StartChild("delete", deleteExe, string.Empty, job,
                    Path.Combine(LogPaths.CycleFolder(cycleFolder), attempt > 1 ? "delete-attempt" + attempt + ".txt" : "delete.txt"));
                var exit = await WaitAsync(child, TimeSpan.FromMinutes(s.DeleteWatchdogMinutes), token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return false;

                Tuple<string, DateTime, DateTime?> latest = null;
                try { latest = DeleteReconciler.LatestPass(cs); }
                catch (Exception ex) { Log("WARNING", "Could not read control.DeleteRun: " + ex.Message); }

                if (latest != null && latest.Item1 == "COMPLETE" && (!lastBefore.HasValue || latest.Item2 > lastBefore.Value))
                {
                    Log("delete", "FINISHED: COMPLETE after " + (DateTime.UtcNow - attemptStart).ToString(@"hh\:mm\:ss") + " (exit code " + exit + ").");
                    return true;
                }
                Log("PROBLEM", "delete pass did NOT complete (exit code " + exit + ", status in control.DeleteRun: " + (latest == null ? "none" : latest.Item1) + ").\nLast output: " + child.Tail());
            }
            return false;
        }

        static string Finish(string cs, Guid cycleId, string outcome, string notes)
        {
            Db(() => Exec(cs, "UPDATE control.CycleRun SET Outcome = @o, Notes = @n, CompletedAtUTC = SYSUTCDATETIME() WHERE CycleID = @id",
                new SqlParameter("@o", outcome),
                new SqlParameter("@n", (object)StagingAudit.Truncate(notes, 900) ?? DBNull.Value),
                new SqlParameter("@id", cycleId)));
            return outcome;
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        /// <param name="logFile">The complete log of this part: it is told with --log-file and writes only there.</param>
        static Child StartChild(string name, string exePath, string arguments, ChildJob job, string logFile)
        {
            var allArguments = (arguments + " " + LogPaths.LogFileArgument + " \"" + logFile + "\"").Trim();
            var info = new ProcessStartInfo(exePath, allArguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exePath)
            };
            var process = new Process { StartInfo = info, EnableRaisingEvents = true };
            var child = new Child { Name = name, Process = process };
            var exited = new TaskCompletionSource<int>();

            // The child also writes its own complete run log. Its output is read here (otherwise the child blocks when a pipe is full),
            // kept for the failure message, and the important lines are copied into the cycle log with the name of the part in front.
            DataReceivedEventHandler remember = (o, e) =>
            {
                if (e.Data == null) return;
                child.Remember(e.Data);
                if (ShouldMirror(e.Data)) Log(name, e.Data.Trim());
            };
            process.OutputDataReceived += remember;
            process.ErrorDataReceived += remember;
            process.Exited += (o, e) =>
            {
                Child ignored;
                try { Active.TryRemove(process.Id, out ignored); } catch { }
                try { exited.TrySetResult(process.ExitCode); } catch { exited.TrySetResult(-1); }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (job != null) job.Add(process);
            child.Exit = exited.Task;
            Active[process.Id] = child;
            Log(name, "STARTED (process " + process.Id + "): " + Path.GetFileName(exePath) + (arguments.Length > 0 ? " " + arguments : string.Empty));
            return child;
        }

        /// <summary>
        /// Which lines of a part go into the cycle log. "important" = everything except routine lines (no change, nothing missing, instant
        /// list reads, passed checks), so a problem or a number that matters is never hidden; the complete output stays in the part's own log.
        /// </summary>
        static bool ShouldMirror(string line)
        {
            if (_childOutput == "none") return false;
            if (_childOutput == "all") return true;

            var t = line.Trim();
            if (t.Length == 0) return false;

            // Repeated or already summarised lines (the complete part log, archived per cycle, has all of them).
            if (t.StartsWith("Run log:", StringComparison.Ordinal) || t.StartsWith("Run started", StringComparison.Ordinal)) return false;
            if (t.StartsWith("CSP Clio Staging", StringComparison.Ordinal) || t.StartsWith("Target:", StringComparison.Ordinal)) return false;   // shown at the start of the cycle

            // incremental: the per-endpoint lines are replaced by the two summary lines of each pass ("Checked ..." and "Changed rows: ...")
            if (t.StartsWith("Incremental endpoints:", StringComparison.Ordinal) || t.StartsWith("Incremental RunID:", StringComparison.Ordinal)) return false;
            if (t.StartsWith("Incremental ", StringComparison.Ordinal) && t.IndexOf(" - ", StringComparison.Ordinal) > 0 &&
                t.EndsWith(" changed row(s).", StringComparison.Ordinal)) return false;
            if (t.StartsWith("INCREMENTAL SYNC COMPLETE", StringComparison.Ordinal)) return false;

            // delete: per-endpoint result lines are hidden when nothing is missing; the final line says how many were checked
            if (t.EndsWith(" OK", StringComparison.Ordinal)) return false;
            if (t.StartsWith("... reading the", StringComparison.Ordinal)) return false;
            if (t.IndexOf("list read in 00:0", StringComparison.Ordinal) >= 0) return false;           // list read in under 10 seconds
            if (t.StartsWith("Endpoint ", StringComparison.Ordinal) || t.StartsWith("(In table =", StringComparison.Ordinal)) return false;   // table header and its legend

            if (t.IndexOf("| PASS", StringComparison.Ordinal) >= 0) return false;                      // validation table: passed rows
            return true;
        }

        /// <summary>Writes a status line every few minutes: which parts run, for how long, and their last output line.</summary>
        static async Task HeartbeatAsync(int minutes, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromMinutes(minutes), token).ConfigureAwait(false); }
                catch (TaskCanceledException) { return; }

                var running = new List<string>();
                foreach (var child in Active.Values.OrderBy(c => c.StartedUtc))
                {
                    var last = (child.LastLine ?? string.Empty).Trim();
                    if (last.Length > 100) last = last.Substring(0, 100) + "...";
                    // The age matters: a part that is reading a long list prints nothing for minutes, and the same line would otherwise look like
                    // new activity in every status line.
                    var age = DateTime.UtcNow - child.LastLineUtc;
                    running.Add(child.Name.PadRight(12) + " running " + (DateTime.UtcNow - child.StartedUtc).ToString(@"hh\:mm\:ss") +
                        (last.Length > 0 ? "\n    last output " + (age < TimeSpan.FromSeconds(60) ? "just now" : (int)age.TotalMinutes + " min ago") + ": " + last : string.Empty));
                }
                Log("status", running.Count == 0 ? "No part is running (waiting between steps or cycles)." : string.Join("\n", running));
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        /// <summary>Deletes the daily cycle logs and the cycle folders older than LogRetentionDays (0 = keep everything). All paths come from LogPaths.</summary>
        static void CleanOldLogs(Settings s)
        {
            if (s.LogRetentionDays <= 0) return;
            try
            {
                var limit = DateTime.Now.AddDays(-s.LogRetentionDays);
                if (!Directory.Exists(LogPaths.Root)) return;
                foreach (var file in Directory.GetFiles(LogPaths.Root, LogPaths.CycleLogPattern))
                    if (File.GetLastWriteTime(file) < limit) File.Delete(file);
                if (Directory.Exists(LogPaths.CyclesRoot))
                    foreach (var directory in Directory.GetDirectories(LogPaths.CyclesRoot))
                        if (Directory.GetLastWriteTime(directory) < limit) Directory.Delete(directory, true);
            }
            catch (Exception ex) { Log("WARNING", "Old logs could not be cleaned: " + ex.Message); }
        }

        /// <summary>Waits for the child; ends it when the watchdog time is over or the supervisor is cancelled. Returns its exit code (-1 when it was ended).</summary>
        static async Task<int> WaitAsync(Child child, TimeSpan watchdog, CancellationToken token)
        {
            var finished = await Task.WhenAny(child.Exit, Task.Delay(watchdog, token)).ConfigureAwait(false);
            if (finished == child.Exit) return child.Exit.Result;

            if (token.IsCancellationRequested) Log(child.Name, "Cancelled: this part is ended.");
            else Log("PROBLEM", child.Name + " ran longer than " + (int)watchdog.TotalMinutes + " minute(s) (watchdog) and is ended.");
            try { if (!child.Process.HasExited) child.Process.Kill(); } catch { }
            return -1;
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        static void EnsureCycleTable(string cs)
        {
            Exec(cs, @"
IF SCHEMA_ID(N'control') IS NULL EXEC(N'CREATE SCHEMA control AUTHORIZATION dbo;');
IF OBJECT_ID(N'control.CycleRun', N'U') IS NULL
    CREATE TABLE control.CycleRun
    (
        CycleID uniqueidentifier NOT NULL CONSTRAINT PK_CycleRun PRIMARY KEY,
        StartedAtUTC datetime2(7) NOT NULL CONSTRAINT DF_CycleRun_StartedAtUTC DEFAULT SYSUTCDATETIME(),
        IncrementalEndedAtUTC datetime2(7) NULL,
        IncrementalExitCode int NULL,
        DeleteStatus varchar(20) NULL,
        DeleteEndedAtUTC datetime2(7) NULL,
        PublishStartedAtUTC datetime2(7) NULL,
        PublishEndedAtUTC datetime2(7) NULL,
        PublishExitCode int NULL,
        Outcome varchar(60) NOT NULL,
        Notes nvarchar(1000) NULL,
        CompletedAtUTC datetime2(7) NULL
    );");
        }

        static void Exec(string cs, string sql, params SqlParameter[] parameters)
        {
            using (var c = new SqlConnection(cs))
            using (var cmd = new SqlCommand(sql, c) { CommandTimeout = 60 })
            {
                if (parameters.Length > 0) cmd.Parameters.AddRange(parameters);
                c.Open();
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>The cycle table is only a record for people: a failure to write it must never stop the cycle.</summary>
        static void Db(Action action)
        {
            try { action(); }
            catch (Exception ex) { Log("WARNING", "control.CycleRun could not be written: " + ex.Message); }
        }

        // ---- The cycle log is meant to be read by people. One line = "time  part  message". The part column has a fixed width, so the
        // ---- eye can follow one part down the file. A message of several lines continues under the message column.
        static readonly object LogLock = new object();
        const int PartColumnWidth = 12;
        const string Rule = "------------------------------------------------------------------------------";
        const string DoubleRule = "==============================================================================";

        /// <summary>A normal line of the supervisor ("cycle" column).</summary>
        static void Log(string message) { Log("cycle", message); }

        /// <summary>One line (or several, separated by new lines) for a part: supervisor, cycle, incremental, delete, publish, status, WARNING, PROBLEM.</summary>
        static void Log(string part, string message)
        {
            var prefix = DateTime.Now.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + (part ?? string.Empty).PadRight(PartColumnWidth) + "  ";
            var continuation = new string(' ', prefix.Length);
            var lines = (message ?? string.Empty).Replace("\r", string.Empty).Split('\n');
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < lines.Length; i++)
                text.AppendLine((i == 0 ? prefix : continuation) + lines[i].TrimEnd());
            Write(text.ToString());
        }

        /// <summary>Lines without a time or part (the cycle banner and the summary table).</summary>
        static void LogRaw(string text)
        {
            Write(text.Replace("\r", string.Empty).TrimEnd('\n') + Environment.NewLine);
        }

        // The file of the supervisor log: a NEW file for every supervisor start and for every following cycle (never one file for the whole day).
        static string _logPath;

        static string NewLogPath()
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var path = Path.Combine(LogPaths.Root, "CSP.Clio.StagingSync-cycle-" + stamp + ".txt");
            for (var n = 2; File.Exists(path); n++)
                path = Path.Combine(LogPaths.Root, "CSP.Clio.StagingSync-cycle-" + stamp + "-" + n + ".txt");
            return path;
        }

        static void Write(string text)
        {
            lock (LogLock)
            {
                Console.Write(text);
                try
                {
                    var path = _logPath ?? LogPaths.CycleLogFile(DateTime.Now);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    if (!File.Exists(path))
                    {
                        File.AppendAllText(path,
                            "CSP Clio Staging - cycle log started " + DateTime.Now.ToString("dddd, d MMMM yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) +
                            " (times are the local time of this server)" + Environment.NewLine +
                            "Columns: time, part, message.   Parts: supervisor, cycle, incremental, delete, publish, status (progress line), WARNING, PROBLEM." + Environment.NewLine +
                            "A cycle starts at a ==== banner and ends with a CYCLE SUMMARY table. The complete log of every part of a cycle is in " + LogPaths.CyclesRoot + "\\<cycle start>\\." + Environment.NewLine +
                            Environment.NewLine);
                    }
                    File.AppendAllText(path, text);
                }
                catch { }
            }
        }

        // ---------------------------------------------------------------------------------------------------------------------------------
        /// <summary>Windows job object with "kill on close": the child processes end when the supervisor ends, however it ends.</summary>
        sealed class ChildJob : IDisposable
        {
            IntPtr _handle;

            public static ChildJob TryCreate()
            {
                try
                {
                    var handle = CreateJobObject(IntPtr.Zero, null);
                    if (handle == IntPtr.Zero) return null;
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    var length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                    var pointer = Marshal.AllocHGlobal(length);
                    try
                    {
                        Marshal.StructureToPtr(info, pointer, false);
                        if (!SetInformationJobObject(handle, 9, pointer, (uint)length)) { CloseHandle(handle); return null; }
                    }
                    finally { Marshal.FreeHGlobal(pointer); }
                    return new ChildJob { _handle = handle };
                }
                catch { return null; }
            }

            public void Add(Process process)
            {
                try { AssignProcessToJobObject(_handle, process.Handle); } catch { }
            }

            public void Dispose()
            {
                if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
            }

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            static extern IntPtr CreateJobObject(IntPtr attributes, string name);
            [DllImport("kernel32.dll")]
            static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr handle);

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                public long PerProcessUserTimeLimit;
                public long PerJobUserTimeLimit;
                public uint LimitFlags;
                public UIntPtr MinimumWorkingSetSize;
                public UIntPtr MaximumWorkingSetSize;
                public uint ActiveProcessLimit;
                public UIntPtr Affinity;
                public uint PriorityClass;
                public uint SchedulingClass;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct IO_COUNTERS
            {
                public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
                public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
            }

            [StructLayout(LayoutKind.Sequential)]
            struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
                public IO_COUNTERS IoInfo;
                public UIntPtr ProcessMemoryLimit;
                public UIntPtr JobMemoryLimit;
                public UIntPtr PeakProcessMemoryUsed;
                public UIntPtr PeakJobMemoryUsed;
            }
        }
    }
}
