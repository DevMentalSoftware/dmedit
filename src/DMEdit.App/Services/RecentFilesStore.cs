using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DMEdit.App.Services;

public sealed class RecentFilesStore {
    private const int MaxEntries = 25;

    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DMEdit", "recentfiles.json");

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly List<string> _pinnedPaths;
    private readonly HashSet<string> _pinnedSet;
    private readonly List<string> _paths;

    private RecentFilesStore(List<string> pinnedPaths, List<string> paths) {
        _pinnedPaths = pinnedPaths;
        _pinnedSet = new HashSet<string>(pinnedPaths, PathComparer);
        _paths = paths;
    }

    /// <summary>All paths: pinned first, then unpinned (for jump list etc.).</summary>
    public IReadOnlyList<string> Paths {
        get {
            var result = new List<string>(_pinnedPaths.Count + _paths.Count);
            result.AddRange(_pinnedPaths);
            result.AddRange(_paths);
            return result;
        }
    }

    public IReadOnlyList<string> PinnedPaths => _pinnedPaths;
    public IReadOnlyList<string> UnpinnedPaths => _paths;

    public bool IsPinned(string path) => _pinnedSet.Contains(path);

    public void Pin(string path) {
        if (!_pinnedSet.Add(path)) return;
        // Remove from unpinned if present.
        _paths.RemoveAll(p => PathComparer.Equals(p, path));
        _pinnedPaths.Add(path);
    }

    public void Unpin(string path) {
        if (!_pinnedSet.Remove(path)) return;
        _pinnedPaths.RemoveAll(p => PathComparer.Equals(p, path));
        // Insert at front of unpinned (it was important enough to pin).
        _paths.Insert(0, path);
    }

    // -----------------------------------------------------------------
    // Persistence DTO — new JSON format is an object with two arrays.
    // Old format was a bare JSON array (just the paths list).
    // -----------------------------------------------------------------

    private sealed class StoreData {
        public List<string>? Pinned { get; set; }
        public List<string>? Recent { get; set; }
    }

    public static RecentFilesStore Load() {
        try {
            if (File.Exists(StorePath)) {
                var json = File.ReadAllText(StorePath);
                if (string.IsNullOrWhiteSpace(json)) {
                    return new RecentFilesStore([], []);
                }
                // Try new object format first.
                if (json.TrimStart().StartsWith('{')) {
                    var data = JsonSerializer.Deserialize<StoreData>(json);
                    if (data is not null) {
                        return new RecentFilesStore(
                            data.Pinned ?? [],
                            data.Recent ?? []);
                    }
                }
                // Fall back to old bare-array format.
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list is not null) {
                    return new RecentFilesStore([], list);
                }
            }
        } catch {
            // Corrupted or unreadable — start fresh.
        }
        return new RecentFilesStore([], []);
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
        var before = _paths.Count + _pinnedPaths.Count;
        _paths.RemoveAll(p => IsLocalPath(p) && !File.Exists(p));
        var removedPinned = _pinnedPaths.RemoveAll(p => IsLocalPath(p) && !File.Exists(p));
        if (removedPinned > 0) {
            _pinnedSet.Clear();
            foreach (var p in _pinnedPaths) _pinnedSet.Add(p);
        }
        if (_paths.Count + _pinnedPaths.Count < before) {
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

    /// <summary>Adds <paramref name="path"/> to the front of the unpinned list (no-op if pinned).</summary>
    public void Push(string path) {
        if (_pinnedSet.Contains(path)) return;
        _paths.RemoveAll(p => PathComparer.Equals(p, path));
        _paths.Insert(0, path);
        if (_paths.Count > MaxEntries) {
            _paths.RemoveRange(MaxEntries, _paths.Count - MaxEntries);
        }
    }

    /// <summary>Removes a path from both pinned and unpinned lists.</summary>
    public void Remove(string path) {
        _paths.RemoveAll(p => PathComparer.Equals(p, path));
        if (_pinnedSet.Remove(path)) {
            _pinnedPaths.RemoveAll(p => PathComparer.Equals(p, path));
        }
    }

    /// <summary>Removes all unpinned entries. Pinned entries are kept.</summary>
    public void Clear() => _paths.Clear();

    /// <summary>Persists the list. Failures are silently swallowed (best-effort).</summary>
    public void Save() {
        try {
            var dir = Path.GetDirectoryName(StorePath)!;
            Directory.CreateDirectory(dir);
            var data = new StoreData {
                Pinned = _pinnedPaths.Count > 0 ? _pinnedPaths : null,
                Recent = _paths,
            };
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            });
            File.WriteAllText(StorePath, json);
        } catch {
            // Best-effort — non-fatal if the OS won't let us write.
        }
    }
}
