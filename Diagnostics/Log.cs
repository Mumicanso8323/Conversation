namespace Conversation.Diagnostics;

using System.Diagnostics;
using System.IO;
using System.Text;

public static class Log {
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null, null);
    public static void Warn(string message) => Write("WARN", message, null, null);
    public static void Error(Exception ex, string? context = null) => Write("ERROR", context ?? "Unhandled exception", ex, context);

    private static void Write(string level, string message, Exception? ex, string? context) {
        try {
            var sb = new StringBuilder();
            sb.Append('[').Append(DateTimeOffset.UtcNow.ToString("O")).Append("] ")
              .Append(level).Append(" ").Append(message);
            if (!string.IsNullOrWhiteSpace(context)) {
                sb.Append(" | context=").Append(context);
            }

            if (ex is not null) {
                sb.AppendLine();
                sb.Append(ex);
            }

            var line = sb.ToString() + Environment.NewLine;
            lock (Gate) {
                try {
                    var dir = Path.GetDirectoryName(AppPaths.LogFilePath);
                    if (!string.IsNullOrWhiteSpace(dir)) {
                        Directory.CreateDirectory(dir);
                    }

                    File.AppendAllText(AppPaths.LogFilePath, line);
                }
                catch {
                    // swallow
                }
            }

            try {
                Debug.WriteLine(line);
            }
            catch {
                // swallow
            }
        }
        catch {
            // swallow
        }
    }
}
