using System;
using System.Configuration;
using System.Globalization;
using System.IO;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// THE ONLY PLACE that decides where a log file goes (shared by CSP.Clio.StagingSync.exe and CSP.Clio.StagingDelete.exe). Layout:
    ///
    ///   Logs\                                          App.config LogDirectory (empty = the folder "Logs" next to the EXE)
    ///     CSP.Clio.StagingSync-cycle-yyyyMMdd-HHmmss.txt   the cycle supervisor: a NEW FILE for every supervisor start and every following cycle
    ///     Cycles\yyyyMMdd-HHmmss\                      one folder per cycle (its start time)
    ///         incremental.txt, delete.txt, publish.txt   the COMPLETE log of each part of that cycle, written by the part itself
    ///         delete-attempt2.txt                        a retried delete pass
    ///     Manual\                                      parts started BY HAND (not by the cycle); each file is replaced by the next run
    ///         incremental-last-run.txt, delete-last-run.txt, full-check-last-run.txt, reconcile-deletes-last-run.txt, last-run.txt
    ///
    /// A part started by the cycle is told its file with the argument  --log-file "full path"  and then writes only there; without the argument it
    /// writes to Manual. So every log line exists in one place only (the cycle log holds a readable extract, the part log the complete text).
    /// </summary>
    internal static class LogPaths
    {
        public const string LogFileArgument = "--log-file";

        /// <summary>The log folder: App.config LogDirectory (absolute, or relative to the EXE folder), else "Logs" next to the EXE.</summary>
        public static string Root
        {
            get
            {
                var configured = (ConfigurationManager.AppSettings["LogDirectory"] ?? string.Empty).Trim();
                var folder = configured.Length == 0 ? "Logs" : configured;
                return Path.GetFullPath(Path.IsPathRooted(folder) ? folder : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, folder));
            }
        }

        public static string CyclesRoot { get { return Path.Combine(Root, "Cycles"); } }

        public static string ManualRoot { get { return Path.Combine(Root, "Manual"); } }

        public const string CycleLogPattern = "CSP.Clio.StagingSync-cycle-*.txt";

        /// <summary>The supervisor's log of a day.</summary>
        public static string CycleLogFile(DateTime day)
        {
            return Path.Combine(Root, "CSP.Clio.StagingSync-cycle-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".txt");
        }

        /// <summary>The folder of one cycle; <paramref name="stamp"/> is the start of the cycle as yyyyMMdd-HHmmss.</summary>
        public static string CycleFolder(string stamp)
        {
            return Path.Combine(CyclesRoot, stamp);
        }

        /// <summary>The log of a part that was started by hand, for example "incremental-last-run".</summary>
        public static string ManualLog(string name)
        {
            return Path.Combine(ManualRoot, name + ".txt");
        }

        /// <summary>The value of --log-file "path" in the arguments (null when the part was started by hand).</summary>
        public static string FromArguments(string[] args)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], LogFileArgument, StringComparison.OrdinalIgnoreCase) && args[i + 1].Trim().Length > 0)
                    return Path.GetFullPath(args[i + 1]);
            return null;
        }
    }
}
