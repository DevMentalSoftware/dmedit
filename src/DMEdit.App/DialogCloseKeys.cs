using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DMEdit.App;

/// <summary>
/// Gives a dialog the standard keyboard ways out: Escape and Ctrl+F4 both
/// close it.  Closing this way is always a cancel, never an accept — the
/// window closes without its result being set, exactly as the title-bar
/// close button does.
///
/// <para>The handler runs on the bubble phase and stands down when the key
/// is already handled, so a dialog (or a child control) that gives Escape
/// its own meaning — clearing a selection, closing a popup — keeps it.</para>
/// </summary>
public static class DialogCloseKeys {
    /// <summary>Attaches the Escape / Ctrl+F4 close handler to <paramref name="window"/>.</summary>
    public static void Attach(Window window) {
        window.AddHandler(InputElement.KeyDownEvent, (_, e) => {
            if (e.Handled || !IsCloseGesture(e.Key, e.KeyModifiers)) {
                return;
            }
            e.Handled = true;
            window.Close();
        }, RoutingStrategies.Bubble);
    }

    /// <summary>True for Escape (no modifiers) and Ctrl+F4.</summary>
    public static bool IsCloseGesture(Key key, KeyModifiers mods) => key switch {
        Key.Escape => mods == KeyModifiers.None,
        Key.F4 => mods == KeyModifiers.Control,
        _ => false,
    };
}
