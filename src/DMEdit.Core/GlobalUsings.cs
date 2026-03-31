// Semantic type aliases for numeric values used throughout the document model.
// These are zero-cost (same IL as the underlying type) but make intent clear
// at every declaration and cast site.
//
// BufOffset:  a character position in buffer-space (real buffer chars).
//             Used for PieceTable text operations: Insert, Delete, GetText,
//             FindPiece, CharAt, etc.
//
// DocOffset:  a character position in document-space (includes virtual
//             pseudo-terminators).  Used for Selection, caret, layout, and
//             navigation.  Equals BufOffset when no pseudo-lines exist.
//
// LineIndex:  a zero-based line number.  int is sufficient — 2 billion lines
//             would require ~8 GB just for the line index array.

global using BufOffset = long;
global using DocOffset = long;
global using LineIndex = int;
