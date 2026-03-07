namespace DevMentalMd.App.Commands;

/// <summary>
/// Metadata describing a single application command: its identity and display
/// name. The <see cref="Category"/> is derived from the <see cref="Id"/>
/// prefix (e.g. <c>"Edit.Undo"</c> → <c>"Edit"</c>), so they can never
/// drift out of sync. Keyboard bindings come from the active profile JSON,
/// not from this record.
/// </summary>
public sealed record CommandDescriptor(string Id, string DisplayName) {
    /// <summary>
    /// The category prefix extracted from <see cref="Id"/>
    /// (everything before the first dot).
    /// </summary>
    public string Category => Id[..Id.IndexOf('.')];
}
