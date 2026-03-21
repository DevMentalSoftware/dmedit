using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using DevMentalMd.Core.Documents;

namespace DevMentalMd.App.Services;

/// <summary>
/// Writes crash report files to the session directory for post-mortem debugging.
/// </summary>
public static class CrashReport {
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DMEdit", "session");

    /// <summary>
    /// Writes a crash report for a failed save operation.
    /// Returns the full path to the report file, or null if writing failed.
    /// </summary>
    public static async Task<string?> WriteAsync(Exception ex, string? filePath, Document? doc) {
        try {
            Directory.CreateDirectory(SessionDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var reportPath = Path.Combine(SessionDir, $"crash-{timestamp}.txt");
            var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";

            var content = $"""
                DMEdit Crash Report
                ========================
                Time:       {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}
                Version:    {version}
                Operation:  Save

                File
                ----
                Path:       {filePath ?? "(none)"}
                Lines:      {doc?.Table.LineCount.ToString() ?? "?"}
                Length:     {doc?.Table.Length.ToString() ?? "?"} chars
                Buffer:     {doc?.Table.Buffer?.GetType().Name ?? "?"}
                Encoding:   {doc?.EncodingInfo.ToString() ?? "?"}

                Exception
                ---------
                Type:       {ex.GetType().FullName}
                Message:    {ex.Message}

                Stack Trace
                -----------
                {ex.StackTrace}
                """;

            if (ex.InnerException is { } inner) {
                content += $"""


                    Inner Exception
                    ---------------
                    Type:       {inner.GetType().FullName}
                    Message:    {inner.Message}

                    Stack Trace
                    -----------
                    {inner.StackTrace}
                    """;
            }

            await File.WriteAllTextAsync(reportPath, content);
            return reportPath;
        } catch {
            return null;
        }
    }
}
