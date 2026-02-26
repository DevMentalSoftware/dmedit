namespace DevMentalMd.Core.Buffers;

/// <summary>
/// An <see cref="IBuffer"/> backed by a CLR <see langword="string"/>.
/// Used for documents loaded entirely into memory (≤ 50 MB).
/// </summary>
public sealed class StringBuffer(string data) : IBuffer {
    public long Length => data.Length;

    public char this[long offset] => data[(int)offset];

    public void CopyTo(long offset, Span<char> destination, int len) =>
        data.AsSpan((int)offset, len).CopyTo(destination);

    public void Dispose() { }
}
