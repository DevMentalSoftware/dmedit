using System.IO;

namespace DMEdit.App.Services;

/// <summary>
/// Tiny diagnostic file logger.  Writes one line per call to
/// <c>%TEMP%\dmedit_perf.log</c>.  Used during perf investigations to
/// capture per-frame phase timings without depending on the editor's
/// stats bar (which only updates when Render runs, masking starvation
/// scenarios) or on Trace.WriteLine (which doesn't auto-route to
/// OutputDebugString in modern .NET).
/// </summary>
/// <remarks>
/// Synchronous file I/O — fast enough for a few lines per insert.
/// Caller can tail with <c>Get-Content -Wait $env:TEMP\dmedit_perf.log</c>
/// from PowerShell, or just open the file after the test.  The file is
/// truncated at startup so each session starts clean.
/// </remarks>
public static class PerfLog {
    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "dmedit_perf.log");
    private static readonly object Gate = new();
    private static bool _initialized;

    public static void Write(string line) {
        try {
            lock (Gate) {
                if (!_initialized) {
                    File.WriteAllText(LogPath, $"--- session start {DateTime.Now:HH:mm:ss.fff} ---{Environment.NewLine}");
                    _initialized = true;
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
        } catch {
            // Best-effort — don't let logging crash the editor.
        }
    }
}
