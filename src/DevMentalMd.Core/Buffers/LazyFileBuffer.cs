using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace DevMentalMd.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> backed by a memory-mapped file, enabling O(1) random-access reads
/// on documents larger than available RAM.
/// </summary>
/// <remarks>
/// <para>
/// <b>Encoding limitation (Milestone 2):</b> This implementation treats the file as
/// UTF-16 LE (two bytes per char). Most real <c>.md</c> files are UTF-8.
/// <see cref="FileLoader"/> uses <c>File.ReadAllText</c> (which decodes UTF-8 correctly)
/// for files ≤ 50 MB; this class is only used for larger files, which must be UTF-16 LE.
/// A UTF-8 transcoding layer is deferred to a later milestone.
/// </para>
/// </remarks>
public sealed class LazyFileBuffer : IBuffer {
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly long _charLength;
    private bool _disposed;

    private LazyFileBuffer(MemoryMappedFile mmf, MemoryMappedViewAccessor view, long charLength) {
        _mmf = mmf;
        _view = view;
        _charLength = charLength;
    }

    /// <summary>
    /// Opens <paramref name="path"/> for memory-mapped read access.
    /// The file must be UTF-16 LE encoded (2 bytes per character, no BOM required).
    /// </summary>
    public static LazyFileBuffer Open(string path) {
        var byteLen = new FileInfo(path).Length;
        var charLen = byteLen / 2; // UTF-16 LE: 2 bytes per char
        var mmf = MemoryMappedFile.CreateFromFile(
            path,
            FileMode.Open,
            mapName: null,
            capacity: byteLen,
            access: MemoryMappedFileAccess.Read);
        var view = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        return new LazyFileBuffer(mmf, view, charLen);
    }

    // -------------------------------------------------------------------------
    // IBuffer
    // -------------------------------------------------------------------------

    public long Length => _charLength;

    public char this[long offset] {
        get {
            CheckDisposed();
            return (char)_view.ReadInt16(offset * 2);
        }
    }

    public void CopyTo(long offset, Span<char> destination, int len) {
        CheckDisposed();
        unsafe {
            byte* ptr = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
            try {
                var src = new ReadOnlySpan<char>((char*)(ptr + offset * 2), len);
                src.CopyTo(destination);
            } finally {
                _view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
        }
    }

    public void Dispose() {
        if (_disposed) {
            return;
        }
        _disposed = true;
        _view.Dispose();
        _mmf.Dispose();
    }

    // -------------------------------------------------------------------------
    // Internal helpers
    // -------------------------------------------------------------------------

    private void CheckDisposed() {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
