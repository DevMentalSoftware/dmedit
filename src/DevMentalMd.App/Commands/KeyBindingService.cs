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
/// single keystroke or a two-keystroke chord. Loads base bindings from the
/// active profile JSON, overlays user overrides from
/// <see cref="AppSettings"/>, and provides O(1) gesture-to-command resolution
/// for dispatch.
/// </summary>
public class KeyBindingService {
    private readonly AppSettings _settings;
    private readonly CommandRegistry _commands;
    private ProfileData _activeProfile;
    private string _activeProfileId;

    // Single-key lookups (non-chord gestures only).
    private Dictionary<KeyGesture, string> _gestureToCommand = new(KeyGestureComparer.Instance);
    private Dictionary<string, ChordGesture?> _commandToGesture = new();
    private Dictionary<string, ChordGesture?> _commandToGesture2 = new();

    // Chord lookups (derived from ChordGesture instances where IsChord).
    private HashSet<KeyGesture> _chordPrefixes = new(KeyGestureComparer.Instance);
    private Dictionary<(KeyGesture, KeyGesture), string> _chordToCommand = new(ChordTupleComparer.Instance);

    // Reserved Alt+letter gestures for top-level menu access keys.
    private static readonly (string CmdId, Key Key)[] MenuAccessKeys = [
        ("Menu.File", Key.F),
        ("Menu.Edit", Key.E),
        ("Menu.Search", Key.S),
        ("Menu.View", Key.V),
        ("Menu.Help", Key.H),
    ];

    public KeyBindingService(AppSettings settings, CommandRegistry commands) {
        _settings = settings;
        _commands = commands;
        _activeProfileId = settings.ActiveProfile ?? "Default";
        _activeProfile = ProfileLoader.Load(_activeProfileId);
        Rebuild();
    }

    // =================================================================
    // Profile management
    // =================================================================

    /// <summary>
    /// The identifier of the currently active profile (e.g. "Default", "VSCode").
    /// </summary>
    public CommandRegistry Registry => _commands;

    public string ActiveProfileId => _activeProfileId;

    /// <summary>
    /// Switches to a different profile. Rebuilds lookup tables and persists
    /// the choice. Does NOT clear user overrides — the caller decides.
    /// </summary>
    public void SetProfile(string profileId) {
        _activeProfileId = profileId;
        _activeProfile = ProfileLoader.Load(profileId);
        _settings.ActiveProfile = profileId == "Default" ? null : profileId;
        _settings.ScheduleSave();
        Rebuild();
    }

    /// <summary>
    /// Returns the profile's default gesture for a command (slot 1 or 2),
    /// without considering user overrides. Used by the UI to show "modified"
    /// indicators when the effective binding differs from the profile default.
    /// </summary>
    public ChordGesture? GetProfileDefault(string commandId, int slot) =>
        GetProfileGesture(commandId, slot);

    /// <summary>
    /// Rebuilds the internal lookup tables from the active profile and user
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

        // Pass 0: reserve Alt+letter gestures for top-level menu access keys.
        // These are registered in both gestureToCmd (for conflict detection)
        // and cmdToGesture (for the settings UI). The dispatch code in
        // MainWindow treats Menu.* commands specially — see TryOpenMenuAccessKey.
        foreach (var (cmdId, key) in MenuAccessKeys) {
            var g = new ChordGesture(new KeyGesture(key, KeyModifiers.Alt));
            cmdToGesture[cmdId] = g;
            RegisterGesture(g, cmdId, gestureToCmd, chordToCmd, chordPrefixes);
        }

        // Pass 1: apply profile defaults for commands that have no user override.
        // Skip Menu.* pseudo-commands — their bindings are fixed in pass 0.
        foreach (var cmd in _commands.All) {
            if (cmd.Id.StartsWith("Menu.", StringComparison.Ordinal)) continue;
            if (overrides1 == null || !overrides1.ContainsKey(cmd.Id)) {
                var gesture = GetProfileGesture(cmd.Id, slot: 1);
                cmdToGesture[cmd.Id] = gesture;
                RegisterGesture(gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }

            if (overrides2 == null || !overrides2.ContainsKey(cmd.Id)) {
                var gesture = GetProfileGesture(cmd.Id, slot: 2);
                cmdToGesture2[cmd.Id] = gesture;
                RegisterGesture(gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }
        }

        // Pass 2: apply user overrides. These are processed last so they
        // always win when a conflict exists.
        if (overrides1 != null) {
            foreach (var cmd in _commands.All) {
                if (cmd.Id.StartsWith("Menu.", StringComparison.Ordinal)) continue;
                if (!overrides1.TryGetValue(cmd.Id, out var str)) continue;
                var gesture = string.IsNullOrEmpty(str) ? null : ChordGesture.Parse(str);
                cmdToGesture[cmd.Id] = gesture;
                RegisterGesture(gesture, cmd.Id, gestureToCmd, chordToCmd, chordPrefixes);
            }
        }

        if (overrides2 != null) {
            foreach (var cmd in _commands.All) {
                if (cmd.Id.StartsWith("Menu.", StringComparison.Ordinal)) continue;
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
    /// Gets the gesture for a command from the active profile.
    /// Returns null if the profile does not define a binding.
    /// </summary>
    private ChordGesture? GetProfileGesture(string commandId, int slot) {
        var dict = slot == 1 ? _activeProfile.Bindings : _activeProfile.Bindings2;
        if (dict != null && dict.TryGetValue(commandId, out var str) && !string.IsNullOrEmpty(str)) {
            return ChordGesture.Parse(str);
        }
        return null;
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
    /// primary and secondary bindings from the active profile.
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
    /// Removes all user overrides, restoring every binding to the
    /// active profile defaults.
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
    /// Returns the <see cref="Command"/> for the given command ID,
    /// or null if not found.
    /// </summary>
    public Command? GetCommand(string commandId) => _commands.TryGet(commandId);

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
