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

        public static RunConsoleLogger Start(bool incremental, bool fullCheck = false, bool reconcile = false, bool deleteSync = false)
        {
            var logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
            Directory.CreateDirectory(logDirectory);

            // FileMode.Create (used by StreamWriter) replaces the previous run's log.
            var fileName = deleteSync
                ? "CSP.Clio.StagingSync-delete-sync-last-run.txt"
                : reconcile
                ? "CSP.Clio.StagingSync-reconcile-deletes-last-run.txt"
                : fullCheck
                ? "CSP.Clio.StagingSync-full-check-last-run.txt"
                : incremental
                    ? "CSP.Clio.StagingSync-incremental-last-run.txt"
                    : "CSP.Clio.StagingSync-last-run.txt";
            return new RunConsoleLogger(Path.Combine(logDirectory, fileName));
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
