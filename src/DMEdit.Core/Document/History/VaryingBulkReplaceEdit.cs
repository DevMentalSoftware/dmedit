namespace DMEdit.Core.Documents.History;

/// <summary>
/// Records a bulk replace where matches have varying lengths and/or varying
/// replacement strings (e.g. regex replace, indentation conversion).
/// Undo restores the saved piece list and line tree in O(1).
/// </summary>
public sealed class VaryingBulkReplaceEdit : IDocumentEdit {
    private readonly (long Pos, int Len)[] _matches;
    private readonly string[] _replacements;
    private readonly Piece[] _savedPieces;
    private readonly int[] _savedLineLengths;
    private readonly int[] _savedDocLineLengths;
    private readonly long _savedAddBufLen;

    public VaryingBulkReplaceEdit(
        (long Pos, int Len)[] matches, string[] replacements,
        Piece[] savedPieces, int[] savedLineLengths, int[] savedDocLineLengths,
        long savedAddBufLen) {
        _matches = matches;
        _replacements = replacements;
        _savedPieces = savedPieces;
        _savedLineLengths = savedLineLengths;
        _savedDocLineLengths = savedDocLineLengths;
        _savedAddBufLen = savedAddBufLen;
    }

    /// <summary>Match positions and lengths.</summary>
    public (long Pos, int Len)[] Matches => _matches;

    /// <summary>Per-match replacement strings.</summary>
    public string[] Replacements => _replacements;

    /// <summary>Number of matches.</summary>
    public int MatchCount => _matches.Length;

    public void Apply(PieceTable table) {
        table.BulkReplace(_matches, _replacements);
    }

    public void Revert(PieceTable table) {
        table.TrimAddBuffer(_savedAddBufLen);
        table.RestorePieces(_savedPieces);
        table.InstallLineTree(_savedLineLengths, _savedDocLineLengths);
    }
}
