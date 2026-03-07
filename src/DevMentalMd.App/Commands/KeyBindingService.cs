using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;
using DevMentalMd.App.Services;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Manages the mapping between keyboard gestures and command IDs.
/// Each command can have a primary gesture and an optional secondary gesture.
/// Either slot can hold a <see cref="ChordGesture"/> representing either a
/// single keystroke or a two-keystroke chord. Loads default bindings from
/// <see cref="CommandRegistry"/>, overlays user overrides from
/// <see cref="AppSettings"/>, and provides O(1) gesture-to-command resolution
/// for dispatch.
/// </summary>
public class KeyBindingService {
    private readonly AppSettings _settings;

    // Single-key lookups (non-chord gestures only).
    private Dictionary<KeyGesture, string> _gestureToCommand = new(KeyGestureComparer.Instance);
    private Dictionary<string, ChordGesture?> _commandToGesture = new();
    private Dictionary<string, ChordGesture?> _commandToGesture2 = new();

    // Chord lookups (derived from ChordGesture instances where IsChord).
    private HashSet<KeyGesture> _chordPrefixes = new(KeyGestureComparer.Instance);
    private Dictionary<(KeyGesture, KeyGesture), string> _chordToCommand = new(ChordTupleComparer.Instance);

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
        var cmdToGesture = new Dictionary<string, ChordGesture?>();
        var cmdToGesture2 = new Dictionary<string, ChordGesture?>();
        var chordPrefixes = new HashSet<KeyGesture>(KeyGestureComparer.Instance);
        var chordToCmd = new Dictionary<(KeyGesture, KeyGesture), string>(ChordTupleComparer.Instance);

        var overrides1 = _settings.KeyBindingOverrides;
        var overrides2 = _settings.KeyBinding2Overrides;

        // Pass 1: apply defaults for commands that have no user override.
        foreach (var cmd in CommandRegistry.All) {
            if (overrides1 == null || !overrides1.ContainsKey(cmd.Id)) {
                cmdToGesture[cmd.Id] = cmd.Gesture;
                RegisterGesture(cmd.Gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }

            if (overrides2 == null || !overrides2.ContainsKey(cmd.Id)) {
                cmdToGesture2[cmd.Id] = cmd.Gesture2;
                RegisterGesture(cmd.Gesture2, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }
        }

        // Pass 2: apply user overrides. These are processed last so they
        // always win when a conflict exists.
        if (overrides1 != null) {
            foreach (var cmd in CommandRegistry.All) {
                if (!overrides1.TryGetValue(cmd.Id, out var str)) continue;
                var gesture = string.IsNullOrEmpty(str) ? null : ChordGesture.Parse(str);
                cmdToGesture[cmd.Id] = gesture;
                RegisterGesture(gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }
        }

        if (overrides2 != null) {
            foreach (var cmd in CommandRegistry.All) {
                if (!overrides2.TryGetValue(cmd.Id, out var str)) continue;
                var gesture = string.IsNullOrEmpty(str) ? null : ChordGesture.Parse(str);
                cmdToGesture2[cmd.Id] = gesture;
                RegisterGesture(gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }
        }

        _gestureToCommand = gestureToCmd;
        _commandToGesture = cmdToGesture;
        _commandToGesture2 = cmdToGesture2;
        _chordPrefixes = chordPrefixes;
        _chordToCommand = chordToCmd;
    }

    /// <summary>
    /// Registers a gesture (single-key or chord) into the appropriate lookup
    /// tables. Chord gestures go into the chord maps; single-key gestures go
    /// into the single-key map.
    /// </summary>
    private static void RegisterGesture(
        ChordGesture? gesture, string commandId,
        Dictionary<KeyGesture, string> gestureToCmd,
        Dictionary<(KeyGesture, KeyGesture), string> chordToCmd,
        HashSet<KeyGesture> chordPrefixes) {
        if (gesture == null) return;
        if (gesture.IsChord) {
            chordToCmd[(gesture.First, gesture.Second!)] = commandId;
            chordPrefixes.Add(gesture.First);
        } else {
            gestureToCmd[gesture.First] = commandId;
        }
    }

    // =================================================================
    // Single-key resolution
    // =================================================================

    /// <summary>
    /// Resolves a single key press to a command ID. Returns null if no
    /// command is bound. Both primary and secondary gestures are checked.
    /// Does not check chords — use <see cref="ResolveChord"/> for those.
    /// </summary>
    public string? Resolve(Key key, KeyModifiers modifiers) {
        var gesture = new KeyGesture(key, modifiers);
        return _gestureToCommand.TryGetValue(gesture, out var id) ? id : null;
    }

    /// <summary>
    /// Returns the current primary gesture for a command, or null if unbound.
    /// May be a chord (<see cref="ChordGesture.IsChord"/>).
    /// </summary>
    public ChordGesture? GetGesture(string commandId) =>
        _commandToGesture.TryGetValue(commandId, out var g) ? g : null;

    /// <summary>
    /// Returns the current secondary gesture for a command, or null if unbound.
    /// May be a chord.
    /// </summary>
    public ChordGesture? GetGesture2(string commandId) =>
        _commandToGesture2.TryGetValue(commandId, out var g) ? g : null;

    /// <summary>
    /// Returns a display string for the primary gesture (e.g. "Ctrl+Z" or
    /// "Ctrl+E, Ctrl+W" for chords), or null if unbound.
    /// </summary>
    public string? GetGestureText(string commandId) =>
        GetGesture(commandId)?.ToString();

    /// <summary>
    /// Returns a display string for the secondary gesture, or null if unbound.
    /// </summary>
    public string? GetGesture2Text(string commandId) =>
        GetGesture2(commandId)?.ToString();

    // =================================================================
    // Chord resolution
    // =================================================================

    /// <summary>
    /// Returns true if the given gesture is the first key of at least one
    /// chord binding. Used by the dispatch loop to enter "waiting for second
    /// key" state.
    /// </summary>
    public bool IsChordPrefix(Key key, KeyModifiers modifiers) {
        var gesture = new KeyGesture(key, modifiers);
        return _chordPrefixes.Contains(gesture);
    }

    /// <summary>
    /// Resolves a two-keystroke chord to a command ID. Returns null if
    /// no chord matches.
    /// </summary>
    public string? ResolveChord(KeyGesture first, Key key, KeyModifiers modifiers) {
        var second = new KeyGesture(key, modifiers);
        return _chordToCommand.TryGetValue((first, second), out var id) ? id : null;
    }

    // =================================================================
    // Binding mutation
    // =================================================================

    /// <summary>
    /// Sets a user override for the primary gesture. Pass null to unbind.
    /// Accepts both single-key and chord gestures.
    /// Rebuilds lookup tables and persists to settings.
    /// </summary>
    public void SetBinding(string commandId, ChordGesture? gesture) {
        _settings.KeyBindingOverrides ??= new Dictionary<string, string>();
        _settings.KeyBindingOverrides[commandId] = gesture?.ToString() ?? "";
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Sets a user override for the secondary gesture. Pass null to unbind.
    /// Accepts both single-key and chord gestures.
    /// Rebuilds lookup tables and persists to settings.
    /// </summary>
    public void SetBinding2(string commandId, ChordGesture? gesture) {
        _settings.KeyBinding2Overrides ??= new Dictionary<string, string>();
        _settings.KeyBinding2Overrides[commandId] = gesture?.ToString() ?? "";
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Removes all user overrides for the given command, restoring the
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

    // =================================================================
    // Conflict detection
    // =================================================================

    /// <summary>
    /// Returns the command ID that conflicts with the given gesture,
    /// excluding the specified command. Handles both single-key gestures
    /// and chords. Returns null if no conflict exists.
    /// </summary>
    public string? FindConflict(ChordGesture gesture, string excludeCommandId) {
        if (gesture.IsChord) {
            if (_chordToCommand.TryGetValue((gesture.First, gesture.Second!), out var id)
                && id != excludeCommandId) {
                return id;
            }
            return null;
        }
        if (_gestureToCommand.TryGetValue(gesture.First, out var sid) && sid != excludeCommandId) {
            return sid;
        }
        return null;
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>
    /// Returns the <see cref="CommandDescriptor"/> for the given command ID,
    /// or null if not found.
    /// </summary>
    public static CommandDescriptor? GetDescriptor(string commandId) =>
        CommandRegistry.All.FirstOrDefault(c => c.Id == commandId);

    /// <summary>
    /// Equality comparer for <c>(KeyGesture, KeyGesture)</c> tuples used
    /// as chord dictionary keys.
    /// </summary>
    private sealed class ChordTupleComparer
        : IEqualityComparer<(KeyGesture, KeyGesture)> {
        public static readonly ChordTupleComparer Instance = new();

        public bool Equals((KeyGesture, KeyGesture) x, (KeyGesture, KeyGesture) y) =>
            KeyGestureComparer.Instance.Equals(x.Item1, y.Item1)
            && KeyGestureComparer.Instance.Equals(x.Item2, y.Item2);

        public int GetHashCode((KeyGesture, KeyGesture) obj) =>
            HashCode.Combine(
                KeyGestureComparer.Instance.GetHashCode(obj.Item1),
                KeyGestureComparer.Instance.GetHashCode(obj.Item2));
    }
}
