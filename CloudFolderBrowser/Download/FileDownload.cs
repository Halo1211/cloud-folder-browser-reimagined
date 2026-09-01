using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace CloudFolderBrowser
{
    public class FileDownload : IFileDownload
    {
        internal DownloadHistoryStore HistoryStore { get; set; } = DownloadHistoryStore.Default;

        public DownloadHistoryEntry HistoryEntry { get; set; }

        public async Task UpdateHistoryAsync(
            DownloadJobStatus status,
            string error = "",
            string sha256 = "")
        {
            try
            {
                DownloadHistoryEntry? entry = PrepareHistoryEntry(status, error, sha256);
                if (entry == null)
                    return;
                await HistoryStore.UpsertAsync(entry);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or Newtonsoft.Json.JsonException
                or CryptographicException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to persist download history: {ex.Message}");
            }
        }

        internal DownloadHistoryEntry? PrepareHistoryEntry(
            DownloadJobStatus status,
            string error = "",
            string sha256 = "")
        {
            if (HistoryEntry == null)
                return null;

            string partialPath = SavePath + ".part";
            HistoryEntry.SavePath = SavePath;
            HistoryEntry.Status = status;
            HistoryEntry.Error = error;
            if (!string.IsNullOrWhiteSpace(sha256))
                HistoryEntry.Sha256 = sha256;
            HistoryEntry.BytesOnDisk = File.Exists(partialPath)
                ? new FileInfo(partialPath).Length
                : File.Exists(SavePath) ? new FileInfo(SavePath).Length : 0;
            return HistoryEntry;
        }

        public string SavePath { get; set; }     

        public Task DownloadTask { get; set; }

        public IProgress<double> Progress { get; set; }

        public double ProgressPercent { get; set; }

        public bool Finished { get; set; } = false;

        public ProgressBar ProgressBar { get; set; }

        public Label ProgressLabel { get; set; }

        public MegaDownload MegaDownload { get; set; }

        public int RemainedRetries { get; set; } = 4;

        public bool DownloadFailed { get; set; } = false;
        
    }
}
