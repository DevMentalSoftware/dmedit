using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DMEdit.Core.Clipboard;
using DMEdit.Core.Documents;

namespace DMEdit.Print.Windows;

/// <summary>
/// Windows clipboard service using Win32 P/Invoke. Copies text directly from
/// piece table buffers into a native HGLOBAL (zero managed string allocation).
/// Pastes by streaming from locked HGLOBAL into the piece table's add buffer.
/// </summary>
public sealed class NativeClipboardService : INativeClipboardService {
    private const uint CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    public unsafe bool Copy(PieceTable table, long start, long len) {
        if (len <= 0 || len > int.MaxValue) return false;
        var charCount = (int)len;
        var byteSize = (nuint)((charCount + 1) * sizeof(char)); // +1 for null terminator

        if (!OpenClipboard(IntPtr.Zero)) return false;
        try {
            EmptyClipboard();
            var hGlobal = GlobalAlloc(GMEM_MOVEABLE, byteSize);
            if (hGlobal == IntPtr.Zero) return false;

            var ptr = (char*)GlobalLock(hGlobal);
            if (ptr == null) {
                GlobalFree(hGlobal);
                return false;
            }
            try {
                var span = new Span<char>(ptr, charCount);
                table.CopyTo(start, charCount, span);
                ptr[charCount] = '\0'; // null terminator
            } finally {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CF_UNICODETEXT, hGlobal) == IntPtr.Zero) {
                GlobalFree(hGlobal);
                return false;
            }
            // SetClipboardData took ownership of hGlobal — do NOT free it.
            return true;
        } finally {
            CloseClipboard();
        }
    }

    public unsafe long Paste(PieceTable table, Action<long, long>? progress,
        CancellationToken cancel) {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return -1;
        if (!OpenClipboard(IntPtr.Zero)) return -1;
        try {
            var hGlobal = GetClipboardData(CF_UNICODETEXT);
            if (hGlobal == IntPtr.Zero) return -1;

            var ptr = (char*)GlobalLock(hGlobal);
            if (ptr == null) return -1;
            try {
                // Determine char count from GlobalSize (bytes) / sizeof(char),
                // then find actual text length (exclude null terminator).
                var byteSize = (long)GlobalSize(hGlobal);
                var maxChars = (int)(byteSize / sizeof(char));
                var charCount = maxChars;
                for (var i = 0; i < maxChars; i++) {
                    if (ptr[i] == '\0') { charCount = i; break; }
                }
                if (charCount == 0) return 0;

                // Stream in chunks to avoid large single Span<char> operations.
                const int chunkSize = 1024 * 1024; // 1M chars
                var offset = 0;
                while (offset < charCount) {
                    cancel.ThrowIfCancellationRequested();
                    var take = Math.Min(chunkSize, charCount - offset);
                    var chunk = new ReadOnlySpan<char>(ptr + offset, take);
                    table.AppendToAddBuffer(chunk);
                    offset += take;
                    progress?.Invoke(offset, charCount);
                }
                return charCount;
            } finally {
                GlobalUnlock(hGlobal);
            }
        } finally {
            CloseClipboard();
        }
    }

    public unsafe long GetClipboardCharCount() {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return -1;
        if (!OpenClipboard(IntPtr.Zero)) return -1;
        try {
            var hGlobal = GetClipboardData(CF_UNICODETEXT);
            if (hGlobal == IntPtr.Zero) return -1;
            var ptr = (char*)GlobalLock(hGlobal);
            if (ptr == null) return -1;
            try {
                var byteSize = (long)GlobalSize(hGlobal);
                var maxChars = (int)(byteSize / sizeof(char));
                var charCount = maxChars;
                for (var i = 0; i < maxChars; i++) {
                    if (ptr[i] == '\0') { charCount = i; break; }
                }
                return charCount;
            } finally {
                GlobalUnlock(hGlobal);
            }
        } finally {
            CloseClipboard();
        }
    }

    public unsafe long PasteToStream(Stream stream, Action<long, long>? progress,
        CancellationToken cancel) {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return -1;
        if (!OpenClipboard(IntPtr.Zero)) return -1;
        try {
            var hGlobal = GetClipboardData(CF_UNICODETEXT);
            if (hGlobal == IntPtr.Zero) return -1;
            var ptr = (char*)GlobalLock(hGlobal);
            if (ptr == null) return -1;
            try {
                var byteSize = (long)GlobalSize(hGlobal);
                var maxChars = (int)(byteSize / sizeof(char));
                var charCount = maxChars;
                for (var i = 0; i < maxChars; i++) {
                    if (ptr[i] == '\0') { charCount = i; break; }
                }
                if (charCount == 0) return 0;

                // Encode UTF-16 chars to UTF-8 and write to stream.
                var encoder = Encoding.UTF8.GetEncoder();
                const int chunkChars = 1024 * 1024;
                var byteBuf = new byte[chunkChars * 3]; // worst case 3 bytes/char
                var offset = 0;
                while (offset < charCount) {
                    cancel.ThrowIfCancellationRequested();
                    var take = Math.Min(chunkChars, charCount - offset);
                    var chars = new ReadOnlySpan<char>(ptr + offset, take);
                    var flush = offset + take >= charCount;
                    encoder.Convert(chars, byteBuf, flush,
                        out _, out var bytesUsed, out _);
                    stream.Write(byteBuf, 0, bytesUsed);
                    offset += take;
                    progress?.Invoke(offset, charCount);
                }
                return charCount;
            } finally {
                GlobalUnlock(hGlobal);
            }
        } finally {
            CloseClipboard();
        }
    }

    // Win32 P/Invoke declarations
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, nuint dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(IntPtr hMem);
}
