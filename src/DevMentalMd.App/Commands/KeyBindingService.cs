using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Manages the mapping between keyboard gestures and command IDs.
/// Loads default bindings from <see cref="CommandRegistry"/>, overlays
/// user overrides from <see cref="AppSettings.KeyBindingOverrides"/>,
/// and provides O(1) gesture-to-command resolution for dispatch.
/// </summary>
public class KeyBindingService {
    private readonly AppSettings _settings;
    private Dictionary<KeyGesture, string> _gestureToCommand = new(KeyGestureComparer.Instance);
    private Dictionary<string, KeyGesture?> _commandToGesture = new();

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
        var overrides = _settings.KeyBindingOverrides;

        // Pass 1: apply defaults for commands that have no user override.
        foreach (var cmd in CommandRegistry.All) {
            if (overrides != null && overrides.ContainsKey(cmd.Id)) continue;
            cmdToGesture[cmd.Id] = cmd.DefaultGesture;
            if (cmd.DefaultGesture != null) {
                gestureToCmd[cmd.DefaultGesture] = cmd.Id;
            }
        }

        // Pass 2: apply user overrides. These are processed last so they
        // always win in the gesture→command map when a conflict exists
        // (e.g. user rebinds Edit.Undo to Ctrl+Y which was Edit.DeleteLine's default).
        if (overrides != null) {
            foreach (var cmd in CommandRegistry.All) {
                if (!overrides.TryGetValue(cmd.Id, out var gestureStr)) continue;
                var gesture = string.IsNullOrEmpty(gestureStr) ? null : ParseGesture(gestureStr);
                cmdToGesture[cmd.Id] = gesture;
                if (gesture != null) {
                    gestureToCmd[gesture] = cmd.Id;
                }
            }
        }

        _gestureToCommand = gestureToCmd;
        _commandToGesture = cmdToGesture;
    }

    /// <summary>
    /// Resolves a key press to a command ID. Returns null if no command is
    /// bound to the given key + modifiers combination.
    /// </summary>
    public string? Resolve(Key key, KeyModifiers modifiers) {
        var gesture = new KeyGesture(key, modifiers);
        return _gestureToCommand.TryGetValue(gesture, out var id) ? id : null;
    }

    /// <summary>
    /// Returns the current gesture for a command, or null if unbound.
    /// </summary>
    public KeyGesture? GetGesture(string commandId) =>
        _commandToGesture.TryGetValue(commandId, out var g) ? g : null;

    /// <summary>
    /// Returns a display string for the current gesture (e.g. "Ctrl+Z"),
    /// or null if unbound.
    /// </summary>
    public string? GetGestureText(string commandId) =>
        GetGesture(commandId)?.ToString();

    /// <summary>
    /// Sets a user override for the given command. Pass null to unbind.
    /// Rebuilds lookup tables and persists to settings.
    /// </summary>
    public void SetBinding(string commandId, KeyGesture? gesture) {
        _settings.KeyBindingOverrides ??= new Dictionary<string, string>();
        _settings.KeyBindingOverrides[commandId] = gesture?.ToString() ?? "";
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Removes the user override for the given command, restoring the
    /// default binding from <see cref="CommandRegistry"/>.
    /// </summary>
    public void ResetBinding(string commandId) {
        if (_settings.KeyBindingOverrides != null) {
            _settings.KeyBindingOverrides.Remove(commandId);
            if (_settings.KeyBindingOverrides.Count == 0) {
                _settings.KeyBindingOverrides = null;
            }
            _settings.ScheduleSave();
        }
        Rebuild();
    }

    /// <summary>
    /// Removes all user overrides, restoring every binding to its default.
    /// </summary>
    public void ResetAll() {
        _settings.KeyBindingOverrides = null;
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Returns the command ID currently bound to the given gesture, excluding
    /// the specified command. Returns null if no conflict exists.
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
