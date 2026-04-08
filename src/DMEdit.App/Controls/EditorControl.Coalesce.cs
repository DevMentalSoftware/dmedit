namespace DMEdit.App.Controls;

// Edit-coalescing partial of EditorControl.  Groups consecutive edits
// of the same "kind" into a single compound undo entry.  Shared state
// (_coalesceKey, _compoundOpen, _coalesceTimer) lives in the main
// EditorControl.cs alongside the other fields.
public sealed partial class EditorControl {

    /// <summary>
    /// Commits the current compound edit (if any) and resets coalescing state.
    /// Call before undo, redo, clipboard ops, cursor movement, focus loss, etc.
    /// </summary>
    public void FlushCompound() {
        _coalesceTimer.Stop();
        if (_compoundOpen) {
            Document?.EndCompound();
            _compoundOpen = false;
        }
        _coalesceKey = null;
    }

    /// <summary>
    /// Ensures a compound edit is open for the given coalesce <paramref name="key"/>.
    /// If the key differs from the current one, flushes the old compound first.
    /// Restarts the idle timer so a pause commits the compound automatically.
    /// </summary>
    private void Coalesce(string key) {
        if (_coalesceKey != key) {
            FlushCompound();
        }
        if (!_compoundOpen) {
            Document?.BeginCompound();
            _compoundOpen = true;
        }
        _coalesceKey = key;
        _coalesceTimer.Stop();
        _coalesceTimer.Start();
    }
}
