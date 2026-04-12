namespace DMEdit.Core.JumpList;

/// <summary>
/// Platform-specific service for managing the OS taskbar jump list
/// (e.g., recent files shown when right-clicking the taskbar icon).
/// </summary>
public interface IJumpListService {
    /// <summary>
    /// Rebuilds the jump list with the given recent file paths.
    /// Each entry launches <paramref name="appExePath"/> with the file
    /// path as a command-line argument.
    /// </summary>
    void UpdateRecentFiles(IReadOnlyList<string> paths, string appExePath);

    /// <summary>Removes all items from the jump list.</summary>
    void Clear();
}
