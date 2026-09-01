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
            SavePath = savePath.Replace("%27", "'").Replace("/", "\\");
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
                    HistoryEntry.ExpectedSize = total;
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
                            HistoryEntry.ExpectedSize = FileInfo.Size;
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

                    if (!FileInfo.HasKnownSize || !File.Exists(partialPath) || new FileInfo(partialPath).Length != FileInfo.Size)
                    {
                        DownloadTask = DownloadFileAsync(
                            downloadUri,
                            sourceUri.AbsoluteUri,
                            partialPath,
                            ParentDownload.CancellationTokenSource.Token,
                            ProgressChanged);
                        await DownloadTask;
                    }

                    var actualSize = new FileInfo(partialPath).Length;
                    if (FileInfo.HasKnownSize && actualSize != FileInfo.Size)
                    {
                        throw new InvalidDataException(
                            $"Incomplete file: received {actualSize} of {FileInfo.Size} bytes.");
                    }

                    File.Move(partialPath, SavePath, true);
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

            var complete = FileInfo.HasKnownSize && destination.Length == FileInfo.Size;
            if (complete)
            {
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
                {
                    AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                    return false;
                }

                if (File.Exists(partialPath))
                    File.Delete(partialPath);
                AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                return true;
            }

            // Preserve the longest valid partial file. Older versions wrote partial
            // data directly to the final path, so migrate it to the .part file.
            if (!FileInfo.HasKnownSize || destination.Length < FileInfo.Size)
            {
                var partialLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : -1;
                if (destination.Length > partialLength)
                {
                    if (File.Exists(partialPath))
                        File.Delete(partialPath);
                    AdaptiveSegmentedDownloader.DeleteSegmentArtifacts(partialPath);
                    File.Move(SavePath, partialPath);
                }
                else
                {
                    destination.Delete();
                }
                return true;
            }

            destination.Delete();
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
                    && existingLength > 0
                    && response.Content.Headers.ContentRange?.Length == existingLength)
                {
                    // A resumed file can already be complete when its original size was
                    // unknown. RFC 9110 represents that case as "bytes */<length>".
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
                        includeContentMd5: existingLength == 0
                            && response.StatusCode == HttpStatusCode.OK);
                    if (responseChecksum != null)
                        _expectedRemoteChecksum = responseChecksum;
                }

                var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (append && response.Content.Headers.ContentRange?.From != existingLength)
                {
                    throw new InvalidDataException(
                        $"Server resumed at byte {response.Content.Headers.ContentRange?.From}, expected {existingLength}.");
                }

                if (!append)
                {
                    if (existingLength > 0)
                        ResetTransferMetrics(0);
                    existingLength = 0;
                }

                var responseTotal = response.Content.Headers.ContentRange?.Length
                    ?? (response.Content.Headers.ContentLength.HasValue
                        ? existingLength + response.Content.Headers.ContentLength.Value
                        : FileInfo.Size);

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

                if (responseTotal > 0 && totalRead != responseTotal)
                {
                    throw new InvalidDataException(
                        $"Connection ended early: received {totalRead} of {responseTotal} bytes.");
                }

                await RemoteChecksumValidator.ValidateFileAsync(
                    outputFile,
                    _expectedRemoteChecksum,
                    cancellationToken).ConfigureAwait(false);

                Debug.Assert(totalRead >= 0);
            }
        }

        private HttpRequestMessage CreateDownloadRequest(Uri downloadUri)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, downloadUri);
            var flareSolverrSession = ParentDownload.FlareSolverrSession;
            var userAgent = flareSolverrSession?.UserAgent;
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                string.IsNullOrWhiteSpace(userAgent)
                    ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CloudFolderBrowser/1.0"
                    : userAgent);
            var cookieHeader = flareSolverrSession?.GetCookieHeader(downloadUri);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            return request;
        }

        private void ValidatePayloadResponse(HttpResponseMessage response, Uri downloadUri)
        {
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
