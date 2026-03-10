using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Input;
using DevMentalMd.App.Commands;

namespace DevMentalMd.App.Tests;

public class ProfileLoaderTests {
    [Fact]
    public void AllProfilesLoad() {
        foreach (var id in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(id);
            Assert.NotNull(profile);
            Assert.False(string.IsNullOrWhiteSpace(profile.Name),
                $"Profile {id} has empty Name");
        }
    }

    [Fact]
    public void AllProfilesHaveBindings() {
        foreach (var id in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(id);
            Assert.NotNull(profile.Bindings);
            Assert.True(profile.Bindings.Count > 0,
                $"Profile {id} has no bindings");
        }
    }

    [Fact]
    public void AllProfileCommandIdsAreValid() {
        var validIds = CommandRegistry.All.Select(c => c.Id).ToHashSet();

        foreach (var id in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(id);

            foreach (var cmdId in profile.Bindings!.Keys) {
                Assert.Contains(cmdId, validIds);
            }

            if (profile.Bindings2 != null) {
                foreach (var cmdId in profile.Bindings2.Keys) {
                    Assert.Contains(cmdId, validIds);
                }
            }
        }
    }

    [Fact]
    public void AllProfileGestureStringsParse() {
        foreach (var id in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(id);

            foreach (var (cmdId, str) in profile.Bindings!) {
                if (string.IsNullOrEmpty(str)) continue; // intentionally unbound
                var gesture = ChordGesture.Parse(str);
                Assert.True(gesture != null,
                    $"Profile {id}: failed to parse binding for {cmdId}: \"{str}\"");
            }

            if (profile.Bindings2 != null) {
                foreach (var (cmdId, str) in profile.Bindings2) {
                    if (string.IsNullOrEmpty(str)) continue; // intentionally unbound
                    var gesture = ChordGesture.Parse(str);
                    Assert.True(gesture != null,
                        $"Profile {id}: failed to parse bindings2 for {cmdId}: \"{str}\"");
                }
            }
        }
    }

    [Fact]
    public void NoProfileHasDuplicateSingleKeyGestures() {
        foreach (var id in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(id);
            var comparer = KeyGestureComparer.Instance;
            var seen = new HashSet<KeyGesture>(comparer);
            var duplicates = new List<string>();

            void Check(string cmdId, string str) {
                var gesture = ChordGesture.Parse(str);
                if (gesture is { IsChord: false }) {
                    if (!seen.Add(gesture.First)) {
                        duplicates.Add($"{cmdId} ({str})");
                    }
                }
            }

            foreach (var (cmdId, str) in profile.Bindings!) {
                Check(cmdId, str);
            }

            if (profile.Bindings2 != null) {
                foreach (var (cmdId, str) in profile.Bindings2) {
                    Check(cmdId, str);
                }
            }

            Assert.True(duplicates.Count == 0,
                $"Profile {id} has duplicate single-key gestures: {string.Join(", ", duplicates)}");
        }
    }

    [Fact]
    public void GetDisplayNameReturnsNonEmpty() {
        foreach (var id in ProfileLoader.ProfileIds) {
            var name = ProfileLoader.GetDisplayName(id);
            Assert.False(string.IsNullOrWhiteSpace(name),
                $"Profile {id} has empty display name");
        }
    }

    [Fact]
    public void DefaultProfileContainsAllExpectedBindings() {
        // The Default profile should have bindings for the majority of commands.
        // Some commands are intentionally unbound (stubs, rarely-used, or user-only).
        var profile = ProfileLoader.Load("Default");
        var boundIds = profile.Bindings!.Keys.ToHashSet();

        // Commands intentionally left unbound in the Default profile.
        var intentionallyUnbound = new HashSet<string> {
            CommandIds.EditInsertLineBelow,
            CommandIds.EditInsertLineAbove,
            CommandIds.EditDuplicateLine,
            CommandIds.EditTab,
            CommandIds.EditSelectAllOccurrences,
            CommandIds.EditColumnSelect,
            CommandIds.FileRevertFile,
            CommandIds.ViewZoomIn,
            CommandIds.ViewZoomOut,
            CommandIds.WindowSettings,
            CommandIds.NavFocusEditor,
        };

        // At minimum, all File, Edit, Nav commands (minus exclusions) should be bound.
        var expectedCategories = new[] { "File", "Edit", "Nav" };
        var allIds = CommandRegistry.All.Select(c => c.Id).ToHashSet();
        foreach (var cat in expectedCategories) {
            var catCommands = allIds
                .Where(id => id.StartsWith(cat + "."))
                .Where(id => !intentionallyUnbound.Contains(id))
                .ToList();
            foreach (var cmdId in catCommands) {
                Assert.Contains(cmdId, boundIds);
            }
        }
    }

    /// <summary>
    /// Verifies that every profile JSON contains an entry in "bindings" for
    /// every command in <see cref="CommandRegistry"/>. Missing commands should
    /// be present with an empty string value to indicate "intentionally unbound".
    /// When the test fails, the output shows the exact JSON entries to add.
    /// </summary>
    [Fact]
    public void AllProfilesContainEveryCommandId() {
        var allIds = CommandRegistry.All.Select(c => c.Id).ToList();
        var failures = new StringBuilder();

        foreach (var profileId in ProfileLoader.ProfileIds) {
            var profile = ProfileLoader.Load(profileId);
            var bindingKeys = profile.Bindings?.Keys.ToHashSet() ?? [];

            var missing = allIds.Where(id => !bindingKeys.Contains(id)).ToList();
            if (missing.Count == 0) {
                continue;
            }

            failures.AppendLine();
            failures.AppendLine($"--- {profileId}.json is missing {missing.Count} command(s) in \"bindings\". Add: ---");
            foreach (var id in missing) {
                failures.AppendLine($"    \"{id}\": \"\",");
            }
        }

        Assert.True(failures.Length == 0,
            $"Some profiles are missing command entries in \"bindings\".{failures}");
    }

    /// <summary>
    /// Helper: reads each profile JSON from disk, adds missing command IDs
    /// with empty-string values, and writes the result back. Call from a
    /// scratch test or manual runner to bulk-fix all profiles.
    /// Skipped by default so it does not run in CI.
    /// </summary>
    [Fact(Skip = "Manual helper — remove Skip to auto-fix profile JSONs")]
    public void FixProfiles_AddMissingCommandIds() {
        var allIds = CommandRegistry.All.Select(c => c.Id).ToList();

        // Walk up from the test output directory to the repo root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !System.IO.File.Exists(Path.Combine(dir.FullName, "DevMentalMD.slnx"))) {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);

        var profilesDir = Path.Combine(dir!.FullName,
            "src", "DevMentalMd.App", "Commands", "Profiles");

        foreach (var profileId in ProfileLoader.ProfileIds) {
            var filePath = Path.Combine(profilesDir, $"{profileId}.json");
            Assert.True(System.IO.File.Exists(filePath), $"Profile not found: {filePath}");

            var json = System.IO.File.ReadAllText(filePath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Rebuild the JSON with missing keys inserted.
            var bindings = root.GetProperty("bindings");
            var existing = new Dictionary<string, string>();
            foreach (var prop in bindings.EnumerateObject()) {
                existing[prop.Name] = prop.Value.GetString() ?? "";
            }

            var changed = false;
            foreach (var id in allIds) {
                if (!existing.ContainsKey(id)) {
                    existing[id] = "";
                    changed = true;
                }
            }

            if (!changed) {
                continue;
            }

            // Rewrite the file preserving structure: reorder bindings to match
            // CommandRegistry order, keeping any extras at the end.
            var ordered = new List<KeyValuePair<string, string>>();
            foreach (var id in allIds) {
                ordered.Add(new(id, existing[id]));
            }
            // Append any keys present in JSON but not in CommandRegistry (shouldn't
            // happen, but don't lose data).
            foreach (var kvp in existing) {
                if (!allIds.Contains(kvp.Key)) {
                    ordered.Add(kvp);
                }
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions {
                Indented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            })) {
                writer.WriteStartObject();
                writer.WriteString("name", root.GetProperty("name").GetString());

                writer.WritePropertyName("bindings");
                writer.WriteStartObject();
                foreach (var kvp in ordered) {
                    writer.WriteString(kvp.Key, kvp.Value);
                }
                writer.WriteEndObject();

                if (root.TryGetProperty("bindings2", out var bindings2)) {
                    writer.WritePropertyName("bindings2");
                    writer.WriteStartObject();
                    foreach (var prop in bindings2.EnumerateObject()) {
                        writer.WriteString(prop.Name, prop.Value.GetString());
                    }
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            var output = Encoding.UTF8.GetString(ms.ToArray());
            // Normalize line endings to match existing files.
            output = output.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
            System.IO.File.WriteAllText(filePath, output + Environment.NewLine);
        }
    }
}
