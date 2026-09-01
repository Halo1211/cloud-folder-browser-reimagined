using CG.Web.MegaApiClient;

using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser
{
    public class MegaDownload: Download
    {
        private int _completionSignaled;
        private int _activeDownloads;
        public string ShareId;      

        public MegaDownload(MegaApiClient megaApiClient, List<CloudFile> files, ProgressBar[] progressBars, Label[] progressLabels, 
            ToolTip toolTip, string baseDownloadPath, int overwriteMode = 3, bool folderNewFiles = true, string shareId = "",
            DownloadHistoryStore? historyStore = null, ChecksumManifestStore? checksumStore = null,
            string requestedRouteId = DownloadRouteIds.Direct)
            : base(historyStore, checksumStore)
        {
            progressbars = progressBars;
            progresslabels = progressLabels;
            ToolTip = toolTip;
            Downloads = new List<IFileDownload>();
            OverwriteMode = overwriteMode;
            ShareId = shareId;
            FailedDownloads = new List<IFileDownload>();

            //MegaApiClient megaApiClient = new MegaApiClient();
            //megaApiClient.LoginAnonymous();

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
                    MegaFileDownload megaFileDownload;
                    if (file.MegaNode == null)
                        throw new InvalidDataException("The selected MEGA file has no node metadata.");
                    string cloudPath = Utility.ApplyFileNameOverride(file.Path, file.LocalNameOverride);
                    string savePath = string.IsNullOrWhiteSpace(file.LocalSavePathOverride)
                        ? Utility.GetSafeDownloadPath(DownloadFolderPath, cloudPath)
                        : Path.GetFullPath(file.LocalSavePathOverride);
                    if (!destinations.Add(savePath))
                        throw new InvalidDataException("Another selected file maps to the same local path.");

                    if (file.MegaNode is PublicNode publicNode)
                        megaFileDownload = new MegaFileDownload(megaApiClient, this, publicNode, savePath, file.PlannedAction == Sync.SyncPlanAction.Overwrite);
                    else
                        megaFileDownload = new MegaFileDownload(megaApiClient, this, file.MegaNode, savePath, file.PlannedAction == Sync.SyncPlanAction.Overwrite);
                    file.DownloadHistoryId ??= Guid.NewGuid();
                    megaFileDownload.HistoryStore = HistoryStore;
                    megaFileDownload.HistoryEntry = new DownloadHistoryEntry
                    {
                        Id = file.DownloadHistoryId.Value,
                        CloudService = CloudServiceType.Mega,
                        FileName = file.Name,
                        CloudPath = file.Path,
                        SourceUrl = file.PublicUrl?.AbsoluteUri ?? string.Empty,
                        SavePath = savePath,
                        ExpectedSize = file.Size,
                        RequestedRouteId = requestedRouteId,
                        EffectiveRouteId = DownloadRouteIds.MegaNative,
                        EffectiveRouteName = "MEGA native",
                        Status = DownloadJobStatus.Queued
                    };
                    DownloadQueue.Enqueue(megaFileDownload);
                    Downloads.Add(megaFileDownload);
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
            progresslabels[progresslabels.Length - 1].Text = "";
            progresslabels[progresslabels.Length - 1].Visible = true;

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
                or Newtonsoft.Json.JsonException
                or System.Security.Cryptography.CryptographicException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to persist queued MEGA downloads: {ex.Message}");
            }

            if (CancellationTokenSource.IsCancellationRequested)
            {
                SignalDownloadCompleted();
                return;
            }

            if (Downloads.Count == 0)
            {
                progresslabels[progresslabels.Length - 1].Text = "No files selected";
                SignalDownloadCompleted();
                return;
            }

            for (int i = 0; i < progressbars.Length; i++)
            {
                if (CancellationTokenSource.IsCancellationRequested)
                    return;

                if (progressbars[i].Tag != null)
                    continue;

                var download = await TryDequeueAsync();
                if (download == null)
                    break;
                if (CancellationTokenSource.IsCancellationRequested)
                    return;

                AssignProgressControls(download, progressbars[i], progresslabels[i]);
                LaunchDownload(download);
            }
        }           

        public async Task UpdateQueue(MegaFileDownload d)
        {
            if (d.Finished)
                return;

            d.Finished = true;
            d.ProgressBar.Tag = null;
            d.ProgressBar.Value = 0;
            d.ProgressBar.Visible = false;
            d.ProgressLabel.Visible = false;

            FinishedDownloads++;
            progresslabels[progresslabels.Length - 1].Text = $"{FinishedDownloads}/{Downloads.Count} files finished";

            if (!CancellationTokenSource.IsCancellationRequested)
            {
                var newDownload = await TryDequeueAsync();
                if (newDownload != null && !CancellationTokenSource.IsCancellationRequested)
                {
                    AssignProgressControls(newDownload, d.ProgressBar, d.ProgressLabel);
                    LaunchDownload(newDownload);
                }
            }

            if (FinishedDownloads == Downloads.Count && !CancellationTokenSource.IsCancellationRequested)
                SignalDownloadCompleted();
        }

        private async Task<MegaFileDownload?> TryDequeueAsync()
        {
            await semaphoreSlim.WaitAsync();
            try
            {
                return DownloadQueue.Count > 0 ? DownloadQueue.Dequeue() as MegaFileDownload : null;
            }
            finally
            {
                semaphoreSlim.Release();
            }
        }

        private void AssignProgressControls(MegaFileDownload download, ProgressBar progressBar, Label progressLabel)
        {
            download.ProgressBar = progressBar;
            download.ProgressLabel = progressLabel;
            progressBar.Visible = true;
            progressLabel.Visible = true;
            ToolTip.SetToolTip(progressLabel, Path.GetFileName(download.SavePath));
        }

        private async Task StartDownloadSafelyAsync(MegaFileDownload download)
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
                System.Diagnostics.Debug.WriteLine($"MEGA download failed before transfer started: {ex}");
                download.DownloadFailed = true;
                if (!FailedDownloads.Contains(download))
                    FailedDownloads.Add(download);
                download.ProgressLabel.Text = $"Failed: {Path.GetFileName(download.SavePath)}";
                await download.UpdateHistoryAsync(DownloadJobStatus.Failed, ex.Message);
                await UpdateQueue(download);
            }
            finally
            {
                if (Interlocked.Decrement(ref _activeDownloads) == 0
                    && CancellationTokenSource.IsCancellationRequested)
                {
                    SignalDownloadCompleted();
                }
            }
        }

        private void LaunchDownload(MegaFileDownload download)
        {
            Interlocked.Increment(ref _activeDownloads);
            _ = StartDownloadSafelyAsync(download);
        }

        public override void Stop()
        {
            CancellationTokenSource.Cancel();
            if (Volatile.Read(ref _activeDownloads) == 0)
                SignalDownloadCompleted();
        }

        private void SignalDownloadCompleted()
        {
            if (Interlocked.Exchange(ref _completionSignaled, 1) == 0)
                OnDownloadCompleted(EventArgs.Empty);
        }

    }
}
