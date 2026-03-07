using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Manages the mapping between keyboard gestures and command IDs.
/// Each command can have a primary gesture and an optional secondary gesture
/// (Gesture2). Both resolve to the same command at runtime.
/// Loads default bindings from <see cref="CommandRegistry"/>, overlays
/// user overrides from <see cref="AppSettings.KeyBindingOverrides"/> and
/// <see cref="AppSettings.KeyBinding2Overrides"/>, and provides O(1)
/// gesture-to-command resolution for dispatch.
/// </summary>
public class KeyBindingService {
    private readonly AppSettings _settings;
    private Dictionary<KeyGesture, string> _gestureToCommand = new(KeyGestureComparer.Instance);
    private Dictionary<string, KeyGesture?> _commandToGesture = new();
    private Dictionary<string, KeyGesture?> _commandToGesture2 = new();

    public KeyBindingService(AppSettings settings) {
        _settings = settings;
        Rebuild();
    }

    /// <summary>
    /// Rebuilds the internal lookup tables from registry defaults and user
    /// overrides. Called on construction and after any binding change.
    /// </summary>
    public void Rebuild() {
        var gestureToCmd = new Dictionary<KeyGesture, string>(KeyGestureComparer.Instance);
        var cmdToGesture = new Dictionary<string, KeyGesture?>();
        var cmdToGesture2 = new Dictionary<string, KeyGesture?>();
        var overrides1 = _settings.KeyBindingOverrides;
        var overrides2 = _settings.KeyBinding2Overrides;

        // Pass 1: apply defaults for commands that have no user override.
        foreach (var cmd in CommandRegistry.All) {
            // Primary gesture
            if (overrides1 == null || !overrides1.ContainsKey(cmd.Id)) {
                cmdToGesture[cmd.Id] = cmd.Gesture;
                if (cmd.Gesture != null) {
                    gestureToCmd[cmd.Gesture] = cmd.Id;
                }
            }

            // Secondary gesture
            if (overrides2 == null || !overrides2.ContainsKey(cmd.Id)) {
                cmdToGesture2[cmd.Id] = cmd.Gesture2;
                if (cmd.Gesture2 != null) {
                    gestureToCmd[cmd.Gesture2] = cmd.Id;
                }
            }
        }

        // Pass 2: apply user overrides. These are processed last so they
        // always win in the gesture→command map when a conflict exists.
        if (overrides1 != null) {
            foreach (var cmd in CommandRegistry.All) {
                if (!overrides1.TryGetValue(cmd.Id, out var gestureStr)) continue;
                var gesture = string.IsNullOrEmpty(gestureStr) ? null : ParseGesture(gestureStr);
                cmdToGesture[cmd.Id] = gesture;
                if (gesture != null) {
                    gestureToCmd[gesture] = cmd.Id;
                }
            }
        }

        if (overrides2 != null) {
            foreach (var cmd in CommandRegistry.All) {
                if (!overrides2.TryGetValue(cmd.Id, out var gestureStr)) continue;
                var gesture = string.IsNullOrEmpty(gestureStr) ? null : ParseGesture(gestureStr);
                cmdToGesture2[cmd.Id] = gesture;
                if (gesture != null) {
                    gestureToCmd[gesture] = cmd.Id;
                }
            }
        }

        _gestureToCommand = gestureToCmd;
        _commandToGesture = cmdToGesture;
        _commandToGesture2 = cmdToGesture2;
    }

    /// <summary>
    /// Resolves a key press to a command ID. Returns null if no command is
    /// bound to the given key + modifiers combination. Both primary and
    /// secondary gestures are checked.
    /// </summary>
    public string? Resolve(Key key, KeyModifiers modifiers) {
        var gesture = new KeyGesture(key, modifiers);
        return _gestureToCommand.TryGetValue(gesture, out var id) ? id : null;
    }

    /// <summary>
    /// Returns the current primary gesture for a command, or null if unbound.
    /// </summary>
    public KeyGesture? GetGesture(string commandId) =>
        _commandToGesture.TryGetValue(commandId, out var g) ? g : null;

    /// <summary>
    /// Returns the current secondary gesture for a command, or null if unbound.
    /// </summary>
    public KeyGesture? GetGesture2(string commandId) =>
        _commandToGesture2.TryGetValue(commandId, out var g) ? g : null;

    /// <summary>
    /// Returns a display string for the primary gesture (e.g. "Ctrl+Z"),
    /// or null if unbound.
    /// </summary>
    public string? GetGestureText(string commandId) =>
        GetGesture(commandId)?.ToString();

    /// <summary>
    /// Returns a display string for the secondary gesture, or null if unbound.
    /// </summary>
    public string? GetGesture2Text(string commandId) =>
        GetGesture2(commandId)?.ToString();

    /// <summary>
    /// Sets a user override for the primary gesture. Pass null to unbind.
    /// Rebuilds lookup tables and persists to settings.
    /// </summary>
    public void SetBinding(string commandId, KeyGesture? gesture) {
        _settings.KeyBindingOverrides ??= new Dictionary<string, string>();
        _settings.KeyBindingOverrides[commandId] = gesture?.ToString() ?? "";
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Sets a user override for the secondary gesture. Pass null to unbind.
    /// Rebuilds lookup tables and persists to settings.
    /// </summary>
    public void SetBinding2(string commandId, KeyGesture? gesture) {
        _settings.KeyBinding2Overrides ??= new Dictionary<string, string>();
        _settings.KeyBinding2Overrides[commandId] = gesture?.ToString() ?? "";
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Removes the user override for the given command, restoring both the
    /// primary and secondary bindings from <see cref="CommandRegistry"/>.
    /// </summary>
    public void ResetBinding(string commandId) {
        var dirty = false;
        if (_settings.KeyBindingOverrides != null) {
            _settings.KeyBindingOverrides.Remove(commandId);
            if (_settings.KeyBindingOverrides.Count == 0) {
                _settings.KeyBindingOverrides = null;
            }
            dirty = true;
        }
        if (_settings.KeyBinding2Overrides != null) {
            _settings.KeyBinding2Overrides.Remove(commandId);
            if (_settings.KeyBinding2Overrides.Count == 0) {
                _settings.KeyBinding2Overrides = null;
            }
            dirty = true;
        }
        if (dirty) {
            _settings.ScheduleSave();
        }
        Rebuild();
    }

    /// <summary>
    /// Removes all user overrides, restoring every binding to its default.
    /// </summary>
    public void ResetAll() {
        _settings.KeyBindingOverrides = null;
        _settings.KeyBinding2Overrides = null;
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Returns the command ID currently bound to the given gesture, excluding
    /// the specified command. Returns null if no conflict exists. Checks both
    /// primary and secondary gesture slots.
    /// </summary>
    public string? FindConflict(KeyGesture gesture, string excludeCommandId) {
        if (_gestureToCommand.TryGetValue(gesture, out var id) && id != excludeCommandId) {
            return id;
        }
        return null;
    }

    /// <summary>
    /// Returns the <see cref="CommandDescriptor"/> for the given command ID,
    /// or null if not found.
    /// </summary>
    public static CommandDescriptor? GetDescriptor(string commandId) =>
        CommandRegistry.All.FirstOrDefault(c => c.Id == commandId);

    private static KeyGesture? ParseGesture(string text) {
        try {
            return KeyGesture.Parse(text);
        } catch {
            return null; // Malformed gesture string — treat as unbound.
        }
    }
}
