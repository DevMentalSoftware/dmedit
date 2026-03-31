using System.Collections.Generic;

namespace DMEdit.App.Services;

/// <summary>
/// Fixed-capacity ring buffer of clipboard text entries. Index 0 is the most
/// recently pushed item. Duplicates are removed on push so each entry appears
/// at most once.
/// </summary>
public sealed class ClipboardRing {
    private readonly List<string> _entries = new();

    public int MaxSize { get; set; } = 10;

    /// <summary>
    /// Maximum character count for a single ring entry. Entries larger than
    /// this are silently dropped to avoid holding large strings in memory.
    /// The system clipboard still has the text for immediate paste.
    /// </summary>
    public int MaxEntryChars { get; set; } = 500;

    public int Count => _entries.Count;

    public IReadOnlyList<string> Entries => _entries;

    /// <summary>
    /// Adds <paramref name="text"/> to the front of the ring. If the same text
    /// already exists it is moved to the front. The ring is trimmed to
    /// <see cref="MaxSize"/>.
    /// </summary>
    public void Push(string text) {
        if (string.IsNullOrEmpty(text)) return;
        if (text.Length > MaxEntryChars) return;
        _entries.Remove(text);
        _entries.Insert(0, text);
        while (_entries.Count > MaxSize) {
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    /// <summary>
    /// Returns the entry at <paramref name="index"/> (0 = most recent), or
    /// null if the index is out of range.
    /// </summary>
    public string? Get(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index] : null;
}
