using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using DMEdit.Core.Documents;

namespace DMEdit.App.Services;

/// <summary>
/// Writes crash report files to the session directory for post-mortem debugging.
/// </summary>
public static class CrashReport {
    private static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DMEdit", "session");

    /// <summary>
    /// Writes a crash report for a failed operation with document context.
    /// Returns the full path to the report file, or null if writing failed.
    /// </summary>
    public static async Task<string?> WriteAsync(
        Exception ex, string operation, string? filePath = null, Document? doc = null) {
        try {
            Directory.CreateDirectory(SessionDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var reportPath = Path.Combine(SessionDir, $"crash-{timestamp}.txt");
            var content = FormatReport(ex, operation, filePath, doc);
            await File.WriteAllTextAsync(reportPath, content);
            return reportPath;
        } catch (Exception writeEx) {
            Debug.WriteLine($"CrashReport.WriteAsync failed: {writeEx.Message}");
            return null;
        }
    }

    /// <summary>
    /// Synchronous crash report writer for use in global exception handlers
    /// where async may not be safe.
    /// </summary>
    public static string? Write(Exception ex, string operation) {
        try {
            Directory.CreateDirectory(SessionDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var reportPath = Path.Combine(SessionDir, $"crash-{timestamp}.txt");
            var content = FormatReport(ex, operation);
            File.WriteAllText(reportPath, content);
            return reportPath;
        } catch (Exception writeEx) {
            Debug.WriteLine($"CrashReport.Write failed: {writeEx.Message}");
            return null;
        }
    }

    private static string FormatReport(
        Exception ex, string operation,
        string? filePath = null, Document? doc = null) {

        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        var sb = new StringBuilder();

        sb.AppendLine("DMEdit Crash Report");
        sb.AppendLine("========================");
        sb.AppendLine($"Time:       {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        sb.AppendLine($"Version:    {version}");
        sb.AppendLine($"Operation:  {operation}");
        sb.AppendLine();

        if (filePath is not null || doc is not null) {
            sb.AppendLine("File");
            sb.AppendLine("----");
            sb.AppendLine($"Path:       {filePath ?? "(none)"}");
            sb.AppendLine($"Lines:      {doc?.Table.LineCount.ToString() ?? "?"}");
            sb.AppendLine($"Length:     {doc?.Table.Length.ToString() ?? "?"} chars");
            sb.AppendLine($"Buffer:     {doc?.Table.Buffer?.GetType().Name ?? "?"}");
            sb.AppendLine($"Encoding:   {doc?.EncodingInfo.ToString() ?? "?"}");
            sb.AppendLine();
        }

        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        sb.AppendLine($"Type:       {ex.GetType().FullName}");
        sb.AppendLine($"Message:    {ex.Message}");
        sb.AppendLine();
        sb.AppendLine("Stack Trace");
        sb.AppendLine("-----------");
        sb.AppendLine(ex.StackTrace);

        if (ex.InnerException is { } inner) {
            sb.AppendLine();
            sb.AppendLine("Inner Exception");
            sb.AppendLine("---------------");
            sb.AppendLine($"Type:       {inner.GetType().FullName}");
            sb.AppendLine($"Message:    {inner.Message}");
            sb.AppendLine();
            sb.AppendLine("Stack Trace");
            sb.AppendLine("-----------");
            sb.AppendLine(inner.StackTrace);
        }

        return sb.ToString();
    }
}
