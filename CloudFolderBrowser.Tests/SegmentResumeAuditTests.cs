using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using CloudFolderBrowser.Networking;

namespace CloudFolderBrowser.Tests;

public sealed class SegmentResumeAuditTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cfb-segment-audit-" + Guid.NewGuid().ToString("N"));
    private const int Length = 16 * 1024 * 1024;

    public SegmentResumeAuditTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("probe")]
    [InlineData("segment")]
    public async Task RangeDownload_RejectsNonByteRangeUnits(string phase)
    {
        using var handler = new RangeHandler((request, from, to) =>
        {
            bool probe = from == 0 && to == 0;
            var response = Response(from, to, 7);
            if (probe == (phase == "probe")) response.Content.Headers.ContentRange!.Unit = "items";
            return response;
        });
        var result = await RunAsync(handler);
        Assert.False(result.Completed);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task RangeDownload_DoesNotTreatPartialContentMd5AsWholeFileDigest()
    {
        using var handler = new RangeHandler((request, from, to) =>
        {
            var response = Response(from, to, 7);
            response.Content.Headers.ContentMD5 = MD5.HashData(Enumerable.Repeat((byte)7, (int)(to - from + 1)).ToArray());
            return response;
        });
        Assert.True((await RunAsync(handler, checksums: true)).Completed);
        Assert.Equal(Length, new FileInfo(Output).Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Resume_DoesNotReuseOldBlocksWithoutStrongRepresentationValidator(bool weakTag)
    {
        string? tag = weakTag ? "W/\"same-weak-tag\"" : null;
        await SeedInterruptedAsync(tag);
        using var next = new RangeHandler((request, from, to) => Response(from, to, 2, tag));
        Assert.True((await RunAsync(next)).Completed);
        byte[] bytes = await File.ReadAllBytesAsync(Output);
        Assert.True(bytes.All(value => value == 2), "Resumed output contains bytes from the previous representation.");
    }

    [Fact]
    public async Task Resume_ReusesBlocksWhenStrongEtagStillMatches()
    {
        const string tag = "\"same-strong-tag\"";
        await SeedInterruptedAsync(tag);
        var ranges = new List<long>();
        using var next = new RangeHandler((request, from, to) =>
        {
            lock (ranges) ranges.Add(from);
            return Response(from, to, 1, tag);
        });
        Assert.True((await RunAsync(next)).Completed);
        Assert.Equal(new long[] { 0, Length / 2 }, ranges.Order());
    }

    [Fact]
    public async Task Resume_ChangedStrongEtagReplacesOldBlocks()
    {
        await SeedInterruptedAsync("\"old-version\"");
        using var next = new RangeHandler((request, from, to) => Response(from, to, 2, "\"new-version\""));
        Assert.True((await RunAsync(next)).Completed);
        Assert.True((await File.ReadAllBytesAsync(Output)).All(value => value == 2));
    }

    [Fact]
    public async Task Resume_WeakEtagWithLastModifiedUsesDateValidator()
    {
        DateTimeOffset modified = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        const string tag = "W/\"weak-tag\"";
        await SeedInterruptedAsync(tag, modified);
        var ranges = new List<long>();
        using var next = new RangeHandler((request, from, to) =>
        {
            lock (ranges) ranges.Add(from);
            if (to != 0) Assert.Equal(modified, request.Headers.IfRange!.Date);
            return Response(from, to, 1, tag, modified);
        });
        Assert.True((await RunAsync(next)).Completed);
        Assert.Equal(new long[] { 0, Length / 2 }, ranges.Order());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RangeDownload_StillValidatesWholeRepresentationDigest(bool corrupt)
    {
        byte[] expected = corrupt ? new byte[16] : MD5.HashData(Enumerable.Repeat((byte)7, Length).ToArray());
        using var handler = new RangeHandler((request, from, to) =>
        {
            var response = Response(from, to, 7);
            response.Headers.TryAddWithoutValidation("Digest", "md5=" + Convert.ToBase64String(expected));
            return response;
        });
        if (corrupt) await Assert.ThrowsAsync<RemoteChecksumMismatchException>(() => RunAsync(handler, checksums: true));
        else Assert.True((await RunAsync(handler, checksums: true)).Completed);
    }

    private async Task SeedInterruptedAsync(string? tag, DateTimeOffset? modified = null)
    {
        var firstComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RangeHandler(async (request, from, to, token) =>
        {
            if (from == Length / 2)
            {
                await firstComplete.Task.WaitAsync(token);
                throw new InvalidDataException("Interrupted fixture transfer");
            }
            return Response(from, to, 1, tag, modified);
        });
        await Assert.ThrowsAsync<InvalidDataException>(() => RunAsync(handler, progress: bytes =>
        { if (bytes >= Length / 2) firstComplete.TrySetResult(); }));
        Assert.Contains(Directory.EnumerateFiles(_root, "*.segment.*"), path => new FileInfo(path).Length == Length / 2);
    }

    private string Output => Path.Combine(_root, "file.part");

    private async Task<SegmentedDownloadResult> RunAsync(RangeHandler handler, bool checksums = false, Action<long>? progress = null)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var downloader = new AdaptiveSegmentedDownloader(client, new BandwidthLimiter(0),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3), 128 * 1024);
        var uri = new Uri("https://" + Path.GetFileName(_root) + ".example/file");
        return await downloader.TryDownloadAsync(uri, "fixture", Output, Length, 2,
            () => new HttpRequestMessage(HttpMethod.Get, uri), _ => { }, (done, total) => progress?.Invoke(done),
            CancellationToken.None, persistentByteMap: true, validateRemoteChecksum: checksums).WaitAsync(TimeSpan.FromSeconds(8));
    }

    private static HttpResponseMessage Response(long from, long to, byte value, string? tag = null, DateTimeOffset? modified = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(Enumerable.Repeat(value, (int)(to - from + 1)).ToArray()) };
        response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, Length);
        if (tag != null) response.Headers.ETag = EntityTagHeaderValue.Parse(tag);
        response.Content.Headers.LastModified = modified;
        return response;
    }

    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, long, long, CancellationToken, Task<HttpResponseMessage>> _handle;
        public RangeHandler(Func<HttpRequestMessage, long, long, HttpResponseMessage> handle)
            : this((request, from, to, _) => Task.FromResult(handle(request, from, to))) { }
        public RangeHandler(Func<HttpRequestMessage, long, long, CancellationToken, Task<HttpResponseMessage>> handle) => _handle = handle;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var range = request.Headers.Range!.Ranges.Single();
            return _handle(request, range.From!.Value, range.To!.Value, token);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
