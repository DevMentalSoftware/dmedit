using System.Collections.Generic;
using System.Linq;
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
}
