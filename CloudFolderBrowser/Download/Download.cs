using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using CloudFolderBrowser.Sync;

namespace CloudFolderBrowser
{
    public abstract class Download : IDownload
    {
        internal DownloadHistoryStore HistoryStore { get; }

        internal ChecksumManifestStore ChecksumStore { get; }
        private readonly object _pauseGate = new();
        private Task? _pauseQueuedTask;

        protected Download(
            DownloadHistoryStore? historyStore = null,
            ChecksumManifestStore? checksumStore = null)
        {
            HistoryStore = historyStore ?? DownloadHistoryStore.Default;
            ChecksumStore = checksumStore ?? ChecksumManifestStore.Default;
        }

        public List<IFileDownload> Downloads { get; set; }

        protected readonly Queue<IFileDownload> DownloadQueue = new Queue<IFileDownload>();

        protected ProgressBar[] progressbars { get; set; }

        protected Label[] progresslabels { get; set; }

        protected ToolTip ToolTip { get; set; }

        public CancellationTokenSource CancellationTokenSource = new CancellationTokenSource();

        public int OverwriteMode { get; set; }

        public CloudServiceType CloudService { get; set; }

        public int FinishedDownloads = 0, MaxDownloadRetries = 4, RetryDelay = 300;

        public bool CheckDownloadedFileSize = true;

        public double CheckFileSizeError = 0.999;

        public List<IFileDownload> FailedDownloads { get; set; }

        public string DownloadFolderPath { get; set; }

        protected SemaphoreSlim semaphoreSlim = new SemaphoreSlim(1, 1);

        public event EventHandler DownloadCompleted;
        protected virtual void OnDownloadCompleted(EventArgs e)
        {
            EventHandler handler = DownloadCompleted;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        public event EventHandler DownloadFailed;
        protected virtual void OnDownloadFailed(DownloadEventArgs e)
        {
            EventHandler handler = DownloadFailed;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        public abstract Task Start();
        public abstract void Stop();

        protected Task PauseQueuedDownloadsAsync()
        {
            lock (_pauseGate)
                return _pauseQueuedTask ??= PauseQueuedDownloadsCoreAsync();
        }

        private async Task PauseQueuedDownloadsCoreAsync()
        {
            try
            {
                DownloadHistoryEntry[] entries = Downloads.OfType<FileDownload>()
                    .Where(download => download.HistoryEntry?.Status == DownloadJobStatus.Queued)
                    .Select(download => download.PrepareHistoryEntry(DownloadJobStatus.Paused))
                    .OfType<DownloadHistoryEntry>()
                    .ToArray();
                if (entries.Length > 0)
                    await HistoryStore.UpsertManyAsync(entries);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or Newtonsoft.Json.JsonException or System.Security.Cryptography.CryptographicException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to persist paused downloads: {ex.Message}");
            }
        }

        protected static void ValidateProgressControls(ProgressBar[] bars, Label[] labels)
        {
            ArgumentNullException.ThrowIfNull(bars);
            ArgumentNullException.ThrowIfNull(labels);
            if (bars.Length == 0 || labels.Length < bars.Length + 1
                || bars.Any(bar => bar == null) || labels.Any(label => label == null))
                throw new ArgumentException("Downloads require at least one progress bar, one label per worker, and a summary label.");
        }
    }
}
