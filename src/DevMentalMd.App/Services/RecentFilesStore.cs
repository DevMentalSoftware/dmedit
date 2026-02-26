using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevMentalMd.App.Services;

public sealed class RecentFilesStore {
    private const int MaxEntries = 10;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DevMentalMD", "recentfiles.json");

    private readonly List<string> _paths;

    private RecentFilesStore(List<string> paths) => _paths = paths;

    public IReadOnlyList<string> Paths => _paths;

    public static RecentFilesStore Load() {
        try {
            if (File.Exists(StorePath)) {
                var json = File.ReadAllText(StorePath);
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list is not null) {
                    return new RecentFilesStore(list);
                }
            }
        } catch {
            // Corrupted or unreadable — start fresh.
        }
        return new RecentFilesStore([]);
    }

    /// <summary>Adds <paramref name="path"/> to the front, deduplicates, and trims to 10.</summary>
    public void Push(string path) {
        _paths.Remove(path);
        _paths.Insert(0, path);
        if (_paths.Count > MaxEntries) {
            _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
        }
    }

    /// <summary>Removes a path (e.g. when the file is found to no longer exist on disk).</summary>
    public void Remove(string path) => _paths.Remove(path);

    /// <summary>Persists the list. Failures are silently swallowed (best-effort).</summary>
    public void Save() {
        try {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_paths));
        } catch {
            // Best-effort — non-fatal if the OS won't let us write.
        }
    }
}
