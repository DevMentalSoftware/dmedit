using DMEdit.Core.Documents;

namespace DMEdit.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> backed by a CLR <see langword="string"/>.
/// Used by tests via <c>PieceTable(string)</c> and <c>Document(string)</c>.
/// Not part of the public API — production code uses <see cref="PagedFileBuffer"/>
/// or <see cref="PieceTable()"/> (empty document).
/// </summary>
internal sealed class StringBuffer : IBuffer {
    private readonly string _data;
    private readonly long[] _lineStarts;
    private readonly int _longestLine;

    public StringBuffer(string data) {
        _data = data;
        (_lineStarts, _longestLine) = BuildLineIndex(data);
    }

    public long Length => _data.Length;

    public char this[long offset] => _data[(int)offset];

    public void CopyTo(long offset, Span<char> destination, int len) =>
        _data.AsSpan((int)offset, len).CopyTo(destination);

    public long LineCount => _lineStarts.Length;

    public int LongestLine => _longestLine;

    public long GetLineStart(long lineIdx) {
        if (lineIdx < 0 || lineIdx >= _lineStarts.Length) return -1L;
        return _lineStarts[lineIdx];
    }

    public void Dispose() { }

    /// <summary>
    /// Builds a line-starts array and computes the longest-line length by
    /// delegating to <see cref="LineScanner"/> — the canonical CR/LF/CRLF
    /// state machine in Core.  Line starts are derived from the scanner's
    /// line-length output via prefix sum.
    /// </summary>
    private static (long[] starts, int longestLine) BuildLineIndex(string data) {
        var scanner = new LineScanner();
        scanner.Scan(data.AsSpan());
        scanner.Finish();

        var lengths = scanner.LineLengths;
        var starts = new long[lengths.Count];
        var cum = 0L;
        for (var i = 0; i < lengths.Count; i++) {
            starts[i] = cum;
            cum += lengths[i];
        }
        return (starts, scanner.LongestLine);
    }
}
