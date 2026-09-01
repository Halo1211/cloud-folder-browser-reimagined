using CG.Web.MegaApiClient;

using CloudFolderBrowser.Sync;
using System.Security.Cryptography;
using System.Diagnostics;

namespace CloudFolderBrowser
{
    public class MegaFileDownload : FileDownload
    {
        public MegaApiClient MegaClient { get; }
        INode Node { get; }
        public MegaDownload ParentDownload { get; set; }
        public bool ForceOverwrite { get; }

        public string ShareId { get; set; }

        public MegaFileDownload(MegaApiClient megaClient, MegaDownload megaDownload, PublicNode fileNode, string savePath, bool forceOverwrite = false)
        {
            SavePath = savePath.Replace(@" /", @"/");
            Node = fileNode;
            MegaClient = megaClient;
            var progressHandler = new Progress<double>(value =>
            {
                ProgressPercent = value;
            });
            progressHandler.ProgressChanged += new EventHandler<double>(ProgreessChanged);
            Progress = progressHandler;
            ParentDownload = megaDownload;
            ForceOverwrite = forceOverwrite;
        }

        public MegaFileDownload(MegaApiClient megaClient, MegaDownload megaDownload, INode fileNode, string savePath, bool forceOverwrite = false)
        {
            SavePath = savePath.Replace(@" /", @"/");
            Node = fileNode;
            MegaClient = megaClient;
            var progressHandler = new Progress<double>(value =>
            {
                ProgressPercent = value;
            });
            progressHandler.ProgressChanged += new EventHandler<double>(ProgreessChanged);
            Progress = progressHandler;
            ParentDownload = megaDownload;            
            ForceOverwrite = forceOverwrite;
        }

        void ProgreessChanged(object? sender, double e)
        {
            ProgressBar.Value = Math.Clamp((int)e, ProgressBar.Minimum, ProgressBar.Maximum);
            ProgressLabel.Text = $"{(int)(e * Node.Size / 100000000)}/{(int)(Node.Size / 1000000)} MB [{Math.Round(e, 2)}%] {Node.Name}";
        }

        public async Task StartDownload()
        {
            ProgressBar.Tag = this;
            ProgressLabel.Text = "";
            ProgressLabel.Visible = true;
            await UpdateHistoryAsync(DownloadJobStatus.Downloading);
            var folderPath = Path.GetDirectoryName(SavePath);
            if (string.IsNullOrWhiteSpace(folderPath))
                throw new InvalidOperationException($"Invalid download path: {SavePath}");
            Directory.CreateDirectory(folderPath);
            FileInfo file = new FileInfo(SavePath);

            DialogResult overwriteFile = DialogResult.Yes;
            if (file.Exists)
            {
                var identicalSize = file.Length == Node.Size;

                if(!identicalSize)
                {
                    overwriteFile = DialogResult.Yes;
                }
                else if (!ForceOverwrite)
                    switch (ParentDownload.OverwriteMode)
                    {
                        case 0:
                            overwriteFile = DialogResult.No;
                            break;
                        case 1:
                            overwriteFile = DialogResult.Yes;
                            break;
                        case 2:
                            if (Node.ModificationDate > file.LastWriteTime)
                                overwriteFile = DialogResult.Yes;
                            else
                                overwriteFile = DialogResult.No;
                            break;
                        case 3:
                            overwriteFile = MessageBox.Show($"File [{file.Name}] already exists. Overwrite?", "", MessageBoxButtons.YesNo);
                            break;
                    }
            }
            if (overwriteFile == DialogResult.No)
            {
                await UpdateHistoryAsync(DownloadJobStatus.Skipped);
                await ParentDownload.UpdateQueue(this);
                return;
            }

            string partialPath = SavePath + ".part";
            if (File.Exists(partialPath))
                File.Delete(partialPath);

            int maxRetries = Math.Max(0, ParentDownload.MaxDownloadRetries);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                RemainedRetries = maxRetries - attempt;
                try
                {
                    if (Node is PublicNode)
                        DownloadTask = MegaClient.DownloadFileAsync(Node, partialPath, Progress, ParentDownload.CancellationTokenSource.Token);
                    else
                    {
                        if (ParentDownload.ShareId != "")
                            DownloadTask = MegaClient.DownloadFileAsync(new PublicNode(Node, ParentDownload.ShareId), partialPath, Progress, ParentDownload.CancellationTokenSource.Token);
                        else
                            DownloadTask = MegaClient.DownloadFileAsync(Node, partialPath, Progress, ParentDownload.CancellationTokenSource.Token);
                    }

                    await DownloadTask;

                    if (!File.Exists(partialPath))
                        throw new InvalidDataException("MEGA download completed without creating the destination file.");

                    if (ParentDownload.CheckDownloadedFileSize
                        && new FileInfo(partialPath).Length < Node.Size * ParentDownload.CheckFileSizeError)
                    {
                        throw new InvalidDataException(
                            $"Incomplete file: received {new FileInfo(partialPath).Length} of {Node.Size} bytes.");
                    }

                    File.Move(partialPath, SavePath, true);
                    Progress.Report(100);
                    string postProcessingMessage = string.Empty;
                    if (Properties.Settings.Default.archivePostProcessingEnabled)
                    {
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
                    if (File.Exists(partialPath))
                        File.Delete(partialPath);
                    ProgressBar.Value = 0;
                    ProgressLabel.Text = "";
                    await UpdateHistoryAsync(DownloadJobStatus.Paused);
                    return;
                }
                catch (Exception ex)
                {
                    TryWriteFailureLog(ex, attempt + 1, maxRetries + 1);
                    if (File.Exists(partialPath))
                        File.Delete(partialPath);

                    if (attempt == maxRetries)
                        break;

                    ProgressBar.Value = 0;
                    ProgressLabel.Text = $"Retry {attempt + 1}/{maxRetries}: {Node.Name}";
                    try
                    {
                        await Task.Delay(
                            Math.Max(100, ParentDownload.RetryDelay),
                            ParentDownload.CancellationTokenSource.Token);
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
            ProgressLabel.Text = $"Failed: {Node.Name}";
            await UpdateHistoryAsync(DownloadJobStatus.Failed, "Retry limit reached.");
            await ParentDownload.UpdateQueue(this);
        }

        public Task RetryDownload()
        {
            return StartDownload();
        }

        private void TryWriteFailureLog(Exception exception, int attempt, int totalAttempts)
        {
            try
            {
                var logFileName = $"00-DOWNLOAD-LOG-{DateTime.Now:MM-dd-yyyy}.txt";
                string log = $"\n{DateTime.Now:O}\nNode id: {Node.Id}\nSavePath: {SavePath}\nAttempt: {attempt}/{totalAttempts}\nException: {exception.GetType().Name}: {exception.Message}\n";
                File.AppendAllText(logFileName, log);
            }
            catch (Exception logException) when (logException is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine($"Unable to write download log: {logException.Message}");
            }
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
                System.Diagnostics.Debug.WriteLine($"Unable to record SHA-256 for {SavePath}: {ex.Message}");
                return string.Empty;
            }
            catch (OperationCanceledException) when (ParentDownload.CancellationTokenSource.IsCancellationRequested)
            {
                // The file has already been committed. A cancelled optional hash
                // must not downgrade a complete transfer to Paused.
                return string.Empty;
            }
        }
    }

    public class MegaFolderDownload
    {
        public MegaApiClient MegaClient { get; }
        public string SavePath { get; set; }
        public INode Node { get; set; }
        public Task<Stream> DownloadTask { get; set; }
        public IProgress<double> Progress { get; }
        public MegaFolderDownload(MegaApiClient megaClient, INode folderNode, string savePath)
        {
            SavePath = savePath;
            Node = folderNode;
            MegaClient = megaClient;
            Progress = new Progress<double>();
        }
        public Task<Stream> StartDownload()
        {
            DownloadTask = MegaClient.DownloadAsync(Node, Progress);
            return DownloadTask;
        }
    }
}
