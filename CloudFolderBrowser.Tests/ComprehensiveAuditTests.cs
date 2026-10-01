using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Networking;
using System.Net;
using System.Net.Http.Headers;

namespace CloudFolderBrowser.Tests;

public sealed class ComprehensiveAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-audit-" + Guid.NewGuid().ToString("N"));

    public ComprehensiveAuditTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void LocalFolder_CountsFilesAcrossTheTree()
    {
        File.WriteAllBytes(Path.Combine(_root, "one.bin"), new byte[3]);
        string child = Directory.CreateDirectory(Path.Combine(_root, "child")).FullName;
        File.WriteAllBytes(Path.Combine(child, "two.bin"), new byte[5]);
        var folder = new LocalFolder(new DirectoryInfo(_root));

        folder.CalculateFolderSize();

        Assert.Equal(1, folder.FilesNumberTopDirectoryOnly);
        Assert.Equal(2, folder.FilesNumber);
        Assert.Equal(8, folder.Size);
    }

    [Fact]
    public async Task ChecksumManifest_RecordReturnsIndependentSnapshot()
    {
        string path = Path.Combine(_root, "file.bin");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        var store = new ChecksumManifestStore(Path.Combine(_root, "checksums.json"));
        ChecksumManifestEntry entry = await store.RecordFileAsync(path);
        entry.Sha256 = "external mutation";

        Assert.True(await store.VerifyFileAsync(path));
    }

    [Fact]
    public async Task SyncPlan_PreservesDistinctCaseSensitiveRemoteNames()
    {
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        folder.AddFile(new CloudFile("File.bin", DateTime.MinValue, DateTime.MinValue, 1) { Path = "/File.bin" });
        folder.AddFile(new CloudFile("file.bin", DateTime.MinValue, DateTime.MinValue, 2) { Path = "/file.bin" });
        var model = new MainFormModel();

        var plan = await model.BuildSyncPlanAsync(new() { folder, folder }, new(), _root, true, false);

        Assert.Equal(2, plan.Count);
        Assert.Equal(new long[] { 1, 2 }, plan.Select(item => item.File.Size));
    }

    [Theory]
    [InlineData("history")]
    [InlineData("checksums")]
    [InlineData("accounts")]
    public async Task Persistence_IgnoresNullEntriesInOtherwiseValidJson(string kind)
    {
        string path = Path.Combine(_root, kind + ".json");
        await File.WriteAllTextAsync(path, "[null]");

        switch (kind)
        {
            case "history":
                Assert.Empty(await new DownloadHistoryStore(path).GetAllAsync());
                break;
            case "checksums":
                Assert.Null(await new ChecksumManifestStore(path).FindAsync(Path.Combine(_root, "file.bin")));
                break;
            case "accounts":
                Assert.Empty(new CloudAccountStore(path).GetAll());
                break;
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Theory]
    [InlineData("1.1.1.1:53/path")]
    [InlineData("user@1.1.1.1:53")]
    [InlineData("1.1.1.1:53?query=value")]
    [InlineData("1.1.1.1:53#fragment")]
    public void DnsEndpoint_RejectsComponentsThatWouldBeSilentlyIgnored(string address)
    {
        Assert.Throws<FormatException>(() => DnsServerEndpoint.Parse(address));
    }

    [Fact]
    public async Task SegmentedFailure_CancelsOtherWorkersImmediately()
    {
        using var handler = new FailingRangeHandler();
        using var client = new HttpClient(handler);
        var downloader = new AdaptiveSegmentedDownloader(client, new BandwidthLimiter(0),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), 16 * 1024);
        var uri = new Uri("https://audit-range.example/file.bin");
        using var cancellation = new CancellationTokenSource();
        Task<SegmentedDownloadResult> task = downloader.TryDownloadAsync(uri, "audit",
            Path.Combine(_root, "file.part"), AdaptiveSegmentedDownloader.MinimumFileSize, 2,
            () => new HttpRequestMessage(HttpMethod.Get, uri), _ => { }, (_, _) => { }, cancellation.Token);

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            cancellation.Cancel();
            try { await task; } catch { }
        }
    }

    private sealed class FailingRangeHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _siblingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var range = request.Headers.Range!.Ranges.Single();
            if (range.From == 0 && range.To == 0)
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[1]) };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 0, AdaptiveSegmentedDownloader.MinimumFileSize);
                return response;
            }
            if (range.From == 0)
            {
                await _siblingStarted.Task.WaitAsync(cancellationToken);
                throw new InvalidDataException("The first worker failed.");
            }
            _siblingStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }

    [Fact]
    public void SyncPreview_CollisionPathUsesSameSanitizationAsDownloader()
    {
        var file = new CloudFile("report?.bin", DateTime.MinValue, DateTime.MinValue, 1)
        { Path = "/folder?/report?.bin" };
        string existing = Path.Combine(_root, "folder_", "report_.bin");
        var item = new SyncPlanItem(file, SyncDifference.Missing, SyncPlanAction.Download, "missing", existing);

        Type rowType = typeof(SyncPreviewForm).GetNestedType("PreviewRow", System.Reflection.BindingFlags.NonPublic)!;
        object row = Activator.CreateInstance(rowType, item)!;
        var getTarget = rowType.GetMethod("GetTargetPath")!;
        Assert.Equal(existing, getTarget.Invoke(row, null));
        rowType.GetProperty("Action")!.SetValue(row, SyncPlanAction.Rename);
        rowType.GetProperty("TargetName")!.SetValue(row, "new?.bin");
        Assert.Equal(Path.Combine(_root, "folder_", "new_.bin"), getTarget.Invoke(row, null));
    }
}
