using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Tests;

public class CommandRegistryTests {
    private static CommandRegistry CreateRegistry() => TestCommands.CreateRegistry();

    [Fact]
    public void AllCommandIdsAreUnique() {
        var registry = CreateRegistry();
        var ids = registry.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void AllDefaultGesturesAreUnique() {
        var registry = CreateRegistry();
        var svc = new KeyBindingService(new AppSettings(), registry);
        var entries = new List<(string Id, KeyGesture Gesture)>();

        foreach (var cmd in registry.All) {
            var g1 = svc.GetGesture(cmd.Id);
            if (g1 is { IsChord: false }) {
                entries.Add((cmd.Id + " (Gesture)", g1.First));
            }
            var g2 = svc.GetGesture2(cmd.Id);
            if (g2 is { IsChord: false }) {
                entries.Add((cmd.Id + " (Gesture2)", g2.First));
            }
        }

        var comparer = KeyGestureComparer.Instance;
        var seen = new HashSet<KeyGesture>(comparer);
        var duplicates = new List<string>();

        foreach (var (id, gesture) in entries) {
            if (!seen.Add(gesture)) {
                duplicates.Add($"{id} ({gesture})");
            }
        }

        Assert.True(duplicates.Count == 0,
            $"Duplicate default gestures: {string.Join(", ", duplicates)}");
    }

    [Fact]
    public void AllDefaultChordsAreUnique() {
        var registry = CreateRegistry();
        var svc = new KeyBindingService(new AppSettings(), registry);
        var seen = new HashSet<(int, int, int, int)>();
        var duplicates = new List<string>();

        foreach (var cmd in registry.All) {
            CheckChord(cmd.Id, svc.GetGesture(cmd.Id), seen, duplicates);
            CheckChord(cmd.Id, svc.GetGesture2(cmd.Id), seen, duplicates);
        }

        Assert.True(duplicates.Count == 0,
            $"Duplicate default chords: {string.Join(", ", duplicates)}");
    }

    private static void CheckChord(string cmdId, ChordGesture? gesture,
        HashSet<(int, int, int, int)> seen, List<string> duplicates) {
        if (gesture is not { IsChord: true }) return;
        var key = (
            (int)gesture.First.Key, (int)gesture.First.KeyModifiers,
            (int)gesture.Second!.Key, (int)gesture.Second.KeyModifiers);
        if (!seen.Add(key)) {
            duplicates.Add($"{cmdId} ({gesture})");
        }
    }

    [Fact]
    public void AllCommandsHaveNonEmptyDisplayName() {
        var registry = CreateRegistry();
        foreach (var cmd in registry.All) {
            Assert.False(string.IsNullOrWhiteSpace(cmd.DisplayName),
                $"Command {cmd.Id} has empty display name");
        }
    }

    [Fact]
    public void AllCommandCategoriesAreValid() {
        var registry = CreateRegistry();
        foreach (var cmd in registry.All) {
            Assert.Contains(cmd.Category, registry.Categories);
        }
    }

    [Fact]
    public void AllCommandIdsContainDot() {
        var registry = CreateRegistry();
        foreach (var cmd in registry.All) {
            Assert.Contains('.', cmd.Id);
        }
    }
}
