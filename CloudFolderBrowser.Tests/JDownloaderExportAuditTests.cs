using System.IO.Compression;
using CloudFolderBrowser.JDownloader;
using Newtonsoft.Json.Linq;

namespace CloudFolderBrowser.Tests;

public sealed class JDownloaderExportAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-export-audit-" + Guid.NewGuid().ToString("N"));
    public JDownloaderExportAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Export_DoesNotRemoveAnExistingLinksDirectory()
    {
        string sentinel = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Links", "user-data")).FullName, "keep.txt");
        File.WriteAllText(sentinel, "retain this data");
        string archivePath = Export("fixture");
        Assert.Equal("retain this data", File.ReadAllText(sentinel));
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.Equal(2, archive.Entries.Count);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("../outside")]
    public void Export_SanitizesOutputFolderAndStaysWithinExportRoot(string name)
    {
        string archive = Export(name);
        string expected = Path.Combine(_root, "linkcontainers", Utility.GetSafePathName(name));
        Assert.Equal(expected, Path.GetDirectoryName(archive));
    }

    [Fact]
    public void Export_FailureDoesNotLeavePartialArchiveOrChangeExistingData()
    {
        string sentinel = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "Links")).FullName, "keep.txt");
        File.WriteAllText(sentinel, "existing data");
        int calls = 0;
        Assert.Throws<InvalidDataException>(() => JDownloaderExport.CreateArchive(new[] { CreateFile(), CreateFile() }, "fixture", _root,
            file => ++calls == 2 ? throw new InvalidDataException("fixture failure") : SyncFilesForm.CreateJdExportLink(file)));
        Assert.Equal("existing data", System.IO.File.ReadAllText(sentinel));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.zip", SearchOption.AllDirectories));
    }

    [Fact]
    public void Export_WritesKnownEmptySizeAndEncodedUrlIntoZipEntry()
    {
        using var archive = ZipFile.OpenRead(Export("fixture"));
        using var reader = new StreamReader(archive.GetEntry("000_000")!.Open());
        var link = JObject.Parse(reader.ReadToEnd())["downloadLink"]!;
        Assert.Equal(0, (long)link["size"]!);
        Assert.Equal(CreateFile().PublicUrl.AbsoluteUri, (string)link["url"]!);
    }

    [Fact]
    public void Export_SeparateRunsKeepBothArchives()
    {
        string first = Export("fixture");
        byte[] original = File.ReadAllBytes(first);
        string second = Export("fixture");
        Assert.NotEqual(first, second);
        Assert.Equal(original, File.ReadAllBytes(first));
        Assert.True(File.Exists(second));
    }

    private string Export(string folder) => JDownloaderExport.CreateArchive(new[] { CreateFile() }, folder, _root, SyncFilesForm.CreateJdExportLink);
    private static CloudFile CreateFile() => new("file.bin", DateTime.MinValue, DateTime.MinValue, 0)
    { Path = "/file.bin", PublicUrl = new Uri("https://example.com/a%23b.bin?token=a%2Bb") };
    public void Dispose() => Directory.Delete(_root, true);
}
