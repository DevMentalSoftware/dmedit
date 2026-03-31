namespace DMEdit.Core.Printing;

/// <summary>
/// Describes an available system printer.
/// </summary>
public sealed class PrinterInfo {
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
}
