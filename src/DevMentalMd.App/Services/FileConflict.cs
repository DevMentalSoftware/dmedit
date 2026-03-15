namespace DevMentalMd.App.Services;

/// <summary>
/// Describes why a file is in conflict with the version on disk.
/// </summary>
public enum FileConflictKind {
    /// <summary>The file no longer exists on disk.</summary>
    Missing,
    /// <summary>The file has been modified externally since it was last loaded or saved.</summary>
    Changed,
}

/// <summary>
/// Records a detected conflict between the editor's version of a file and
/// what is on disk. Created during session restore or by the file watcher
/// at runtime.
/// </summary>
public sealed class FileConflict {
    public required FileConflictKind Kind { get; init; }
    public required string FilePath { get; init; }
    public required string? ExpectedSha1 { get; init; }
    public string? ActualSha1 { get; init; }
}
