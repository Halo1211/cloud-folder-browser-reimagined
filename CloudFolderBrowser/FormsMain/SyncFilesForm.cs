using System.Data;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using CG.Web.MegaApiClient;
using CloudFolderBrowser.FormsSecondary;
using CloudFolderBrowser.JDownloader;
using Newtonsoft.Json;
using YandexDiskSharp.Models;
using CloudFolderBrowser.Theming;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser
{
    public partial class SyncFilesForm : ThemedForm
    {
        List<CloudFile> checkedFiles;
        TreeModel newFiles_model, newFilesFlat_model;
        long checkedFilesSize = 0;
        CloudFolder rootFolder;
        CloudServiceType cloudServiceType;
        List<ProgressBar> progressBars;
        List<Label> progressLabels;
        bool HideForm = true;
        MegaApiClient megaApiClient;
        NetworkCredential? NetworkCredential;
        Download Download;
        bool downloadCompletionHandled;
        bool downloadInProgress;
        bool closeWhenDownloadStops;

        private sealed class DownloadRouteOption
        {
            public string RouteId { get; init; } = DownloadRouteIds.Direct;
            public string DisplayName { get; init; } = string.Empty;
            public CloudAccountProfile? Account { get; init; }
            public bool IsAutomatic => RouteId == DownloadRouteIds.Automatic;
            public override string ToString() => DisplayName;
        }

        MainForm MainForm;
        internal MainForm HostMainForm => MainForm;
        MainFormModel Model;
        readonly string? DownloadBasePathOverride;
        readonly CloudAccountStore AccountStore;
        string DownloadBasePath => string.IsNullOrWhiteSpace(DownloadBasePathOverride)
            ? MainForm.syncFolderPath
            : DownloadBasePathOverride;

        public event EventHandler DownloadCompleted;
        protected virtual void OnDownloadCompleted(EventArgs e)
        {
            EventHandler handler = DownloadCompleted;
            if (handler != null)
            {
                handler(this, e);
            }
        }

        public int OverwriteMode = 0;
        public int MaximumDownloads = 4;
        public int RetryDelay = 300;
        public int RetryMax = 4;
        public double CheckFileSizeError = 0.999;
        public bool FolderNewFiles = false;
        public bool CheckDownloadedFileSize = false;

        //Textbox filter
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        public void UpdateSettings()
        {
            OverwriteMode = Properties.Settings.Default.overwriteMode;
            MaximumDownloads = Math.Clamp(Properties.Settings.Default.maximumDownloads, 1, 4);
            RetryDelay = Math.Max(100, Properties.Settings.Default.retryDelay);
            RetryMax = Math.Max(0, Properties.Settings.Default.retryMax);
            CheckFileSizeError = Math.Clamp(Properties.Settings.Default.checkFileSizeError, 0.01, 1.0);
            FolderNewFiles = Properties.Settings.Default.folderNewFiles;
            CheckDownloadedFileSize = Properties.Settings.Default.checkDownloadedFileSize;
        }

        public SyncFilesForm(
            MainForm parentForm,
            CloudFolder newFilesFolder,
            MainFormModel model,
            string? downloadBasePathOverride = null,
            CloudAccountStore? cloudAccountStore = null)
        {
            InitializeComponent();

            downloadFiles_button.Tag = "primary";
            stopDownload_button.Tag = "danger";
            ConfigureModernUi();

            if (Properties.Settings.Default.maximumDownloads == 0)
            {
                //default settings
                Properties.Settings.Default.overwriteMode = OverwriteMode;
                Properties.Settings.Default.maximumDownloads = MaximumDownloads;
                Properties.Settings.Default.retryDelay = RetryDelay;
                Properties.Settings.Default.retryMax = RetryMax;
                Properties.Settings.Default.checkFileSizeError = CheckFileSizeError;
                Properties.Settings.Default.folderNewFiles = FolderNewFiles;
                Properties.Settings.Default.checkDownloadedFileSize = CheckDownloadedFileSize;
            }
            UpdateSettings();

            MainForm = parentForm;
            Model = model;
            DownloadBasePathOverride = downloadBasePathOverride;
            AccountStore = cloudAccountStore ?? CloudAccountStore.Default;
            NetworkCredential = Model.WebdavCredential;
            PopulateDownloadRoutes();
            SendMessage(filter_textBox.Handle, 0x1501, 1, "Search missing files by name");
            filter_textBox.TextChangedComplete += filter_TextChangedComplete;

            cloudServiceType = Model.CloudServiceType;
            importMega_button.Text = "Manage cloud accounts";
            importMega_button.Enabled = true;
            if (cloudServiceType == CloudServiceType.Mega)
            {
                if (!Model.LoadedFromFile)
                {
                    if (MainForm.usingFogLink)
                        downloadFiles_button.Enabled = false;

                    if (Properties.Settings.Default.loginedMega)
                    {
                        importMega_button.Text = "Import to MEGA";
                        importMega_button.Enabled = true;
                    }
                    else
                    {
                        importMega_button.Text = "Connect MEGA account";
                    }
                }
                else
                {
                    downloadFiles_button.Enabled = false;
                    MessageBox.Show("Files were loaded from file and will not be available for download. Load from link instead.");
                }

                if (MainForm.usingFogLink && !Properties.Settings.Default.loginedMega)
                    MessageBox.Show("Not signed in MEGA: unable to import files.");

                getJdLinks_button.Enabled = true;
            }
            if (cloudServiceType == CloudServiceType.Yadisk)
            {
                if (Properties.Settings.Default.loginedYandex)
                {
                    importMega_button.Text = "Import to Yandex Disk";
                    importMega_button.Enabled = true;
                }
                else
                {
                    importMega_button.Text = "Connect Yandex account";
                }
                getJdLinks_button.Enabled = true;
            }

            progressBars = new List<ProgressBar> { progressBar1, progressBar2, progressBar3, progressBar4 };
            progressLabels = new List<Label> { label1, label2, label3, label4, DownloadProgress_label };

            rootFolder = newFilesFolder;
            if (transferSummaryLabel != null)
            {
                long totalBytes = Math.Max(0, newFilesFolder.Files.Sum(file => file.Size));
                transferSummaryLabel.Text =
                    $"{newFilesFolder.Files.Count:N0} file(s) • {FormatBytes(totalBytes)}\nReview selections before starting.";
            }
            nodeCheckBox2.CheckStateChanged += new EventHandler<TreePathEventArgs>(NodeCheckStateChanged);
            newFilesTreeViewAdv.ShowNodeToolTips = true;
            newFilesTreeViewAdv.NodeControls[2].ToolTipProvider = new ToolTipProvider();
            newFilesTreeViewAdv.NodeFilter = filter;

            newFiles_model = new TreeModel();
            newFilesFlat_model = new TreeModel();

            ColumnNode rootFlatNode = new ColumnNode(newFilesFolder.Name, newFilesFolder.Created, newFilesFolder.Modified, newFilesFolder.Size);
            newFilesFlat_model.Nodes.Add(rootFlatNode);
            ColumnNode rootNode = new ColumnNode(newFilesFolder.Name, newFilesFolder.Created, newFilesFolder.Modified, newFilesFolder.Size);
            rootNode.Tag = rootFlatNode.Tag = Model.CloudPublicFolder;

            newFilesTreeViewAdv.Model = new SortedTreeModel(newFiles_model);
            newFilesTreeViewAdv.BeginUpdate();
            newFiles_model.Nodes.Add(rootNode);
            List<ColumnNode> folderNodes = new List<ColumnNode>();
            folderNodes.Add(rootNode);
            foreach (CloudFile file in newFilesFolder.Files)
            {
                ColumnNode ffileNode = new ColumnNode(file.Name, file.Created, file.Modified, file.Size);
                ffileNode.Tag = file;
                rootFlatNode.Nodes.Add(ffileNode);

                string[] folders = Utility.ParsePath(file.Path);
                if (folders.Length == 0) //file is in root folder
                {
                    ColumnNode subNode = new ColumnNode(file.Name, file.Created, file.Modified, file.Size);
                    subNode.Tag = file;
                    rootNode.Nodes.Add(subNode);
                    rootNode.Size += subNode.Size;
                }
                else
                {
                    ColumnNode currentNode = rootNode;
                    //creating folder nodes and file nodes from file path
                    string currentFolderPath = @"/";
                    for (int i = 0; i < folders.Length; i++)
                    {
                        if (folders[i] == "")
                        {
                        }
                        currentFolderPath += folders[i] + @"/";
                        ColumnNode subNode = new ColumnNode(folders[i], file.Created, file.Modified, 0);
                        var folderNode = folderNodes.Find(x => x.Path == currentFolderPath);
                        if (folderNode != null)
                            currentNode = folderNode;
                        else
                        {
                            subNode.Path = currentFolderPath;
                            var cloudFolder = Model.AllFolders.Where(x => x.Path == currentFolderPath).FirstOrDefault();
                            subNode.Tag = cloudFolder;
                            currentNode.Nodes.Add(subNode);
                            folderNodes.Add(subNode);
                            currentNode = subNode;
                        }
                        if (i == folders.Length - 1) //it's file
                        {
                            ColumnNode fileNode = new ColumnNode(file.Name, file.Created, file.Modified, file.Size);
                            fileNode.Tag = file;
                            currentNode.Nodes.Add(fileNode);
                            ffileNode.LinkedNode = fileNode;
                            fileNode.LinkedNode = ffileNode;
                        }
                        currentNode.Size += file.Size;
                    }
                }
            }
            newFilesTreeViewAdv.EndUpdate();
            if (newFilesTreeViewAdv.Root.Children.Count > 0)
                newFilesTreeViewAdv.Root.Children[0].Expand();
            if (newFiles_model.Nodes.Count > 0 && newFiles_model.Nodes[0] is ColumnNode modelRootNode)
            {
                modelRootNode.IsChecked = true;
                CheckAllSubnodes(modelRootNode, false);
            }
            newFilesTreeViewAdv.Columns[0].MinColumnWidth = 100;

            checkAllToolStripMenuItem.Click += CheckAllToolStripMenuItem_Click;
            checkNoneToolStripMenuItem.Click += CheckNoneToolStripMenuItem_Click;
            expandAllToolStripMenuItem.Click += ExpandAllToolStripMenuItem_Click;
            collapseAllToolStripMenuItem.Click += CollapseAllToolStripMenuItem_Click;
            Show();
            NormalizeSyncLayout();
        }

        private static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return $"{value:0.##} {units[unit]}";
        }

        public void CloseForm()
        {
            if (downloadInProgress && Download != null)
            {
                closeWhenDownloadStops = true;
                Hide();
                Download.Stop();
                return;
            }

            HideForm = false;
            Close();
        }

        List<CloudFolder> checkedFolders = new List<CloudFolder>();

        void GetCheckedFiles(Node node)
        {
            if (node.Tag is CloudFolder currentFolder)
                checkedFolders.Add(currentFolder);

            if (node.CheckState == CheckState.Checked)
            {
                AddAllFiles(node);
                return;
            }

            foreach (Node subnode in node.Nodes)
            {
                if (subnode.Tag is CloudFolder folder)
                {
                    if (subnode.CheckState == CheckState.Indeterminate)
                    {
                        GetCheckedFiles(subnode);
                    }
                    if (subnode.CheckState == CheckState.Checked)
                    {
                        checkedFolders.Add(folder);
                        AddAllFiles(subnode);
                    }
                    continue;
                }
                if (subnode.CheckState == CheckState.Checked)
                {
                    checkedFiles.Add((CloudFile)(subnode.Tag));
                    checkedFilesSize += ((CloudFile)(subnode.Tag)).Size;
                }
            }
        }

        void AddAllFiles(Node node)
        {
            foreach (Node subnode in node.Nodes)
            {
                if (subnode.Tag == null || subnode.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
                {
                    if (subnode.Tag is CloudFolder folder)
                        checkedFolders.Add(folder);
                    AddAllFiles(subnode);
                }
                else
                {
                    checkedFiles.Add((CloudFile)(subnode.Tag));
                    checkedFilesSize += ((CloudFile)(subnode.Tag)).Size;
                }
            }
        }

        void CreateJdLinkcontainer()
        {
            var appPath = Directory.GetCurrentDirectory();

            DirectoryInfo di = new DirectoryInfo($"{appPath}\\Links");

            if (checkedFiles.Count == 0)
            {
                MessageBox.Show("No files checked!");
                return;
            }
            if (!di.Exists)
                di.Create();
            else
            {
                foreach (FileInfo file in di.EnumerateFiles())
                    file.Delete();
                foreach (DirectoryInfo dir in di.EnumerateDirectories())
                    dir.Delete(true);
            }

            List<JDPackage> packages = new List<JDPackage>();

            if (cloudServiceType == CloudServiceType.Mega && megaApiClient == null)
            {
                megaApiClient = NetworkClientAdapters.CreateMegaClient();
                megaApiClient.LoginAnonymous();
            }

            foreach (CloudFile file in checkedFiles)
            {
                //string folderPath = file.Path.Replace(file.Name, "");
                string folderPath = Path.GetDirectoryName(file.Path) ?? string.Empty;
                JDPackage pak;
                if (!packages.ConvertAll(x => x.name).Contains(folderPath))
                {
                    pak = new JDPackage(folderPath, folderPath);
                    pak.numberId = packages.Count.ToString("D3");
                    pak.downloadFolder = folderPath;
                    packages.Add(pak);
                    File.WriteAllText($"{di.FullName}\\{pak.numberId}", JsonConvert.SerializeObject(pak));
                }
                else
                {
                    pak = packages.Find(x => x.name == folderPath)
                        ?? throw new InvalidDataException($"Unable to create a package for {file.Path}.");
                }
                JDLink link;
                switch (cloudServiceType)
                {
                    case CloudServiceType.Mega:
                        //string downloadLink = ""; //megaApiClient.GetDownloadLink(file.MegaNode).ToString();
                        link = new JDLink(file.Name, file.PublicUrl?.OriginalString
                            ?? throw new InvalidDataException($"MEGA link is missing for {file.Name}."));
                        break;
                    case CloudServiceType.Yadisk:
                        YandexDiskSharp.RestClient restClient = NetworkClientAdapters.CreateYandexClient();
                        string downloadLink = restClient.GetPublicResourceDownloadLink(rootFolder.PublicKey, file.Path).Href.ToString();
                        link = new JDLink(file.Name, downloadLink);
                        break;
                    default:
                        link = new JDLink(file.Name, System.Web.HttpUtility.UrlDecode(
                            file.PublicUrl?.AbsoluteUri
                            ?? throw new InvalidDataException($"Download link is missing for {file.Name}.")));
                        //link = new JDLink(file.Name, file.PublicUrl.AbsoluteUri.Replace("#", "%23").Replace(",", "%2C").Replace("?", "%3F"));                        
                        break;
                }
                link.downloadLink.size = file.Size;
                File.WriteAllText($"{di.FullName}\\{pak.numberId}_{pak.linksCount.ToString("D3")}", JsonConvert.SerializeObject(link));
                pak.linksCount++;
            }

            DialogResult dialogResult = MessageBox.Show("Got links for " + (checkedFiles.Count) + " files! Continue?", "Result", MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.No)
                return;

            Random rnd = new Random();
            int number = rnd.Next(1, 9999);

            string rootFolderName = rootFolder.Name;
            foreach (char c in Path.GetInvalidFileNameChars())
                rootFolderName = rootFolderName.Replace(c.ToString(), "");

            string dirPath = $"{appPath}\\linkcontainers\\{rootFolderName}";

            Directory.CreateDirectory(dirPath);
            System.IO.Compression.ZipFile.CreateFromDirectory($"{appPath}\\Links", dirPath + @"\linkcollector" + number + ".zip");

            DownloadsFinishedForm downloadsFinishedForm = new DownloadsFinishedForm(dirPath, @"linkcollector" + number + ".zip created!");
            downloadsFinishedForm.Show();
        }

        async Task AddCheckedFilesToYadiskAsync()
        {
            if (checkedFilesSize > MainForm.freeSpace)
            {
                MessageBox.Show("Not enougt free space on Yandex disk");
                return;
            }
            if (checkedFiles.Count > 0)
            {
                DialogResult dialogResult = MessageBox.Show(checkedFiles.Count + $" new files found. Size: {Math.Round(checkedFilesSize / 1000000.0, 2)} MB. Download?", "", MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    if (MainForm.rc == null
                        || MainForm.yadiskFolder == null)
                    {
                        MessageBox.Show("Yandex Disk is not ready. Connect the account and try again.");
                        return;
                    }

                    foreach (CloudFile file in checkedFiles)
                    {
                        string savePath = Model.CloudPublicFolder.Name + file.Path;
                        string[] folders = Utility.ParsePath(savePath);
                        CloudFolder currentFolder = MainForm.yadiskFolder;
                        savePath = currentFolder.Path;
                        for (int i = 0; i < folders.Length; i++)
                        {
                            if (!currentFolder.Subfolders.ConvertAll(x => x.Name).Contains(folders[i]))
                            {
                                string createdPath = CombineYandexPath(currentFolder.Path, folders[i]);
                                await MainForm.rc.CreateResourceAsync(createdPath);
                                CloudFolder createdFolder = new CloudFolder(folders[i], DateTime.Now, DateTime.Now, 0);
                                createdFolder.Path = createdPath;
                                currentFolder.Subfolders.Add(createdFolder);
                            }
                            IFolder? nextFolder = currentFolder.Subfolders.Find(x => x.Name == folders[i]);
                            if (nextFolder is not CloudFolder cloudFolder)
                                throw new InvalidDataException($"Unable to create Yandex Disk folder {folders[i]}.");
                            currentFolder = cloudFolder;
                            savePath = CombineYandexPath(savePath, folders[i]);
                        }
                        //Uri link = (rc.GetPublicResourceDownloadLink(cloudPublicFolder.PublicKey, file.Path)).Href;  

                        //TODO: add whole folders if all files inside are checked
                        _ = await MainForm.rc.SaveToDiskPublicResourceAsync(
                            Model.CloudPublicFolder.PublicKey,
                            file.Name,
                            file.Path,
                            savePath);
                    }
                    MessageBox.Show("Finished");
                }
            }
            else
                MessageBox.Show("No files checked!");
        }

        void ImportCheckedToMega()
        {
            if (checkedFilesSize > MainForm.freeSpace)
            {
                MessageBox.Show("MEGA: Not enough free space");
                return;
            }
            if (checkedFiles.Count > 0)
            {
                DialogResult dialogResult = MessageBox.Show(checkedFiles.Count + $" new files found. Size: {Math.Round(checkedFilesSize / 1000000.0, 2)} MB. Download?", "", MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    if (MainForm.MegaRootNode == null)
                    {
                        MessageBox.Show("Sign in to MEGA before importing files.");
                        return;
                    }

                    var nodes = new List<INode>() { };
                    foreach (CloudFolder folder in checkedFolders)
                    {
                        if (folder.MegaNode != null)
                            nodes.Add(folder.MegaNode);
                    }
                    foreach (CloudFile file in checkedFiles)
                    {
                        if (file.MegaNode != null)
                            nodes.Add(file.MegaNode);
                    }
                    MainForm.megaClient.ImportNodes(nodes.ToArray(), MainForm.MegaRootNode);
                    MessageBox.Show("Finished");
                }
            }
            else
                MessageBox.Show("No files checked!");
        }

        async Task ImportCheckedEncryptedToMega()
        {
            if (checkedFilesSize > MainForm.freeSpace)
            {
                MessageBox.Show("MEGA: Not enough free space");
                return;
            }
            if (checkedFiles.Count > 0)
            {
                DialogResult dialogResult = MessageBox.Show(checkedFiles.Count + $" new files found. Size: {Math.Round(checkedFilesSize / 1000000.0, 2)} MB. Download?", "", MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    var nodes = new List<string>() { };
                    foreach (CloudFolder folder in checkedFolders)
                    {
                        if (!string.IsNullOrEmpty(folder.EncryptedUrl))
                            nodes.Add(folder.EncryptedUrl);
                    }
                    foreach (CloudFile file in checkedFiles)
                    {
                        nodes.Add(file.EncryptedUrl);
                    }
                    using HttpClient client = AppHttpClientFactory.CreateClient(
                        TimeSpan.FromSeconds(60), routeKey: "FogLink");
                    //MegaApiClient tempClient = new MegaApiClient();
                    //var token = tempClient.Login(Properties.Settings.Default.megaLogin, Properties.Settings.Default.megaPassword);
                    //tempClient.Logout();
                    var postData = new ImportLinksData() { login = Properties.Settings.Default.megaLogin, password = Properties.Settings.Default.megaPassword, links = nodes.ToArray() };
                    var postJson = JsonConvert.SerializeObject(postData);
                    var content = new StringContent(postJson, Encoding.UTF8, "application/json");
                    client.BaseAddress = FogLink.ServerAddress;
                    using HttpResponseMessage response = await client.PostAsync(
                        $"MegaPrivater/import", content);
                    var megaCode = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        int code;
                        var codeOk = int.TryParse(megaCode, out code);
                        var error = codeOk ? ((ApiResultCode)code).ToString() : response.StatusCode.ToString();
                        MessageBox.Show($"Failed to import files: {(error)}");
                    }
                    else
                        MessageBox.Show("Finished");
                }
            }
            else
                MessageBox.Show("No files checked!");
        }

        public class ImportLinksData
        {
            public string logonToken;
            public string[] links;
            public string login;
            public string password;
        }

        private async Task<bool> DownloadFiles(DownloadRouteOption route)
        {
            if (string.IsNullOrWhiteSpace(DownloadBasePath)
                || !Directory.Exists(DownloadBasePath))
            {
                MessageBox.Show("Select a valid local sync folder before downloading.");
                return false;
            }

            checkedFiles = new List<CloudFile>();
            checkedFilesSize = 0;
            ColumnNode? rootNode = GetDisplayedRootNode();
            if (rootNode == null)
            {
                MessageBox.Show("The file list is not ready yet.");
                return false;
            }
            GetCheckedFiles(rootNode);

            if (checkedFiles.Count == 0)
            {
                MessageBox.Show("No files checked!");
                return false;
            }

            IReadOnlyList<CloudAccountProfile> activeDebridAccounts = AccountStore.GetAll()
                .Where(account => account.IsActive
                    && !string.IsNullOrWhiteSpace(account.Secret)
                    && CloudProviderRegistry.Default.SupportsLinkResolver(account.Provider))
                .ToArray();
            if (route.IsAutomatic
                && checkedFiles.Any(file => file.RequiresLinkResolver)
                && activeDebridAccounts.Count == 0)
            {
                MessageBox.Show(
                    "This share requires a link resolver, but no active debrid account is configured. Add an account or choose another route.",
                    "Automatic route needs an account",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }

            IDownloadLinkResolver? linkResolver = route.IsAutomatic
                ? new AutomaticDownloadLinkResolver(
                    activeDebridAccounts,
                    GetPreferredDebridAccountId())
                : route.Account != null
                    ? CloudProviderRegistry.Default.CreateLinkResolver(route.Account)
                    : null;

            if (linkResolver == null && checkedFiles.Any(file => file.RequiresLinkResolver))
            {
                MessageBox.Show(
                    "This share exposes a web page rather than a direct file. Select an active debrid provider in Download route, then try again.",
                    "Debrid route required",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return false;
            }

            DialogResult dialogResult =
                MessageBox.Show($"Got links for {checkedFiles.Count} files [{(int)(checkedFilesSize / 1000000)} MB]  Continue?", "Result",
                MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.No)
                return false;

            ProgressBar[] usedProgressBars = new ProgressBar[MaximumDownloads];
            Label[] usedLabels = new Label[MaximumDownloads + 1];
            for (int i = 0; i < MaximumDownloads; i++)
            {
                usedProgressBars[i] = progressBars[i];
                usedLabels[i] = progressLabels[i];
            }
            usedLabels[MaximumDownloads] = progressLabels[progressLabels.Count - 1];

            Download = new CommonDownload(checkedFiles, usedProgressBars, usedLabels, toolTip1, cloudServiceType,
                DownloadBasePath, OverwriteMode, NetworkCredential, FolderNewFiles,
                Model.FlareSolverrSession, linkResolver, requestedRouteId: route.RouteId);
            Download.MaxDownloadRetries = RetryMax;
            Download.RetryDelay = RetryDelay;

            Download.CheckFileSizeError = CheckFileSizeError;
            Download.CheckDownloadedFileSize = CheckDownloadedFileSize;

            Download.DownloadCompleted += Download_DownloadCompleted;
            downloadCompletionHandled = false;
            await Download.Start();

            if (!downloadCompletionHandled)
            {
                stopDownload_button.Enabled = true;
                stopDownload_button.Visible = true;
            }
            return true;
        }

        private static string CombineYandexPath(string parent, string child)
        {
            string normalizedParent = string.IsNullOrWhiteSpace(parent) ? "disk:/" : parent.TrimEnd('/');
            if (normalizedParent.Equals("disk:", StringComparison.OrdinalIgnoreCase))
                normalizedParent = "disk:";
            return normalizedParent + "/" + child.Trim('/');
        }

        private async Task<bool> DownloadMega(string requestedRouteId)
        {
            if (string.IsNullOrWhiteSpace(DownloadBasePath)
                || !Directory.Exists(DownloadBasePath))
            {
                MessageBox.Show("Select a valid local sync folder before downloading.");
                return false;
            }

            checkedFiles = new List<CloudFile>();
            checkedFilesSize = 0;
            ColumnNode? rootNode = GetDisplayedRootNode();
            if (rootNode == null)
            {
                MessageBox.Show("The file list is not ready yet.");
                return false;
            }
            GetCheckedFiles(rootNode);

            if (checkedFiles.Count == 0)
            {
                MessageBox.Show("No files checked!");
                return false;
            }

            DialogResult dialogResult = MessageBox.Show($"Got links for {checkedFiles.Count} files [{(int)(checkedFilesSize / 1000000)} MB]  Continue?", "Result", MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.No)
                return false;

            ProgressBar[] usedProgressBars = new ProgressBar[MaximumDownloads];
            Label[] usedLabels = new Label[MaximumDownloads + 1];
            for (int i = 0; i < MaximumDownloads; i++)
            {
                usedProgressBars[i] = progressBars[i];
                usedLabels[i] = progressLabels[i];
            }
            usedLabels[MaximumDownloads] = progressLabels[progressLabels.Count - 1];

            if (megaApiClient == null)
            {
                megaApiClient = NetworkClientAdapters.CreateMegaClient();
                if (Properties.Settings.Default.loginedMega && Properties.Settings.Default.loginTokenMega != "")
                {
                    var megaLoginToken = JsonConvert.DeserializeObject<MegaApiClient.LogonSessionToken>(
                        Properties.Settings.Default.loginTokenMega, new JsonSerializerSettings()
                        {
                            TypeNameHandling = TypeNameHandling.Auto
                        });
                    try
                    {
                        await megaApiClient.LoginAsync(megaLoginToken);
                    }
                    catch
                    {
                        await megaApiClient.LoginAnonymousAsync();
                    }

                }
                else
                {                    
                    await megaApiClient.LoginAnonymousAsync();
                }                
            }

            Download = new MegaDownload(megaApiClient, checkedFiles, usedProgressBars, usedLabels, toolTip1,
                DownloadBasePath, OverwriteMode, FolderNewFiles, Model.CloudPublicFolder.PublicKey,
                requestedRouteId: requestedRouteId);
            Download.MaxDownloadRetries = RetryMax;
            Download.RetryDelay = RetryDelay;

            Download.CheckFileSizeError = CheckFileSizeError;
            Download.CheckDownloadedFileSize = CheckDownloadedFileSize;

            Download.DownloadCompleted += Download_DownloadCompleted;
            downloadCompletionHandled = false;
            await Download.Start();

            if (!downloadCompletionHandled)
            {
                stopDownload_button.Enabled = true;
                stopDownload_button.Visible = true;
            }
            return true;
        }

        #region #TREEVIEW

        private void treeViewAdv_Expanded(object sender, TreeViewAdvEventArgs e)
        {
            if (!e.Node.CanExpand)
                return;
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[0]);
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[3]);
            //e.Node.Tree.Columns[0].Width += (int)Math.Round(e.Node.Tree.Columns[0].Width * 0.3, 0);
        }

        private void treeViewAdv_Collapsed(object sender, TreeViewAdvEventArgs e)
        {
            if (!e.Node.CanExpand)
                return;
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[0], false);
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[3], false);
            //e.Node.Tree.Columns[0].Width += (int)Math.Round(e.Node.Tree.Columns[0].Width * 0.2, 0);
        }

        void CheckIndex(object? sender, NodeControlValueEventArgs e)
        {
            var currentNode = (ColumnNode)(e.Node.Tag);
            var parentNode = (currentNode.Parent);
            bool parentChecked = false;
            bool parentCheckBoxEnabled = true;
            if (parentNode.Index != -1)
            {
                parentChecked = ((ColumnNode)parentNode).CheckState == CheckState.Checked;
                parentCheckBoxEnabled = ((ColumnNode)parentNode).CheckBoxEnabled;
            }

            if (parentCheckBoxEnabled && !parentChecked)
                currentNode.CheckBoxEnabled = true;
            else
                currentNode.CheckBoxEnabled = false;
            //if (currentNode.CheckBoxEnabled)
            //    currentNode.CheckState = CheckState.Checked;
            //else
            //    currentNode.CheckState = CheckState.Unchecked;
            e.Value = currentNode.CheckBoxEnabled;
        }

        void CheckAllSubnodes(ColumnNode node, bool uncheck)
        {
            CheckState cst = uncheck ? CheckState.Unchecked : CheckState.Checked;

            foreach (ColumnNode subnode in node.Nodes)
            {
                CheckState origState = subnode.CheckState;
                if (subnode.Tag == null || subnode.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
                {
                    if (cst == CheckState.Unchecked)
                    {

                    }
                    //    checkedFilesSize -= ((CloudFolder)subnode.Tag).Size;
                    if (cst == CheckState.Checked)
                    {
                        if (origState == CheckState.Indeterminate)
                        {

                        }
                        if (origState == CheckState.Unchecked)
                        {
                            //checkedFilesSize += ((CloudFolder)subnode.Tag).SizeTopDirectoryOnly;
                            //long folderFilesSize = ((CloudFolder)subnode.Tag).Files.Sum(x => x.Size);
                            //checkedFilesSize += Math.Round(folderFilesSize * b2Mb, 2);                            
                        }
                    }
                    //    checkedFilesSize += ((CloudFolder)subnode.Tag).Size;
                    subnode.CheckState = cst;
                    CheckAllSubnodes(subnode, uncheck);
                }
                else
                    subnode.CheckState = cst;

            }
        }

        void NodeCheckStateChanged(object? sender, TreePathEventArgs e)
        {
            ColumnNode checkedNode = (ColumnNode)e.Path.LastNode;

            if (checkedNode.CheckState == CheckState.Checked)
            {
                //if (checkedNode.Tag?.GetType().ToString() != "CloudFolderBrowser.CloudFile") checkedFolders.Add(MainForm.);
                //if (checkedNode.Parent.CheckState != CheckState.Checked && checkedNode.Parent.Index != -1)
                //    UpdateParentCheckState((ColumnNode)checkedNode.Parent);
                CheckAllSubnodes(checkedNode, false);
                //checkedFilesSize += ((CloudFolder)checkedNode.Tag).Size - ((CloudFolder)checkedNode.Tag).SizeTopDirectoryOnly;
                //label1.Text = $"{Math.Round(checkedFilesSize * b2Mb, 2)} MB checked";
            }
            else if (checkedNode.CheckState == CheckState.Unchecked)
            {
                //checkedFilesSize -= ((CloudFolder)checkedNode.Tag).Size;
                //if (checkedFilesSize < 0.0001)
                //    checkedFilesSize = 0;
                //label1.Text = $"{Math.Round(checkedFilesSize * b2Mb, 2)} MB checked";

                //if (checkedNode.Parent.Index != -1)
                //    UpdateParentCheckState((ColumnNode)checkedNode.Parent);
                CheckAllSubnodes(checkedNode, true);

            }
            else if (checkedNode.CheckState == CheckState.Indeterminate)
            {
                if (checkedNode.Tag?.GetType().ToString() == "CloudFolderBrowser.CloudFile")
                {
                    checkedNode.CheckState = CheckState.Unchecked;
                }
                //checkedFilesSize += ((CloudFolder)checkedNode.Tag).SizeTopDirectoryOnly;//((CloudFolder)parentNode.Tag).Files.Sum(x => x.Size) * b2Mb;
                //checkedFilesSize = (long)Math.Round((double)checkedFilesSize, 2);
                //label1.Text = $"{Math.Round(checkedFilesSize * b2Mb, 2)} MB checked";
                //if (parentNode.Parent.Index != -1)
                //    UpdateParentCheckState((ColumnNode)parentNode.Parent);
                //CheckAllSubnodes(parentNode, true);

            }
            if (checkedNode.Parent.Index != -1)
                UpdateParentCheckState((ColumnNode)checkedNode.Parent);

        }

        static void UpdateParentCheckState(ColumnNode parentNode)
        {
            CheckState origState = parentNode.CheckState;
            int UnCheckedNodes = 0, CheckedNodes = 0, MixedNodes = 0;

            foreach (ColumnNode tnChild in parentNode.Nodes)
            {
                if (tnChild.CheckState == CheckState.Checked)
                    CheckedNodes++;
                else if (tnChild.CheckState == CheckState.Indeterminate)
                {
                    MixedNodes++;
                    break;
                }
                else
                    UnCheckedNodes++;
            }

            if (MixedNodes > 0)
            {
                // at least one child is mixed, so parent must be mixed
                parentNode.CheckState = CheckState.Indeterminate;
            }
            else if (CheckedNodes > 0 && UnCheckedNodes == 0)
            {
                // all children are checked
                //if (parentNode.CheckState == CheckState.Indeterminate)
                parentNode.CheckState = CheckState.Checked;
                //parentNode.CheckState = CheckState.Indeterminate;
                //else
                //   parentNode.CheckState = CheckState.Indeterminate;
            }
            else if (CheckedNodes > 0)
            {
                // some children are checked, the rest are unchecked                   
                parentNode.CheckState = CheckState.Indeterminate;
            }
            if (CheckedNodes == 0 && MixedNodes == 0)
            {
                //if (parentNode.CheckState != CheckState.Unchecked)
                //    parentNode.CheckState = CheckState.Indeterminate;
                // all children are unchecked
                //if (parentNode.CheckState == CheckState.Checked)
                //    parentNode.CheckState = CheckState.Indeterminate;
                //else
                parentNode.CheckState = CheckState.Unchecked;
            }

            if (parentNode.CheckState != origState && parentNode.Parent.Index != -1)
                UpdateParentCheckState((ColumnNode)parentNode.Parent);

        }

        void TransferNodeCheckState(ColumnNode node)
        {
            foreach (ColumnNode subnode in node.Nodes)
            {
                if (flatList2_checkBox.Checked) //hierarchy -> flat
                {
                    if (subnode.Tag == null || subnode.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
                    {
                        TransferNodeCheckState(subnode);
                        continue;
                    }
                    else
                        subnode.LinkedNode.IsChecked = subnode.IsChecked;
                }
                else
                {//flat -> hierarchy                
                    subnode.LinkedNode.IsChecked = subnode.IsChecked;
                    if (subnode.LinkedNode.Parent.Index != -1)
                        UpdateParentCheckState((ColumnNode)subnode.LinkedNode.Parent);
                }

            }
        }

        Node? FindNodeByPath(Node root, string path)
        {
            string[] parsedPath = Utility.ParsePath(path, true);
            Node currentNode = root;
            foreach (string segment in parsedPath)
            {
                Node? nextNode = currentNode.Nodes
                    .Cast<Node>()
                    .FirstOrDefault(node => node.Text == segment);
                if (nextNode == null)
                    return null;

                currentNode = nextNode;
            }
            return currentNode;

        }

        private void treeViewAdv_ColumnClicked(object sender, TreeColumnEventArgs e)
        {
            TreeColumn clicked = e.Column;
            if (clicked.SortOrder == SortOrder.Ascending)
                clicked.SortOrder = SortOrder.Descending;
            else
                clicked.SortOrder = SortOrder.Ascending;

            if (sender is TreeViewAdv treeView && treeView.Model is SortedTreeModel sortedModel)
                sortedModel.Comparer = new FolderItemSorter(clicked.Header, clicked.SortOrder);
        }

        private void CollapseAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (flatList2_checkBox.Checked)
                return;
            newFilesTreeViewAdv.Model = new SortedTreeModel(newFiles_model);
            if (newFilesTreeViewAdv.Root.Children.Count > 0)
                newFilesTreeViewAdv.Root.Children[0].Expand();
            newFilesTreeViewAdv.AutoSizeColumn(newFilesTreeViewAdv.Columns[0]);
        }

        private void ExpandAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            newFilesTreeViewAdv.ExpandAll();
            newFilesTreeViewAdv.AutoSizeColumn(newFilesTreeViewAdv.Columns[0]);
        }

        private void CheckNoneToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            ColumnNode? rootNode = GetDisplayedRootNode();
            if (rootNode == null)
                return;

            rootNode.IsChecked = false;
            CheckAllSubnodes(rootNode, true);
            newFilesTreeViewAdv.Refresh();
        }

        private void CheckAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            ColumnNode? rootNode = GetDisplayedRootNode();
            if (rootNode == null)
                return;

            rootNode.IsChecked = true;
            CheckAllSubnodes(rootNode, false);
            newFilesTreeViewAdv.Refresh();
        }

        private bool filter(object obj)
        {
            TreeNodeAdv? viewNode = obj as TreeNodeAdv;
            Node? node = viewNode != null ? viewNode.Tag as Node : obj as Node;
            if (node == null)
                return false;

            return node.Text?.Contains(
                filter_textBox.Text,
                StringComparison.CurrentCultureIgnoreCase) == true
                || node.Nodes.Any(filter);
        }

        #endregion

        #region #BUTTONS

        private async void importMega_button_Click(object sender, EventArgs e)
        {
            importMega_button.Enabled = false;
            try
            {
                if ((cloudServiceType == CloudServiceType.Mega && !Properties.Settings.Default.loginedMega)
                    || (cloudServiceType == CloudServiceType.Yadisk && !Properties.Settings.Default.loginedYandex)
                    || cloudServiceType is not (CloudServiceType.Mega or CloudServiceType.Yadisk))
                {
                    using var accounts = new AccountManagerForm(MainForm, AccountStore);
                    accounts.ShowDialog(this);
                    PopulateDownloadRoutes();
                    if (cloudServiceType == CloudServiceType.Mega)
                        importMega_button.Text = Properties.Settings.Default.loginedMega
                            ? "Import to MEGA"
                            : "Connect MEGA account";
                    else if (cloudServiceType == CloudServiceType.Yadisk)
                        importMega_button.Text = Properties.Settings.Default.loginedYandex
                            ? "Import to Yandex Disk"
                            : "Connect Yandex account";
                    return;
                }

                checkedFiles = new List<CloudFile>();
                checkedFilesSize = 0;
                checkedFolders = new List<CloudFolder>();
                ColumnNode? rootNode = GetDisplayedRootNode();
                if (rootNode == null)
                {
                    MessageBox.Show("The file list is empty or is not ready yet.");
                    return;
                }
                GetCheckedFiles(rootNode);
                if (cloudServiceType == CloudServiceType.Yadisk)
                    await AddCheckedFilesToYadiskAsync();
                else if (MainForm.usingFogLink)
                    await ImportCheckedEncryptedToMega();
                else
                    ImportCheckedToMega();
            }
            catch (System.Exception ex)
            {
                string providerName = cloudServiceType == CloudServiceType.Yadisk ? "Yandex Disk" : "MEGA";
                MessageBox.Show(
                    $"Unable to import files to {providerName}: {ex.Message}",
                    $"{providerName} import failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                importMega_button.Enabled = true;
            }
        }

        private void getJdLinks_button_Click(object sender, EventArgs e)
        {
            checkedFiles = new List<CloudFile>();
            checkedFilesSize = 0;
            ColumnNode? rootNode = GetDisplayedRootNode();
            if (rootNode == null)
            {
                MessageBox.Show("The file list is not ready yet.");
                return;
            }
            GetCheckedFiles(rootNode);
            CreateJdLinkcontainer();
        }

        private async void downloadFiles_button_Click(object sender, EventArgs e)
        {
            if (downloadInProgress)
                return;

            downloadInProgress = true;
            downloadFiles_button.Enabled = false;
            try
            {
                DownloadRouteOption route = GetSelectedDownloadRoute();
                bool started;
                if (cloudServiceType == CloudServiceType.Mega && route.Account == null)
                    started = await DownloadMega(route.RouteId);
                else
                    started = await DownloadFiles(route);

                if (!started)
                {
                    downloadInProgress = false;
                    downloadFiles_button.Enabled = true;
                }
            }
            catch (System.Exception ex)
            {
                downloadInProgress = false;
                downloadFiles_button.Enabled = true;
                MessageBox.Show(
                    "Unable to start downloads: " + ex.Message,
                    "Download error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private void PopulateDownloadRoutes()
        {
            if (downloadRouteComboBox == null)
                return;
            var routes = new List<DownloadRouteOption>
            {
                new()
                {
                    RouteId = DownloadRouteIds.Automatic,
                    DisplayName = "Automatic — recommended"
                },
                new()
                {
                    RouteId = DownloadRouteIds.Direct,
                    DisplayName = "Direct (built-in downloader)"
                }
            };
            try
            {
                routes.AddRange(AccountStore.GetAll()
                    .Where(account => account.IsActive
                        && CloudProviderRegistry.Default.SupportsLinkResolver(account.Provider))
                    .OrderBy(account => account.ProviderName)
                    .Select(account => new DownloadRouteOption
                    {
                        RouteId = DownloadRouteIds.ForAccount(account.Id),
                        DisplayName = $"{account.ProviderName} — {account.DisplayName}",
                        Account = account
                    }));
            }
            catch (System.Exception ex)
            {
                Model?.WriteToLog($"\n{DateTime.Now:O}\nUnable to load download routes: {ex}\n", true);
            }
            downloadRouteComboBox.DataSource = routes;
            string preferredRouteId = string.IsNullOrWhiteSpace(Model?.PreferredDownloadRouteId)
                ? NormalizeSavedRouteId(Properties.Settings.Default.preferredDebridAccountId)
                : Model.PreferredDownloadRouteId;
            int preferredIndex = routes.FindIndex(route =>
                route.RouteId.Equals(preferredRouteId, StringComparison.OrdinalIgnoreCase));
            int selectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
            downloadRouteComboBox.SelectedIndex = selectedIndex;
        }

        private DownloadRouteOption GetSelectedDownloadRoute()
        {
            return downloadRouteComboBox?.SelectedItem as DownloadRouteOption
                ?? new DownloadRouteOption
                {
                    RouteId = DownloadRouteIds.Automatic,
                    DisplayName = "Automatic — recommended"
                };
        }

        private static string NormalizeSavedRouteId(string? routeId)
        {
            if (string.IsNullOrWhiteSpace(routeId))
                return DownloadRouteIds.Automatic;
            if (routeId.Equals(DownloadRouteIds.Automatic, StringComparison.OrdinalIgnoreCase)
                || routeId.Equals(DownloadRouteIds.Direct, StringComparison.OrdinalIgnoreCase)
                || routeId.StartsWith("debrid:", StringComparison.OrdinalIgnoreCase))
            {
                return routeId;
            }
            return Guid.TryParse(routeId, out Guid accountId)
                ? DownloadRouteIds.ForAccount(accountId)
                : DownloadRouteIds.Automatic;
        }

        private static Guid? GetPreferredDebridAccountId()
        {
            string value = Properties.Settings.Default.preferredDebridAccountId;
            if (DownloadRouteIds.TryGetAccountId(value, out Guid routeAccountId))
                return routeAccountId;
            return Guid.TryParse(value, out Guid legacyAccountId) ? legacyAccountId : null;
        }

        private ColumnNode? GetDisplayedRootNode()
        {
            if (newFilesTreeViewAdv.Model is not SortedTreeModel sortedModel
                || sortedModel.InnerModel is not TreeModel treeModel
                || treeModel.Nodes.Count == 0)
            {
                return null;
            }

            return treeModel.Nodes[0] as ColumnNode;
        }

        private void stopDownloads_Click(object sender, EventArgs e)
        {
            stopDownload_button.Enabled = false;
            Download?.Stop();
        }

        #endregion

        private void syncFilesForm2_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (HideForm)
            {
                this.Hide();
                e.Cancel = true;
            }
        }

        private void Download_DownloadCompleted(object? sender, EventArgs e)
        {
            if (downloadCompletionHandled)
                return;
            downloadCompletionHandled = true;
            downloadInProgress = false;
            Download completedDownload = sender as Download ?? Download;

            for (int i = 0; i < progressBars.Count; i++)
            {
                progressBars[i].Value = 0;
                progressLabels[i].Text = "";
            }
            if (progressLabels.Count > 0)
                progressLabels[progressLabels.Count - 1].Text = "";
            stopDownload_button.Enabled = false;
            stopDownload_button.Visible = false;
            downloadFiles_button.Enabled = true;

            string message2 = "";
            string message1 = completedDownload?.CancellationTokenSource.IsCancellationRequested == true
                ? "Downloads paused. Select Download again to resume."
                : "All downloads are finished!";
            if (completedDownload?.CancellationTokenSource.IsCancellationRequested == true)
                downloadFiles_button.Text = "Resume checked files";

            if (completedDownload != null && !closeWhenDownloadStops)
            {
                if (completedDownload.FailedDownloads.Count > 0)
                    message2 += $" Failed: {completedDownload.FailedDownloads.Count}";

                DownloadsFinishedForm downloadsFinishedForm = new DownloadsFinishedForm(completedDownload.DownloadFolderPath, message1, message2);
                downloadsFinishedForm.Show();
            }

            OnDownloadCompleted(EventArgs.Empty);

            if (closeWhenDownloadStops)
            {
                HideForm = false;
                Close();
            }
        }

        private void filter_TextChangedComplete(object? sender, EventArgs e)
        {
            newFilesTreeViewAdv.UpdateNodeFilter();
        }

        private void flatList2_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            if (flatList2_checkBox.Checked)
            {
                newFilesTreeViewAdv.Model = new SortedTreeModel(newFilesFlat_model);
                if (newFiles_model.Nodes.Count > 0 && newFiles_model.Nodes[0] is ColumnNode sourceRoot)
                    TransferNodeCheckState(sourceRoot);
                newFilesTreeViewAdv.ShowNodeToolTips = true;
                newFilesTreeViewAdv.ExpandAll();
                newFilesTreeViewAdv.AutoSizeColumn(newFilesTreeViewAdv.Columns[0]);
                newFilesTreeViewAdv.AutoSizeColumn(newFilesTreeViewAdv.Columns[3]);
            }
            else
            {
                newFilesTreeViewAdv.Model = new SortedTreeModel(newFiles_model);
                if (newFilesFlat_model.Nodes.Count > 0 && newFilesFlat_model.Nodes[0] is ColumnNode sourceRoot)
                    TransferNodeCheckState(sourceRoot);
                newFilesTreeViewAdv.ShowNodeToolTips = false;
                if (newFilesTreeViewAdv.Root.Children.Count > 0)
                    newFilesTreeViewAdv.Root.Children[0].Expand();
            }
        }

        private void settingsToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            new SyncSettingsForm(this).ShowDialog();
        }       
    }
}
