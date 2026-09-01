using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser
{
    public class CommonDownload: Download
    {
        internal HttpClient DownloadClient { get; }
        internal CloudflareSession? FlareSolverrSession { get; private set; }
        internal IDownloadLinkResolver? LinkResolver { get; }
        internal BandwidthLimiter BandwidthLimiter { get; }
        internal bool ValidateRemoteChecksums { get; }
        internal ToolTip DownloadToolTip => ToolTip;
        private readonly SemaphoreSlim _flareSolverrRefreshLock = new(1, 1);
        private DateTime _lastFlareSolverrRefreshUtc = DateTime.MinValue;
        private int _completionSignaled;
        private int _activeDownloads;
        private int _downloadClientDisposed;
        private readonly int _maximumDownloadsPerHost;
        private readonly Dictionary<string, int> _activeDownloadsByHost =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _schedulerGate = new();

        public CommonDownload(List<CloudFile> files, ProgressBar[] progressBars,
            Label[] progressLabels, ToolTip toolTip, CloudServiceType cloudServiceType,
            string baseDownloadPath, int overwriteMode = 3, NetworkCredential? networkCredential = null,
            bool folderNewFiles = true, CloudflareSession? flareSolverrSession = null,
            IDownloadLinkResolver? linkResolver = null,
            DownloadHistoryStore? historyStore = null,
            ChecksumManifestStore? checksumStore = null,
            string requestedRouteId = DownloadRouteIds.Direct)
            : base(historyStore, checksumStore)
        {
            progressbars = progressBars;
            progresslabels = progressLabels;
            Downloads = new List<IFileDownload>();
            OverwriteMode = overwriteMode;
            CloudService = cloudServiceType;
            ToolTip = toolTip;
            FlareSolverrSession = flareSolverrSession;
            LinkResolver = linkResolver;
            BandwidthLimiter = new BandwidthLimiter(Properties.Settings.Default.bandwidthLimitKibPerSecond);
            ValidateRemoteChecksums = true;
            _maximumDownloadsPerHost = Math.Clamp(
                Properties.Settings.Default.maximumDownloadsPerHost > 0
                    ? Properties.Settings.Default.maximumDownloadsPerHost
                    : 2,
                1,
                32);
            int maximumSegments = Math.Clamp(
                Properties.Settings.Default.maximumSegmentsPerFile > 0
                    ? Properties.Settings.Default.maximumSegmentsPerFile
                    : 4,
                1,
                8);

            var handler = AppHttpClientFactory.CreateHandler(
                DecompressionMethods.None,
                allowAutoRedirect: true,
                maxConnectionsPerServer: Math.Clamp(
                    progressBars.Length * maximumSegments,
                    4,
                    16),
                routeKey: cloudServiceType.ToString());
            DownloadClient = new HttpClient(handler)
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            DownloadClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            DownloadClient.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            if (networkCredential != null)
            {
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    $"{networkCredential.UserName}:{networkCredential.Password}"));
                DownloadClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
            }

            FailedDownloads = new List<IFileDownload>();

            if (folderNewFiles)
                DownloadFolderPath = Path.Combine(baseDownloadPath, "0_New Files", DateTime.Now.ToString("yyyy-MM-dd"));
            else
                DownloadFolderPath = Path.GetFullPath(baseDownloadPath);

            Directory.CreateDirectory(DownloadFolderPath);
            var invalidFiles = new List<string>();
            var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CloudFile file in files)
            {
                try
                {
                    string cloudPath = Utility.ApplyFileNameOverride(file.Path, file.LocalNameOverride);
                    string savePath = string.IsNullOrWhiteSpace(file.LocalSavePathOverride)
                        ? Utility.GetSafeDownloadPath(DownloadFolderPath, cloudPath)
                        : Path.GetFullPath(file.LocalSavePathOverride);
                    if (!destinations.Add(savePath))
                        throw new InvalidDataException("Another selected file maps to the same local path.");

                    CommonFileDownload fileDownload = new CommonFileDownload(this, file, savePath, networkCredential);
                    fileDownload.HistoryStore = HistoryStore;
                    file.DownloadHistoryId ??= Guid.NewGuid();
                    fileDownload.HistoryEntry = new DownloadHistoryEntry
                    {
                        Id = file.DownloadHistoryId.Value,
                        CloudService = cloudServiceType,
                        FileName = file.Name,
                        CloudPath = file.Path,
                        SourceUrl = file.PublicUrl?.AbsoluteUri ?? string.Empty,
                        SavePath = savePath,
                        ExpectedSize = file.Size,
                        RequestedRouteId = requestedRouteId,
                        EffectiveRouteId = linkResolver == null ? DownloadRouteIds.Direct : string.Empty,
                        EffectiveRouteName = linkResolver == null ? "Direct" : string.Empty,
                        Status = DownloadJobStatus.Queued
                    };
                    DownloadQueue.Enqueue(fileDownload);
                    Downloads.Add(fileDownload);
                }
                catch (Exception ex)
                {
                    invalidFiles.Add($"{file.Name}: {ex.Message}");
                }
            }

            if (invalidFiles.Count > 0)
                MessageBox.Show(
                    "Some files were skipped because their paths are invalid:\n\n" +
                    string.Join("\n", invalidFiles.Take(10)),
                    "Invalid download paths",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }

        public override async Task Start()
        {
            semaphoreSlim = new SemaphoreSlim(1, 1);
            try
            {
                DownloadHistoryEntry[] queuedEntries = Downloads
                    .OfType<FileDownload>()
                    .Select(download => download.PrepareHistoryEntry(DownloadJobStatus.Queued))
                    .Where(entry => entry != null)
                    .Cast<DownloadHistoryEntry>()
                    .ToArray();
                await HistoryStore.UpsertManyAsync(queuedEntries);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or JsonException
                or System.Security.Cryptography.CryptographicException)
            {
                Debug.WriteLine($"Unable to persist queued downloads: {ex.Message}");
            }

            if (CancellationTokenSource.IsCancellationRequested)
            {
                DisposeDownloadClient();
                SignalDownloadCompleted();
                return;
            }

            progresslabels[progresslabels.Length - 1].Text = "";
            progresslabels[progresslabels.Length - 1].Visible = true;

            if (Downloads.Count == 0)
            {
                progresslabels[progresslabels.Length - 1].Text = "No files selected";
                DisposeDownloadClient();
                SignalDownloadCompleted();
                return;
            }
               
            for (int i = 0; i < progressbars.Length; i++)
            {
                if (CancellationTokenSource.IsCancellationRequested)
                    return;

                if (progressbars[i].Tag == null)
                {
                    var download = await TryDequeueAsync();
                    if (download == null)
                        break;
                    if (CancellationTokenSource.IsCancellationRequested)
                        return;

                    AssignProgressControls(download, progressbars[i], progresslabels[i]);
                    LaunchDownload(download);
                }
            }
        }

        internal async Task<DebridResolvedLink> ResolveDownloadUriAsync(
            CloudFile file,
            Uri source,
            CancellationToken cancellationToken)
        {
            if (LinkResolver == null)
            {
                return new DebridResolvedLink(
                    source,
                    RouteId: DownloadRouteIds.Direct,
                    RouteDisplayName: "Direct");
            }
            DebridResolvedLink resolved = LinkResolver is IAutomaticDownloadLinkResolver automatic
                ? await automatic.ResolveAsync(file, source, cancellationToken)
                : await LinkResolver.ResolveAsync(source, cancellationToken);
            if (!file.HasKnownSize && resolved.FileSize is >= 0)
            {
                file.Size = resolved.FileSize.Value;
                file.HasKnownSize = true;
            }
            return resolved;
        }

        public async Task UpdateQueue(CommonFileDownload d)
        {
            if (d.Finished)
                return;

            d.Finished = true;
            ReleaseSchedulerSlot(d);
            d.ProgressBar.Tag = null;
            d.ProgressBar.Value = 0;
            d.ProgressBar.Visible = false;
            d.ProgressLabel.Visible = false;

            FinishedDownloads++;
            progresslabels[progresslabels.Length - 1].Text = $"{FinishedDownloads}/{Downloads.Count} files finished";

            if (!CancellationTokenSource.IsCancellationRequested)
            {
                var newd = await TryDequeueAsync();
                if (newd != null && !CancellationTokenSource.IsCancellationRequested)
                {
                    AssignProgressControls(newd, d.ProgressBar, d.ProgressLabel);
                    LaunchDownload(newd);
                }
            }

            if (FinishedDownloads == Downloads.Count && !CancellationTokenSource.IsCancellationRequested)
            {
                DisposeDownloadClient();
                SignalDownloadCompleted();
            }
        }

        private async Task<CommonFileDownload?> TryDequeueAsync()
        {
            await semaphoreSlim.WaitAsync();
            try
            {
                if (DownloadQueue.Count == 0)
                    return null;
                var pending = new List<CommonFileDownload>();
                while (DownloadQueue.Count > 0)
                {
                    if (DownloadQueue.Dequeue() is CommonFileDownload item)
                        pending.Add(item);
                }

                CommonFileDownload? selected = pending
                    .Where(item => GetActiveHostCount(item.SchedulerHost) < _maximumDownloadsPerHost)
                    .OrderBy(item => GetActiveHostCount(item.SchedulerHost))
                    .ThenByDescending(item => item.FileInfo.DownloadPriority)
                    .ThenBy(item => item.FileInfo.HasKnownSize ? item.FileInfo.Size : long.MaxValue)
                    .FirstOrDefault();
                foreach (CommonFileDownload item in pending)
                {
                    if (!ReferenceEquals(item, selected))
                        DownloadQueue.Enqueue(item);
                }
                if (selected != null)
                {
                    lock (_schedulerGate)
                    {
                        int current = _activeDownloadsByHost.TryGetValue(
                            selected.SchedulerHost, out int count) ? count : 0;
                        _activeDownloadsByHost[selected.SchedulerHost] = current + 1;
                    }
                }
                return selected;
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private int GetActiveHostCount(string host)
        {
            lock (_schedulerGate)
                return _activeDownloadsByHost.TryGetValue(host, out int count) ? count : 0;
        }

        private void ReleaseSchedulerSlot(CommonFileDownload download)
        {
            string host = download.SchedulerHost;
            lock (_schedulerGate)
            {
                int count = _activeDownloadsByHost.TryGetValue(host, out int current) ? current : 0;
                if (count <= 1)
                    _activeDownloadsByHost.Remove(host);
                else
                    _activeDownloadsByHost[host] = count - 1;
            }
        }

        private void AssignProgressControls(CommonFileDownload download, ProgressBar progressBar, Label progressLabel)
        {
            download.ProgressBar = progressBar;
            download.ProgressLabel = progressLabel;
            progressBar.Visible = true;
            progressLabel.Visible = true;
            ToolTip.SetToolTip(progressLabel, download.FileInfo.Name);
        }

        private async Task StartDownloadSafelyAsync(CommonFileDownload download)
        {
            try
            {
                await download.StartDownload();
            }
            catch (OperationCanceledException) when (CancellationTokenSource.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Download failed before transfer started: {ex}");
                download.DownloadFailed = true;
                if (!FailedDownloads.Contains(download))
                    FailedDownloads.Add(download);
                download.ProgressLabel.Text = $"Failed: {download.FileInfo.Name}";
                await download.UpdateHistoryAsync(DownloadJobStatus.Failed, ex.Message);
                await UpdateQueue(download);
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeDownloads) == 0
                    && CancellationTokenSource.IsCancellationRequested)
                {
                    DisposeDownloadClient();
                    SignalDownloadCompleted();
                }
            }
        }

        private void LaunchDownload(CommonFileDownload download)
        {
            Interlocked.Increment(ref _activeDownloads);
            _ = StartDownloadSafelyAsync(download);
        }

        internal async Task<bool> TryRefreshFlareSolverrSessionAsync(Uri targetUrl, CancellationToken cancellationToken)
        {
            if (!Properties.Settings.Default.flareSolverrEnabled)
                return false;

            await _flareSolverrRefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (FlareSolverrSession != null
                    && DateTime.UtcNow - _lastFlareSolverrRefreshUtc < TimeSpan.FromSeconds(10))
                {
                    return true;
                }

                var timeoutSeconds = Math.Clamp(
                    Properties.Settings.Default.flareSolverrTimeoutSeconds, 10, 300);
                using var flareSolverr = new FlareSolverrClient(
                    Properties.Settings.Default.flareSolverrUrl,
                    TimeSpan.FromSeconds(timeoutSeconds));
                FlareSolverrSession = await flareSolverr.GetSessionAsync(targetUrl, cancellationToken)
                    .ConfigureAwait(false);
                _lastFlareSolverrRefreshUtc = DateTime.UtcNow;
                return true;
            }
            catch (FlareSolverrException ex)
            {
                Debug.WriteLine($"FlareSolverr refresh failed: {ex.Message}");
                return false;
            }
            finally
            {
                _flareSolverrRefreshLock.Release();
            }
        }

        public override void Stop()
        {
            CancellationTokenSource.Cancel();
            if (Volatile.Read(ref _activeDownloads) == 0)
            {
                DisposeDownloadClient();
                SignalDownloadCompleted();
            }
        }

        private void DisposeDownloadClient()
        {
            if (Interlocked.Exchange(ref _downloadClientDisposed, 1) == 0)
            {
                DownloadClient.Dispose();
                (LinkResolver as IDisposable)?.Dispose();
            }
        }

        private void SignalDownloadCompleted()
        {
            if (Interlocked.Exchange(ref _completionSignaled, 1) == 0)
                OnDownloadCompleted(EventArgs.Empty);
        }

    }

    public class DownloadEventArgs: EventArgs
    {
        public CommonFileDownload Download { get; }

        public DownloadEventArgs(CommonFileDownload dl)
        {
            Download = dl;
        }
    }
}
