using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DMEdit.Core.Clipboard;
using DMEdit.Core.Documents;

namespace DMEdit.App.Services;

/// <summary>
/// Linux clipboard service using <c>xclip</c>/<c>xsel</c> (X11) or
/// <c>wl-copy</c>/<c>wl-paste</c> (Wayland) via process piping.
/// Copy streams piece data directly to the process stdin (true streaming,
/// zero managed string allocation). Paste streams from stdout into the
/// piece table's add buffer.
/// </summary>
public sealed class LinuxClipboardService : INativeClipboardService {
    private readonly bool _isWayland;
    private readonly string _copyTool;
    private readonly string _copyArgs;
    private readonly string _pasteTool;
    private readonly string _pasteArgs;

    private LinuxClipboardService(bool isWayland,
        string copyTool, string copyArgs,
        string pasteTool, string pasteArgs) {
        _isWayland = isWayland;
        _copyTool = copyTool;
        _copyArgs = copyArgs;
        _pasteTool = pasteTool;
        _pasteArgs = pasteArgs;
    }

    /// <summary>
    /// Attempts to create a Linux clipboard service. Returns null if not on
    /// Linux or no supported clipboard tool is found.
    /// </summary>
    public static LinuxClipboardService? TryCreate() {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return null;

        var isWayland = !string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

        if (isWayland) {
            if (ToolExists("wl-copy") && ToolExists("wl-paste")) {
                return new LinuxClipboardService(true,
                    "wl-copy", "",
                    "wl-paste", "--no-newline");
            }
        }

        // Fall back to X11 tools even on Wayland (XWayland)
        if (ToolExists("xclip")) {
            return new LinuxClipboardService(false,
                "xclip", "-i -selection clipboard",
                "xclip", "-o -selection clipboard");
        }
        if (ToolExists("xsel")) {
            return new LinuxClipboardService(false,
                "xsel", "--clipboard --input",
                "xsel", "--clipboard --output");
        }

        return null;
    }

    public bool Copy(PieceTable table, long start, long len) {
        if (len <= 0) return false;
        try {
            using var proc = StartProcess(_copyTool, _copyArgs, redirectInput: true);
            if (proc == null) return false;

            var stdin = proc.StandardInput.BaseStream;
            var encoder = Encoding.UTF8.GetEncoder();
            var byteBuf = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024); // 4 MB
            try {
                table.ForEachPiece(start, len, span => {
                    // Encode UTF-16 span to UTF-8 and write to stdin.
                    var charIndex = 0;
                    while (charIndex < span.Length) {
                        var remaining = span[charIndex..];
                        encoder.Convert(remaining, byteBuf,
                            flush: false, out var charsUsed,
                            out var bytesUsed, out _);
                        stdin.Write(byteBuf, 0, bytesUsed);
                        charIndex += charsUsed;
                    }
                });
                // Flush encoder
                encoder.Convert(ReadOnlySpan<char>.Empty, byteBuf,
                    flush: true, out _, out var finalBytes, out _);
                if (finalBytes > 0) stdin.Write(byteBuf, 0, finalBytes);
            } finally {
                ArrayPool<byte>.Shared.Return(byteBuf);
            }

            stdin.Close();
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        } catch {
            return false;
        }
    }

    public long Paste(PieceTable table, Action<long, long>? progress,
        CancellationToken cancel) {
        try {
            using var proc = StartProcess(_pasteTool, _pasteArgs, redirectOutput: true);
            if (proc == null) return -1;

            var stdout = proc.StandardOutput.BaseStream;
            var byteBuf = ArrayPool<byte>.Shared.Rent(1024 * 1024); // 1 MB
            long totalChars = 0;
            try {
                int bytesRead;
                while ((bytesRead = stdout.Read(byteBuf, 0, byteBuf.Length)) > 0) {
                    cancel.ThrowIfCancellationRequested();
                    // Feed raw UTF-8 bytes directly into the chunked buffer,
                    // avoiding the UTF-8 → UTF-16 → UTF-8 round-trip.
                    var before = table.AddBufferLength;
                    table.AddBuffer.AppendUtf8(byteBuf.AsSpan(0, bytesRead));
                    totalChars += table.AddBufferLength - before;
                    // Total is unknown for streaming; report bytes as estimate.
                    progress?.Invoke(totalChars, 0);
                }
            } finally {
                ArrayPool<byte>.Shared.Return(byteBuf);
            }

            proc.WaitForExit(5000);
            return totalChars;
        } catch (OperationCanceledException) {
            return -1;
        } catch {
            return -1;
        }
    }

    private static Process? StartProcess(string tool, string args,
        bool redirectInput = false, bool redirectOutput = false) {
        try {
            return Process.Start(new ProcessStartInfo {
                FileName = tool,
                Arguments = args,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = redirectOutput,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        } catch {
            return null;
        }
    }

    private static bool ToolExists(string name) {
        try {
            using var proc = Process.Start(new ProcessStartInfo {
                FileName = "which",
                Arguments = name,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc == null) return false;
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        } catch {
            return false;
        }
    }
}
