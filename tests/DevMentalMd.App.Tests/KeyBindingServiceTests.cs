using Avalonia.Input;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Tests;

public class KeyBindingServiceTests {
    private static KeyBindingService CreateService(AppSettings? settings = null) =>
        new(settings ?? new AppSettings());

    // =================================================================
    // Primary gesture (existing tests)
    // =================================================================

    [Fact]
    public void ResolveDefaultBinding() {
        var svc = CreateService();
        var id = svc.Resolve(Key.Z, KeyModifiers.Control);
        Assert.Equal(CommandIds.EditUndo, id);
    }

    [Fact]
    public void ResolveUnboundGestureReturnsNull() {
        var svc = CreateService();
        // F12 with no modifiers is not bound to anything by default.
        var id = svc.Resolve(Key.F12, KeyModifiers.None);
        Assert.Null(id);
    }

    [Fact]
    public void SetBindingOverridesDefault() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.EditUndo, new KeyGesture(Key.Y, KeyModifiers.Control));

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Equal(CommandIds.EditUndo, svc.Resolve(Key.Y, KeyModifiers.Control));
    }

    [Fact]
    public void SetBindingNullUnbinds() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.EditUndo, null);

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Null(svc.GetGesture(CommandIds.EditUndo));
    }

    [Fact]
    public void ResetBindingRestoresDefault() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.EditUndo, new KeyGesture(Key.Y, KeyModifiers.Control));
        svc.ResetBinding(CommandIds.EditUndo);

        Assert.Equal(CommandIds.EditUndo, svc.Resolve(Key.Z, KeyModifiers.Control));
    }

    [Fact]
    public void ResetAllRestoresDefaults() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.EditUndo, new KeyGesture(Key.Y, KeyModifiers.Control));
        svc.SetBinding(CommandIds.EditRedo, null);
        svc.ResetAll();

        Assert.Equal(CommandIds.EditUndo, svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Equal(CommandIds.EditRedo,
            svc.Resolve(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void FindConflictDetectsExisting() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Z, KeyModifiers.Control), CommandIds.EditRedo);
        Assert.Equal(CommandIds.EditUndo, conflict);
    }

    [Fact]
    public void FindConflictExcludesSelf() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Z, KeyModifiers.Control), CommandIds.EditUndo);
        Assert.Null(conflict);
    }

    [Fact]
    public void FindConflictReturnsNullWhenFree() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.F12, KeyModifiers.None), CommandIds.EditUndo);
        Assert.Null(conflict);
    }

    [Fact]
    public void GetGestureTextFormatsCorrectly() {
        var svc = CreateService();
        var text = svc.GetGestureText(CommandIds.EditUndo);
        Assert.NotNull(text);
        Assert.Contains("Z", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGestureReturnsNullForUnboundCommand() {
        var svc = CreateService();
        Assert.Null(svc.GetGesture(CommandIds.ViewLineNumbers));
    }

    [Fact]
    public void OverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                [CommandIds.EditUndo] = "Ctrl+Y",
            },
        };
        var svc = CreateService(settings);

        Assert.Equal(CommandIds.EditUndo, svc.Resolve(Key.Y, KeyModifiers.Control));
        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
    }

    [Fact]
    public void EmptyOverrideStringUnbinds() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                [CommandIds.EditUndo] = "",
            },
        };
        var svc = CreateService(settings);

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Null(svc.GetGesture(CommandIds.EditUndo));
    }

    [Fact]
    public void GetDescriptorReturnsCorrectCommand() {
        var desc = KeyBindingService.GetDescriptor(CommandIds.EditUndo);
        Assert.NotNull(desc);
        Assert.Equal("Undo", desc.DisplayName);
        Assert.Equal("Edit", desc.Category);
    }

    [Fact]
    public void GetDescriptorReturnsNullForUnknown() {
        var desc = KeyBindingService.GetDescriptor("Nonexistent.Command");
        Assert.Null(desc);
    }

    // =================================================================
    // Gesture2 (secondary gesture) tests
    // =================================================================

    [Fact]
    public void ResolveGesture2() {
        var svc = CreateService();
        var id = svc.Resolve(Key.Q, KeyModifiers.Control);
        Assert.Equal(CommandIds.FileExit, id);
    }

    [Fact]
    public void GetGesture2ReturnsSecondary() {
        var svc = CreateService();
        var g2 = svc.GetGesture2(CommandIds.FileExit);
        Assert.NotNull(g2);
        Assert.Equal(Key.Q, g2.First.Key);
        Assert.Equal(KeyModifiers.Control, g2.First.KeyModifiers);
    }

    [Fact]
    public void GetGesture2TextFormatsCorrectly() {
        var svc = CreateService();
        var text = svc.GetGesture2Text(CommandIds.FileExit);
        Assert.NotNull(text);
        Assert.Contains("Q", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGesture2ReturnsNullWhenNoSecondary() {
        var svc = CreateService();
        Assert.Null(svc.GetGesture2(CommandIds.EditCut));
    }

    [Fact]
    public void PrimaryGestureStillWorks() {
        var svc = CreateService();
        var id = svc.Resolve(Key.F4, KeyModifiers.Alt);
        Assert.Equal(CommandIds.FileExit, id);
    }

    [Fact]
    public void SetBinding2OverridesGesture2() {
        var svc = CreateService();
        svc.SetBinding2(CommandIds.FileExit, new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.W, KeyModifiers.Control));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void SetBinding2NullUnbindsSecondary() {
        var svc = CreateService();
        svc.SetBinding2(CommandIds.FileExit, null);

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Null(svc.GetGesture2(CommandIds.FileExit));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void ResetBindingRestoresBothSlots() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.FileExit, new KeyGesture(Key.F5, KeyModifiers.Alt));
        svc.SetBinding2(CommandIds.FileExit, new KeyGesture(Key.W, KeyModifiers.Control));
        svc.ResetBinding(CommandIds.FileExit);

        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void ResetAllRestoresGesture2Defaults() {
        var svc = CreateService();
        svc.SetBinding2(CommandIds.FileExit, null);
        svc.ResetAll();

        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void ConflictDetectedAcrossSlots() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Q, KeyModifiers.Control), CommandIds.EditUndo);
        Assert.Equal(CommandIds.FileExit, conflict);
    }

    [Fact]
    public void Gesture2OverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBinding2Overrides = new Dictionary<string, string> {
                [CommandIds.FileExit] = "Ctrl+W",
            },
        };
        var svc = CreateService(settings);

        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.W, KeyModifiers.Control));
        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void EmptyGesture2OverrideUnbinds() {
        var settings = new AppSettings {
            KeyBinding2Overrides = new Dictionary<string, string> {
                [CommandIds.FileExit] = "",
            },
        };
        var svc = CreateService(settings);

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Null(svc.GetGesture2(CommandIds.FileExit));
    }

    // =================================================================
    // Chord tests (chords live in Gesture/Gesture2 slots)
    // =================================================================

    [Fact]
    public void IsChordPrefixReturnsTrueForChordFirstKey() {
        var svc = CreateService();
        Assert.True(svc.IsChordPrefix(Key.E, KeyModifiers.Control));
    }

    [Fact]
    public void IsChordPrefixReturnsFalseForNonChord() {
        var svc = CreateService();
        Assert.False(svc.IsChordPrefix(Key.F12, KeyModifiers.None));
    }

    [Fact]
    public void ResolveChordReturnsCommandId() {
        var svc = CreateService();
        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        var id = svc.ResolveChord(first, Key.W, KeyModifiers.Control);
        Assert.Equal(CommandIds.ViewWrapLines, id);
    }

    [Fact]
    public void ResolveChordReturnsNullForWrongSecondKey() {
        var svc = CreateService();
        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        var id = svc.ResolveChord(first, Key.X, KeyModifiers.Control);
        Assert.Null(id);
    }

    [Fact]
    public void ChordGestureInSlotIsNotResolvedAsSingleKey() {
        var svc = CreateService();
        // The chord's first key (Ctrl+E) should NOT resolve as a single gesture.
        Assert.Null(svc.Resolve(Key.E, KeyModifiers.Control));
    }

    [Fact]
    public void GetGestureReturnsChordGesture() {
        var svc = CreateService();
        var g = svc.GetGesture(CommandIds.ViewWrapLines);
        Assert.NotNull(g);
        Assert.True(g.IsChord);
        Assert.Equal(Key.E, g.First.Key);
        Assert.Equal(KeyModifiers.Control, g.First.KeyModifiers);
        Assert.Equal(Key.W, g.Second!.Key);
        Assert.Equal(KeyModifiers.Control, g.Second.KeyModifiers);
    }

    [Fact]
    public void GetGestureTextFormatsChordCorrectly() {
        var svc = CreateService();
        var text = svc.GetGestureText(CommandIds.ViewWrapLines);
        Assert.NotNull(text);
        Assert.Contains("E", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("W", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(",", text);
    }

    [Fact]
    public void SetBindingWithChordOverridesDefault() {
        var svc = CreateService();
        var newChord = new ChordGesture(
            new KeyGesture(Key.M, KeyModifiers.Control),
            new KeyGesture(Key.M, KeyModifiers.None));
        svc.SetBinding(CommandIds.ViewWrapLines, newChord);

        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));

        var newFirst = new KeyGesture(Key.M, KeyModifiers.Control);
        Assert.Equal(CommandIds.ViewWrapLines, svc.ResolveChord(newFirst, Key.M, KeyModifiers.None));
    }

    [Fact]
    public void SetBindingNullUnbindsChord() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.ViewWrapLines, null);

        Assert.Null(svc.GetGesture(CommandIds.ViewWrapLines));
        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));
    }

    [Fact]
    public void ResetBindingRestoresChord() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.ViewWrapLines, null);
        svc.ResetBinding(CommandIds.ViewWrapLines);

        var g = svc.GetGesture(CommandIds.ViewWrapLines);
        Assert.NotNull(g);
        Assert.True(g.IsChord);
        Assert.Equal(Key.E, g.First.Key);
    }

    [Fact]
    public void ResetAllRestoresChordDefaults() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.ViewWrapLines, null);
        svc.ResetAll();

        var g = svc.GetGesture(CommandIds.ViewWrapLines);
        Assert.NotNull(g);
        Assert.True(g.IsChord);
    }

    [Fact]
    public void ChordOverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                [CommandIds.ViewWrapLines] = "Ctrl+M, M",
            },
        };
        var svc = CreateService(settings);

        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));

        var newFirst = new KeyGesture(Key.M, KeyModifiers.Control);
        Assert.Equal(CommandIds.ViewWrapLines, svc.ResolveChord(newFirst, Key.M, KeyModifiers.None));
    }

    [Fact]
    public void FindConflictDetectsChordConflict() {
        var svc = CreateService();
        var chord = new ChordGesture(
            new KeyGesture(Key.E, KeyModifiers.Control),
            new KeyGesture(Key.W, KeyModifiers.Control));
        var conflict = svc.FindConflict(chord, CommandIds.EditUndo);
        Assert.Equal(CommandIds.ViewWrapLines, conflict);
    }

    [Fact]
    public void FindConflictExcludesSelfForChord() {
        var svc = CreateService();
        var chord = new ChordGesture(
            new KeyGesture(Key.E, KeyModifiers.Control),
            new KeyGesture(Key.W, KeyModifiers.Control));
        var conflict = svc.FindConflict(chord, CommandIds.ViewWrapLines);
        Assert.Null(conflict);
    }
}
