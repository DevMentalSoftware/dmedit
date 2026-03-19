// Semantic type aliases for numeric values used throughout the document model.
// These are zero-cost (same IL as the underlying type) but make intent clear
// at every declaration and cast site.
//
// CharOffset: a character position measured from the start of the document.
//             Must be long because documents can exceed 2 GB.
//
// LineIndex:  a zero-based line number.  int is sufficient — 2 billion lines
//             would require ~8 GB just for the line index array.

global using CharOffset = long;
global using LineIndex = int;
