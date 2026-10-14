using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Web;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using System.Security.Cryptography;

namespace CloudFolderBrowser
{
    public class CommonFileDownload : FileDownload
    {
        private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(90);
        private const int BufferSize = 128 * 1024;

        public CommonDownload ParentDownload { get; set; }
        public CloudFile FileInfo { get; }
        private readonly NetworkCredential? _networkCredential;
        private long _transferStartBytes;
        private long _transferStartTimestamp;
        private long _lastSpeedBytes;
        private long _lastSpeedTimestamp;
        private double _bytesPerSecond;
        private int _activeSegmentCount = 1;
        private RemoteChecksum? _expectedRemoteChecksum;

        public double BytesPerSecond => _bytesPerSecond;
        public TimeSpan? EstimatedRemaining { get; private set; }
        internal string SchedulerHost => FileInfo.PublicUrl?.Host?.ToLowerInvariant() ?? "unknown";

        public CommonFileDownload(CommonDownload commonDownload, CloudFile fileInfo, string savePath, NetworkCredential? networkCredential)
        {
            SavePath = Path.GetFullPath(savePath);
            FileInfo = fileInfo;
            ParentDownload = commonDownload;
            _networkCredential = networkCredential;

            var progressHandler = new Progress<double>(value => ProgressPercent = value);
            progressHandler.ProgressChanged += ProgreessChanged;
            Progress = progressHandler;
        }

        private void ProgreessChanged(object? sender, double percentage)
        {
            ProgressBar.Value = Math.Clamp((int)percentage, ProgressBar.Minimum, ProgressBar.Maximum);
            string segmentText = _activeSegmentCount > 1 ? $"{_activeSegmentCount} segments • " : string.Empty;
            ProgressLabel.Text =
                $"{percentage:0.0}% • {segmentText}{FormatRate(_bytesPerSecond)}{FormatEta(EstimatedRemaining)}{FileInfo.Name}";
            ParentDownload.DownloadToolTip.SetToolTip(
                ProgressLabel,
                $"{FileInfo.Name}\n{percentage * FileInfo.Size / 100_000_000:0}/{FileInfo.Size / 1_000_000:0} MB • " +
                $"{FormatRate(_bytesPerSecond)}{FormatEta(EstimatedRemaining).TrimEnd(' ', '•')}");
        }

        private void ProgressChanged(long complete, long total)
        {
            if (total > 0 && !FileInfo.HasKnownSize)
            {
                FileInfo.Size = total;
                FileInfo.HasKnownSize = true;
                if (HistoryEntry != null)
                {
                    HistoryEntry.ExpectedSize = total;
                    HistoryEntry.HasKnownSize = true;
                }
            }
            if (total <= 0)
                total = FileInfo.Size;

            var percentage = total > 0 ? Math.Round(100.0 * complete / total, 2) : 0;
            UpdateTransferMetrics(complete, total);
            Progress.Report(Math.Clamp(percentage, 0, 100));
        }

        public async Task StartDownload()
        {
            ProgressBar.Tag = this;
            ProgressLabel.Text = string.Empty;
            ProgressLabel.Visible = true;
            await UpdateHistoryAsync(DownloadJobStatus.Downloading);

            var directory = Path.GetDirectoryName(SavePath);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException($"Invalid download path: {SavePath}");

            Utility.ValidateDownloadTargets(new[] { SavePath });
            Directory.CreateDirectory(directory);
            SanitizeSavePath();

            var partialPath = SavePath + ".part";
            if (!PrepareExistingFile(partialPath))
            {
                await UpdateHistoryAsync(DownloadJobStatus.Skipped);
                await ParentDownload.UpdateQueue(this);
                return;
            }

            Uri sourceUri = BuildDownloadUri();
            Uri downloadUri = sourceUri;
            bool refreshResolvedLink = ParentDownload.LinkResolver != null;
            var maxRetries = Math.Max(0, ParentDownload.MaxDownloadRetries);
            RemainedRetries = maxRetries;
            Exception? lastFailure = null;

            for (var attempt = 0; attempt <= maxRetries; attempt++)
            {
                RemainedRetries = maxRetries - attempt;

                try
                {
                    ParentDownload.CancellationTokenSource.Token.ThrowIfCancellationRequested();

                    if (refreshResolvedLink)
                    {
                        ProgressLabel.Text = $"Resolving with {ParentDownload.LinkResolver!.DisplayName}: {FileInfo.Name}";
                        DebridResolvedLink resolved = await ParentDownload.ResolveDownloadUriAsync(
                            FileInfo,
                            sourceUri,
                            ParentDownload.CancellationTokenSource.Token);
                        downloadUri = resolved.DownloadUri;
                        if (HistoryEntry != null && FileInfo.HasKnownSize)
                        {
                            HistoryEntry.ExpectedSize = FileInfo.Size;
                            HistoryEntry.HasKnownSize = true;
                        }
                        if (HistoryEntry != null)
                        {
                            HistoryEntry.EffectiveRouteId = resolved.RouteId;
                            HistoryEntry.EffectiveRouteName = resolved.RouteDisplayName;
                            await UpdateHistoryAsync(DownloadJobStatus.Downloading);
                        }
                        if (!string.IsNullOrWhiteSpace(resolved.RouteDisplayName))
                            ProgressLabel.Text = $"{resolved.RouteDisplayName}: {FileInfo.Name}";
                        refreshResolvedLink = false;
                    }

                    DownloadTask = DownloadFileAsync(
                        downloadUri,
                        sourceUri.AbsoluteUri,
                        partialPath,
                        ParentDownload.CancellationTokenSource.Token,
                        ProgressChanged);
                    await DownloadTask;

                    var actualSize = new FileInfo(partialPath).Length;
                    if (FileInfo.HasKnownSize && actualSize != FileInfo.Size)
                    {
                        throw new InvalidDataException(
                            $"Incomplete file: received {actualSize} of {FileInfo.Size} bytes.");
                    }

                    File.Move(partialPath, SavePath, true);
                    Utility.PreserveDownloadedModifiedTime(SavePath, FileInfo.Modified);
                    AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                    if (ParentDownload.LinkResolver is IAutomaticDownloadLinkResolver automaticResolver)
                        automaticResolver.ReportSuccess(FileInfo, sourceUri);
                    Progress.Report(100);
                    string postProcessingMessage = string.Empty;
                    if (Properties.Settings.Default.archivePostProcessingEnabled)
                    {
                        ProgressLabel.Text = $"Post-processing: {FileInfo.Name}";
                        try
                        {
                            PostProcessingResult result = await new DownloadPostProcessor().ProcessAsync(
                                SavePath,
                                ParentDownload.CancellationTokenSource.Token);
                            postProcessingMessage = result.Processed ? string.Empty : result.Message;
                        }
                        catch (OperationCanceledException) when (ParentDownload.CancellationTokenSource.IsCancellationRequested)
                        {
                            postProcessingMessage = "Post-processing cancelled; the downloaded file is complete.";
                        }
                        catch (Exception ex) when (ex is IOException
                            or InvalidDataException
                            or UnauthorizedAccessException
                            or System.ComponentModel.Win32Exception)
                        {
                            postProcessingMessage = "Post-processing warning: " + ex.Message;
                            Debug.WriteLine(postProcessingMessage);
                        }
                    }
                    string sha256 = await TryRecordChecksumAsync();
                    await UpdateHistoryAsync(
                        DownloadJobStatus.Completed,
                        error: postProcessingMessage,
                        sha256: sha256);
                    await ParentDownload.UpdateQueue(this);
                    return;
                }
                catch (OperationCanceledException) when (ParentDownload.CancellationTokenSource.IsCancellationRequested)
                {
                    // Keep the .part file so a later run can resume it.
                    await UpdateHistoryAsync(DownloadJobStatus.Paused);
                    return;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                    if (ex is RemoteChecksumMismatchException)
                    {
                        if (File.Exists(partialPath))
                            File.Delete(partialPath);
                        AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                    }
                    if (ParentDownload.CancellationTokenSource.IsCancellationRequested)
                    {
                        await UpdateHistoryAsync(DownloadJobStatus.Paused);
                        return;
                    }

                    LogFailure(downloadUri, attempt + 1, maxRetries + 1, ex);

                    if (ParentDownload.LinkResolver is IAutomaticDownloadLinkResolver automaticResolver)
                    {
                        automaticResolver.ReportFailure(FileInfo, sourceUri, ex);
                        refreshResolvedLink = true;
                    }

                    if (attempt == maxRetries)
                        break;

                    if (ex is HttpRequestException httpException
                        && httpException.StatusCode is HttpStatusCode.Forbidden
                            or HttpStatusCode.Unauthorized
                            or HttpStatusCode.NotFound
                            or HttpStatusCode.ServiceUnavailable
                            or HttpStatusCode.TooManyRequests)
                    {
                        if (ParentDownload.LinkResolver != null)
                        {
                            refreshResolvedLink = true;
                            ProgressLabel.Text = $"Refreshing {ParentDownload.LinkResolver.DisplayName} link: {FileInfo.Name}";
                        }
                        else
                        {
                            ProgressLabel.Text = $"Refreshing browser session: {FileInfo.Name}";
                            await ParentDownload.TryRefreshFlareSolverrSessionAsync(
                                FileInfo.PublicUrl ?? downloadUri,
                                ParentDownload.CancellationTokenSource.Token);
                        }
                    }

                    ProgressLabel.Text =
                        $"Retry {attempt + 1}/{maxRetries}: {FileInfo.Name}";
                    try
                    {
                        await Task.Delay(GetRetryDelay(attempt), ParentDownload.CancellationTokenSource.Token);
                    }
                    catch (OperationCanceledException) when (ParentDownload.CancellationTokenSource.IsCancellationRequested)
                    {
                        await UpdateHistoryAsync(DownloadJobStatus.Paused);
                        return;
                    }
                }
            }

            DownloadFailed = true;
            if (!ParentDownload.FailedDownloads.Contains(this))
                ParentDownload.FailedDownloads.Add(this);
            ProgressLabel.Text = $"Failed: {FileInfo.Name}";
            await UpdateHistoryAsync(
                DownloadJobStatus.Failed,
                lastFailure == null ? "Retry limit reached." : GetSafeFailureMessage(lastFailure, downloadUri));
            await ParentDownload.UpdateQueue(this);
        }

        public Task RetryDownload()
        {
            // Kept for API compatibility. StartDownload now owns an iterative retry
            // loop, avoiding recursive tasks and preserving partial files.
            return StartDownload();
        }

        private bool PrepareExistingFile(string partialPath)
        {
            var destination = new FileInfo(SavePath);
            if (!destination.Exists)
            {
                if (File.Exists(partialPath) && FileInfo.HasKnownSize && new FileInfo(partialPath).Length > FileInfo.Size)
                {
                    File.Delete(partialPath);
                    AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                }
                return true;
            }

            var overwrite = FileInfo.PlannedAction == SyncPlanAction.Overwrite
                ? DialogResult.Yes
                : ParentDownload.OverwriteMode switch
                {
                    0 => DialogResult.No,
                    1 => DialogResult.Yes,
                    2 => FileInfo.Modified > destination.LastWriteTime ? DialogResult.Yes : DialogResult.No,
                    _ => MessageBox.Show(
                        $"File [{destination.Name}] already exists. Overwrite?",
                        string.Empty,
                        MessageBoxButtons.YesNo)
                };

            if (overwrite == DialogResult.No)
                return false;

            // A final file may belong to an older remote version. Never use its
            // bytes as a resume prefix or remove it before its replacement succeeds.
            // Only the explicitly partial .part path is eligible for resuming.
            if (FileInfo.PlannedAction != SyncPlanAction.Overwrite)
                return true;
            if (File.Exists(partialPath))
                File.Delete(partialPath);
            AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
            return true;
        }

        private Uri BuildDownloadUri()
        {
            var publicUri = FileInfo.PublicUrl
                ?? throw new InvalidOperationException($"Missing public URL for {FileInfo.Name}.");

            if (ParentDownload.CloudService == CloudServiceType.Allsync)
            {
                var encodedSegments = FileInfo.Path
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.EscapeDataString);
                var builder = new UriBuilder(publicUri.Scheme, publicUri.Host, publicUri.Port)
                {
                    Path = "/public.php/webdav/" + string.Join("/", encodedSegments)
                };
                return builder.Uri;
            }

            if (ParentDownload.CloudService == CloudServiceType.QCloud)
            {
                var builder = new UriBuilder(publicUri.Scheme, publicUri.Host, publicUri.Port)
                {
                    Path = $"/index.php/s/{_networkCredential?.UserName}/download",
                    Query = $"path=/&files={HttpUtility.UrlEncode(FileInfo.Path)}"
                };
                return builder.Uri;
            }

            return publicUri;
        }

        private async Task DownloadFileAsync(
            Uri downloadUri,
            string stableIdentity,
            string outputFile,
            CancellationToken cancellationToken,
            Action<long, long> progressCallback)
        {
            Utility.ValidateFileSystemPath(SavePath);
            Utility.ValidateFileSystemPath(outputFile);
            var existingLength = File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
            if (FileInfo.HasKnownSize && existingLength > FileInfo.Size)
            {
                File.Delete(outputFile);
                existingLength = 0;
            }
            ResetTransferMetrics(existingLength);

            if (Properties.Settings.Default.segmentedDownloadsEnabled
                && FileInfo.HasKnownSize
                && existingLength == 0)
            {
                int proposedSegments = AdaptiveSegmentedDownloader.CalculateSegmentCount(
                    FileInfo.Size,
                    Math.Clamp(Properties.Settings.Default.maximumSegmentsPerFile, 1, 8));
                if (proposedSegments > 1)
                {
                    _activeSegmentCount = proposedSegments;
                    var segmentedDownloader = new AdaptiveSegmentedDownloader(
                        ParentDownload.DownloadClient,
                        ParentDownload.BandwidthLimiter,
                        HeaderTimeout,
                        ReadStallTimeout,
                        BufferSize);
                    SegmentedDownloadResult segmented = await segmentedDownloader.TryDownloadAsync(
                        downloadUri,
                        stableIdentity,
                        outputFile,
                        FileInfo.Size,
                        proposedSegments,
                        () => CreateDownloadRequest(downloadUri),
                        response => ValidatePayloadResponse(response, downloadUri),
                        progressCallback,
                        cancellationToken,
                        persistentByteMap: true,
                        adaptiveWorkers: true,
                        validateRemoteChecksum: ParentDownload.ValidateRemoteChecksums,
                        workerCountChanged: count => _activeSegmentCount = count).ConfigureAwait(false);
                    if (segmented.Completed)
                        return;
                    _activeSegmentCount = 1;
                    ResetTransferMetrics(0);
                }
            }

            using var request = CreateDownloadRequest(downloadUri);
            if (existingLength > 0)
                request.Headers.Range = new RangeHeaderValue(existingLength, null);

            HttpResponseMessage response;
            using (var headerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                headerCts.CancelAfter(HeaderTimeout);
                try
                {
                    response = await ParentDownload.DownloadClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        headerCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"No response headers received within {HeaderTimeout.TotalSeconds:0} seconds.");
                }
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                    && existingLength > 0 && !FileInfo.HasKnownSize
                    && response.Content.Headers.ContentRange is { HasRange: false, Length: >= 0 } staleRange
                    && string.Equals(staleRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                    && staleRange.Length < existingLength)
                {
                    // The old prefix is longer than the current representation.
                    // Restart once without a Range header; the empty partial file
                    // makes this fallback ineligible on the next call.
                    response.Dispose();
                    File.Delete(outputFile);
                    _expectedRemoteChecksum = null;
                    await DownloadFileAsync(downloadUri, stableIdentity, outputFile,
                        cancellationToken, progressCallback).ConfigureAwait(false);
                    return;
                }
                if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable
                    && existingLength > 0
                    && response.Content.Headers.ContentRange is { HasRange: false } completeRange
                    && string.Equals(completeRange.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                    && completeRange.Length == existingLength)
                {
                    // A resumed file can already be complete when its original size was
                    // unknown. RFC 9110 represents that case as "bytes */<length>".
                    if (FileInfo.HasKnownSize && existingLength != FileInfo.Size)
                        throw new InvalidDataException("The remote size differs from the expected file size.");
                    RemoteChecksum? checksum = ParentDownload.ValidateRemoteChecksums
                        ? RemoteChecksumValidator.Read(response, includeContentMd5: false)
                        : null;
                    await RemoteChecksumValidator.ValidateFileAsync(outputFile, checksum, cancellationToken)
                        .ConfigureAwait(false);
                    SetKnownSize(existingLength);
                    progressCallback(existingLength, existingLength);
                    return;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} while downloading {downloadUri}.",
                        null,
                        response.StatusCode);
                }

                ValidatePayloadResponse(response, downloadUri);
                if (ParentDownload.ValidateRemoteChecksums)
                {
                    RemoteChecksum? responseChecksum = RemoteChecksumValidator.Read(
                        response,
                        includeContentMd5: response.StatusCode == HttpStatusCode.OK);
                    // A full response replaces the representation, including any
                    // checksum remembered from an earlier failed attempt.
                    _expectedRemoteChecksum = responseChecksum
                        ?? (response.StatusCode == HttpStatusCode.PartialContent ? _expectedRemoteChecksum : null);
                }

                var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                ContentRangeHeaderValue? range = response.StatusCode == HttpStatusCode.PartialContent
                    ? response.Content.Headers.ContentRange : null;
                if (response.StatusCode == HttpStatusCode.PartialContent
                    && (range == null || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
                        || range.From != (append ? existingLength : 0)
                        || range.To is not long end
                        || range.Length is not long length
                        || end != length - 1
                        || (response.Content.Headers.ContentLength is long contentLength
                            && contentLength != end - range.From + 1)))
                {
                    throw new InvalidDataException(
                        "Server returned an inconsistent Content-Range for the download.");
                }

                if (!append)
                {
                    if (existingLength > 0)
                        ResetTransferMetrics(0);
                    existingLength = 0;
                }

                var responseTotal = range?.Length
                    ?? (response.Content.Headers.ContentLength.HasValue
                        ? existingLength + response.Content.Headers.ContentLength.Value
                        : FileInfo.HasKnownSize ? FileInfo.Size : 0);

                if (FileInfo.HasKnownSize && responseTotal != FileInfo.Size)
                    throw new InvalidDataException($"Server reports {responseTotal} bytes, expected {FileInfo.Size} bytes.");
                if (response.Content.Headers.ContentLength.HasValue || range?.Length != null)
                    SetKnownSize(responseTotal);

                await using var destination = new FileStream(
                    outputFile,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.Read,
                    BufferSize,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                if (append)
                    destination.Position = existingLength;
                else
                    destination.SetLength(0);

                await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[BufferSize];
                long totalRead = existingLength;

                while (true)
                {
                    int read;
                    using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        readCts.CancelAfter(ReadStallTimeout);
                        try
                        {
                            read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), readCts.Token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            throw new TimeoutException(
                                $"Download stalled for {ReadStallTimeout.TotalSeconds:0} seconds at byte {totalRead}.");
                        }
                    }

                    if (read == 0)
                        break;

                    await ParentDownload.BandwidthLimiter.ThrottleAsync(read, cancellationToken).ConfigureAwait(false);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalRead += read;
                    progressCallback?.Invoke(totalRead, responseTotal);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                await source.DisposeAsync().ConfigureAwait(false);
                await destination.DisposeAsync().ConfigureAwait(false);

                if (FileInfo.HasKnownSize && totalRead != responseTotal)
                {
                    throw new InvalidDataException(
                        $"Connection ended early: received {totalRead} of {responseTotal} bytes.");
                }

                await RemoteChecksumValidator.ValidateFileAsync(
                    outputFile,
                    _expectedRemoteChecksum,
                    cancellationToken).ConfigureAwait(false);

                SetKnownSize(totalRead);

                Debug.Assert(totalRead >= 0);
            }
        }

        private void SetKnownSize(long size)
        {
            if (FileInfo.HasKnownSize)
                return;
            FileInfo.Size = size;
            FileInfo.HasKnownSize = true;
            if (HistoryEntry != null)
            {
                HistoryEntry.ExpectedSize = size;
                HistoryEntry.HasKnownSize = true;
            }
        }

        private HttpRequestMessage CreateDownloadRequest(Uri downloadUri)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            if (_networkCredential != null && FileInfo.PublicUrl is { IsAbsoluteUri: true } source
                && source.GetLeftPart(UriPartial.Authority).Equals(
                    downloadUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
            {
                string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{_networkCredential.UserName}:{_networkCredential.Password}"));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
            var flareSolverrSession = ParentDownload.FlareSolverrSession;
            var userAgent = flareSolverrSession?.UserAgent;
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                string.IsNullOrWhiteSpace(userAgent)
                    ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0"
                    : userAgent);
            return request;
        }

        private void ValidatePayloadResponse(HttpResponseMessage response, Uri downloadUri)
        {
            if (response.Content.Headers.ContentEncoding.Any(encoding =>
                !string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Server returned a content encoding that would change the downloaded file bytes.");
            if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.ResetContent)
            {
                throw new InvalidDataException(
                    $"HTTP {(int)response.StatusCode} returned no file content for {FileInfo.Name}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            var isExpectedHtmlFile = Path.GetExtension(FileInfo.Name)
                .Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(FileInfo.Name).Equals(".htm", StringComparison.OrdinalIgnoreCase);
            if (ParentDownload.CloudService is CloudServiceType.Allsync
                    or CloudServiceType.Dropbox
                    or CloudServiceType.GoogleDrive
                    or CloudServiceType.TeraBox
                    or CloudServiceType.Other
                && !isExpectedHtmlFile
                && string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException(
                    "Received an HTML page instead of the requested file. The share may require account access or confirmation.",
                    null,
                    HttpStatusCode.Forbidden);
            }
        }

        private void SanitizeSavePath()
        {
            var filename = Path.GetFileName(SavePath);
            var directory = Path.GetDirectoryName(SavePath);
            foreach (var character in Path.GetInvalidFileNameChars())
                filename = filename.Replace(character.ToString(), string.Empty);
            SavePath = Path.Combine(directory!, filename);
        }

        private TimeSpan GetRetryDelay(int attempt)
        {
            var baseDelay = Math.Max(1000, ParentDownload.RetryDelay);
            var multiplier = 1 << Math.Min(attempt, 5);
            var delay = Math.Min(30000, baseDelay * multiplier);
            return TimeSpan.FromMilliseconds(delay + Random.Shared.Next(100, 750));
        }

        private void ResetTransferMetrics(long existingBytes)
        {
            long now = Stopwatch.GetTimestamp();
            _transferStartBytes = existingBytes;
            _lastSpeedBytes = existingBytes;
            _transferStartTimestamp = now;
            _lastSpeedTimestamp = now;
            _bytesPerSecond = 0;
            EstimatedRemaining = null;
        }

        private void UpdateTransferMetrics(long complete, long total)
        {
            long now = Stopwatch.GetTimestamp();
            double sampleSeconds = (now - _lastSpeedTimestamp) / (double)Stopwatch.Frequency;
            if (sampleSeconds >= 0.35)
            {
                long sampleBytes = Math.Max(0, complete - _lastSpeedBytes);
                double instant = sampleBytes / sampleSeconds;
                _bytesPerSecond = _bytesPerSecond <= 0
                    ? instant
                    : (_bytesPerSecond * 0.72) + (instant * 0.28);
                _lastSpeedBytes = complete;
                _lastSpeedTimestamp = now;
            }
            else if (_bytesPerSecond <= 0)
            {
                double elapsed = Math.Max(0.001, (now - _transferStartTimestamp) / (double)Stopwatch.Frequency);
                _bytesPerSecond = Math.Max(0, complete - _transferStartBytes) / elapsed;
            }

            EstimatedRemaining = total > complete && _bytesPerSecond > 1
                ? TimeSpan.FromSeconds((total - complete) / _bytesPerSecond)
                : null;
        }

        private static string FormatRate(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
                return string.Empty;
            string value = bytesPerSecond >= 1024 * 1024
                ? $"{bytesPerSecond / (1024 * 1024):0.0} MB/s"
                : $"{bytesPerSecond / 1024:0} KB/s";
            return value + " • ";
        }

        private static string FormatEta(TimeSpan? eta)
        {
            if (eta is not { } value || value <= TimeSpan.Zero)
                return string.Empty;
            string text = value.TotalHours >= 1
                ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
                : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
            return $"ETA {text} • ";
        }

        private async Task<string> TryRecordChecksumAsync()
        {
            if (!Properties.Settings.Default.verifySha256)
                return string.Empty;

            try
            {
                ChecksumManifestEntry entry = await ParentDownload.ChecksumStore.RecordFileAsync(
                    SavePath,
                    ParentDownload.CancellationTokenSource.Token);
                return entry.Sha256;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                Debug.WriteLine($"Unable to record SHA-256 for {SavePath}: {ex.Message}");
                return string.Empty;
            }
            catch (OperationCanceledException) when (ParentDownload.CancellationTokenSource.IsCancellationRequested)
            {
                // The file has already been committed. A cancelled optional hash
                // must not downgrade a complete transfer to Paused.
                return string.Empty;
            }
        }

        private void LogFailure(Uri downloadUri, int attempt, int totalAttempts, Exception exception)
        {
            try
            {
                string safeUri = GetSafeUriForLog(downloadUri);
                string safeMessage = exception.Message.Replace(
                    downloadUri.AbsoluteUri,
                    safeUri,
                    StringComparison.OrdinalIgnoreCase);
                var logFileName = $"download-log-{DateTime.Now:MM-dd-yyyy}.txt";
                var log = new StringBuilder()
                    .AppendLine(DateTime.Now.ToString("O"))
                    .AppendLine($"DownloadUri: {safeUri}")
                    .AppendLine($"SavePath: {SavePath}")
                    .AppendLine($"Attempt: {attempt}/{totalAttempts}")
                    .AppendLine($"PartialBytes: {(File.Exists(SavePath + ".part") ? new FileInfo(SavePath + ".part").Length : 0)}")
                    .AppendLine($"ExpectedBytes: {FileInfo.Size}")
                    .AppendLine($"Exception: {exception.GetType().Name}: {safeMessage}")
                    .AppendLine()
                    .ToString();
                File.AppendAllText(logFileName, log);
            }
            catch (IOException logException)
            {
                Debug.WriteLine($"Unable to write download log: {logException.Message}");
            }
            catch (UnauthorizedAccessException logException)
            {
                Debug.WriteLine($"Unable to write download log: {logException.Message}");
            }
        }

        internal static string GetSafeUriForLog(Uri uri)
        {
            if (!uri.IsAbsoluteUri)
                return "[relative URL redacted]";
            return $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : $":{uri.Port}")}/…";
        }

        private static string GetSafeFailureMessage(Exception exception, Uri uri)
        {
            string message = string.IsNullOrWhiteSpace(exception.Message)
                ? exception.GetType().Name
                : exception.Message;
            return message.Replace(
                uri.AbsoluteUri,
                GetSafeUriForLog(uri),
                StringComparison.OrdinalIgnoreCase);
        }
    }
}
