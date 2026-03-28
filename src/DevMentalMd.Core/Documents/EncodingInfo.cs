using System.Text;

namespace DevMentalMd.Core.Documents;

/// <summary>
/// Represents the file encoding of a document.
/// Named <c>FileEncoding</c> to avoid collision with <see cref="System.Text.Encoding"/>.
/// </summary>
public enum FileEncoding {
    /// <summary>UTF-8 without BOM.</summary>
    Utf8,

    /// <summary>UTF-8 with BOM (EF BB BF).</summary>
    Utf8Bom,

    /// <summary>UTF-16 Little Endian (with BOM FF FE).</summary>
    Utf16Le,

    /// <summary>UTF-16 Big Endian (with BOM FE FF).</summary>
    Utf16Be,

    /// <summary>Windows-1252 (Western European).</summary>
    Windows1252,

    /// <summary>US-ASCII (7-bit).</summary>
    Ascii,

    /// <summary>Not yet detected — set before file loading completes.</summary>
    Unknown,
}

/// <summary>
/// Tracks the detected (or user-assigned) file encoding for a document.
/// Follows the same pattern as <see cref="LineEndingInfo"/> and <see cref="IndentInfo"/>.
/// </summary>
public readonly record struct EncodingInfo(FileEncoding Encoding) {
    /// <summary>Display label for the status bar.</summary>
    public string Label => Encoding switch {
        FileEncoding.Utf8 => "UTF-8",
        FileEncoding.Utf8Bom => "UTF-8 with BOM",
        FileEncoding.Utf16Le => "UTF-16 LE",
        FileEncoding.Utf16Be => "UTF-16 BE",
        FileEncoding.Windows1252 => "Windows-1252",
        FileEncoding.Ascii => "ASCII",
        FileEncoding.Unknown => "",
        _ => "UTF-8",
    };

    /// <summary>Default encoding: UTF-8 without BOM.</summary>
    public static EncodingInfo Default => new(FileEncoding.Utf8);

    /// <summary>
    /// Returns the <see cref="System.Text.Encoding"/> instance for this encoding.
    /// </summary>
    public Encoding GetDotNetEncoding() => Encoding switch {
        FileEncoding.Utf8 => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        FileEncoding.Utf8Bom => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        FileEncoding.Utf16Le => new UnicodeEncoding(bigEndian: false, byteOrderMark: true),
        FileEncoding.Utf16Be => new UnicodeEncoding(bigEndian: true, byteOrderMark: true),
        FileEncoding.Windows1252 => System.Text.Encoding.GetEncoding(1252),
        FileEncoding.Ascii => System.Text.Encoding.ASCII,
        FileEncoding.Unknown => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    /// <summary>
    /// Returns the BOM bytes to write at the start of the file, or <c>null</c> if
    /// this encoding does not use a BOM.
    /// </summary>
    public byte[]? GetPreamble() => Encoding switch {
        FileEncoding.Utf8Bom => [0xEF, 0xBB, 0xBF],
        FileEncoding.Utf16Le => [0xFF, 0xFE],
        FileEncoding.Utf16Be => [0xFE, 0xFF],
        _ => null,
    };

    /// <summary>
    /// Maps from a detected <see cref="System.Text.Encoding"/> and BOM presence
    /// to an <see cref="EncodingInfo"/>. Used by buffer classes after BOM detection.
    /// </summary>
    public static EncodingInfo FromDetection(Encoding encoding, bool hadBom) {
        if (encoding is UTF8Encoding || encoding.CodePage == 65001) {
            return new EncodingInfo(hadBom ? FileEncoding.Utf8Bom : FileEncoding.Utf8);
        }
        if (encoding is UnicodeEncoding ue) {
            // UnicodeEncoding: CodePage 1200 = LE, 1201 = BE
            return encoding.CodePage == 1201
                ? new EncodingInfo(FileEncoding.Utf16Be)
                : new EncodingInfo(FileEncoding.Utf16Le);
        }
        if (encoding.CodePage == 1252) {
            return new EncodingInfo(FileEncoding.Windows1252);
        }
        if (encoding.CodePage == 20127) {
            return new EncodingInfo(FileEncoding.Ascii);
        }
        // Fallback
        return Default;
    }
}
