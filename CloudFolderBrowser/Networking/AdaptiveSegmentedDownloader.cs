using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace CloudFolderBrowser.Networking;

internal sealed record SegmentedDownloadResult(bool Completed, int SegmentCount)
{
    public static SegmentedDownloadResult NotAttempted { get; } = new(false, 1);
}

internal sealed class RangeDownloadNotSupportedException : IOException
{
    public RangeDownloadNotSupportedException(string message) : base(message)
    {
    }
}

/// <summary>
/// Downloads a known-size HTTP resource into resumable range sidecars and
/// assembles them atomically. Range support is proven with a 0-0 request and
/// every response is checked before bytes are accepted.
/// </summary>
internal sealed class AdaptiveSegmentedDownloader
{
    internal const long MinimumFileSize = 16L * 1024 * 1024;
    internal const long MinimumSegmentSize = 8L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, DateTime> RangeDisabledUntil =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, HostTransferProfile> HostProfiles =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HttpClient _client;
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly TimeSpan _headerTimeout;
    private readonly TimeSpan _readStallTimeout;
    private readonly int _bufferSize;

    public AdaptiveSegmentedDownloader(
        HttpClient client,
        BandwidthLimiter bandwidthLimiter,
        TimeSpan headerTimeout,
        TimeSpan readStallTimeout,
        int bufferSize)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _bandwidthLimiter = bandwidthLimiter ?? throw new ArgumentNullException(nameof(bandwidthLimiter));
        _headerTimeout = headerTimeout;
        _readStallTimeout = readStallTimeout;
        _bufferSize = Math.Max(16 * 1024, bufferSize);
    }

    public async Task<SegmentedDownloadResult> TryDownloadAsync(
        Uri downloadUri,
        string stableIdentity,
        string outputFile,
        long expectedLength,
        int maximumSegments,
        Func<HttpRequestMessage> requestFactory,
        Action<HttpResponseMessage> validateResponse,
        Action<long, long> progress,
        CancellationToken cancellationToken,
        bool persistentByteMap = false,
        bool adaptiveWorkers = false,
        bool validateRemoteChecksum = false,
        Action<int>? workerCountChanged = null)
    {
        ArgumentNullException.ThrowIfNull(downloadUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(stableIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFile);
        ArgumentNullException.ThrowIfNull(requestFactory);
        ArgumentNullException.ThrowIfNull(validateResponse);
        ArgumentNullException.ThrowIfNull(progress);

        int segmentCount = CalculateSegmentCount(expectedLength, maximumSegments);
        long existingLength = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
        if (segmentCount < 2 || existingLength > 0)
            return SegmentedDownloadResult.NotAttempted;
        if (existingLength == 0 && File.Exists(outputFile))
            File.Delete(outputFile);

        string authorityKey = downloadUri.GetLeftPart(UriPartial.Authority);
        int workerCount = SelectWorkerCount(authorityKey, segmentCount, adaptiveWorkers);
        workerCountChanged?.Invoke(workerCount);
        if (RangeDisabledUntil.TryGetValue(authorityKey, out DateTime disabledUntil))
        {
            if (disabledUntil > DateTime.UtcNow)
                return SegmentedDownloadResult.NotAttempted;
            RangeDisabledUntil.TryRemove(authorityKey, out _);
        }

        RangeProbe probe;
        try
        {
            probe = await ProbeAsync(
                downloadUri,
                expectedLength,
                requestFactory,
                validateResponse,
                cancellationToken).ConfigureAwait(false);
            RangeDisabledUntil.TryRemove(authorityKey, out _);
        }
        catch (RangeDownloadNotSupportedException)
        {
            RangeDisabledUntil[authorityKey] = DateTime.UtcNow.AddMinutes(10);
            DeleteSegmentArtifacts(outputFile);
            return SegmentedDownloadResult.NotAttempted;
        }

        Segment[] segments = CreateSegments(
            outputFile,
            stableIdentity,
            probe,
            persistentByteMap ? null : segmentCount);
        DeleteUnexpectedSegmentArtifacts(outputFile, segments);
        SegmentManifestStore? manifest = persistentByteMap
            ? await SegmentManifestStore.OpenAsync(
                outputFile,
                stableIdentity,
                probe,
                segments,
                validateRemoteChecksum,
                cancellationToken).ConfigureAwait(false)
            : null;
        string assemblyPath = outputFile + ".assembling";
        TryDelete(assemblyPath);

        long completedBytes = 0;
        foreach (Segment segment in segments)
        {
            if (!File.Exists(segment.Path))
                continue;
            long length = new FileInfo(segment.Path).Length;
            if (length > segment.Length)
            {
                File.Delete(segment.Path);
                continue;
            }
            completedBytes += length;
        }
        progress(completedBytes, probe.TotalLength);

        using var siblingCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new ConcurrentQueue<Segment>(segments.Where(segment =>
            !File.Exists(segment.Path) || new FileInfo(segment.Path).Length != segment.Length));
        var stopwatch = Stopwatch.StartNew();
        Task[] tasks = Enumerable.Range(0, Math.Min(workerCount, Math.Max(1, pending.Count)))
            .Select(_ => DownloadWorkerAsync(
                pending,
                downloadUri,
                probe,
                requestFactory,
                validateResponse,
                bytes =>
                {
                    long total = Interlocked.Add(ref completedBytes, bytes);
                    progress(total, probe.TotalLength);
                },
                manifest,
                siblingCancellation.Token))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            siblingCancellation.Cancel();
            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            catch
            {
            }

            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);

            RangeDownloadNotSupportedException? rangeFailure = tasks
                .SelectMany(task => task.Exception == null
                    ? Enumerable.Empty<Exception>()
                    : task.Exception.Flatten().InnerExceptions)
                .OfType<RangeDownloadNotSupportedException>()
                .FirstOrDefault();
            if (rangeFailure != null)
            {
                RangeDisabledUntil[authorityKey] = DateTime.UtcNow.AddMinutes(10);
                DeleteSegmentArtifacts(outputFile);
                return SegmentedDownloadResult.NotAttempted;
            }

            RecordHostFailure(authorityKey);

            Exception failure = tasks
                .SelectMany(task => task.Exception == null
                    ? Enumerable.Empty<Exception>()
                    : task.Exception.Flatten().InnerExceptions)
                .FirstOrDefault(exception => exception is not OperationCanceledException)
                ?? new IOException("A segmented download worker stopped unexpectedly.");
            throw failure;
        }

        await AssembleAsync(segments, assemblyPath, probe.TotalLength, cancellationToken)
            .ConfigureAwait(false);
        if (validateRemoteChecksum)
        {
            await RemoteChecksumValidator.ValidateFileAsync(
                assemblyPath,
                probe.Checksum,
                cancellationToken).ConfigureAwait(false);
        }
        File.Move(assemblyPath, outputFile, true);
        foreach (Segment segment in segments)
            TryDelete(segment.Path);
        progress(probe.TotalLength, probe.TotalLength);
        TryDelete(SegmentManifestStore.GetPath(outputFile));
        RecordHostSuccess(authorityKey, probe.TotalLength, stopwatch.Elapsed);
        return new SegmentedDownloadResult(true, workerCount);
    }

    internal static int CalculateSegmentCount(long fileLength, int maximumSegments)
    {
        if (fileLength < MinimumFileSize || maximumSegments < 2)
            return 1;
        long sizeLimitedCount = Math.Max(1, fileLength / MinimumSegmentSize);
        return (int)Math.Clamp(sizeLimitedCount, 1, Math.Clamp(maximumSegments, 1, 8));
    }

    internal static int SelectWorkerCount(string authority, int maximum, bool adaptive)
    {
        int requested = Math.Clamp(maximum, 1, 8);
        if (!adaptive || !HostProfiles.TryGetValue(authority, out HostTransferProfile? profile))
            return requested;
        lock (profile.Gate)
        {
            if (profile.ConsecutiveFailures > 0)
                return Math.Max(2, requested - Math.Min(3, profile.ConsecutiveFailures));
            if (profile.BytesPerSecond > 12 * 1024 * 1024)
                return requested;
            if (profile.BytesPerSecond > 0 && profile.BytesPerSecond < 1024 * 1024)
                return Math.Min(requested, 2);
            if (profile.BytesPerSecond > 0 && profile.BytesPerSecond < 4 * 1024 * 1024)
                return Math.Min(requested, 3);
            return requested;
        }
    }

    private async Task<RangeProbe> ProbeAsync(
        Uri downloadUri,
        long expectedLength,
        Func<HttpRequestMessage> requestFactory,
        Action<HttpResponseMessage> validateResponse,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = requestFactory();
        ConfigureRangeRequest(request, 0, 0, validator: null);
        using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new RangeDownloadNotSupportedException(
                $"Server answered range probe with HTTP {(int)response.StatusCode} instead of 206.");
        }

        validateResponse(response);
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (range?.From != 0 || range.To != 0 || range.Length is not long totalLength || totalLength <= 0)
            throw new RangeDownloadNotSupportedException("Server returned an invalid Content-Range for the range probe.");
        if (response.Content.Headers.ContentLength is long probeLength && probeLength != 1)
            throw new RangeDownloadNotSupportedException("Server returned an invalid range-probe Content-Length.");
        EnsureIdentityEncoding(response);
        if (expectedLength > 0 && totalLength != expectedLength)
        {
            throw new InvalidDataException(
                $"Server reports {totalLength} bytes, expected {expectedLength} bytes.");
        }

        return new RangeProbe(
            totalLength,
            response.Headers.ETag,
            response.Content.Headers.LastModified,
            RemoteChecksumValidator.Read(response));
    }

    private async Task DownloadWorkerAsync(
        ConcurrentQueue<Segment> pending,
        Uri downloadUri,
        RangeProbe probe,
        Func<HttpRequestMessage> requestFactory,
        Action<HttpResponseMessage> validateResponse,
        Action<int> reportBytes,
        SegmentManifestStore? manifest,
        CancellationToken cancellationToken)
    {
        while (pending.TryDequeue(out Segment? segment))
        {
            await DownloadSegmentAsync(
                downloadUri,
                segment,
                probe,
                requestFactory,
                validateResponse,
                reportBytes,
                cancellationToken).ConfigureAwait(false);
            if (manifest != null)
                await manifest.RecordCompletedAsync(segment, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DownloadSegmentAsync(
        Uri downloadUri,
        Segment segment,
        RangeProbe probe,
        Func<HttpRequestMessage> requestFactory,
        Action<HttpResponseMessage> validateResponse,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        long existing = File.Exists(segment.Path) ? new FileInfo(segment.Path).Length : 0;
        if (existing == segment.Length)
            return;
        if (existing > segment.Length)
        {
            File.Delete(segment.Path);
            existing = 0;
        }

        await using var destination = new FileStream(
            segment.Path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            _bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        destination.Position = existing;
        long nextByte = segment.From + existing;

        while (nextByte <= segment.To)
        {
            using HttpRequestMessage request = requestFactory();
            ConfigureRangeRequest(request, nextByte, segment.To, probe);
            using HttpResponseMessage response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                throw new RangeDownloadNotSupportedException(
                    $"Server answered a segment request with HTTP {(int)response.StatusCode} instead of 206.");
            }

            validateResponse(response);
            EnsureIdentityEncoding(response);
            ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
            if (range?.From != nextByte
                || range.To is not long responseTo
                || responseTo < nextByte
                || responseTo > segment.To
                || range.Length != probe.TotalLength)
            {
                throw new RangeDownloadNotSupportedException("Server returned an inconsistent Content-Range.");
            }
            ValidateRepresentation(response, probe);

            long expectedResponseBytes = responseTo - nextByte + 1;
            long received = await CopyResponseAsync(
                response,
                destination,
                expectedResponseBytes,
                reportBytes,
                cancellationToken).ConfigureAwait(false);
            if (received != expectedResponseBytes)
            {
                throw new InvalidDataException(
                    $"Segment ended early: received {received} of {expectedResponseBytes} bytes.");
            }
            nextByte = responseTo + 1;
        }

        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (destination.Length != segment.Length)
        {
            throw new InvalidDataException(
                $"Segment has {destination.Length} bytes, expected {segment.Length} bytes.");
        }
    }

    private async Task<long> CopyResponseAsync(
        HttpResponseMessage response,
        FileStream destination,
        long maximumBytes,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        byte[] buffer = new byte[_bufferSize];
        long received = 0;
        while (received < maximumBytes)
        {
            int requested = (int)Math.Min(buffer.Length, maximumBytes - received);
            int read;
            using (var readCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                readCancellation.CancelAfter(_readStallTimeout);
                try
                {
                    read = await source.ReadAsync(
                        buffer.AsMemory(0, requested), readCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"A download segment stalled for {_readStallTimeout.TotalSeconds:0} seconds.");
                }
            }

            if (read == 0)
                break;
            await _bandwidthLimiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            received += read;
            reportBytes(read);
        }
        return received;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var headerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        headerCancellation.CancelAfter(_headerTimeout);
        try
        {
            return await _client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                headerCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"No segment response headers received within {_headerTimeout.TotalSeconds:0} seconds.");
        }
    }

    private static void ConfigureRangeRequest(
        HttpRequestMessage request,
        long from,
        long to,
        RangeProbe? validator)
    {
        request.Headers.Range = new RangeHeaderValue(from, to);
        request.Headers.AcceptEncoding.Clear();
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        if (validator?.ETag is { IsWeak: false } etag)
            request.Headers.IfRange = new RangeConditionHeaderValue(etag);
        else if (validator?.LastModified is DateTimeOffset modified)
            request.Headers.IfRange = new RangeConditionHeaderValue(modified);
    }

    private static void EnsureIdentityEncoding(HttpResponseMessage response)
    {
        if (response.Content.Headers.ContentEncoding.Any(encoding =>
            !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase)))
        {
            throw new RangeDownloadNotSupportedException(
                "Content-Encoding changes byte offsets, so segmented download was disabled.");
        }
    }

    private static void ValidateRepresentation(HttpResponseMessage response, RangeProbe probe)
    {
        EntityTagHeaderValue? responseTag = response.Headers.ETag;
        if (probe.ETag != null && responseTag != null
            && !probe.ETag.Tag.Equals(responseTag.Tag, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The remote file changed while its segments were downloading.");
        }
        DateTimeOffset? modified = response.Content.Headers.LastModified;
        if (probe.ETag == null && probe.LastModified != null && modified != null
            && probe.LastModified != modified)
        {
            throw new InvalidDataException("The remote file modification date changed during download.");
        }
    }

    private static Segment[] CreateSegments(
        string outputFile,
        string stableIdentity,
        RangeProbe probe,
        int? requestedCount)
    {
        string validator = probe.ETag?.Tag
            ?? probe.LastModified?.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? "none";
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{stableIdentity}\n{probe.TotalLength}\n{validator}")))[..16];
        int count = requestedCount
            ?? checked((int)Math.Ceiling(probe.TotalLength / (double)MinimumSegmentSize));
        count = Math.Max(1, count);
        long baseLength = requestedCount.HasValue
            ? probe.TotalLength / count
            : MinimumSegmentSize;
        long remainder = requestedCount.HasValue ? probe.TotalLength % count : 0;
        var segments = new Segment[count];
        long from = 0;
        for (int index = 0; index < count; index++)
        {
            long length = requestedCount.HasValue
                ? baseLength + (index < remainder ? 1 : 0)
                : Math.Min(baseLength, probe.TotalLength - from);
            long to = from + length - 1;
            string path = $"{outputFile}.segment.{fingerprint}.{index:D2}.{from}-{to}";
            segments[index] = new Segment(from, to, path);
            from = to + 1;
        }
        return segments;
    }

    private static async Task AssembleAsync(
        IReadOnlyList<Segment> segments,
        string assemblyPath,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        await using var destination = new FileStream(
            assemblyPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        foreach (Segment segment in segments)
        {
            await using var source = new FileStream(
                segment.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                256 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 256 * 1024, cancellationToken).ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (destination.Length != expectedLength)
        {
            throw new InvalidDataException(
                $"Segment assembly produced {destination.Length} of {expectedLength} bytes.");
        }
    }

    private static void DeleteUnexpectedSegmentArtifacts(
        string outputFile,
        IReadOnlyList<Segment> expectedSegments)
    {
        var expected = expectedSegments.Select(segment => segment.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in EnumerateSegmentArtifacts(outputFile))
        {
            if (!expected.Contains(path))
                TryDelete(path);
        }
    }

    internal static void DeleteSegmentArtifacts(string outputFile)
    {
        foreach (string path in EnumerateSegmentArtifacts(outputFile))
            TryDelete(path);
        TryDelete(outputFile + ".assembling");
        TryDelete(SegmentManifestStore.GetPath(outputFile));
    }

    private static IEnumerable<string> EnumerateSegmentArtifacts(string outputFile)
    {
        string? directory = Path.GetDirectoryName(outputFile);
        string fileName = Path.GetFileName(outputFile);
        return string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)
            ? Array.Empty<string>()
            : Directory.EnumerateFiles(directory, fileName + ".segment.*", SearchOption.TopDirectoryOnly);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Unable to delete segment artifact '{path}': {ex.Message}");
        }
    }

    private sealed record RangeProbe(
        long TotalLength,
        EntityTagHeaderValue? ETag,
        DateTimeOffset? LastModified,
        RemoteChecksum? Checksum);

    private sealed record Segment(long From, long To, string Path)
    {
        public long Length => To - From + 1;
    }

    private static void RecordHostSuccess(string authority, long bytes, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
            return;
        HostTransferProfile profile = HostProfiles.GetOrAdd(authority, _ => new HostTransferProfile());
        lock (profile.Gate)
        {
            double sample = bytes / elapsed.TotalSeconds;
            profile.BytesPerSecond = profile.BytesPerSecond <= 0
                ? sample
                : (profile.BytesPerSecond * 0.7) + (sample * 0.3);
            profile.ConsecutiveFailures = 0;
        }
    }

    private static void RecordHostFailure(string authority)
    {
        HostTransferProfile profile = HostProfiles.GetOrAdd(authority, _ => new HostTransferProfile());
        lock (profile.Gate)
            profile.ConsecutiveFailures = Math.Min(8, profile.ConsecutiveFailures + 1);
    }

    private sealed class HostTransferProfile
    {
        public object Gate { get; } = new();
        public double BytesPerSecond { get; set; }
        public int ConsecutiveFailures { get; set; }
    }

    private sealed class SegmentManifestStore
    {
        private readonly string _path;
        private readonly SegmentManifest _manifest;
        private readonly bool _hashSegments;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private SegmentManifestStore(string path, SegmentManifest manifest, bool hashSegments)
        {
            _path = path;
            _manifest = manifest;
            _hashSegments = hashSegments;
        }

        public static string GetPath(string outputFile) => outputFile + ".segments.json";

        public static async Task<SegmentManifestStore> OpenAsync(
            string outputFile,
            string stableIdentity,
            RangeProbe probe,
            IReadOnlyList<Segment> segments,
            bool hashSegments,
            CancellationToken cancellationToken)
        {
            string path = GetPath(outputFile);
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity)));
            SegmentManifest? manifest = null;
            if (File.Exists(path))
            {
                try
                {
                    string json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                    manifest = JsonConvert.DeserializeObject<SegmentManifest>(json);
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    Debug.WriteLine($"Unable to read segment manifest: {ex.Message}");
                }
            }

            string validator = probe.ETag?.Tag
                ?? probe.LastModified?.UtcDateTime.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? "none";
            if (manifest == null
                || manifest.TotalLength != probe.TotalLength
                || !manifest.IdentityHash.Equals(identity, StringComparison.Ordinal)
                || !manifest.Validator.Equals(validator, StringComparison.Ordinal))
            {
                manifest = new SegmentManifest
                {
                    IdentityHash = identity,
                    Validator = validator,
                    TotalLength = probe.TotalLength,
                    Blocks = segments.Select(segment => new SegmentManifestBlock
                    {
                        From = segment.From,
                        To = segment.To
                    }).ToList()
                };
            }

            var store = new SegmentManifestStore(path, manifest, hashSegments);
            await store.ValidateExistingAsync(segments, cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(cancellationToken).ConfigureAwait(false);
            return store;
        }

        public async Task RecordCompletedAsync(Segment segment, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SegmentManifestBlock? block = _manifest.Blocks.FirstOrDefault(item =>
                    item.From == segment.From && item.To == segment.To);
                if (block == null)
                    return;
                block.Completed = true;
                block.Sha256 = _hashSegments
                    ? await RemoteChecksumValidator.CalculateSha256Async(segment.Path, cancellationToken)
                        .ConfigureAwait(false)
                    : string.Empty;
                await SaveCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task ValidateExistingAsync(
            IReadOnlyList<Segment> segments,
            CancellationToken cancellationToken)
        {
            foreach (Segment segment in segments)
            {
                SegmentManifestBlock? block = _manifest.Blocks.FirstOrDefault(item =>
                    item.From == segment.From && item.To == segment.To);
                if (!File.Exists(segment.Path) || new FileInfo(segment.Path).Length != segment.Length)
                {
                    if (block != null)
                    {
                        block.Completed = false;
                        block.Sha256 = string.Empty;
                    }
                    continue;
                }
                if (_hashSegments && !string.IsNullOrWhiteSpace(block?.Sha256))
                {
                    string actual = await RemoteChecksumValidator.CalculateSha256Async(
                        segment.Path, cancellationToken).ConfigureAwait(false);
                    if (!actual.Equals(block.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(segment.Path);
                        block.Completed = false;
                        block.Sha256 = string.Empty;
                    }
                }
            }
        }

        private Task SaveAsync(CancellationToken cancellationToken) =>
            SaveCoreAsync(cancellationToken);

        private async Task SaveCoreAsync(CancellationToken cancellationToken)
        {
            string temp = _path + ".tmp";
            await File.WriteAllTextAsync(
                temp,
                JsonConvert.SerializeObject(_manifest, Formatting.Indented),
                cancellationToken).ConfigureAwait(false);
            File.Move(temp, _path, true);
        }
    }

    private sealed class SegmentManifest
    {
        public string IdentityHash { get; set; } = string.Empty;
        public string Validator { get; set; } = string.Empty;
        public long TotalLength { get; set; }
        public List<SegmentManifestBlock> Blocks { get; set; } = new();
    }

    private sealed class SegmentManifestBlock
    {
        public long From { get; set; }
        public long To { get; set; }
        public bool Completed { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
