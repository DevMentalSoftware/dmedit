namespace DevMentalMd.App;

/// <summary>
/// The user's chosen resolution for a file conflict.
/// </summary>
public enum FileConflictChoice {
    /// <summary>Load the current disk version (discard edits).</summary>
    LoadDiskVersion,
    /// <summary>Locate the file via a file dialog (for missing files).</summary>
    LocateFile,
    /// <summary>Keep the editor's version, ignoring the disk change.</summary>
    KeepMyVersion,
    /// <summary>Close the tab (discard everything).</summary>
    Discard,
}
