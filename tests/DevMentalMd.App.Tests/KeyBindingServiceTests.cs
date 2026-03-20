using Avalonia.Input;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Tests;

public class KeyBindingServiceTests {
    private static KeyBindingService CreateService(AppSettings? settings = null) =>
        new(settings ?? new AppSettings(), TestCommands.CreateRegistry());

    // =================================================================
    // Primary gesture (existing tests)
    // =================================================================

    [Fact]
    public void ResolveDefaultBinding() {
        var svc = CreateService();
        var id = svc.Resolve(Key.Z, KeyModifiers.Control);
        Assert.Equal("Edit.Undo", id);
    }

    [Fact]
    public void ResolveUnboundGestureReturnsNull() {
        var svc = CreateService();
        var id = svc.Resolve(Key.F12, KeyModifiers.None);
        Assert.Null(id);
    }

    [Fact]
    public void SetBindingOverridesDefault() {
        var svc = CreateService();
        svc.SetBinding("Edit.Undo", new KeyGesture(Key.Y, KeyModifiers.Control));

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Equal("Edit.Undo", svc.Resolve(Key.Y, KeyModifiers.Control));
    }

    [Fact]
    public void SetBindingNullUnbinds() {
        var svc = CreateService();
        svc.SetBinding("Edit.Undo", null);

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Null(svc.GetGesture("Edit.Undo"));
    }

    [Fact]
    public void ResetBindingRestoresDefault() {
        var svc = CreateService();
        svc.SetBinding("Edit.Undo", new KeyGesture(Key.Y, KeyModifiers.Control));
        svc.ResetBinding("Edit.Undo");

        Assert.Equal("Edit.Undo", svc.Resolve(Key.Z, KeyModifiers.Control));
    }

    [Fact]
    public void ResetAllRestoresDefaults() {
        var svc = CreateService();
        svc.SetBinding("Edit.Undo", new KeyGesture(Key.Y, KeyModifiers.Control));
        svc.SetBinding("Edit.Redo", null);
        svc.ResetAll();

        Assert.Equal("Edit.Undo", svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Equal("Edit.Redo",
            svc.Resolve(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
    }

    [Fact]
    public void FindConflictDetectsExisting() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Z, KeyModifiers.Control), "Edit.Redo");
        Assert.Equal("Edit.Undo", conflict);
    }

    [Fact]
    public void FindConflictExcludesSelf() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Z, KeyModifiers.Control), "Edit.Undo");
        Assert.Null(conflict);
    }

    [Fact]
    public void FindConflictReturnsNullWhenFree() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.F12, KeyModifiers.None), "Edit.Undo");
        Assert.Null(conflict);
    }

    [Fact]
    public void GetGestureTextFormatsCorrectly() {
        var svc = CreateService();
        var text = svc.GetGestureText("Edit.Undo");
        Assert.NotNull(text);
        Assert.Contains("Z", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGestureReturnsNullForUnboundCommand() {
        var svc = CreateService();
        Assert.Null(svc.GetGesture("View.LineNumbers"));
    }

    [Fact]
    public void OverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                ["Edit.Undo"] = "Ctrl+Y",
            },
        };
        var svc = CreateService(settings);

        Assert.Equal("Edit.Undo", svc.Resolve(Key.Y, KeyModifiers.Control));
        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
    }

    [Fact]
    public void EmptyOverrideStringUnbinds() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                ["Edit.Undo"] = "",
            },
        };
        var svc = CreateService(settings);

        Assert.Null(svc.Resolve(Key.Z, KeyModifiers.Control));
        Assert.Null(svc.GetGesture("Edit.Undo"));
    }

    [Fact]
    public void GetCommandReturnsCorrectCommand() {
        var svc = CreateService();
        var cmd = svc.GetCommand("Edit.Undo");
        Assert.NotNull(cmd);
        Assert.Equal("Undo", cmd.DisplayName);
        Assert.Equal("Edit", cmd.Category);
    }

    [Fact]
    public void GetCommandReturnsNullForUnknown() {
        var svc = CreateService();
        var cmd = svc.GetCommand("Nonexistent.Command");
        Assert.Null(cmd);
    }

    // =================================================================
    // Gesture2 (secondary gesture) tests
    // =================================================================

    [Fact]
    public void ResolveGesture2() {
        var svc = CreateService();
        var id = svc.Resolve(Key.Q, KeyModifiers.Control);
        Assert.Equal("File.Exit", id);
    }

    [Fact]
    public void GetGesture2ReturnsSecondary() {
        var svc = CreateService();
        var g2 = svc.GetGesture2("File.Exit");
        Assert.NotNull(g2);
        Assert.Equal(Key.Q, g2.First.Key);
        Assert.Equal(KeyModifiers.Control, g2.First.KeyModifiers);
    }

    [Fact]
    public void GetGesture2TextFormatsCorrectly() {
        var svc = CreateService();
        var text = svc.GetGesture2Text("File.Exit");
        Assert.NotNull(text);
        Assert.Contains("Q", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGesture2ReturnsNullWhenNoSecondary() {
        var svc = CreateService();
        Assert.Null(svc.GetGesture2("Edit.Cut"));
    }

    [Fact]
    public void PrimaryGestureStillWorks() {
        var svc = CreateService();
        var id = svc.Resolve(Key.F4, KeyModifiers.Alt);
        Assert.Equal("File.Exit", id);
    }

    [Fact]
    public void SetBinding2OverridesGesture2() {
        var svc = CreateService();
        svc.SetBinding2("File.Exit", new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Equal("File.Exit", svc.Resolve(Key.W, KeyModifiers.Control));
        Assert.Equal("File.Exit", svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void SetBinding2NullUnbindsSecondary() {
        var svc = CreateService();
        svc.SetBinding2("File.Exit", null);

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Null(svc.GetGesture2("File.Exit"));
        Assert.Equal("File.Exit", svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void ResetBindingRestoresBothSlots() {
        var svc = CreateService();
        svc.SetBinding("File.Exit", new KeyGesture(Key.F5, KeyModifiers.Alt));
        svc.SetBinding2("File.Exit", new KeyGesture(Key.W, KeyModifiers.Control));
        svc.ResetBinding("File.Exit");

        Assert.Equal("File.Exit", svc.Resolve(Key.F4, KeyModifiers.Alt));
        Assert.Equal("File.Exit", svc.Resolve(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void ResetAllRestoresGesture2Defaults() {
        var svc = CreateService();
        svc.SetBinding2("File.Exit", null);
        svc.ResetAll();

        Assert.Equal("File.Exit", svc.Resolve(Key.Q, KeyModifiers.Control));
    }

    [Fact]
    public void ConflictDetectedAcrossSlots() {
        var svc = CreateService();
        var conflict = svc.FindConflict(
            new KeyGesture(Key.Q, KeyModifiers.Control), "Edit.Undo");
        Assert.Equal("File.Exit", conflict);
    }

    [Fact]
    public void Gesture2OverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBinding2Overrides = new Dictionary<string, string> {
                ["File.Exit"] = "Ctrl+W",
            },
        };
        var svc = CreateService(settings);

        Assert.Equal("File.Exit", svc.Resolve(Key.W, KeyModifiers.Control));
        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Equal("File.Exit", svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void EmptyGesture2OverrideUnbinds() {
        var settings = new AppSettings {
            KeyBinding2Overrides = new Dictionary<string, string> {
                ["File.Exit"] = "",
            },
        };
        var svc = CreateService(settings);

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Null(svc.GetGesture2("File.Exit"));
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
        Assert.Equal("View.WrapLines", id);
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
        Assert.Null(svc.Resolve(Key.E, KeyModifiers.Control));
    }

    [Fact]
    public void GetGestureReturnsChordGesture() {
        var svc = CreateService();
        var g = svc.GetGesture("View.WrapLines");
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
        var text = svc.GetGestureText("View.WrapLines");
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
        svc.SetBinding("View.WrapLines", newChord);

        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));

        var newFirst = new KeyGesture(Key.M, KeyModifiers.Control);
        Assert.Equal("View.WrapLines", svc.ResolveChord(newFirst, Key.M, KeyModifiers.None));
    }

    [Fact]
    public void SetBindingNullUnbindsChord() {
        var svc = CreateService();
        svc.SetBinding("View.WrapLines", null);

        Assert.Null(svc.GetGesture("View.WrapLines"));
        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));
    }

    [Fact]
    public void ResetBindingRestoresChord() {
        var svc = CreateService();
        svc.SetBinding("View.WrapLines", null);
        svc.ResetBinding("View.WrapLines");

        var g = svc.GetGesture("View.WrapLines");
        Assert.NotNull(g);
        Assert.True(g.IsChord);
        Assert.Equal(Key.E, g.First.Key);
    }

    [Fact]
    public void ResetAllRestoresChordDefaults() {
        var svc = CreateService();
        svc.SetBinding("View.WrapLines", null);
        svc.ResetAll();

        var g = svc.GetGesture("View.WrapLines");
        Assert.NotNull(g);
        Assert.True(g.IsChord);
    }

    [Fact]
    public void ChordOverridesLoadFromSettings() {
        var settings = new AppSettings {
            KeyBindingOverrides = new Dictionary<string, string> {
                ["View.WrapLines"] = "Ctrl+M, M",
            },
        };
        var svc = CreateService(settings);

        var first = new KeyGesture(Key.E, KeyModifiers.Control);
        Assert.Null(svc.ResolveChord(first, Key.W, KeyModifiers.Control));

        var newFirst = new KeyGesture(Key.M, KeyModifiers.Control);
        Assert.Equal("View.WrapLines", svc.ResolveChord(newFirst, Key.M, KeyModifiers.None));
    }

    [Fact]
    public void FindConflictDetectsChordConflict() {
        var svc = CreateService();
        var chord = new ChordGesture(
            new KeyGesture(Key.E, KeyModifiers.Control),
            new KeyGesture(Key.W, KeyModifiers.Control));
        var conflict = svc.FindConflict(chord, "Edit.Undo");
        Assert.Equal("View.WrapLines", conflict);
    }

    [Fact]
    public void FindConflictExcludesSelfForChord() {
        var svc = CreateService();
        var chord = new ChordGesture(
            new KeyGesture(Key.E, KeyModifiers.Control),
            new KeyGesture(Key.W, KeyModifiers.Control));
        var conflict = svc.FindConflict(chord, "View.WrapLines");
        Assert.Null(conflict);
    }
}
