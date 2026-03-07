using Avalonia.Input;

namespace DevMentalMd.App.Commands;

/// <summary>
/// Metadata describing a single application command: its identity, display
/// name, category, and default keyboard shortcut.
/// </summary>
public sealed record CommandDescriptor(
    string Id,
    string DisplayName,
    string Category,
    KeyGesture? DefaultGesture = null);
