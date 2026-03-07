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
        // Default: Ctrl+Z → Edit.Undo. Override: Ctrl+Y → Edit.Undo.
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
        // Ctrl+Z is bound to Edit.Undo. Check if Ctrl+Z conflicts for Edit.Redo.
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
        // The text should contain the key and modifier in some form.
        Assert.Contains("Z", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetGestureReturnsNullForUnboundCommand() {
        var svc = CreateService();
        // View.LineNumbers has no default gesture.
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
        // File.Exit has Gesture2 = Ctrl+Q
        var id = svc.Resolve(Key.Q, KeyModifiers.Control);
        Assert.Equal(CommandIds.FileExit, id);
    }

    [Fact]
    public void GetGesture2ReturnsSecondary() {
        var svc = CreateService();
        var g2 = svc.GetGesture2(CommandIds.FileExit);
        Assert.NotNull(g2);
        Assert.Equal(Key.Q, g2.Key);
        Assert.Equal(KeyModifiers.Control, g2.KeyModifiers);
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
        // Edit.Cut has no Gesture2.
        Assert.Null(svc.GetGesture2(CommandIds.EditCut));
    }

    [Fact]
    public void PrimaryGestureStillWorks() {
        var svc = CreateService();
        // File.Exit primary = Alt+F4
        var id = svc.Resolve(Key.F4, KeyModifiers.Alt);
        Assert.Equal(CommandIds.FileExit, id);
    }

    [Fact]
    public void SetBinding2OverridesGesture2() {
        var svc = CreateService();
        // Override File.Exit's Gesture2 from Ctrl+Q to Ctrl+W.
        svc.SetBinding2(CommandIds.FileExit, new KeyGesture(Key.W, KeyModifiers.Control));

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.W, KeyModifiers.Control));
        // Primary gesture unaffected.
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void SetBinding2NullUnbindsSecondary() {
        var svc = CreateService();
        svc.SetBinding2(CommandIds.FileExit, null);

        Assert.Null(svc.Resolve(Key.Q, KeyModifiers.Control));
        Assert.Null(svc.GetGesture2(CommandIds.FileExit));
        // Primary still works.
        Assert.Equal(CommandIds.FileExit, svc.Resolve(Key.F4, KeyModifiers.Alt));
    }

    [Fact]
    public void ResetBindingRestoresBothSlots() {
        var svc = CreateService();
        svc.SetBinding(CommandIds.FileExit, new KeyGesture(Key.F5, KeyModifiers.Alt));
        svc.SetBinding2(CommandIds.FileExit, new KeyGesture(Key.W, KeyModifiers.Control));
        svc.ResetBinding(CommandIds.FileExit);

        // Both should be restored to defaults.
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
        // Ctrl+Q is File.Exit's Gesture2. Try to assign it to Edit.Undo.
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
        // Primary unaffected.
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

    [Fact]
    public void EditRedoHasGesture2() {
        var svc = CreateService();
        // Edit.Redo: Gesture = Ctrl+Shift+Z, Gesture2 = Ctrl+Y
        Assert.Equal(CommandIds.EditRedo, svc.Resolve(Key.Z, KeyModifiers.Control | KeyModifiers.Shift));
        Assert.Equal(CommandIds.EditRedo, svc.Resolve(Key.Y, KeyModifiers.Control));

        var g2 = svc.GetGesture2(CommandIds.EditRedo);
        Assert.NotNull(g2);
        Assert.Equal(Key.Y, g2.Key);
        Assert.Equal(KeyModifiers.Control, g2.KeyModifiers);
    }
}
