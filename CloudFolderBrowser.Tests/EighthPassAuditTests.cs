using CloudFolderBrowser.Providers;
using System.Text;

namespace CloudFolderBrowser.Tests;

public sealed class EighthPassAuditTests
{
    [Theory]
    [InlineData("")]
    [InlineData("relative\\payload.bin")]
    [InlineData("C:\\")]
    [InlineData("C:\\bad\0name.bin")]
    public void Retry_RejectsInvalidSavePaths(string savePath)
    {
        var entry = RetryEntry(savePath);

        Assert.Null(entry.ToRetryFile());
        Assert.Empty(MainForm.GetRetryableEntries(new[] { entry }));
    }

    [Fact]
    public void Retry_RejectsNegativeExpectedSize()
    {
        DownloadHistoryEntry entry = RetryEntry(Path.Combine(Path.GetTempPath(), "retry.bin"));
        entry.ExpectedSize = -1;

        Assert.Null(entry.ToRetryFile());
        Assert.Empty(MainForm.GetRetryableEntries(new[] { entry }));
    }

    [Fact]
    public void Retry_RejectsMalformedSourceBeforeCredentialLookup()
    {
        DownloadHistoryEntry entry = RetryEntry(Path.Combine(Path.GetTempPath(), "retry.bin"));
        entry.CloudService = CloudServiceType.Allsync;
        entry.SourceUrl = "not a URL";

        Assert.Empty(MainForm.GetRetryableEntries(new[] { entry }));
    }

    [Fact]
    public void Retry_PreservesValidAbsoluteDestination()
    {
        string destination = Path.Combine(Path.GetTempPath(), "retry.bin");
        DownloadHistoryEntry entry = RetryEntry(destination);

        Assert.Equal(destination, entry.ToRetryFile()?.LocalSavePathOverride);
        Assert.Single(MainForm.GetRetryableEntries(new[] { entry }));
    }

    [Theory]
    [InlineData("https://host.example/root/%2e%2e/private.bin")]
    [InlineData("https://host.example/root/%252e%252e/private.bin")]
    [InlineData("https://host.example/root/sub%2f..%2fprivate.bin")]
    [InlineData("https://host.example/root/sub%5c..%5cprivate.bin")]
    public void WebIndex_RejectsAmbiguousEscapedTraversal(string candidate)
    {
        Assert.False(WebIndexProviderUtilities.IsWithinRoot(
            new Uri("https://host.example/root/"), new Uri(candidate)));
    }

    [Fact]
    public void WebIndex_AllowsOrdinaryEscapedFileName()
    {
        Assert.True(WebIndexProviderUtilities.IsWithinRoot(
            new Uri("https://host.example/root/"),
            new Uri("https://host.example/root/a%20b.bin")));
    }

    [Fact]
    public async Task H5Ai_SkipsMalformedLinkAndKeepsValidFiles()
    {
        const string html = """
            <html><body><table>
              <tr><td><img alt="file"></td><td><a href="http://[">broken.bin</a></td><td>2026-01-01</td><td>1 KB</td></tr>
              <tr><td><img alt="file"></td><td><a href="good.bin">good.bin</a></td><td>2026-01-01</td><td>2 KB</td></tr>
            </table></body></html>
            """;
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            ContentType = "text/html; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(html)
        });

        CloudFolder root = await new H5AiIndexProvider().LoadAsync(
            new CloudProviderLoadContext(), server.Url("h5ailink/").AbsoluteUri);

        Assert.Equal("good.bin", Assert.Single(root.Files).Name);
    }

    [Fact]
    public void ProviderRegistry_ContinuesAfterBrokenPluginMatcher()
    {
        var registry = new CloudProviderRegistry();
        registry.Register(new TestProvider("working", shouldThrow: false));
        registry.Register(new TestProvider("broken", shouldThrow: true));

        Assert.Equal("working", registry.Resolve("https://example.com/file")?.Id);
    }

    private static DownloadHistoryEntry RetryEntry(string savePath) => new()
    {
        FileName = "retry.bin",
        CloudPath = "/retry.bin",
        SourceUrl = "https://example.com/retry.bin",
        SavePath = savePath,
        ExpectedSize = 10
    };

    private sealed class TestProvider(string id, bool shouldThrow) : ICloudProviderDescriptor
    {
        public string Id => id;
        public string DisplayName => id;
        public CloudServiceType ServiceType => CloudServiceType.Other;
        public CloudProviderCapabilities Capabilities => CloudProviderCapabilities.Browse;
        public bool CanHandle(Uri uri) => shouldThrow
            ? throw new InvalidOperationException("Plugin matcher failed")
            : uri.Host == "example.com";
    }
}
