using Avalonia.Input;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Metadata describing a single application command: its identity, display
/// name, and default keyboard shortcuts. The <see cref="Category"/> is
/// derived from the <see cref="Id"/> prefix (e.g. <c>"Edit.Undo"</c> →
/// <c>"Edit"</c>), so they can never drift out of sync.
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    string DisplayName,
    KeyGesture? Gesture = null,
    KeyGesture? Gesture2 = null) {
    /// <summary>
    /// The category prefix extracted from <see cref="Id"/>
    /// (everything before the first dot).
    /// </summary>
    public string Category => Id[..Id.IndexOf('.')];
}
