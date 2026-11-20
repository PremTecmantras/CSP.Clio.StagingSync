using System;
using System.IO;
using System.Text;

namespace CSP.Clio.Sync
{
    /// <summary>
    /// Mirrors all Console.Out and Console.Error messages to one replace-on-start run log.
    /// The log is deliberately outside the database so it remains available if SQL work fails.
    /// </summary>
    internal sealed class RunConsoleLogger : IDisposable
    {
        readonly TextWriter originalOut;
        readonly TextWriter originalError;
        readonly StreamWriter file;

        RunConsoleLogger(string logPath)
        {
            originalOut = Console.Out;
            originalError = Console.Error;
            // A second copy of the same command (for example one started by hand while the scheduled one runs) must not crash on the
            // locked log: share the file for reading, and fall back to a numbered name when it cannot be replaced.
            try { file = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true }; }
            catch (IOException)
            {
                logPath = Path.Combine(Path.GetDirectoryName(logPath), Path.GetFileNameWithoutExtension(logPath) + "-pid" + System.Diagnostics.Process.GetCurrentProcess().Id + Path.GetExtension(logPath));
                file = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
            }

            var writeLock = new object();
            Console.SetOut(new TeeTextWriter(originalOut, file, writeLock));
            Console.SetError(new TeeTextWriter(originalError, file, writeLock));
            Console.WriteLine("Run log: " + logPath);
            Console.WriteLine("Run started (local time): " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        /// <summary>
        /// Starts the log of this run. The path comes from LogPaths only: the file given with --log-file (a part started by the cycle), otherwise
        /// the "Manual" log called <paramref name="manualName"/> (replaced by the next run of the same part).
        /// </summary>
        public static RunConsoleLogger StartFor(string[] args, string manualName)
        {
            var path = LogPaths.FromArguments(args) ?? LogPaths.ManualLog(manualName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            return new RunConsoleLogger(path);
        }

        /// <summary>The manual log name of the main EXE for the command that is being started.</summary>
        public static string ManualNameFor(bool incremental, bool fullCheck, bool reconcile)
        {
            return reconcile ? "reconcile-deletes-last-run"
                : fullCheck ? "full-check-last-run"
                : incremental ? "incremental-last-run"
                : "last-run";
        }

        public void Dispose()
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            file.Dispose();
        }

        sealed class TeeTextWriter : TextWriter
        {
            readonly TextWriter console;
            readonly TextWriter file;
            readonly object writeLock;

            public TeeTextWriter(TextWriter console, TextWriter file, object writeLock)
            {
                this.console = console;
                this.file = file;
                this.writeLock = writeLock;
            }

            public override Encoding Encoding { get { return file.Encoding; } }

            public override void Write(char value)
            {
                lock (writeLock)
                {
                    console.Write(value);
                    file.Write(value);
                }
            }

            public override void Write(string value)
            {
                lock (writeLock)
                {
                    console.Write(value);
                    file.Write(value);
                }
            }

            public override void WriteLine(string value)
            {
                lock (writeLock)
                {
                    console.WriteLine(value);
                    file.WriteLine(value);
                }
            }

            public override void WriteLine()
            {
                lock (writeLock)
                {
                    console.WriteLine();
                    file.WriteLine();
                }
            }
        }
    }
}
