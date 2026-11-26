namespace RevitAgent.Bridge.Runtime
{
    using System;
    using System.IO;
    using System.Text;
    using System.Threading;
    using RevitAgent.Contracts.Bridge.Wire;

    /// <summary>
    /// Size-capped diagnostic log at <c>%LOCALAPPDATA%\RevitAgent\logs\bridge-&lt;pid&gt;.log</c>
    /// (root overridable with REVITAGENT_HOME like the gateway). Rotates to <c>.1.log</c> at 2 MB.
    /// Lines are clipped to 2 KB, so payloads are never logged in full. Never throws.
    /// </summary>
    internal sealed class BridgeLog : IBridgeLog
    {
        private const long MaxBytes = 2 * 1024 * 1024;
        private const int MaxMessageChars = 2048;
        private const int MaxExceptionChars = 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        private readonly object _gate = new object();

        public BridgeLog(int pid)
        {
            LogDir = Path.Combine(AgentRoot(), "logs");
            FilePath = Path.Combine(LogDir, "bridge-" + pid + ".log");
        }

        public string LogDir { get; }

        public string FilePath { get; }

        public static string AgentRoot()
        {
            var home = Environment.GetEnvironmentVariable("REVITAGENT_HOME");
            return !string.IsNullOrWhiteSpace(home)
                ? home!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAgent");
        }

        public void Info(string message) => Write("INFO", message, null);

        public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

        /// <summary>Deletes bridge logs older than <paramref name="maxAgeDays"/> days (best effort).</summary>
        public void CleanupOldLogs(int maxAgeDays = 14)
        {
            try
            {
                var cutoff = DateTime.Now.AddDays(-maxAgeDays);
                foreach (var file in Directory.GetFiles(LogDir, "bridge-*.log"))
                {
                    if (!string.Equals(file, FilePath, StringComparison.OrdinalIgnoreCase) && File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception)
            {
                // best effort
            }
        }

        private void Write(string level, string message, Exception? exception)
        {
            try
            {
                var sb = new StringBuilder(256);
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                    .Append(" [").Append(level).Append("] [t")
                    .Append(Thread.CurrentThread.ManagedThreadId).Append("] ")
                    .Append(Clip(message ?? string.Empty, MaxMessageChars));
                if (exception != null)
                {
                    sb.Append(" | ").Append(Clip(exception.ToString(), MaxExceptionChars));
                }

                sb.Append(Environment.NewLine);
                lock (_gate)
                {
                    Directory.CreateDirectory(LogDir);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var rotated = Path.Combine(LogDir, Path.GetFileNameWithoutExtension(FilePath) + ".1.log");
                        File.Delete(rotated);
                        File.Move(FilePath, rotated);
                    }

                    File.AppendAllText(FilePath, sb.ToString(), Utf8);
                }
            }
            catch (Exception)
            {
                // Logging must never break Revit.
            }
        }

        private static string Clip(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...(clipped " + (s.Length - max) + " chars)";
    }
}
