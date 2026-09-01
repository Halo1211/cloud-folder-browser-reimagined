namespace CloudFolderBrowser.Tests;

public sealed class UtilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-tests-" + Guid.NewGuid().ToString("N"));

    public UtilityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void SafeDownloadPath_RejectsParentTraversal()
    {
        Assert.Throws<InvalidDataException>(() =>
            Utility.GetSafeDownloadPath(_root, "/folder/../outside.txt"));
    }

    [Fact]
    public void SafeDownloadPath_NormalizesReservedWindowsName()
    {
        string result = Utility.GetSafeDownloadPath(_root, "/folder/CON.txt");
        Assert.EndsWith(Path.Combine("folder", "_CON.txt"), result, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Path.GetFullPath(_root), result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyFileNameOverride_PreservesCloudDirectory()
    {
        Assert.Equal("/a/b/new.txt", Utility.ApplyFileNameOverride("/a/b/old.txt", "new.txt"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }
}
