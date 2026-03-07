using Avalonia.Input;
using DevMentalMd.App.Commands;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Tests;

public class KeyBindingServiceTests {
    private static KeyBindingService CreateService(AppSettings? settings = null) =>
        new(settings ?? new AppSettings());

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
}
