using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace DMEdit.App.Services;

/// <summary>
/// Ensures only one instance of DMEdit runs at a time. When a second instance
/// starts (e.g. Explorer context menu on multiple selected files), it sends
/// its file path to the first instance via a named pipe, then exits.
/// </summary>
internal sealed class SingleInstanceService : IDisposable {
    private const string MutexName = "DMEdit-SingleInstance-A7F3C";
    private const string PipeName = "DMEdit-FileOpen-A7F3C";

    private readonly Mutex _mutex;
    private readonly bool _isOwner;
    private CancellationTokenSource? _cts;

    public SingleInstanceService() {
        _mutex = new Mutex(true, MutexName, out _isOwner);
    }

    /// <summary>True if this process is the first (owning) instance.</summary>
    public bool IsOwner => _isOwner;

    /// <summary>
    /// Raised on the calling thread's context when another instance sends a
    /// file path. The handler receives the absolute file path.
    /// </summary>
    public event Action<string>? FileRequested;

    /// <summary>
    /// Sends a file path to the owning instance, then returns.
    /// Called by secondary instances before they exit.
    /// </summary>
    public static void SendToOwner(string filePath) {
        try {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000); // 3 s timeout
            using var writer = new StreamWriter(client) { AutoFlush = true };
            writer.WriteLine(filePath);
        } catch {
            // If the pipe isn't available, fall through — the file simply
            // won't open. This is best-effort.
        }
    }

    /// <summary>
    /// Starts listening for file-open requests from secondary instances.
    /// Must be called by the owning instance.
    /// </summary>
    public void StartListening() {
        if (!_isOwner) {
            return;
        }
        _cts = new CancellationTokenSource();
        _ = ListenLoopAsync(_cts.Token);
    }

    private async Task ListenLoopAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                try {
                    using var reader = new StreamReader(server);
                    var line = await reader.ReadLineAsync(ct);
                    if (!string.IsNullOrWhiteSpace(line)) {
                        FileRequested?.Invoke(line);
                    }
                } finally {
                    server.Dispose();
                }
            } catch (OperationCanceledException) {
                break;
            } catch {
                // Pipe error — wait briefly then retry.
                try { await Task.Delay(100, ct); } catch { break; }
            }
        }
    }

    public void Dispose() {
        _cts?.Cancel();
        _cts?.Dispose();
        if (_isOwner) {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}
