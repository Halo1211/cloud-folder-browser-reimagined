using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.IO.Compression;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser.Tests;

[CollectionDefinition("Real download integration", DisableParallelization = true)]
public sealed class RealDownloadIntegrationCollection;

[Collection("Real download integration")]
public sealed class RealDownloadIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "cfb-real-download-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> _controls = new();
    private readonly bool _originalVerifySha256;
    private readonly int _originalMaximumSegments;
    private readonly bool _originalSegmentedDownloads;
    private readonly bool _originalPersistentByteMap;
    private readonly bool _originalRemoteChecksum;
    private readonly bool _originalSmartScheduler;
    private readonly int _originalMaximumDownloadsPerHost;
    private readonly bool _originalArchivePostProcessing;
    private readonly bool _originalPar2Repair;
    private readonly string _originalArchiveToolPath;
    private readonly string _originalArchivePassword;
    private readonly SynchronizationContext? _originalSynchronizationContext;
    private readonly bool _originalWinFormsContextAutoInstall;

    public RealDownloadIntegrationTests()
    {
        // WinForms controls install a synchronization context whose continuations require
        // a message loop. Integration tests intentionally run headless, so keep their
        // async transfer pipeline on the thread pool instead of a context that cannot pump.
        _originalSynchronizationContext = SynchronizationContext.Current;
        _originalWinFormsContextAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
        WindowsFormsSynchronizationContext.AutoInstall = false;
        SynchronizationContext.SetSynchronizationContext(null);
        Directory.CreateDirectory(_root);
        _originalVerifySha256 = Properties.Settings.Default.verifySha256;
        _originalMaximumSegments = Properties.Settings.Default.maximumSegmentsPerFile;
        _originalSegmentedDownloads = Properties.Settings.Default.segmentedDownloadsEnabled;
        _originalPersistentByteMap = Properties.Settings.Default.persistentByteMapEnabled;
        _originalRemoteChecksum = Properties.Settings.Default.remoteChecksumValidationEnabled;
        _originalSmartScheduler = Properties.Settings.Default.smartSchedulerEnabled;
        _originalMaximumDownloadsPerHost = Properties.Settings.Default.maximumDownloadsPerHost;
        _originalArchivePostProcessing = Properties.Settings.Default.archivePostProcessingEnabled;
        _originalPar2Repair = Properties.Settings.Default.par2RepairEnabled;
        _originalArchiveToolPath = Properties.Settings.Default.archiveToolPath;
        _originalArchivePassword = Properties.Settings.Default.protectedArchivePassword;
        Properties.Settings.Default.verifySha256 = true;
        Properties.Settings.Default.maximumSegmentsPerFile = 4;
        Properties.Settings.Default.segmentedDownloadsEnabled = true;
        Properties.Settings.Default.persistentByteMapEnabled = false;
        Properties.Settings.Default.remoteChecksumValidationEnabled = false;
        Properties.Settings.Default.smartSchedulerEnabled = false;
        Properties.Settings.Default.archivePostProcessingEnabled = false;
        Properties.Settings.Default.par2RepairEnabled = false;
    }

    [Fact]
    public async Task DirectDownload_WritesExactBytesHistoryAndChecksum()
    {
        byte[] payload = CreatePayload(512 * 1024, 17);
        await using var server = new LoopbackDownloadServer((request, _) =>
            LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("direct.bin", server.Url("files/direct.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        DownloadHistoryEntry history = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Equal(DownloadJobStatus.Completed, history.Status);
        Assert.Equal(payload.Length, history.BytesOnDisk);
        Assert.Equal(64, history.Sha256.Length);
        Assert.Equal("GET", Assert.Single(server.Requests).Method);
    }

    [Fact]
    public async Task HistoryRetry_UnknownSizePreservesPartialBytesAndLearnsRemoteSize()
    {
        byte[] payload = CreatePayload(256 * 1024, 47);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        string destination = Path.Combine(_root, "history-resume.bin");
        await File.WriteAllBytesAsync(destination + ".part", payload[..8192]);
        var entry = new DownloadHistoryEntry
        {
            FileName = "history-resume.bin", CloudPath = "/history-resume.bin", SourceUrl = server.Url("file.bin").AbsoluteUri,
            SavePath = destination, ExpectedSize = 0, HasKnownSize = false, Status = DownloadJobStatus.Paused
        };
        await GetHistoryStore().UpsertAsync(entry);
        var restored = Assert.Single(await GetHistoryStore().GetAllAsync());
        CloudFile retry = Assert.IsType<CloudFile>(restored.ToRetryFile());

        CommonDownload download = await RunDownloadAsync(new[] { retry }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal("bytes=8192-", Assert.Single(server.Requests).Headers["Range"]);
        Assert.Equal(payload, await File.ReadAllBytesAsync(destination));
        var completed = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Equal(entry.Id, completed.Id);
        Assert.True(completed.HasKnownSize);
        Assert.Equal(payload.Length, completed.ExpectedSize);
    }

    [Fact]
    public async Task HistoryRetry_ResolverRequiredDoesNotRequestSharePageAsAFile()
    {
        byte[] payload = CreatePayload(1024, 48);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        var entry = new DownloadHistoryEntry
        {
            FileName = "resolved-retry.bin", CloudPath = "/resolved-retry.bin", SourceUrl = server.Url("share").AbsoluteUri,
            SavePath = Path.Combine(_root, "resolved-retry.bin"), HasKnownSize = false,
            RequiresLinkResolver = true, Status = DownloadJobStatus.Paused
        };
        await GetHistoryStore().UpsertAsync(entry);
        var restored = Assert.Single(await GetHistoryStore().GetAllAsync());
        CloudFile retry = Assert.IsType<CloudFile>(restored.ToRetryFile());
        var route = new RecordingResolver(server.Url("download.bin"), payload.Length);
        var account = new CloudAccountProfile { Provider = CloudAccountProvider.RealDebrid, IsActive = true, Secret = "fixture" };
        using var automatic = new AutomaticDownloadLinkResolver(new[] { account }, null, _ => route);

        CommonDownload download = await RunDownloadAsync(new[] { retry }, CloudServiceType.Other, automatic);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(1, route.ResolveCount);
        Assert.Equal("/download.bin", Assert.Single(server.Requests).Path);
        Assert.Equal(payload, await File.ReadAllBytesAsync(entry.SavePath));
        Assert.True(Assert.Single(await GetHistoryStore().GetAllAsync()).RequiresLinkResolver);
    }

    [Fact]
    public async Task LargeFile_UsesValidatedConcurrentSegmentsAndAssemblesExactBytes()
    {
        byte[] payload = CreatePayload(32 * 1024 * 1024, 211);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackResponse.ForPayload(payload, request);
            response.ChunkSize = 64 * 1024;
            response.DelayPerChunk = TimeSpan.FromMilliseconds(2);
            response.Headers["ETag"] = "\"segmented-v1\"";
            return response;
        });
        CloudFile file = CreateFile("segmented.bin", server.Url("large/segmented.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.True(server.MaxConcurrentConnections >= 2);
        string[] ranges = server.Requests
            .Where(request => request.Headers.ContainsKey("Range"))
            .Select(request => request.Headers["Range"])
            .ToArray();
        Assert.Contains("bytes=0-0", ranges);
        Assert.Equal(5, ranges.Length);
        Assert.All(server.Requests, request =>
            Assert.Equal("identity", request.Headers.GetValueOrDefault("Accept-Encoding")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.segment.*"));
    }

    [Fact]
    public async Task RangeIgnoringServer_FallsBackToSingleStreamWithoutCorruption()
    {
        byte[] payload = CreatePayload(17 * 1024 * 1024, 223);
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            Body = payload,
            StatusCode = HttpStatusCode.OK
        });
        CloudFile file = CreateFile("range-fallback.bin", server.Url("range-fallback.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Collection(
            server.Requests,
            request => Assert.Equal("bytes=0-0", request.Headers["Range"]),
            request => Assert.False(request.Headers.ContainsKey("Range")));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.segment.*"));
    }

    [Fact]
    public async Task SegmentedToggleOff_UsesSingleStreamForLargeFile()
    {
        Properties.Settings.Default.segmentedDownloadsEnabled = false;
        byte[] payload = CreatePayload(17 * 1024 * 1024, 224);
        await using var server = new LoopbackDownloadServer((request, _) =>
            LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("segments-disabled.bin", server.Url("segments-disabled.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        LoopbackRequest request = Assert.Single(server.Requests);
        Assert.False(request.Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task PersistentByteMap_ResumesAfterWorkerLimitChanges()
    {
        Properties.Settings.Default.persistentByteMapEnabled = true;
        byte[] payload = CreatePayload(24 * 1024 * 1024, 225);
        int brokenOnce = 0;
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackResponse.ForPayload(payload, request);
            response.Headers["ETag"] = "\"byte-map-v1\"";
            if (request.Headers.TryGetValue("Range", out string? range)
                && range != "bytes=0-0"
                && Interlocked.CompareExchange(ref brokenOnce, 1, 0) == 0)
            {
                response.Body = response.Body[..(response.Body.Length / 2)];
            }
            return response;
        });
        CloudFile first = CreateFile("byte-map.bin", server.Url("byte-map.bin"), payload.Length);

        CommonDownload failed = await RunDownloadAsync(
            new[] { first }, CloudServiceType.Other, maxRetries: 0);
        Assert.Single(failed.FailedDownloads);
        Assert.True(File.Exists(first.LocalSavePathOverride! + ".part.segments.json"));
        int requestsBeforeResume = server.Requests.Count;

        Properties.Settings.Default.maximumSegmentsPerFile = 2;
        CloudFile resumed = CreateFile("byte-map.bin", server.Url("byte-map.bin"), payload.Length);
        CommonDownload completed = await RunDownloadAsync(
            new[] { resumed }, CloudServiceType.Other, maxRetries: 1);

        Assert.Empty(completed.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(resumed.LocalSavePathOverride!));
        Assert.Contains(server.Requests.Skip(requestsBeforeResume), request =>
            request.Headers.TryGetValue("Range", out string? range)
            && new[] { "bytes=4194304-", "bytes=12582912-", "bytes=20971520-" }
                .Any(prefix => range.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.False(File.Exists(resumed.LocalSavePathOverride! + ".part.segments.json"));
    }

    [Fact]
    public async Task RemoteChecksumToggle_RejectsMismatchedProviderHash()
    {
        Properties.Settings.Default.remoteChecksumValidationEnabled = true;
        byte[] payload = CreatePayload(380_000, 226);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackResponse.ForPayload(payload, request);
            response.Headers["x-goog-hash"] = "md5=" + Convert.ToBase64String(new byte[16]);
            return response;
        });
        CloudFile file = CreateFile("bad-md5.bin", server.Url("bad-md5.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, maxRetries: 1);

        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride!));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task SmartScheduler_RespectsPerHostLimit()
    {
        Properties.Settings.Default.smartSchedulerEnabled = true;
        Properties.Settings.Default.maximumDownloadsPerHost = 1;
        byte[] payload = CreatePayload(450_000, 228);
        int activeHandlers = 0;
        int maximumHandlers = 0;
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            int active = Interlocked.Increment(ref activeHandlers);
            int observed;
            do
            {
                observed = Volatile.Read(ref maximumHandlers);
                if (active <= observed)
                    break;
            }
            while (Interlocked.CompareExchange(ref maximumHandlers, active, observed) != observed);
            Thread.Sleep(25);
            LoopbackResponse response = LoopbackResponse.ForPayload(payload, request);
            response.ChunkSize = 32 * 1024;
            response.DelayPerChunk = TimeSpan.FromMilliseconds(3);
            Interlocked.Decrement(ref activeHandlers);
            return response;
        });
        CloudFile[] files = Enumerable.Range(0, 3)
            .Select(index => CreateFile($"scheduled-{index}.bin", server.Url($"scheduled-{index}.bin"), payload.Length))
            .ToArray();

        CommonDownload download = await RunDownloadAsync(
            files, CloudServiceType.Other, concurrency: 3);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(1, maximumHandlers);
    }

    [Fact]
    public async Task ArchiveToggle_ExtractsZipWithoutDeletingArchive()
    {
        Properties.Settings.Default.archivePostProcessingEnabled = true;
        Properties.Settings.Default.archiveToolPath = string.Empty;
        Properties.Settings.Default.protectedArchivePassword = string.Empty;
        string archivePath = Path.Combine(_root, "bundle.zip");
        await using (FileStream stream = File.Create(archivePath))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            ZipArchiveEntry entry = archive.CreateEntry("folder/readme.txt");
            await using Stream writer = entry.Open();
            await writer.WriteAsync("archive-content"u8.ToArray());
        }

        PostProcessingResult result = await new DownloadPostProcessor().ProcessAsync(
            archivePath, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.True(File.Exists(archivePath));
        Assert.Equal(
            "archive-content",
            await File.ReadAllTextAsync(Path.Combine(_root, "bundle", "folder", "readme.txt")));
    }

    [Fact]
    public async Task BrokenSegment_RetryResumesSidecarsInsteadOfRestartingFile()
    {
        byte[] payload = CreatePayload(16 * 1024 * 1024, 227);
        int brokenOnce = 0;
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackDownloadServerRangeResponse(payload, request);
            response.Headers["ETag"] = "\"resume-v1\"";
            if (request.Headers.TryGetValue("Range", out string? range)
                && range != "bytes=0-0"
                && Interlocked.CompareExchange(ref brokenOnce, 1, 0) == 0)
            {
                response.Body = response.Body[..(response.Body.Length / 2)];
            }
            return response;
        });
        CloudFile file = CreateFile("segment-resume.bin", server.Url("segment-resume.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, maxRetries: 1);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.True(brokenOnce == 1);
        Assert.True(server.Requests.Count(request =>
            request.Headers.GetValueOrDefault("Range") == "bytes=0-0") >= 2);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.segment.*"));
    }

    private static LoopbackResponse LoopbackDownloadServerRangeResponse(
        byte[] payload,
        LoopbackRequest request) =>
        LoopbackResponse.ForPayload(payload, request);

    [Fact]
    public async Task ExistingPart_ResumesWithHttpRangeAndKeepsExactContent()
    {
        byte[] payload = CreatePayload(768 * 1024, 31);
        await using var server = new LoopbackDownloadServer((request, _) =>
            LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("resume.bin", server.Url("resume.bin"), payload.Length);
        const int partialLength = 210_123;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride! + ".part", payload[..partialLength]);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        LoopbackRequest request = Assert.Single(server.Requests);
        Assert.Equal($"bytes={partialLength}-", request.Headers["Range"]);
    }

    [Fact]
    public async Task ServerIgnoringRange_RestartsPartialFileWithoutDuplicatingBytes()
    {
        byte[] payload = CreatePayload(410_000, 37);
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            Body = payload,
            StatusCode = HttpStatusCode.OK
        });
        CloudFile file = CreateFile("range-ignored.bin", server.Url("range-ignored.bin"), payload.Length);
        const int partialLength = 91_337;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride! + ".part", payload[..partialLength]);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Equal($"bytes={partialLength}-", Assert.Single(server.Requests).Headers["Range"]);
    }

    [Fact]
    public async Task UnknownSizePartAlreadyComplete_AcceptsRangeNotSatisfiableTotal()
    {
        byte[] payload = CreatePayload(275_000, 39);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            var response = new LoopbackResponse
            {
                StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable
            };
            response.Headers["Content-Range"] = $"bytes */{payload.Length}";
            return response;
        });
        CloudFile file = CreateFile("already-complete.bin", server.Url("already-complete.bin"), 0);
        file.HasKnownSize = false;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride! + ".part", payload);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.True(file.HasKnownSize);
        Assert.Equal(payload.Length, file.Size);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Equal($"bytes={payload.Length}-", Assert.Single(server.Requests).Headers["Range"]);
    }

    [Fact]
    public async Task BrokenConnection_RetriesUsingBytesAlreadyWritten()
    {
        byte[] payload = CreatePayload(640 * 1024, 43);
        int firstPartLength = payload.Length / 3;
        await using var server = new LoopbackDownloadServer((request, ordinal) =>
        {
            if (ordinal == 1)
            {
                return new LoopbackResponse
                {
                    Body = payload[..firstPartLength],
                    DeclaredContentLength = payload.Length
                };
            }
            return LoopbackResponse.ForPayload(payload, request);
        });
        CloudFile file = CreateFile("retry.bin", server.Url("retry.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, maxRetries: 2);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        LoopbackRequest[] requests = server.Requests.ToArray();
        Assert.True(requests.Length >= 2);
        Assert.Equal($"bytes={firstPartLength}-", requests[1].Headers["Range"]);
    }

    [Fact]
    public async Task UnknownLengthChunkedResponse_CompletesWithoutFalseSizeFailure()
    {
        byte[] payload = CreatePayload(333_333, 59);
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            Body = payload,
            UseChunkedEncoding = true,
            ChunkSize = 8191
        });
        CloudFile file = CreateFile("unknown-size.bin", server.Url("stream.bin"), 0);
        file.HasKnownSize = false;

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.False(File.Exists(file.LocalSavePathOverride! + ".part"));
    }

    [Fact]
    public async Task HtmlMasqueradingAsFile_IsRejectedWithoutCreatingFinalFile()
    {
        byte[] html = Encoding.UTF8.GetBytes("<html><body>Sign in required</body></html>");
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            Body = html,
            ContentType = "text/html; charset=utf-8"
        });
        CloudFile file = CreateFile("archive.zip", server.Url("share"), html.Length);

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride!));
        Assert.False(File.Exists(file.LocalSavePathOverride! + ".part"));
        DownloadHistoryEntry history = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Equal(DownloadJobStatus.Failed, history.Status);
    }

    [Fact]
    public async Task NoContentResponse_IsRejectedInsteadOfCreatingFalseEmptySuccess()
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = HttpStatusCode.NoContent
        });
        CloudFile file = CreateFile("missing.bin", server.Url("missing.bin"), 0);
        file.HasKnownSize = false;

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride!));
        DownloadHistoryEntry history = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Contains("no file content", history.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DebridRoute_ResolvesTeraBoxShareThenDownloadsActualBytes()
    {
        byte[] payload = CreatePayload(420_000, 71);
        await using var server = new LoopbackDownloadServer((request, _) =>
            LoopbackResponse.ForPayload(payload, request));
        var resolver = new RecordingResolver(server.Url("resolved/terabox.zip"), payload.Length);
        CloudFile file = CreateFile(
            "TeraBox share.zip",
            new Uri("https://www.terabox.com/s/1integration-test"),
            0);
        file.HasKnownSize = false;
        file.RequiresLinkResolver = true;

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.TeraBox, resolver);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(payload.Length, file.Size);
        Assert.True(file.HasKnownSize);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        DownloadHistoryEntry history = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Equal(payload.Length, history.ExpectedSize);
    }

    [Fact]
    public async Task ExpiredDebridLink_RefreshesResolverThenDownloadsReplacement()
    {
        byte[] payload = CreatePayload(305_000, 79);
        await using var server = new LoopbackDownloadServer((request, _) =>
            request.Path == "/expired"
                ? new LoopbackResponse { StatusCode = HttpStatusCode.Forbidden }
                : LoopbackResponse.ForPayload(payload, request));
        var resolver = new SequenceResolver(
            server.Url("expired"),
            server.Url("fresh"),
            payload.Length);
        CloudFile file = CreateFile(
            "refreshed.bin",
            new Uri("https://www.terabox.com/s/1refresh-test"),
            0);
        file.HasKnownSize = false;
        file.RequiresLinkResolver = true;

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.TeraBox, resolver, maxRetries: 1);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(2, resolver.ResolveCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Collection(
            server.Requests,
            request => Assert.Equal("/expired", request.Path),
            request => Assert.Equal("/fresh", request.Path));
    }

    [Fact]
    public async Task AutomaticRoute_DirectAccessFailureFallsBackAndPersistsEffectiveRoute()
    {
        byte[] payload = CreatePayload(220_000, 79);
        await using var server = new LoopbackDownloadServer((request, _) =>
            request.Path == "/blocked"
                ? new LoopbackResponse { StatusCode = HttpStatusCode.Forbidden }
                : LoopbackResponse.ForPayload(payload, request));
        Guid accountId = Guid.NewGuid();
        var account = new CloudAccountProfile
        {
            Id = accountId,
            Provider = CloudAccountProvider.RealDebrid,
            DisplayName = "Fallback",
            Secret = "token",
            IsActive = true
        };
        using var resolver = new AutomaticDownloadLinkResolver(
            new[] { account },
            preferredAccountId: null,
            _ => new RecordingResolver(server.Url("fresh"), payload.Length));
        CloudFile file = CreateFile("auto-fallback.bin", server.Url("blocked"), payload.Length);

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Other, resolver, maxRetries: 1);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        DownloadHistoryEntry history = Assert.Single(await GetHistoryStore().GetAllAsync());
        Assert.Equal(DownloadRouteIds.ForAccount(accountId), history.EffectiveRouteId);
        Assert.Equal("Real-Debrid", history.EffectiveRouteName);
        Assert.Collection(
            server.Requests,
            request => Assert.Equal("/blocked", request.Path),
            request => Assert.Equal("/fresh", request.Path));
    }

    [Fact]
    public async Task AllSyncDirectRoute_UsesWebDavPathAndBasicAuthentication()
    {
        byte[] payload = CreatePayload(256_000, 83);
        await using var server = new LoopbackDownloadServer((request, _) =>
            LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("manual.pdf", server.Url("original-share"), payload.Length);
        file.Path = "/Library/Guides/manual.pdf";
        var credential = new NetworkCredential("share-key", "secret password");

        CommonDownload download = await RunDownloadAsync(
            new[] { file }, CloudServiceType.Allsync, networkCredential: credential);

        Assert.Empty(download.FailedDownloads);
        LoopbackRequest request = Assert.Single(server.Requests);
        Assert.Equal("/public.php/webdav/Library/Guides/manual.pdf", request.Path);
        string expected = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes("share-key:secret password"));
        Assert.Equal(expected, request.Headers["Authorization"]);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
    }

    [Fact]
    public async Task MultipleFiles_TransferConcurrentlyAndRemainByteExact()
    {
        var payloads = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["/parallel/one.bin"] = CreatePayload(380_000, 91),
            ["/parallel/two.bin"] = CreatePayload(390_000, 97),
            ["/parallel/three.bin"] = CreatePayload(400_000, 101)
        };
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackResponse.ForPayload(payloads[request.Path], request);
            response.ChunkSize = 16 * 1024;
            response.DelayPerChunk = TimeSpan.FromMilliseconds(8);
            return response;
        });
        CloudFile[] files = payloads.Select(pair =>
            CreateFile(Path.GetFileName(pair.Key), server.Url(pair.Key), pair.Value.Length)).ToArray();

        CommonDownload download = await RunDownloadAsync(
            files, CloudServiceType.Other, concurrency: 3);

        Assert.Empty(download.FailedDownloads);
        Assert.True(server.MaxConcurrentConnections >= 2);
        foreach (CloudFile file in files)
            Assert.Equal(payloads[new Uri(file.PublicUrl!.AbsoluteUri).AbsolutePath], await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
    }

    [Fact]
    public async Task PauseThenNewRun_LeavesPartAndResumesItWithRange()
    {
        byte[] payload = CreatePayload(2 * 1024 * 1024, 109);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            LoopbackResponse response = LoopbackResponse.ForPayload(payload, request);
            response.ChunkSize = 16 * 1024;
            response.DelayPerChunk = TimeSpan.FromMilliseconds(12);
            return response;
        });
        CloudFile firstFile = CreateFile("paused.bin", server.Url("slow/paused.bin"), payload.Length);
        CommonDownload firstRun = CreateDownload(new[] { firstFile }, CloudServiceType.Other, concurrency: 1);
        Task firstCompletion = WaitForCompletionAsync(firstRun);
        await firstRun.Start().WaitAsync(TimeSpan.FromSeconds(8));
        await WaitUntilAsync(
            () => File.Exists(firstFile.LocalSavePathOverride! + ".part")
                && new FileInfo(firstFile.LocalSavePathOverride! + ".part").Length >= 128 * 1024,
            TimeSpan.FromSeconds(8));
        firstRun.Stop();
        await firstCompletion.WaitAsync(TimeSpan.FromSeconds(8));

        string partialPath = firstFile.LocalSavePathOverride! + ".part";
        Assert.True(File.Exists(partialPath));
        long partialLength = new FileInfo(partialPath).Length;
        Assert.InRange(partialLength, 1, payload.Length - 1);

        CloudFile resumedFile = CreateFile("paused.bin", server.Url("slow/paused.bin"), payload.Length);
        CommonDownload secondRun = await RunDownloadAsync(
            new[] { resumedFile }, CloudServiceType.Other, concurrency: 1);

        Assert.Empty(secondRun.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(resumedFile.LocalSavePathOverride!));
        Assert.Contains(server.Requests, request =>
            request.Headers.TryGetValue("Range", out string? range)
            && range == $"bytes={partialLength}-");
    }

    [Fact]
    public async Task Overwrite_DoesNotResumeFromUnrelatedExistingFile()
    {
        byte[] payload = CreatePayload(4096, 310);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("overwrite.bin", server.Url("overwrite.bin"), payload.Length);
        file.PlannedAction = SyncPlanAction.Overwrite;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride, new byte[1024]);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
        Assert.False(Assert.Single(server.Requests).Headers.ContainsKey("Range"));
    }

    [Fact]
    public async Task FailedOverwrite_PreservesOriginalFile()
    {
        byte[] original = CreatePayload(1024, 311);
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse { StatusCode = HttpStatusCode.NotFound });
        CloudFile file = CreateFile("preserve.bin", server.Url("preserve.bin"), 4096);
        file.PlannedAction = SyncPlanAction.Overwrite;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride, original);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Single(download.FailedDownloads);
        Assert.True(File.Exists(file.LocalSavePathOverride));
        Assert.Equal(original, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
    }

    [Fact]
    public async Task NeverOverwrite_ProtectsExistingFileEvenWhenSizeDiffers()
    {
        byte[] original = CreatePayload(1024, 312);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(new byte[4096], request));
        CloudFile file = CreateFile("skip-existing.bin", server.Url("skip-existing.bin"), 4096);
        await File.WriteAllBytesAsync(file.LocalSavePathOverride, original);
        CommonDownload download = CreateDownload(new[] { file }, CloudServiceType.Other);
        download.OverwriteMode = 0;
        Task completion = WaitForCompletionAsync(download);

        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Empty(server.Requests);
        Assert.Equal(original, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
        Assert.Equal(DownloadJobStatus.Skipped, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
    }

    [Fact]
    public async Task Download_PreservesLiteralPercentEncodedTextInLocalPath()
    {
        byte[] payload = CreatePayload(1024, 313);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("literal%27.bin", server.Url("literal.bin"), payload.Length);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.True(File.Exists(file.LocalSavePathOverride));
        Assert.Equal(file.LocalSavePathOverride, Assert.Single(await GetHistoryStore().GetAllAsync()).SavePath);
    }

    [Fact]
    public async Task UnknownEmptyFile_RecordsKnownZeroSize()
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse());
        CloudFile file = CreateFile("empty-unknown.bin", server.Url("empty.bin"), 0);
        file.HasKnownSize = false;

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        Assert.Empty(download.FailedDownloads);
        Assert.True(file.HasKnownSize);
        Assert.Equal(0, file.Size);
    }

    [Fact]
    public async Task PartialResponseWithoutContentRange_IsRejected()
    {
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = HttpStatusCode.PartialContent,
            Body = new byte[1024]
        });
        CloudFile file = CreateFile("invalid-range.bin", server.Url("invalid.bin"), 0);
        file.HasKnownSize = false;

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride));
    }

    [Fact]
    public async Task CompletePartialFile_IsStillCheckedAgainstRemoteChecksum()
    {
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable };
            response.Headers["Content-Range"] = "bytes */1024";
            response.Headers["x-goog-hash"] = "md5=" + Convert.ToBase64String(new byte[16]);
            return response;
        });
        CloudFile file = CreateFile("unchecked-part.bin", server.Url("part.bin"), 1024);
        await File.WriteAllBytesAsync(file.LocalSavePathOverride + ".part", new byte[1024]);

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Single(download.FailedDownloads);
        Assert.Single(server.Requests);
        Assert.False(File.Exists(file.LocalSavePathOverride));
    }

    [Theory]
    [InlineData("items */1024")]
    [InlineData("bytes 0-1023/1024")]
    public async Task SixthPass_CompletePartRejectsInvalidUnsatisfiedRange(string contentRange)
    {
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable };
            response.Headers["Content-Range"] = contentRange;
            return response;
        });
        CloudFile file = CreateFile("invalid-complete.bin", server.Url("part.bin"), 1024);
        await File.WriteAllBytesAsync(file.LocalSavePathOverride + ".part", new byte[1024]);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride));
    }

    [Fact]
    public async Task SixthPass_ResumeAcceptsCaseInsensitiveByteUnit()
    {
        byte[] payload = CreatePayload(1024, 71);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            var response = LoopbackResponse.ForPayload(payload, request);
            response.Headers["Content-Range"] = response.Headers["Content-Range"].Replace("bytes", "BYTES");
            return response;
        });
        CloudFile file = CreateFile("uppercase.bin", server.Url("part.bin"), payload.Length);
        await File.WriteAllBytesAsync(file.LocalSavePathOverride + ".part", payload[..512]);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
    }

    [Fact]
    public async Task SixthPass_FullResponseIgnoresContentRangeMetadata()
    {
        byte[] payload = CreatePayload(1024, 72);
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { Body = payload };
            response.Headers["Content-Range"] = "bytes 0-1023/2048";
            return response;
        });
        CloudFile file = CreateFile("full.bin", server.Url("full.bin"), payload.Length);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
    }

    [Fact]
    public async Task SixthPass_RequestsIdentityEncodingAndRejectsEncodedRepresentation()
    {
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { Body = new byte[1024] };
            response.Headers["Content-Encoding"] = "gzip";
            return response;
        });
        CloudFile file = CreateFile("encoded.bin", server.Url("encoded.bin"), 0);
        file.HasKnownSize = false;
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride));
        Assert.Equal("identity", Assert.Single(server.Requests).Headers["Accept-Encoding"]);
    }

    [Fact]
    public async Task WebDavListing_PreservesPlusAndHonorsHiddenFalse()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/a+b.bin</d:href><d:propstat><d:prop>
                <d:ishidden>0</d:ishidden><d:getcontentlength>4</d:getcontentlength>
              </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)207,
            Body = Encoding.UTF8.GetBytes(xml),
            ContentType = "application/xml"
        });
        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential("test", "test"));
        client.Server = server.BaseUri.GetLeftPart(UriPartial.Authority);

        var item = Assert.Single(await client.List());

        Assert.Equal("a+b.bin", item.DisplayName);
        Assert.False(item.IsHidden);
        Assert.Equal(4, item.ContentLength);
    }

    [Fact]
    public async Task WebDavListing_SkipsResponsesWithoutHrefAndFailedProperties()
    {
        const string xml = """
            <d:multistatus xmlns:d="DAV:" xmlns:x="urn:custom">
              <d:response><d:status>HTTP/1.1 404 Not Found</d:status></d:response>
              <d:response><d:href>/</d:href><d:propstat><d:prop>
                <d:resourcetype><d:collection /></d:resourcetype>
              </d:prop></d:propstat></d:response>
              <d:response><d:href>/folder/</d:href>
                <d:propstat><d:prop>
                  <d:getcontentlength>4</d:getcontentlength>
                  <d:resourcetype><x:something /><d:collection /></d:resourcetype>
                </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat>
                <d:propstat><d:prop><d:getcontentlength>99</d:getcontentlength></d:prop>
                  <d:status>HTTP/1.1 404 Not Found</d:status></d:propstat>
              </d:response>
            </d:multistatus>
            """;
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        {
            StatusCode = (HttpStatusCode)207,
            Body = Encoding.UTF8.GetBytes(xml),
            ContentType = "application/xml"
        });
        var client = NetworkClientAdapters.CreateWebDavClient(new NetworkCredential("test", "test"));
        client.Server = server.BaseUri.GetLeftPart(UriPartial.Authority);

        var item = Assert.Single(await client.List());

        Assert.True(item.IsCollection);
        Assert.Equal(4, item.ContentLength);
    }

    [Fact]
    public async Task MegaNeverOverwrite_ProtectsDifferentSizedExistingFile()
    {
        string destination = Path.Combine(_root, "mega-skip.bin");
        byte[] original = CreatePayload(1024, 315);
        await File.WriteAllBytesAsync(destination, original);
        using var bar = new ProgressBar();
        using var label = new Label();
        using var summary = new Label();
        using var tooltip = new ToolTip();
        SynchronizationContext.SetSynchronizationContext(null);
        var node = new AuditMegaNode();
        var file = new CloudFile(node.Name, DateTime.MinValue, DateTime.MinValue, node.Size)
        {
            Path = "/" + node.Name,
            MegaNode = node,
            LocalSavePathOverride = destination
        };
        var download = new MegaDownload(new CG.Web.MegaApiClient.MegaApiClient(), new() { file },
            new[] { bar }, new[] { label, summary }, tooltip, _root, overwriteMode: 0,
            folderNewFiles: false, historyStore: GetHistoryStore()) { MaxDownloadRetries = 0 };
        Task completion = WaitForCompletionAsync(download);

        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        await download.Start();
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(original, await File.ReadAllBytesAsync(destination));
        Assert.Equal(DownloadJobStatus.Skipped, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
    }

    private sealed class AuditMegaNode : CG.Web.MegaApiClient.INode
    {
        public string Id => "audit-node";
        public string ParentId => "root";
        public string Name => "mega-skip.bin";
        public long Size => 4096;
        public DateTime? ModificationDate => null;
        public DateTime CreationDate => DateTime.MinValue;
        public CG.Web.MegaApiClient.NodeType Type => CG.Web.MegaApiClient.NodeType.File;
        public string Owner => "owner";
        public bool Equals(CG.Web.MegaApiClient.INodeInfo? other) => other?.Id == Id;
    }

    [Fact]
    public async Task SegmentedDownload_RecoversFromNullManifestFields()
    {
        byte[] payload = CreatePayload(17 * 1024 * 1024, 316);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("null-map.bin", server.Url("null-map.bin"), payload.Length);
        await File.WriteAllTextAsync(file.LocalSavePathOverride + ".part.segments.json",
            "{\"IdentityHash\":null,\"Validator\":null,\"TotalLength\":" + payload.Length + ",\"Blocks\":null}");

        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);

        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../unsafe-sibling/outside.txt")]
    [InlineData("..\\outside.txt")]
    public async Task ArchiveExtraction_RejectsEntriesOutsideDestination(string entryName)
    {
        Properties.Settings.Default.archivePostProcessingEnabled = true;
        Properties.Settings.Default.archiveToolPath = string.Empty;
        Properties.Settings.Default.protectedArchivePassword = string.Empty;
        string archivePath = Path.Combine(_root, "unsafe.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open());
            writer.Write("must not escape");
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DownloadPostProcessor().ProcessAsync(archivePath, CancellationToken.None));

        Assert.True(File.Exists(archivePath));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "unsafe-sibling", "outside.txt")));
    }

    [Theory]
    [InlineData("file.bin.part")]
    [InlineData("file.bin.part.assembling")]
    [InlineData("file.bin.part.segments.json")]
    [InlineData("file.bin.part.segment.anything")]
    [InlineData("file.bin/child.bin")]
    public void Download_RejectsTargetsThatOverlapOtherTransferFiles(string conflictingName)
    {
        CloudFile first = CreateFile("file.bin", new Uri("https://example.invalid/first"), 1);
        CloudFile second = CreateFile(conflictingName, new Uri("https://example.invalid/second"), 1);

        Assert.Throws<InvalidDataException>(() => CreateDownload(new[] { first, second }, CloudServiceType.Other));
    }

    [Fact]
    public void Download_RejectsZeroWorkerControls()
    {
        CloudFile file = CreateFile("no-worker.bin", new Uri("https://example.invalid/file"), 1);
        Assert.Throws<ArgumentException>(() => CreateDownload(new[] { file }, CloudServiceType.Other, concurrency: 0));
    }

    [Fact]
    public async Task Download_RepeatedStartPreservesCompletedHistory()
    {
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(new byte[1024], request));
        CloudFile file = CreateFile("start-once.bin", server.Url("file"), 1024);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);

        await download.Start();

        Assert.Equal(DownloadJobStatus.Completed, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Par2Processing_DisabledRepairDoesNotReportSuccess()
    {
        Properties.Settings.Default.archivePostProcessingEnabled = true;
        Properties.Settings.Default.par2RepairEnabled = false;
        string path = Path.Combine(_root, "unrepaired.par2");
        await File.WriteAllBytesAsync(path, new byte[1]);

        var result = await new DownloadPostProcessor().ProcessAsync(path, CancellationToken.None);

        Assert.False(result.Processed);
        Assert.DoesNotContain("completed", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StopBeforeStart_PersistsPausedHistoryWithoutRequests()
    {
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(new byte[1], request));
        CloudFile file = CreateFile("stopped-before-start.bin", server.Url("file"), 1);
        CommonDownload download = CreateDownload(new[] { file }, CloudServiceType.Other);
        Task completion = WaitForCompletionAsync(download);
        download.Stop();

        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Empty(server.Requests);
        Assert.Equal(DownloadJobStatus.Paused, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
    }

    [Fact]
    public async Task Stop_PersistsPausedHistoryForQueuedFilesBeforeCompletionEvent()
    {
        byte[] payload = CreatePayload(1024 * 1024, 401);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            var response = LoopbackResponse.ForPayload(payload, request);
            response.ChunkSize = 16 * 1024;
            response.DelayPerChunk = TimeSpan.FromMilliseconds(12);
            return response;
        });
        CloudFile[] files = Enumerable.Range(0, 3)
            .Select(index => CreateFile($"stop-queued-{index}.bin", server.Url($"file-{index}"), payload.Length)).ToArray();
        CommonDownload download = CreateDownload(files, CloudServiceType.Other, concurrency: 1);
        Task completion = WaitForCompletionAsync(download);
        await download.Start();
        await WaitUntilAsync(() => server.Requests.Count > 0, TimeSpan.FromSeconds(8));

        download.Stop();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        var history = await GetHistoryStore().GetAllAsync();
        Assert.Equal(3, history.Count);
        Assert.All(history, entry => Assert.Equal(DownloadJobStatus.Paused, entry.Status));
    }

    [Fact]
    public async Task MegaStopBeforeStart_PersistsPausedHistory()
    {
        using var bar = new ProgressBar();
        using var label = new Label();
        using var summary = new Label();
        using var tooltip = new ToolTip();
        SynchronizationContext.SetSynchronizationContext(null);
        var node = new AuditMegaNode();
        var file = new CloudFile(node.Name, DateTime.MinValue, DateTime.MinValue, node.Size)
        {
            Path = "/" + node.Name, MegaNode = node, LocalSavePathOverride = Path.Combine(_root, node.Name)
        };
        var download = new MegaDownload(new CG.Web.MegaApiClient.MegaApiClient(), new() { file },
            new[] { bar }, new[] { label, summary }, tooltip, _root,
            folderNewFiles: false, historyStore: GetHistoryStore());
        Task completion = WaitForCompletionAsync(download);
        download.Stop();

        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(DownloadJobStatus.Paused, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
        Assert.False(File.Exists(file.LocalSavePathOverride));
    }

    [Fact]
    public async Task StopAfterCommit_DoesNotDowngradeCompletedDownloadDuringPostProcessing()
    {
        Properties.Settings.Default.archivePostProcessingEnabled = true;
        Properties.Settings.Default.archiveToolPath = string.Empty;
        Properties.Settings.Default.protectedArchivePassword = string.Empty;
        byte[] payload;
        using (var stream = new MemoryStream())
        {
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var writer = new StreamWriter(zip.CreateEntry("payload.txt").Open());
                writer.Write("payload");
            }
            payload = stream.ToArray();
        }
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("cancel-extraction.zip", server.Url("archive"), payload.Length);
        CommonDownload? download = null;
        var resolver = new CancelAfterCommitResolver(() => download!.Stop());
        download = CreateDownload(new[] { file }, CloudServiceType.Other, resolver);
        Task completion = WaitForCompletionAsync(download);

        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Equal(DownloadJobStatus.Completed, Assert.Single(await GetHistoryStore().GetAllAsync()).Status);
        Assert.Empty(download.FailedDownloads);
        Assert.Single(server.Requests);
    }

    private sealed class CancelAfterCommitResolver(Action onSuccess) : IAutomaticDownloadLinkResolver
    {
        public string DisplayName => "Test";
        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken) => Task.FromResult(new DebridResolvedLink(source));
        public Task<DebridResolvedLink> ResolveAsync(CloudFile file, Uri source, CancellationToken cancellationToken) => ResolveAsync(source, cancellationToken);
        public void ReportSuccess(CloudFile file, Uri source) => onSuccess();
        public void ReportFailure(CloudFile file, Uri source, Exception exception) { }
    }

    [Fact]
    public async Task TenthPass_ResolvedUrlDoesNotReceiveSourceCredentials()
    {
        byte[] payload = CreatePayload(128, 201);
        await using var source = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        await using var destination = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("scoped-auth.bin", source.Url("source"), payload.Length);
        CommonDownload download = await RunDownloadAsync([file], CloudServiceType.Other,
            new RecordingResolver(destination.Url("resolved"), payload.Length), maxRetries: 0,
            networkCredential: new NetworkCredential("fixture-user", "fixture-secret"));
        Assert.Empty(download.FailedDownloads);
        Assert.False(Assert.Single(destination.Requests).Headers.ContainsKey("Authorization"));
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task TenthPass_SameOriginResolvedUrlStillReceivesSourceCredentials()
    {
        byte[] payload = CreatePayload(128, 202);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("same-auth.bin", server.Url("source"), payload.Length);
        CommonDownload download = await RunDownloadAsync([file], CloudServiceType.Other,
            new RecordingResolver(server.Url("resolved"), payload.Length), maxRetries: 0,
            networkCredential: new NetworkCredential("fixture-user", "fixture-secret"));
        Assert.Empty(download.FailedDownloads);
        Assert.True(Assert.Single(server.Requests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task TenthPass_RetryFullResponseDoesNotUsePreviousRepresentationChecksum()
    {
        byte[] first = CreatePayload(128, 203);
        byte[] second = CreatePayload(128, 204);
        await using var server = new LoopbackDownloadServer((_, index) =>
        {
            var response = new LoopbackResponse { Body = second };
            if (index == 1)
                response.Headers["Digest"] = "sha-256=" + Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(first));
            return response;
        });
        CloudFile file = CreateFile("new-representation.bin", server.Url("file"), second.Length);
        CommonDownload download = await RunDownloadAsync([file], CloudServiceType.Other, maxRetries: 1);
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(second, await File.ReadAllBytesAsync(file.LocalSavePathOverride!));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task TenthPass_RestartedFullResponseStillValidatesContentMd5()
    {
        byte[] payload = CreatePayload(128, 205);
        await using var server = new LoopbackDownloadServer((_, _) =>
        {
            var response = new LoopbackResponse { Body = payload };
            response.Headers["Content-MD5"] = Convert.ToBase64String(new byte[16]);
            return response;
        });
        CloudFile file = CreateFile("restart-hash.bin", server.Url("file"), payload.Length);
        await File.WriteAllBytesAsync(file.LocalSavePathOverride + ".part", payload[..16]);
        CommonDownload download = await RunDownloadAsync([file], CloudServiceType.Other, maxRetries: 0);
        Assert.Single(download.FailedDownloads);
        Assert.False(File.Exists(file.LocalSavePathOverride));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TenthPass_RedirectReevaluatesCookieDomainAndPath(bool differentHost)
    {
        byte[] payload = CreatePayload(128, 206);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            if (request.Path == "/private/source")
            {
                var redirect = new LoopbackResponse { StatusCode = HttpStatusCode.Found };
                redirect.Headers["Location"] = differentHost ? "http://localhost:PORT/final" : "/final";
                // The fixture's handler is supplied the Host header of the actual listener.
                redirect.Headers["Location"] = redirect.Headers["Location"].Replace("PORT", request.Headers["Host"].Split(':')[1]);
                return redirect;
            }
            return LoopbackResponse.ForPayload(payload, request);
        });
        CloudFile file = CreateFile("redirect-cookie.bin", server.Url("private/source"), payload.Length);
        var session = new CloudflareSession("fixture-browser", [new FlareSolverrCookie
        {
            Name = "clearance", Value = "fixture-cookie", Domain = "127.0.0.1", Path = differentHost ? "/" : "/private"
        }]);
        CommonDownload download = CreateDownload([file], CloudServiceType.Other, maxRetries: 0, session: session);
        Task completion = WaitForCompletionAsync(download);
        await download.Start();
        await completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(download.FailedDownloads);
        Assert.Collection(server.Requests,
            request => Assert.Contains("clearance=fixture-cookie", request.Headers.GetValueOrDefault("Cookie") ?? ""),
            request => Assert.False(request.Headers.ContainsKey("Cookie")));
    }

    [Fact]
    public async Task OctoberAudit_UnknownEstimatedSizeDoesNotRejectChunkedPayload()
    {
        byte[] payload = CreatePayload(37, 5);
        await using var server = new LoopbackDownloadServer((_, _) => new LoopbackResponse
        { Body = payload, UseChunkedEncoding = true });
        CloudFile file = CreateFile("estimated.bin", server.Url("estimated.bin"), 40);
        file.HasKnownSize = false;
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
        Assert.Equal(payload.Length, file.Size);
    }

    [Fact]
    public async Task OctoberAudit_ShortenedUnknownRemoteRestartsStalePartial()
    {
        byte[] payload = CreatePayload(37, 7);
        await using var server = new LoopbackDownloadServer((request, _) =>
        {
            if (request.Headers.ContainsKey("Range"))
            {
                var response = new LoopbackResponse { StatusCode = HttpStatusCode.RequestedRangeNotSatisfiable };
                response.Headers["Content-Range"] = "bytes */37";
                return response;
            }
            return new LoopbackResponse { Body = payload };
        });
        CloudFile file = CreateFile("shortened.bin", server.Url("shortened.bin"), 0);
        file.HasKnownSize = false;
        await File.WriteAllBytesAsync(file.LocalSavePathOverride + ".part", new byte[50]);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other, maxRetries: 0);
        Assert.Empty(download.FailedDownloads);
        Assert.Equal(payload, await File.ReadAllBytesAsync(file.LocalSavePathOverride));
        Assert.Equal(2, server.Requests.Count);
    }

    [Fact]
    public async Task OctoberAudit_DownloadPreservesCloudModifiedTimeForNextSync()
    {
        byte[] payload = CreatePayload(37, 9);
        await using var server = new LoopbackDownloadServer((request, _) => LoopbackResponse.ForPayload(payload, request));
        CloudFile file = CreateFile("modified.bin", server.Url("modified.bin"), payload.Length);
        file.Modified = DateTime.UtcNow.AddDays(1);
        CommonDownload download = await RunDownloadAsync(new[] { file }, CloudServiceType.Other);
        Assert.Empty(download.FailedDownloads);
        var folder = new CloudFolder("root", DateTime.MinValue, DateTime.MinValue, 0) { Path = "/" };
        folder.AddFile(file);
        Assert.Empty(await new MainFormModel().BuildSyncPlanAsync(new() { folder }, new(), _root, true, false));
    }

    private async Task<CommonDownload> RunDownloadAsync(
        IEnumerable<CloudFile> files,
        CloudServiceType service,
        IDownloadLinkResolver? resolver = null,
        int maxRetries = 1,
        int concurrency = 1,
        NetworkCredential? networkCredential = null)
    {
        CommonDownload download = CreateDownload(
            files, service, resolver, maxRetries, concurrency, networkCredential);
        Task completion = WaitForCompletionAsync(download);
        await download.Start().WaitAsync(TimeSpan.FromSeconds(8));
        await completion.WaitAsync(TimeSpan.FromSeconds(25));
        return download;
    }

    private CommonDownload CreateDownload(
        IEnumerable<CloudFile> files,
        CloudServiceType service,
        IDownloadLinkResolver? resolver = null,
        int maxRetries = 1,
        int concurrency = 1,
        NetworkCredential? networkCredential = null,
        CloudflareSession? session = null)
    {
        var bars = Enumerable.Range(0, concurrency).Select(_ => new ProgressBar()).ToArray();
        var labels = Enumerable.Range(0, concurrency + 1).Select(_ => new Label()).ToArray();
        var toolTip = new ToolTip();
        _controls.AddRange(bars);
        _controls.AddRange(labels);
        _controls.Add(toolTip);

        // Creating the first WinForms component can install a context even when the
        // process-wide auto-install flag was already observed by WinForms. Clear it
        // before CommonFileDownload creates Progress<T> and before Start awaits I/O.
        SynchronizationContext.SetSynchronizationContext(null);

        var download = new CommonDownload(
            files.ToList(),
            bars,
            labels,
            toolTip,
            service,
            _root,
            overwriteMode: 1,
            networkCredential: networkCredential,
            folderNewFiles: false,
            flareSolverrSession: session,
            linkResolver: resolver,
            historyStore: GetHistoryStore(),
            checksumStore: GetChecksumStore())
        {
            MaxDownloadRetries = maxRetries,
            RetryDelay = 100,
            CheckDownloadedFileSize = true
        };
        return download;
    }

    private static Task WaitForCompletionAsync(Download download)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        download.DownloadCompleted += (_, _) => completion.TrySetResult();
        return completion.Task;
    }

    private CloudFile CreateFile(string name, Uri publicUrl, long size)
    {
        string savePath = Path.Combine(_root, name);
        return new CloudFile(name, DateTime.UtcNow, DateTime.UtcNow, size)
        {
            Path = "/" + name,
            PublicUrl = publicUrl,
            LocalSavePathOverride = savePath,
            PlannedAction = SyncPlanAction.Download
        };
    }

    private DownloadHistoryStore GetHistoryStore() =>
        new(Path.Combine(_root, "history.json"));

    private ChecksumManifestStore GetChecksumStore() =>
        new(Path.Combine(_root, "checksums.json"));

    private static byte[] CreatePayload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("The integration-test condition was not reached in time.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        Properties.Settings.Default.verifySha256 = _originalVerifySha256;
        Properties.Settings.Default.maximumSegmentsPerFile = _originalMaximumSegments;
        Properties.Settings.Default.segmentedDownloadsEnabled = _originalSegmentedDownloads;
        Properties.Settings.Default.persistentByteMapEnabled = _originalPersistentByteMap;
        Properties.Settings.Default.remoteChecksumValidationEnabled = _originalRemoteChecksum;
        Properties.Settings.Default.smartSchedulerEnabled = _originalSmartScheduler;
        Properties.Settings.Default.maximumDownloadsPerHost = _originalMaximumDownloadsPerHost;
        Properties.Settings.Default.archivePostProcessingEnabled = _originalArchivePostProcessing;
        Properties.Settings.Default.par2RepairEnabled = _originalPar2Repair;
        Properties.Settings.Default.archiveToolPath = _originalArchiveToolPath;
        Properties.Settings.Default.protectedArchivePassword = _originalArchivePassword;
        foreach (IDisposable control in _controls)
            control.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
        SynchronizationContext.SetSynchronizationContext(_originalSynchronizationContext);
        WindowsFormsSynchronizationContext.AutoInstall = _originalWinFormsContextAutoInstall;
    }

    private sealed class RecordingResolver : IDownloadLinkResolver
    {
        private readonly Uri _result;
        private readonly long _size;
        public int ResolveCount { get; private set; }
        public string DisplayName => "Simulated debrid";

        public RecordingResolver(Uri result, long size)
        {
            _result = result;
            _size = size;
        }

        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult(new DebridResolvedLink(_result, "terabox.zip", _size));
        }
    }

    private sealed class SequenceResolver : IDownloadLinkResolver
    {
        private readonly Uri[] _results;
        private readonly long _size;
        public int ResolveCount { get; private set; }
        public string DisplayName => "Simulated rotating debrid";

        public SequenceResolver(Uri first, Uri second, long size)
        {
            _results = new[] { first, second };
            _size = size;
        }

        public Task<DebridResolvedLink> ResolveAsync(Uri source, CancellationToken cancellationToken)
        {
            Uri result = _results[Math.Min(ResolveCount, _results.Length - 1)];
            ResolveCount++;
            return Task.FromResult(new DebridResolvedLink(result, "refreshed.bin", _size));
        }
    }
}

internal sealed record LoopbackRequest(
    string Method,
    string Path,
    IReadOnlyDictionary<string, string> Headers);

internal sealed class LoopbackResponse
{
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public long? DeclaredContentLength { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public bool UseChunkedEncoding { get; init; }
    public int ChunkSize { get; set; } = 64 * 1024;
    public TimeSpan DelayPerChunk { get; set; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static LoopbackResponse ForPayload(byte[] payload, LoopbackRequest request)
    {
        if (request.Headers.TryGetValue("Range", out string? range)
            && TryParseRange(range, payload.Length, out long start, out long end)
            && start >= 0
            && start < payload.Length)
        {
            var response = new LoopbackResponse
            {
                StatusCode = HttpStatusCode.PartialContent,
                Body = payload[(int)start..((int)end + 1)]
            };
            response.Headers["Content-Range"] = $"bytes {start}-{end}/{payload.Length}";
            response.Headers["Accept-Ranges"] = "bytes";
            return response;
        }

        var full = new LoopbackResponse { Body = payload };
        full.Headers["Accept-Ranges"] = "bytes";
        return full;
    }

    private static bool TryParseRange(string value, int payloadLength, out long start, out long end)
    {
        start = 0;
        end = payloadLength - 1;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;
        string[] parts = value[6..].Split('-', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], out start))
            return false;
        if (!string.IsNullOrWhiteSpace(parts[1]) && !long.TryParse(parts[1], out end))
            return false;
        end = Math.Min(end, payloadLength - 1L);
        return end >= start;
    }
}

internal sealed class LoopbackDownloadServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Func<LoopbackRequest, int, LoopbackResponse> _handler;
    private Task? _acceptLoop;
    private int _requestCount;
    private int _activeConnections;
    private int _maxConcurrentConnections;

    public ConcurrentQueue<LoopbackRequest> Requests { get; } = new();
    public int MaxConcurrentConnections => Volatile.Read(ref _maxConcurrentConnections);
    public Uri BaseUri { get; private set; } = null!;

    public LoopbackDownloadServer(Func<LoopbackRequest, int, LoopbackResponse> handler)
    {
        _handler = handler;
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public Uri Url(string relativePath) => new(BaseUri, relativePath.TrimStart('/'));

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_shutdown.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        int active = Interlocked.Increment(ref _activeConnections);
        UpdateMaximum(active);
        try
        {
            using (client)
            using (NetworkStream stream = client.GetStream())
            {
                LoopbackRequest? request = await ReadRequestAsync(stream, _shutdown.Token);
                if (request == null)
                    return;
                Requests.Enqueue(request);
                int ordinal = Interlocked.Increment(ref _requestCount);
                LoopbackResponse response = _handler(request, ordinal);
                await WriteResponseAsync(stream, response, _shutdown.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.Decrement(ref _activeConnections);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxConcurrentConnections);
            if (candidate <= observed)
                return;
        }
        while (Interlocked.CompareExchange(ref _maxConcurrentConnections, candidate, observed) != observed);
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        int matched = 0;
        byte[] terminator = "\r\n\r\n"u8.ToArray();
        while (buffer.Length < 64 * 1024)
        {
            int read = await stream.ReadAsync(one, token);
            if (read == 0)
                return null;
            buffer.WriteByte(one[0]);
            matched = one[0] == terminator[matched]
                ? matched + 1
                : one[0] == terminator[0] ? 1 : 0;
            if (matched == terminator.Length)
                break;
        }

        string headerText = Encoding.ASCII.GetString(buffer.ToArray());
        string[] lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string[] requestLine = lines[0].Split(' ', 3);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int separator = line.IndexOf(':');
            if (separator > 0)
                headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }
        if (headers.TryGetValue("Content-Length", out string? contentLengthText)
            && int.TryParse(contentLengthText, out int contentLength)
            && contentLength > 0)
        {
            byte[] requestBody = new byte[contentLength];
            int offset = 0;
            while (offset < requestBody.Length)
            {
                int read = await stream.ReadAsync(
                    requestBody.AsMemory(offset, requestBody.Length - offset), token);
                if (read == 0)
                    break;
                offset += read;
            }
        }
        string path = Uri.TryCreate(requestLine[1], UriKind.Absolute, out Uri? absolute)
            ? absolute.PathAndQuery
            : requestLine[1];
        return new LoopbackRequest(requestLine[0], path, headers);
    }

    private static async Task WriteResponseAsync(
        NetworkStream stream,
        LoopbackResponse response,
        CancellationToken token)
    {
        string reason = response.StatusCode switch
        {
            HttpStatusCode.OK => "OK",
            HttpStatusCode.PartialContent => "Partial Content",
            HttpStatusCode.Forbidden => "Forbidden",
            HttpStatusCode.NotFound => "Not Found",
            HttpStatusCode.NoContent => "No Content",
            HttpStatusCode.RequestedRangeNotSatisfiable => "Range Not Satisfiable",
            _ => response.StatusCode.ToString()
        };
        var header = new StringBuilder()
            .Append("HTTP/1.1 ").Append((int)response.StatusCode).Append(' ').Append(reason).Append("\r\n")
            .Append("Content-Type: ").Append(response.ContentType).Append("\r\n")
            .Append("Connection: close\r\n");
        if (response.UseChunkedEncoding)
            header.Append("Transfer-Encoding: chunked\r\n");
        else
            header.Append("Content-Length: ").Append(response.DeclaredContentLength ?? response.Body.LongLength).Append("\r\n");
        foreach ((string name, string value) in response.Headers)
            header.Append(name).Append(": ").Append(value).Append("\r\n");
        header.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header.ToString()), token);

        int chunkSize = Math.Max(1, response.ChunkSize);
        for (int offset = 0; offset < response.Body.Length; offset += chunkSize)
        {
            int length = Math.Min(chunkSize, response.Body.Length - offset);
            if (response.UseChunkedEncoding)
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"{length:X}\r\n"), token);
            await stream.WriteAsync(response.Body.AsMemory(offset, length), token);
            if (response.UseChunkedEncoding)
                await stream.WriteAsync("\r\n"u8.ToArray(), token);
            await stream.FlushAsync(token);
            if (response.DelayPerChunk > TimeSpan.Zero)
                await Task.Delay(response.DelayPerChunk, token);
        }
        if (response.UseChunkedEncoding)
        {
            await stream.WriteAsync("0\r\n\r\n"u8.ToArray(), token);
            await stream.FlushAsync(token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        if (_acceptLoop != null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _shutdown.Dispose();
    }
}
