namespace CloudFolderBrowser.Tests;

public sealed class FolderTests
{
    [Fact]
    public void CalculateFolderSize_IsIdempotent()
    {
        var child = new CloudFolder("child", DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/child/",
            SizeTopDirectoryOnly = 20
        };
        child.AddFile(new CloudFile("child.bin", DateTime.MinValue, DateTime.MinValue, 20));
        var root = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0)
        {
            Path = "/",
            SizeTopDirectoryOnly = 10
        };
        root.AddFile(new CloudFile("root.bin", DateTime.MinValue, DateTime.MinValue, 10));
        root.AddSubfolder(child);

        root.CalculateFolderSize();
        long firstSize = root.Size;
        int firstCount = root.FilesNumber;
        root.CalculateFolderSize();

        Assert.Equal(30, firstSize);
        Assert.Equal(firstSize, root.Size);
        Assert.Equal(2, firstCount);
        Assert.Equal(firstCount, root.FilesNumber);
    }

    [Fact]
    public void SaveToJson_RejectsFolderWithoutSourceUrl()
    {
        var folder = new CloudFolder("empty", DateTime.MinValue, DateTime.MinValue, 0);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(folder.SaveToJson);

        Assert.Contains("source URL", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YandexResourceConversion_PreservesFileSize()
    {
        var resource = new YandexDiskSharp.Models.Resource(
            "public-key",
            "archive.bin",
            DateTime.UtcNow.AddDays(-2),
            DateTime.UtcNow.AddDays(-1),
            "/archive.bin",
            "application/octet-stream",
            123_456,
            "data",
            new Uri("https://disk.yandex.example/archive.bin"));

        var file = new CloudFile(resource);

        Assert.Equal(123_456, file.Size);
        Assert.True(file.HasKnownSize);
    }
}
