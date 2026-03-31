using System.Collections.Generic;

namespace DMEdit.App.Commands;

/// <summary>
/// Deserialization target for embedded profile JSON resources.
/// Maps command IDs to gesture strings for primary and secondary slots.
/// </summary>
public sealed class ProfileData {
    /// <summary>Display name shown in the profile dropdown (e.g. "VS Code").</summary>
    public string Name { get; set; } = "";

    /// <summary>Primary gesture bindings. Key = command ID, value = gesture string.</summary>
    public Dictionary<string, string>? Bindings { get; set; }

    /// <summary>Secondary gesture bindings. Key = command ID, value = gesture string.</summary>
    public Dictionary<string, string>? Bindings2 { get; set; }
}
