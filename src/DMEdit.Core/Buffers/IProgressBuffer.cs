using DMEdit.Core.Documents;

namespace DMEdit.Core.Buffers;

/// <summary>
/// Common surface for buffers that load their content asynchronously on a
/// background thread.  Both <see cref="PagedFileBuffer"/> and
/// <see cref="StreamingFileBuffer"/> implement this so the file loader can
/// drive them through a single uniform code path (see
/// <c>FileLoader.RegisterAsyncBuffer</c>).
/// </summary>
public interface IProgressBuffer : IBuffer {
    /// <summary>Fired after each chunk is decoded (on the background thread).</summary>
    public event Action? ProgressChanged;

    /// <summary>Fired once when loading finishes (on the background thread).</summary>
    public event Action? LoadComplete;

    /// <summary>
    /// Line ending info accumulated during the background scan.
    /// Accurate only after <see cref="LoadComplete"/> fires; before that,
    /// reflects whatever has been scanned so far.
    /// </summary>
    LineEndingInfo DetectedLineEnding { get; }

    /// <summary>
    /// Indent style detected during the scan.  Same staleness rules as
    /// <see cref="DetectedLineEnding"/>.
    /// </summary>
    IndentInfo DetectedIndent { get; }

    /// <summary>
    /// Encoding (and BOM presence) detected during the scan.  Same staleness
    /// rules as <see cref="DetectedLineEnding"/>.
    /// </summary>
    EncodingInfo DetectedEncoding { get; }

    /// <summary>
    /// SHA-1 hash of the original source bytes (lowercase hex).  <c>null</c>
    /// until <see cref="LoadComplete"/> fires.
    /// </summary>
    string? Sha1 { get; }

    /// <summary>
    /// Non-null if the background scan terminated with an error.  Available
    /// after <see cref="LoadComplete"/> fires.
    /// </summary>
    Exception? ScanError { get; }

    /// <summary>
    /// Starts reading on a thread-pool worker.  Returns immediately; the
    /// first chunk fires <see cref="ProgressChanged"/> within milliseconds.
    /// </summary>
    void StartLoading(CancellationToken externalCt = default);
}
