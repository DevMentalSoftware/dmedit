using System.IO.Compression;
using System.Text;
using DevMentalMd.Core.IO;

namespace DevMentalMd.Core.Tests;

/// <summary>
/// Tests for transparent zip file detection and loading in <see cref="FileLoader"/>.
/// </summary>
public class ZipFileTests : IDisposable {
    private readonly List<string> _tempFiles = [];

    public void Dispose() {
        foreach (var path in _tempFiles) {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    private string TempPath(string ext = ".zip") {
        var path = Path.Combine(Path.GetTempPath(), $"devmentalmd_test_{Guid.NewGuid():N}{ext}");
        _tempFiles.Add(path);
        return path;
    }

    private static string CreateSingleEntryZip(string zipPath, string entryName, string content) {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
        return zipPath;
    }

    private static string CreateMultiEntryZip(string zipPath, params (string name, string content)[] entries) {
        using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries) {
            var entry = zip.CreateEntry(name);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        return zipPath;
    }

    // -----------------------------------------------------------------
    // IsZipFile
    // -----------------------------------------------------------------

    [Fact]
    public void IsZipFile_ReturnsTrueForZip() {
        var path = TempPath();
        CreateSingleEntryZip(path, "test.txt", "hello");
        Assert.True(FileLoader.IsZipFile(path));
    }

    [Fact]
    public void IsZipFile_ReturnsFalseForPlainText() {
        var path = TempPath(".txt");
        File.WriteAllText(path, "Just plain text", Encoding.UTF8);
        Assert.False(FileLoader.IsZipFile(path));
    }

    [Fact]
    public void IsZipFile_ReturnsFalseForEmptyFile() {
        var path = TempPath(".txt");
        File.WriteAllBytes(path, []);
        Assert.False(FileLoader.IsZipFile(path));
    }

    [Fact]
    public void IsZipFile_ReturnsFalseForShortFile() {
        var path = TempPath(".txt");
        File.WriteAllBytes(path, [0x50, 0x4B]); // only 2 bytes
        Assert.False(FileLoader.IsZipFile(path));
    }

    // -----------------------------------------------------------------
    // Load / LoadAsync — single-entry zip
    // -----------------------------------------------------------------

    [Fact]
    public void Load_SingleEntryZip_ReturnsContent() {
        var path = TempPath();
        var content = "# Hello from zip\n\nThis is the inner file.\n";
        CreateSingleEntryZip(path, "readme.md", content);

        var result = FileLoader.Load(path);

        Assert.True(result.WasZipped);
        Assert.Equal("readme.md", result.InnerEntryName);
        Assert.Contains("\u2192", result.DisplayName); // → arrow
        Assert.Contains("readme.md", result.DisplayName);
        Assert.Equal(content, result.Document.Table.GetText());
    }

    [Fact]
    public async Task LoadAsync_SingleEntryZip_ReturnsContent() {
        var path = TempPath();
        var content = "Line 1\nLine 2\nLine 3\n";
        CreateSingleEntryZip(path, "data.txt", content);

        var result = await FileLoader.LoadAsync(path);

        Assert.True(result.WasZipped);
        Assert.Equal("data.txt", result.InnerEntryName);
        Assert.Equal(content, result.Document.Table.GetText());
    }

    [Fact]
    public void Load_SingleEntryZip_MultiLineContent() {
        var path = TempPath();
        var sb = new StringBuilder();
        for (var i = 0; i < 100; i++) {
            sb.AppendLine($"Line {i}: The quick brown fox jumps over the lazy dog.");
        }
        var content = sb.ToString();
        CreateSingleEntryZip(path, "lines.txt", content);

        var result = FileLoader.Load(path);

        Assert.True(result.WasZipped);
        Assert.Equal(content, result.Document.Table.GetText());
    }

    // -----------------------------------------------------------------
    // Load — multi-entry zip (should fail)
    // -----------------------------------------------------------------

    [Fact]
    public void Load_MultiEntryZip_Throws() {
        var path = TempPath();
        CreateMultiEntryZip(path,
            ("file1.txt", "content1"),
            ("file2.txt", "content2"));

        Assert.Throws<IOException>(() => FileLoader.Load(path));
    }

    [Fact]
    public void Load_EmptyZip_NotDetectedAsZip() {
        // An empty ZipArchive has no local file headers (PK 03 04), only the
        // end-of-central-directory record (PK 05 06). IsZipFile returns false,
        // so the file is loaded as plain text (binary garbage, but no crash).
        var path = TempPath();
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var _ = new ZipArchive(fs, ZipArchiveMode.Create)) {
            // empty archive
        }
        Assert.False(FileLoader.IsZipFile(path));
    }

    // -----------------------------------------------------------------
    // Load — plain text (non-zip)
    // -----------------------------------------------------------------

    [Fact]
    public void Load_PlainText_ReturnsNotZipped() {
        var path = TempPath(".txt");
        var content = "Hello, world!\n";
        File.WriteAllText(path, content, Encoding.UTF8);

        var result = FileLoader.Load(path);

        Assert.False(result.WasZipped);
        Assert.Null(result.InnerEntryName);
        Assert.Equal(content, result.Document.Table.GetText());
    }

    [Fact]
    public async Task LoadAsync_PlainText_ReturnsNotZipped() {
        var path = TempPath(".txt");
        var content = "Hello async!\n";
        File.WriteAllText(path, content, Encoding.UTF8);

        var result = await FileLoader.LoadAsync(path);

        Assert.False(result.WasZipped);
        Assert.Null(result.InnerEntryName);
        Assert.Equal(content, result.Document.Table.GetText());
    }

    // -----------------------------------------------------------------
    // DisplayName format
    // -----------------------------------------------------------------

    [Fact]
    public void Load_ZipDisplayName_ContainsArrowAndEntryName() {
        var path = TempPath();
        CreateSingleEntryZip(path, "model.xml", "<root/>");

        var result = FileLoader.Load(path);

        var fileName = Path.GetFileName(path);
        Assert.StartsWith(fileName, result.DisplayName);
        Assert.Contains("model.xml", result.DisplayName);
    }

    [Fact]
    public void Load_PlainTextDisplayName_IsFileName() {
        var path = TempPath(".md");
        File.WriteAllText(path, "test", Encoding.UTF8);

        var result = FileLoader.Load(path);

        Assert.Equal(Path.GetFileName(path), result.DisplayName);
    }
}
