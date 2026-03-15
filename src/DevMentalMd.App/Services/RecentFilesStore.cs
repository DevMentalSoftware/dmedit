using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DevMentalMd.App.Services;

public sealed class RecentFilesStore {
    private const int MaxEntries = 25;

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

    /// <summary>
    /// Removes local-only entries whose files no longer exist on disk.
    /// Network paths (UNC shares and mapped network drives) are left alone
    /// because the server may simply be unreachable right now — those get
    /// pruned lazily when the user tries to open them and the open fails.
    /// Returns <c>true</c> if any entries were removed (caller should
    /// rebuild the menu).
    /// </summary>
    public bool PruneMissing() {
        var before = _paths.Count;
        _paths.RemoveAll(p => IsLocalPath(p) && !File.Exists(p));
        if (_paths.Count < before) {
            Save();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Returns <c>true</c> if <paramref name="path"/> is on a local drive
    /// (not a UNC share or mapped network drive). Unknown or inaccessible
    /// drives are treated as non-local to err on the side of keeping them.
    /// </summary>
    internal static bool IsLocalPath(string path) {
        // Long-path UNC: \\?\UNC\server\share\... — network.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        // Strip Win32 extended-length (\\?\) and device (\\.\) prefixes.
        // After this, a local long path like \\?\C:\... becomes C:\...
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal)) {
            path = path[4..];
        }
        // UNC paths (\\server\share\...) are always network.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) {
            return false;
        }
        // Drive-letter paths: check DriveInfo.DriveType.
        var root = Path.GetPathRoot(path);
        if (root is { Length: >= 2 } && root[1] == ':') {
            try {
                var info = new DriveInfo(root[..2]);
                return info.DriveType != DriveType.Network;
            } catch {
                return false; // Unknown — assume non-local to be safe.
            }
        }
        // Unix absolute paths (/home/...) — local by definition.
        // (Network mounts like NFS/CIFS appear as local paths on Linux;
        // detecting those requires stat/statfs which isn't worth the cost.)
        if (path.StartsWith('/')) {
            return true;
        }
        return false; // Relative or unrecognizable — leave it alone.
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
