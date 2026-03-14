using DevMentalMd.Core.Documents;

namespace DevMentalMd.Core.Buffers;

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
}
