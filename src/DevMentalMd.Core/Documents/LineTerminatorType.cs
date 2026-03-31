namespace DevMentalMd.Core.Documents;

/// <summary>
/// The type of terminator at the end of a line in the line tree.
/// Used to compute content length (excluding dead zone) from the
/// full line length stored in the tree.
/// </summary>
public enum LineTerminatorType : byte {
    /// <summary>No terminator — final line of the document.</summary>
    None,

    /// <summary>Unix-style: <c>\n</c> (1 char dead zone).</summary>
    LF,

    /// <summary>Classic Mac-style: <c>\r</c> (1 char dead zone).</summary>
    CR,

    /// <summary>Windows-style: <c>\r\n</c> (2 char dead zone).</summary>
    CRLF,

    /// <summary>Pseudo-line split at <see cref="PieceTable.MaxPseudoLine"/> (0 char dead zone).</summary>
    Pseudo,
}

public static class LineTerminatorTypeExtensions {
    /// <summary>
    /// Returns the number of real buffer characters consumed by the dead zone
    /// (terminator) at the end of a line.
    /// </summary>
    public static int DeadZoneWidth(this LineTerminatorType type) => type switch {
        LineTerminatorType.None => 0,
        LineTerminatorType.LF => 1,
        LineTerminatorType.CR => 1,
        LineTerminatorType.CRLF => 2,
        LineTerminatorType.Pseudo => 0,
        _ => 0,
    };

    /// <summary>
    /// Returns the dead zone width in document-offset space.  Same as
    /// <see cref="DeadZoneWidth"/> for real terminators, but 1 for
    /// pseudo-terminators (the virtual gap).
    /// </summary>
    public static int VirtualDeadZoneWidth(this LineTerminatorType type) => type switch {
        LineTerminatorType.None => 0,
        LineTerminatorType.LF => 1,
        LineTerminatorType.CR => 1,
        LineTerminatorType.CRLF => 2,
        LineTerminatorType.Pseudo => 1,
        _ => 0,
    };
}
