using CloudFolderBrowser.Providers;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using System.Text;

namespace CloudFolderBrowser.Tests;

public sealed class ProviderRegistryTests
{
    [Theory]
    [InlineData("https://mega.nz/folder/abc#key", CloudServiceType.Mega)]
    [InlineData("https://example.allsync.com/s/key", CloudServiceType.Allsync)]
    [InlineData("https://efss.qloud.example/s/key", CloudServiceType.QCloud)]
    [InlineData("https://yadi.sk/d/example", CloudServiceType.Yadisk)]
    [InlineData("https://www.terabox.com/s/example", CloudServiceType.TeraBox)]
    [InlineData("https://mediafire.com/file/example", CloudServiceType.Other)]
    public void Resolve_RecognizesBuiltInProviders(string url, CloudServiceType expected)
    {
        Assert.Equal(expected, CloudProviderRegistry.Default.Resolve(url)?.ServiceType);
    }

    [Fact]
    public void Resolve_UnsupportedSchemeReturnsNull()
    {
        Assert.Null(CloudProviderRegistry.Default.Resolve("ftp://example.com/files"));
    }

    [Fact]
    public async Task LoadAsync_RunsPortableV2ProviderThroughRegistry()
    {
        var registry = new CloudProviderRegistry();
        registry.Register(new PortableV2Provider());
        CloudFolder folder = await registry.LoadPortableAsync(
            "https://portable.example/share");

        Assert.Equal("portable", folder.Name);
    }

    [Fact]
    public async Task LoadAsync_KeepsV1ProvidersCompatible()
    {
        var registry = new CloudProviderRegistry();
        registry.Register(new LegacyV1Provider());

        CloudFolder folder = await registry.LoadAsync(
            "https://legacy.example/share",
            new CloudProviderLoadContext(new RecordingHost()));

        Assert.Equal("legacy", folder.Name);
    }

    [Fact]
    public async Task LoadPortableAsync_RejectsHostIntegratedProviderClearly()
    {
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => CloudProviderRegistry.Default.LoadPortableAsync(
                "https://efss.qloud.example/s/example"));

        Assert.Contains("requires an application host", exception.Message);
    }

    [Fact]
    public void Register_RejectsUnsupportedContractVersion()
    {
        var registry = new CloudProviderRegistry();

        NotSupportedException exception = Assert.Throws<NotSupportedException>(
            () => registry.Register(new FutureProvider()));

        Assert.Contains("contract v99", exception.Message);
    }

    [Fact]
    public void BuiltIns_ExposeUniqueV2HealthAndResolverMetadata()
    {
        IReadOnlyList<ICloudProviderDescriptor> providers = CloudProviderRegistry.Default.Providers;
        Assert.Equal(
            providers.Count,
            providers.Select(provider => provider.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(
            providers.Where(provider =>
                provider.Capabilities.HasFlag(CloudProviderCapabilities.Browse)),
            provider => Assert.True(
                provider is ICloudFolderProviderV2 or ICloudFolderProvider,
                $"Browse provider '{provider.Id}' has no executable loader."));

        IReadOnlyList<CloudProviderHealthDefinition> health =
            CloudProviderRegistry.Default.GetHealthDefinitions();
        Assert.Contains(health, target => target.RouteKey == "Mega");
        Assert.Contains(health, target => target.RouteKey == "TorBox");

        var profile = new CloudAccountProfile
        {
            Provider = CloudAccountProvider.RealDebrid,
            DisplayName = "test",
            Secret = "not-used"
        };
        IDownloadLinkResolver? resolver = CloudProviderRegistry.Default.CreateLinkResolver(profile);
        Assert.NotNull(resolver);
        Assert.IsAssignableFrom<IDisposable>(resolver).Dispose();
    }

    [Theory]
    [InlineData("yandex", CloudProviderExecutionMode.Portable)]
    [InlineData("h5ai", CloudProviderExecutionMode.Portable)]
    [InlineData("thetrove", CloudProviderExecutionMode.Portable)]
    [InlineData("mega", CloudProviderExecutionMode.Portable)]
    [InlineData("allsync", CloudProviderExecutionMode.Portable)]
    [InlineData("qloud", CloudProviderExecutionMode.HostIntegrated)]
    public void BuiltIns_DeclareTheirExecutionBoundary(
        string providerId,
        CloudProviderExecutionMode expected)
    {
        ICloudProviderV2 provider = Assert.IsAssignableFrom<ICloudProviderV2>(
            CloudProviderRegistry.Default.Providers.Single(item => item.Id == providerId));

        Assert.Equal(expected, provider.ExecutionMode);
    }

    [Fact]
    public async Task H5AiProvider_ParsesNestedIndexWithoutMainForm()
    {
        const string rootHtml = """
            <html><body><table>
              <tr><td><img alt="folder"></td><td><a href="nested/">nested</a></td><td>2026-01-01</td><td>-</td></tr>
              <tr><td><img alt="file"></td><td><a href="root.bin">root.bin</a></td><td>2026-01-02</td><td>1 KB</td></tr>
            </table></body></html>
            """;
        const string nestedHtml = """
            <html><body><table>
              <tr><td><img alt="file"></td><td><a href="child.bin">child.bin</a></td><td>2026-01-03</td><td>2 KiB</td></tr>
            </table></body></html>
            """;
        await using var server = new LoopbackDownloadServer((request, _) => new LoopbackResponse
        {
            ContentType = "text/html; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(
                request.Path.EndsWith("/nested/", StringComparison.Ordinal)
                    ? nestedHtml
                    : rootHtml)
        });
        Uri url = server.Url("h5ailink/");
        var provider = new H5AiIndexProvider();

        CloudFolder root = await provider.LoadAsync(
            new CloudProviderLoadContext(), url.AbsoluteUri);

        CloudFile rootFile = Assert.Single(root.Files);
        Assert.Equal("root.bin", rootFile.Name);
        Assert.Equal(1000, rootFile.Size);
        CloudFolder nested = Assert.IsType<CloudFolder>(Assert.Single(root.Subfolders));
        CloudFile child = Assert.Single(nested.Files);
        Assert.Equal(2048, child.Size);
        Assert.Equal("/nested/child.bin", child.Path);
        Assert.Equal(2, server.Requests.Count);
    }

    [Theory]
    [InlineData("1 KB", 1000)]
    [InlineData("1 KiB", 1024)]
    [InlineData("1.5 MiB", 1572864)]
    [InlineData("unknown", -1)]
    public void WebIndexSizeParser_HandlesDecimalAndBinaryUnits(string value, long expected)
    {
        Assert.Equal(expected, WebIndexProviderUtilities.ParseSizeToBytes(value));
    }

    private sealed class RecordingHost : ICloudProviderHost
    {
        public string? ProviderId { get; private set; }

        public Task<CloudFolder> LoadHostIntegratedAsync(
            string providerId,
            string url,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            ProviderId = providerId;
            return Task.FromResult(CreateFolder("host"));
        }
    }

    private sealed class PortableV2Provider : ICloudFolderProviderV2
    {
        public string Id => "portable-test";
        public string DisplayName => "Portable test";
        public CloudServiceType ServiceType => CloudServiceType.Other;
        public CloudProviderCapabilities Capabilities => CloudProviderCapabilities.Browse;
        public bool CanHandle(Uri uri) => uri.Host == "portable.example";

        public Task<CloudFolder> LoadAsync(
            CloudProviderLoadContext context,
            string url,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateFolder("portable"));
    }

    private sealed class LegacyV1Provider : ICloudFolderProvider
    {
        public string Id => "legacy-test";
        public string DisplayName => "Legacy test";
        public CloudServiceType ServiceType => CloudServiceType.Other;
        public CloudProviderCapabilities Capabilities => CloudProviderCapabilities.Browse;
        public bool CanHandle(Uri uri) => uri.Host == "legacy.example";

        public Task<CloudFolder> LoadAsync(
            string url,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateFolder("legacy"));
    }

    private sealed class FutureProvider : ICloudProviderV2
    {
        public int ContractVersion => 99;
        public string Id => "future-test";
        public string DisplayName => "Future test";
        public CloudServiceType ServiceType => CloudServiceType.Other;
        public CloudProviderCapabilities Capabilities => CloudProviderCapabilities.None;
        public bool CanHandle(Uri uri) => false;
    }

    private static CloudFolder CreateFolder(string name) =>
        new(name, DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
}
