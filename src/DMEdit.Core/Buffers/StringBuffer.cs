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

    private static (long[] starts, int longestLine) BuildLineIndex(string data) {
        var starts = new List<long> { 0L };
        for (var i = 0; i < data.Length; i++) {
            var ch = data[i];
            if (ch == '\n') {
                starts.Add(i + 1);
            } else if (ch == '\r') {
                if (i + 1 < data.Length && data[i + 1] == '\n') {
                    starts.Add(i + 2);
                    i++; // skip \n of \r\n pair
                } else {
                    starts.Add(i + 1);
                }
            }
        }

        var maxLen = 0;
        for (var i = 1; i < starts.Count; i++) {
            var len = (int)(starts[i] - starts[i - 1]);
            if (len > maxLen) maxLen = len;
        }
        var lastLen = (int)(data.Length - starts[^1]);
        if (lastLen > maxLen) maxLen = lastLen;

        return (starts.ToArray(), maxLen);
    }
}
