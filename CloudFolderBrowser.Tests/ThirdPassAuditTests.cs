using System.IO.Compression;
using System.Text;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.Tests;

[Collection("Real download integration")]
public sealed class ThirdPassAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-third-audit-" + Guid.NewGuid().ToString("N"));
    private readonly bool _scoring = Properties.Settings.Default.routeScoringEnabled;
    private readonly bool _archives = Properties.Settings.Default.archivePostProcessingEnabled;
    private readonly bool _repair = Properties.Settings.Default.par2RepairEnabled;
    private readonly string _archiveTool = Properties.Settings.Default.archiveToolPath;
    private readonly string _par2Tool = Properties.Settings.Default.par2ToolPath;
    private readonly string _password = Properties.Settings.Default.protectedArchivePassword;

    public ThirdPassAuditTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("factory")]
    [InlineData("unsupported")]
    [InlineData("timeout")]
    public async Task AutomaticRoute_ContinuesAfterUnavailableResolver(string failure)
    {
        var first = Account(CloudAccountProvider.AllDebrid);
        var second = Account(CloudAccountProvider.TorBox);
        using var automatic = new AutomaticDownloadLinkResolver(new[] { first, second }, null, account =>
        {
            if (account.Id == first.Id)
            {
                if (failure == "factory") throw new InvalidOperationException("Resolver cannot initialize");
                if (failure == "unsupported") throw new NotSupportedException("Resolver unavailable");
                return new TestResolver(() => throw new TaskCanceledException("Provider request timed out"));
            }
            return new TestResolver(() => new DebridResolvedLink(new Uri("https://cdn.example/result.bin")));
        });
        CloudFile file = RequiredFile();

        DebridResolvedLink result = await automatic.ResolveAsync(file, file.PublicUrl!, CancellationToken.None);

        Assert.Equal(DownloadRouteIds.ForAccount(second.Id), result.RouteId);
    }

    [Fact]
    public async Task AutomaticRoute_CompatibilityFlagKeepsAutomaticScoringAfterCooldown()
    {
        Properties.Settings.Default.routeScoringEnabled = false;
        DateTime now = new(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        var first = Account(CloudAccountProvider.AllDebrid);
        var second = Account(CloudAccountProvider.TorBox);
        int firstAttempts = 0;
        using var automatic = new AutomaticDownloadLinkResolver(new[] { first, second }, null, account =>
            account.Id == first.Id
                ? new TestResolver(() => { firstAttempts++; throw new InvalidOperationException("Temporary failure"); })
                : new TestResolver(() => new DebridResolvedLink(new Uri("https://cdn.example/result.bin"))), () => now);
        CloudFile learning = RequiredFile();
        await automatic.ResolveAsync(learning, learning.PublicUrl!, CancellationToken.None);
        now = now.AddMinutes(2);
        CloudFile next = RequiredFile();

        await automatic.ResolveAsync(next, next.PublicUrl!, CancellationToken.None);

        Assert.Equal(1, firstAttempts);
    }

    [Theory]
    [InlineData("film2.par2")]
    [InlineData("filmography.par2")]
    [InlineData("film.7z.par2")]
    public async Task ArchiveRepair_DoesNotChooseUnrelatedPar2(string otherName)
    {
        ConfigureArchive();
        Properties.Settings.Default.par2RepairEnabled = true;
        Properties.Settings.Default.par2ToolPath = Path.Combine(_root, "missing-tool.exe");
        string archivePath = CreateZip("film.zip", "payload.txt");
        await File.WriteAllTextAsync(Path.Combine(_root, otherName), "unrelated parity data");

        PostProcessingResult result = await new DownloadPostProcessor().ProcessAsync(archivePath, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(_root, "film", "payload.txt")));
    }

    [Theory]
    [InlineData("./")]
    [InlineData("folder/../")]
    public async Task ArchiveExtraction_AcceptsEntryForExtractionRoot(string directoryEntry)
    {
        ConfigureArchive();
        string archivePath = CreateZip("root-entry.zip", "payload.txt", directoryEntry);

        PostProcessingResult result = await new DownloadPostProcessor().ProcessAsync(archivePath, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal("payload", await File.ReadAllTextAsync(Path.Combine(_root, "root-entry", "payload.txt")));
    }

    [Theory]
    [InlineData("film.par2")]
    [InlineData("film.zip.par2")]
    [InlineData("film.vol00+01.par2")]
    [InlineData("film.zip.vol000+001.par2")]
    public async Task ArchiveRepair_SelectsMatchingBaseOrVolume(string parityName)
    {
        ConfigureArchive();
        Properties.Settings.Default.par2RepairEnabled = true;
        Properties.Settings.Default.par2ToolPath = Path.Combine(_root, "missing-tool.exe");
        string archivePath = CreateZip("film.zip", "payload.txt");
        await File.WriteAllTextAsync(Path.Combine(_root, parityName), "parity data");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new DownloadPostProcessor().ProcessAsync(archivePath, CancellationToken.None));
    }

    [Fact]
    public async Task AutomaticRoute_UserCancellationDoesNotTryAnotherProvider()
    {
        var first = Account(CloudAccountProvider.AllDebrid);
        var second = Account(CloudAccountProvider.TorBox);
        using var cancellation = new CancellationTokenSource();
        int secondAttempts = 0;
        using var automatic = new AutomaticDownloadLinkResolver(new[] { first, second }, null, account =>
            account.Id == first.Id
                ? new TestResolver(() => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); })
                : new TestResolver(() => { secondAttempts++; return new DebridResolvedLink(new Uri("https://cdn.example/result.bin")); }));
        var file = RequiredFile();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => automatic.ResolveAsync(file, file.PublicUrl!, cancellation.Token));

        Assert.Equal(0, secondAttempts);
    }

    [Theory]
    [InlineData("12 bananas")]
    [InlineData("100 %")]
    [InlineData("1 XB")]
    public void WebIndex_UnknownSizeUnitsRemainUnknown(string value)
    {
        Assert.Equal(-1, WebIndexProviderUtilities.ParseSizeToBytes(value));
    }

    [Fact]
    public void WebIndex_RootPathComparisonPreservesRemoteCase()
    {
        Assert.False(WebIndexProviderUtilities.IsWithinRoot(
            new Uri("https://host.example/Library/"), new Uri("https://host.example/library/private.bin")));
        Assert.True(WebIndexProviderUtilities.IsWithinRoot(
            new Uri("https://HOST.example/Library/"), new Uri("https://host.example/Library/file.bin")));
    }

    [Fact]
    public async Task WebIndex_LoadsFoldersDifferingOnlyByCase()
    {
        const string rootHtml = """
            <html><body><table>
              <tr><td><img alt="folder"></td><td><a href="A/">A</a></td><td>2026-01-01</td><td>-</td></tr>
              <tr><td><img alt="folder"></td><td><a href="a/">a</a></td><td>2026-01-01</td><td>-</td></tr>
            </table></body></html>
            """;
        await using var server = new LoopbackDownloadServer((request, _) => new LoopbackResponse
        {
            ContentType = "text/html; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(request.Path == "/h5ailink/" ? rootHtml : "<html><body><table></table></body></html>")
        });

        var root = await new H5AiIndexProvider().LoadAsync(new CloudProviderLoadContext(), server.Url("h5ailink/").AbsoluteUri);

        Assert.Equal(2, root.Subfolders.Count);
        Assert.Equal(3, server.Requests.Count);
    }

    private void ConfigureArchive()
    {
        Properties.Settings.Default.archivePostProcessingEnabled = true;
        Properties.Settings.Default.par2RepairEnabled = false;
        Properties.Settings.Default.archiveToolPath = string.Empty;
        Properties.Settings.Default.protectedArchivePassword = string.Empty;
    }

    private string CreateZip(string name, string fileName, string? directoryEntry = null)
    {
        string path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (directoryEntry != null) zip.CreateEntry(directoryEntry);
        using var writer = new StreamWriter(zip.CreateEntry(fileName).Open());
        writer.Write("payload");
        return path;
    }

    private static CloudAccountProfile Account(CloudAccountProvider provider) => new()
    {
        Provider = provider, DisplayName = provider.ToString(), Secret = "test token", IsActive = true
    };

    private static CloudFile RequiredFile() => new("file.bin", DateTime.MinValue, DateTime.MinValue, 0)
    {
        PublicUrl = new Uri("https://hoster.example/file"), RequiresLinkResolver = true, DownloadHistoryId = Guid.NewGuid()
    };

    private sealed class TestResolver(Func<DebridResolvedLink> resolve) : IDownloadLinkResolver
    {
        public string DisplayName => "Test";
        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(resolve());
        }
    }

    public void Dispose()
    {
        Properties.Settings.Default.routeScoringEnabled = _scoring;
        Properties.Settings.Default.archivePostProcessingEnabled = _archives;
        Properties.Settings.Default.par2RepairEnabled = _repair;
        Properties.Settings.Default.archiveToolPath = _archiveTool;
        Properties.Settings.Default.par2ToolPath = _par2Tool;
        Properties.Settings.Default.protectedArchivePassword = _password;
        Directory.Delete(_root, true);
    }
}
