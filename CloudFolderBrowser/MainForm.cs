using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Xml;
using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using Bluegrams.Application;
using CG.Web.MegaApiClient;
using HtmlAgilityPack;
using Newtonsoft.Json;
using WebDAVClient;
using YandexDiskSharp;
using Exception = System.Exception;
using CloudFolderBrowser.Theming;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Providers;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Branding;


//https://github.com/kozakovi4/YandexDiskSharp
//https://github.com/AdamsLair/treeviewadv
//https://sourceforge.net/projects/treeviewadv/
//https://github.com/Toqe/Downloader

namespace CloudFolderBrowser
{
    public enum CloudServiceType
    {
        Yadisk = 0,
        Mega = 1,
        h5ai = 2,
        Allsync = 3,
        QCloud = 4,
        TheTrove = 5,
        Other = 6,
        Dropbox = 7,
        GoogleDrive = 8,
        TeraBox = 9
    }

    public partial class MainForm : ThemedForm, ICloudProviderHost, ICloudProviderCredentialBroker
    {
        private delegate Task<CloudFolder> HostProviderLoader(
            string url,
            IProgress<int>? progress,
            CancellationToken cancellationToken);

        private ComboBox? themeMode_comboBox;
        private Panel? appHeader_panel;
        private readonly Button downloadManager_button = new();
        private CancellationTokenSource? syncFolderLoadCancellation;
        private int syncFolderLoadGeneration;
        private CancellationTokenSource? folderComparisonCancellation;
        private readonly CloudAccountStore accountStore = CloudAccountStore.Default;
        private readonly IReadOnlyDictionary<string, HostProviderLoader> hostProviderLoaders;

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public MainFormModel Model { get; set; } = new MainFormModel();

        private void networkSettings_button_Click(object sender, EventArgs e)
        {
            new FormsSecondary.SyncSettingsForm(null).ShowDialog(this);
        }

        private void downloadManager_button_Click(object? sender, EventArgs e)
        {
            using var manager = new DownloadManagerForm();
            manager.RetryRequested += DownloadManager_RetryRequested;
            manager.ShowDialog(this);
        }

        private void DownloadManager_RetryRequested(object? sender, IReadOnlyList<DownloadHistoryEntry> entries)
        {
            var unsupportedMega = entries.Where(entry => entry.CloudService == CloudServiceType.Mega).ToList();
            if (unsupportedMega.Count > 0)
            {
                MessageBox.Show(
                    "MEGA jobs need their encrypted node metadata. Reload the original MEGA share, then compare again to retry those files.",
                    "MEGA retry",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }

            foreach (var group in entries
                .Where(entry => entry.CloudService != CloudServiceType.Mega)
                .GroupBy(entry => new
                {
                    entry.CloudService,
                    RequestedRouteId = string.IsNullOrWhiteSpace(entry.RequestedRouteId)
                        ? DownloadRouteIds.Automatic
                        : entry.RequestedRouteId
                }))
            {
                DownloadHistoryEntry first = group.First();
                string basePath = Path.GetDirectoryName(first.SavePath) ?? syncFolderPath;
                if (!Directory.Exists(basePath))
                {
                    MessageBox.Show($"Download folder no longer exists: {basePath}");
                    continue;
                }

                var resumeModel = new MainFormModel
                {
                    CloudServiceType = group.Key.CloudService,
                    PreferredDownloadRouteId = group.Key.RequestedRouteId
                };
                var root = new CloudFolder("Resumed downloads", DateTime.Now, DateTime.Now, 0)
                {
                    Path = "/"
                };

                foreach (DownloadHistoryEntry entry in group)
                {
                    if (!Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out Uri? sourceUri))
                        continue;
                    var file = new CloudFile(entry.FileName, DateTime.MinValue, DateTime.MinValue, entry.ExpectedSize)
                    {
                        Path = entry.CloudPath,
                        PublicUrl = sourceUri,
                        LocalSavePathOverride = entry.SavePath,
                        DownloadHistoryId = entry.Id,
                        PlannedAction = SyncPlanAction.Download
                    };
                    root.AddFile(file);
                    root.SizeTopDirectoryOnly += file.Size;
                }

                if (root.Files.Count == 0)
                    continue;

                resumeModel.CloudPublicFolder = root;
                resumeModel.AllFolders = new List<CloudFolder> { root };
                if (group.Key.CloudService is CloudServiceType.Allsync or CloudServiceType.QCloud)
                {
                    string? shareKey = ExtractShareKey(first.SourceUrl);
                    if (!string.IsNullOrWhiteSpace(shareKey))
                    {
                        Model.savedPasswords.TryGetValue(shareKey, out string? savedPassword);
                        resumeModel.WebdavCredential = new NetworkCredential(shareKey, savedPassword ?? string.Empty);
                    }
                }

                var syncForm = new SyncFilesForm(this, root, resumeModel, basePath);
                activeSyncForm = syncForm;
                activeSyncForm.DownloadCompleted += SyncForm_DownloadCompleted;
            }
        }

        private static string? ExtractShareKey(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return null;
            string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            int shareIndex = Array.FindIndex(segments, segment => segment.Equals("s", StringComparison.OrdinalIgnoreCase));
            return shareIndex >= 0 && shareIndex + 1 < segments.Length
                ? Uri.UnescapeDataString(segments[shareIndex + 1])
                : null;
        }

        private static readonly string AppVersion = GetApplicationVersion();

        private static string GetApplicationVersion()
        {
            Version? version = typeof(MainForm).Assembly.GetName().Version;
            return version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
        }

        public bool UseProgressPanel = false;

        public static RestClient rc;
        public string syncFolderPath = "";
        public TreeModel cloudPublicFolder_model, cloudFlatFolder_model, syncFolder_model, newFiles_model;
        public LocalFolder syncFolder;
        public CloudFolder yadiskFolder;

        public List<CloudFolder> checkedFolders, mixedFolders;
        Dictionary<string, string> publicFolders = new Dictionary<string, string>();
        string hotDictKey = "";

        SyncFilesForm activeSyncForm;
        public bool usingFogLink = false;

        public IClient webdavClient;
        public MegaApiClient megaClient = null!;
        public INode? MegaRootNode;

        int checkedFilesNumber = 0;
        double checkedFilesSize = 0.0;
        public long freeSpace;

        const double b2Mb = 1.0 / (1024 * 1024);

        const string browserUserAgentString = @"Mozilla/5.0 (Macintosh; Intel Mac OS X 10_13_6) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/74.0.3729.108 Safari/537.36";
        //filter textbox 
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, int wParam, [MarshalAs(UnmanagedType.LPWStr)] string lParam);

        public MainForm()
        {
            InitializeComponent();

            hostProviderLoaders = new Dictionary<string, HostProviderLoader>(StringComparer.OrdinalIgnoreCase)
            {
                ["allsync"] = LoadAllsyncProviderAsync,
                ["qloud"] = LoadAllsyncProviderAsync
            };

            IReadOnlyList<string> providerErrors = CloudProviderRegistry.Default.LoadPlugins(
                Path.Combine(AppContext.BaseDirectory, "Providers"));
            foreach (string providerError in providerErrors)
                Model.WriteToLog($"\n{DateTime.Now:O}\nProvider plugin error: {providerError}\n", true);

            Model.UserAgent = browserUserAgentString;
            PortableSettingsProvider.ApplyProvider(Properties.Settings.Default);
            Bluegrams.Application.PortableSettingsProvider.AllRoaming = true;
            megaClient = NetworkClientAdapters.CreateMegaClient();
            rc = NetworkClientAdapters.CreateYandexClient();

            //copy local settings to roaming
            if (File.Exists("portable.config"))
            {
                try
                {
                    XmlDocument a = new();
                    a.Load("portable.config");

                    var roam = a.GetElementsByTagName("Roaming");
                    var local = a.GetElementsByTagName("PC_" + Environment.MachineName);

                    if (roam.Count > 0
                        && local.Count > 0
                        && roam[0] is XmlElement roamingElement
                        && local[0] is XmlElement localElement
                        && roamingElement.IsEmpty
                        && !localElement.IsEmpty
                        && localElement.FirstChild != null)
                    {
                        roamingElement.AppendChild(localElement.FirstChild.CloneNode(true));
                        a.Save("portable.config");
                    }
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to read portable.config: {ex}\n", true);
                }
            }

            ConfigureModernUi();
            bool isUiSnapshot = string.Equals(
                Environment.GetEnvironmentVariable("CFB_UI_SNAPSHOT"),
                "1",
                StringComparison.Ordinal);
            if (!isUiSnapshot)
                MigrateLegacyAccounts();
            UpdateAccountButtonState();

            SendMessage(filter_textBox.Handle, 0x1501, 1, "Search cloud files by name");
            filter_textBox.TextChangedComplete += filter_TextChangedComplete;

            beforeDate_dateTimePicker.MaxDate = DateTime.Today;
            afterDate_dateTimePicker.MaxDate = DateTime.Today;
            beforeDate_dateTimePicker.Value = DateTime.Today;

            if (Properties.Settings.Default.publicFoldersJson == "")
                publicFolders.Add("ExampleFolderName", "https://examplefolderurl.com");
            else
                publicFolders = DeserializeDictionarySetting(
                    Properties.Settings.Default.publicFoldersJson,
                    "saved shares");

            if (publicFolders.Count == 0)
                publicFolders.Add("ExampleFolderName", "https://examplefolderurl.com");

            publicFolders_comboBox.DataSource = new BindingSource { DataSource = publicFolders };
            publicFolders_comboBox.DisplayMember = "Key";
            publicFolders_comboBox.ValueMember = "Value";

            if (publicFolders.Count <= 1)
            {
                deletePublicFolder_button.Enabled = false;
            }
            else
                deletePublicFolder_button.Enabled = true;

            //nodeCheckBox1.IsEditEnabledValueNeeded += CheckIndex;
            nodeCheckBox1.IsVisibleValueNeeded += CheckIndex;

            nodeCheckBox1.CheckStateChanged += new EventHandler<TreePathEventArgs>(NodeCheckStateChanged);
            cloudPublicFolder_treeViewAdv.NodeControls[2].ToolTipProvider = new ToolTipProvider();

            refreshFolder_menuItem.Click += refreshFolder_menuItem_Click;
            openFolder_menuItem.Click += openFolder_menuItem_Click;

            if (Properties.Settings.Default.lastSyncFolderPath != "")
            {
                syncFolderPath = Properties.Settings.Default.lastSyncFolderPath;
                syncFolderPath_textBox.Text = syncFolderPath;
            }

            if (Properties.Settings.Default.savedPasswordsJson != "")
            {
                Model.savedPasswords = DeserializeDictionarySetting(
                    Properties.Settings.Default.savedPasswordsJson,
                    "saved share credentials");
            }

            checkAllToolStripMenuItem.Click += CheckAllToolStripMenuItem_Click;
            checkNoneToolStripMenuItem.Click += CheckNoneToolStripMenuItem_Click;
            expandAllToolStripMenuItem.Click += ExpandAllToolStripMenuItem_Click;
            collapseAllToolStripMenuItem.Click += CollapseAllToolStripMenuItem_Click;

            appVersion_linkLabel.Text = "v " + AppVersion;

            if (!isUiSnapshot)
                Shown += RestoreActiveCloudAccountsOnShown;

            string savedFogLinkAddress = Properties.Settings.Default.fogLinkAddress;
            if (!Uri.TryCreate(savedFogLinkAddress, UriKind.Absolute, out Uri? fogLinkUri)
                || fogLinkUri.Scheme is not ("http" or "https"))
            {
                fogLinkUri = new Uri("https://foglink.onrender.com/");
                Properties.Settings.Default.fogLinkAddress = fogLinkUri.OriginalString;
                Properties.Settings.Default.Save();
            }
            FogLink.ServerAddress = fogLinkUri;

            if (ProgressStage is Progress<int> progressStage)
                progressStage.ProgressChanged += MainForm_ProgressChanged;
            MainProgressBar.Value = 0;
            SetDoubleBuffered(ProgressLoading_panel);
            SetDoubleBuffered(MainProgressBar);
            SetDoubleBuffered(tableLayoutPanel1);
            //SetDoubleBuffered(cloudPublicFolder_treeViewAdv);

            UseProgressPanel = Properties.Settings.Default.useProgressBar;
            enableProgressPanel_checkBox.Checked = UseProgressPanel;
        }

        private Dictionary<string, string> DeserializeDictionarySetting(string json, string settingName)
        {
            try
            {
                return JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>();
            }
            catch (JsonException ex)
            {
                Model.WriteToLog($"\n{DateTime.Now:O}\nInvalid {settingName} JSON: {ex}\n", true);
                return new Dictionary<string, string>();
            }
        }

        private async void RestoreMegaSessionOnShown(object? sender, EventArgs e)
        {
            Shown -= RestoreMegaSessionOnShown;
            try
            {
                var token = JsonConvert.DeserializeObject<MegaApiClient.LogonSessionToken>(
                    Properties.Settings.Default.loginTokenMega,
                    new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Auto });
                if (token == null)
                    throw new JsonException("The stored MEGA token is empty.");
                await LoginMega(token);
            }
            catch (Exception ex) when (ex is JsonException or ApiException or HttpRequestException or InvalidDataException)
            {
                LogoutMega();
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to restore MEGA session: {ex}\n", true);
            }
        }

        private void ConfigureLegacyModernUi()
        {
            SuspendLayout();
            try
            {
                appHeader_panel = new Panel
                {
                    Name = "appHeader_panel",
                    Tag = "header",
                    Location = new Point(0, 0),
                    Size = new Size(ClientSize.Width, 76),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
                };

                var accent = new Panel
                {
                    Name = "headerAccent_panel",
                    Tag = "accent",
                    BackColor = ThemeManager.Palette.Accent,
                    Location = new Point(0, 0),
                    Size = new Size(5, 76),
                    Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left
                };
                var title = new Label
                {
                    AutoSize = true,
                    Font = new Font("Segoe UI Semibold", 17F, FontStyle.Bold, GraphicsUnit.Point),
                    Location = new Point(20, 11),
                    Text = "Cloud Folder Browser"
                };
                var subtitle = new Label
                {
                    AutoSize = true,
                    Tag = "muted",
                    Location = new Point(22, 43),
                    Text = "Reliable cloud sync  •  resumable downloads"
                };
                var themeLabel = new Label
                {
                    AutoSize = true,
                    Tag = "muted",
                    Location = new Point(ClientSize.Width - 302, 29),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right,
                    Text = "Theme"
                };
                themeMode_comboBox = new ComboBox
                {
                    Name = "themeMode_comboBox",
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Location = new Point(ClientSize.Width - 252, 25),
                    Size = new Size(132, 23),
                    Anchor = AnchorStyles.Top | AnchorStyles.Right
                };
                themeMode_comboBox.Items.AddRange(new object[] { "System", "Light", "Dark" });

                int savedTheme = Math.Clamp(Properties.Settings.Default.themeMode, 0, 2);
                ThemeManager.SetMode((AppThemeMode)savedTheme);
                themeMode_comboBox.SelectedIndex = savedTheme;
                themeMode_comboBox.SelectedIndexChanged += themeMode_comboBox_SelectedIndexChanged;

                appHeader_panel.Controls.Add(accent);
                appHeader_panel.Controls.Add(title);
                appHeader_panel.Controls.Add(subtitle);
                appHeader_panel.Controls.Add(themeLabel);
                appHeader_panel.Controls.Add(themeMode_comboBox);

                loginMega_button.Size = new Size(96, 34);
                loginMega_button.Location = new Point(ClientSize.Width - 106, 20);
                loginMega_button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                appHeader_panel.Controls.Add(loginMega_button);
                Controls.Add(appHeader_panel);
                appHeader_panel.SendToBack();

                foreach (Button button in new[]
                {
                    addNewPublicFolder_button, editPublicFolderKey_button, deletePublicFolder_button,
                    LoadFromFile_button, SaveToFile_button, networkSettings_button, fogLink_button
                })
                {
                    button.Top = 91;
                    button.Height = 32;
                }

                addNewPublicFolder_button.Left = 15;
                addNewPublicFolder_button.Width = 52;
                editPublicFolderKey_button.Left = 72;
                editPublicFolderKey_button.Width = 52;
                deletePublicFolder_button.Left = 129;
                deletePublicFolder_button.Width = 66;
                deletePublicFolder_button.Tag = "danger";

                publicFolders_comboBox.Location = new Point(201, 95);
                publicFolders_comboBox.Size = new Size(230, 23);
                LoadFromFile_button.Left = 438;
                LoadFromFile_button.Width = 100;
                SaveToFile_button.Left = 544;
                SaveToFile_button.Width = 100;
                networkSettings_button.Left = ClientSize.Width - 274;
                networkSettings_button.Width = 128;
                networkSettings_button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                fogLink_button.Left = ClientSize.Width - 140;
                fogLink_button.Width = 125;
                fogLink_button.Anchor = AnchorStyles.Top | AnchorStyles.Right;

                loadPublicFolderKey_button.Tag = "primary";
                syncFolders_button.Tag = "primary";
                tableLayoutPanel1.Padding = new Padding(0);
                panel1.Tag = "card";
                panel2.Tag = "card";
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private void themeMode_comboBox_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (themeMode_comboBox == null || themeMode_comboBox.SelectedIndex < 0)
                return;

            Properties.Settings.Default.themeMode = themeMode_comboBox.SelectedIndex;
            Properties.Settings.Default.Save();
            ThemeManager.SetMode((AppThemeMode)themeMode_comboBox.SelectedIndex);
        }

        void UpdatePublicFoldersSetting()
        {
            if (publicFolders.Count <= 1)
            {
                deletePublicFolder_button.Enabled = false;
            }
            else
                deletePublicFolder_button.Enabled = true;
            Properties.Settings.Default.publicFoldersJson = JsonConvert.SerializeObject(publicFolders);
            Properties.Settings.Default.Save();
        }

        string GetFolderPath()
        {
            FolderBrowserDialog fldsd = new FolderBrowserDialog();
            fldsd.Description = "Choose folder to sync";
            if (Properties.Settings.Default.lastSyncFolderPath != "")
                fldsd.InitialDirectory = Properties.Settings.Default.lastSyncFolderPath;
            else
                fldsd.InitialDirectory = System.IO.Directory.GetCurrentDirectory();
            fldsd.ShowDialog();
            return fldsd.SelectedPath;
        }

        #region #NODE CHECKBOX

        void CheckIndex(object? sender, NodeControlValueEventArgs e)
        {
            var currentNode = (ColumnNode)(e.Node.Tag);
            var parentNode = (currentNode.Parent);
            string g = currentNode.Tag.GetType().ToString();
            bool isFolder = g == "CloudFolderBrowser.CloudFolder";
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

            e.Value = isFolder && currentNode.CheckBoxEnabled;
        }

        void NodeCheckStateChanged(object? sender, TreePathEventArgs e)
        {
            ColumnNode checkedNode = (ColumnNode)e.Path.LastNode;
            if (checkedNode.CheckState == CheckState.Checked)
            {
                //if (checkedNode.Parent.CheckState != CheckState.Checked && checkedNode.Parent.Index !=-1)
                //    UpdateParentCheckState((ColumnNode)checkedNode.Parent);
                //CheckAllSubnodes(checkedNode, false);
                checkedFilesSize += ((CloudFolder)checkedNode.Tag).Size - ((CloudFolder)checkedNode.Tag).SizeTopDirectoryOnly;
                checkedFilesNumber += ((CloudFolder)checkedNode.Tag).FilesNumber - ((CloudFolder)checkedNode.Tag).FilesNumberTopDirectoryOnly;
            }
            if (checkedNode.CheckState == CheckState.Unchecked)
            {
                checkedFilesSize -= ((CloudFolder)checkedNode.Tag).Size;
                checkedFilesNumber -= ((CloudFolder)checkedNode.Tag).FilesNumber;
                if (checkedFilesSize < 0.0001)
                    checkedFilesSize = 0;
                //if (checkedNode.Parent.Index != -1)
                //    UpdateParentCheckState((ColumnNode)checkedNode.Parent);                
                //CheckAllSubnodes(checkedNode, true);                                               
            }
            if (checkedNode.CheckState == CheckState.Indeterminate)
            {
                checkedFilesSize += ((CloudFolder)checkedNode.Tag).SizeTopDirectoryOnly;
                checkedFilesNumber += ((CloudFolder)checkedNode.Tag).FilesNumberTopDirectoryOnly;
            }
            checkedFiles_label.Text = $"{checkedFilesNumber} files • {Math.Round(checkedFilesSize * b2Mb, 2)} MB";
            return;
        }

        static void UpdateParentCheckState(ColumnNode parentNode)
        {
            CheckState origState = parentNode.CheckState;
            int UnCheckedNodes = 0, CheckedNodes = 0, MixedNodes = 0;

            foreach (ColumnNode tnChild in parentNode.Nodes)
            {
                if (tnChild.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
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
            }

            if (MixedNodes > 0)
            {
                // at least one child is mixed, so parent must be mixed
                //parentNode.CheckState = CheckState.Indeterminate;
            }
            else if (CheckedNodes > 0 && UnCheckedNodes == 0)
            {
                // all children are checked
                if (parentNode.CheckState == CheckState.Indeterminate)
                    parentNode.CheckState = CheckState.Checked;
                //parentNode.CheckState = CheckState.Indeterminate;
                //else
                //   parentNode.CheckState = CheckState.Indeterminate;
            }
            else if (CheckedNodes > 0)
            {
                // some children are checked, the rest are unchecked
                if (parentNode.CheckState == CheckState.Checked)
                    parentNode.CheckState = CheckState.Unchecked;

                //parentNode.CheckState = CheckState.Indeterminate;
            }
            else
            {
                //if (parentNode.CheckState != CheckState.Unchecked)
                //    parentNode.CheckState = CheckState.Indeterminate;
                // all children are unchecked
                //if (parentNode.CheckState == CheckState.Checked)
                //    parentNode.CheckState = CheckState.Indeterminate;
                //else
                //    parentNode.CheckState = CheckState.Unchecked;
            }

            if (parentNode.CheckState != origState && parentNode.Parent.Index != -1)
                UpdateParentCheckState((ColumnNode)parentNode.Parent);

        }

        void CheckAllSubnodes(ColumnNode node, bool uncheck)
        {
            CheckState cst = uncheck ? CheckState.Unchecked : CheckState.Checked;

            foreach (ColumnNode subnode in node.Nodes)
            {
                CheckState origState = subnode.CheckState;
                if (subnode.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
                {
                    if (cst == CheckState.Unchecked)
                    {

                    }
                    if (cst == CheckState.Checked)
                    {
                        if (origState == CheckState.Indeterminate)
                        {

                        }
                        if (origState == CheckState.Unchecked)
                        {
                            checkedFilesSize += ((CloudFolder)subnode.Tag).SizeTopDirectoryOnly;
                        }
                    }

                    subnode.CheckState = cst;
                    CheckAllSubnodes(subnode, uncheck);
                }
            }
        }

        void GetCheckedFolders(ColumnNode node)
        {
            if (node.CheckState == CheckState.Checked)
            {
                checkedFolders.Add((CloudFolder)(node.Tag));
                return;
            }
            //if mixed
            if (node.CheckState == CheckState.Indeterminate)
                mixedFolders.Add((CloudFolder)(node.Tag));

            foreach (ColumnNode subnode in node.Nodes)
            {
                if (subnode.CheckState == CheckState.Checked)
                {
                    checkedFolders.Add((CloudFolder)(subnode.Tag));
                    continue;
                }
                if (subnode.CheckState == CheckState.Indeterminate)
                {
                    mixedFolders.Add((CloudFolder)(subnode.Tag));
                    foreach (ColumnNode subnodeChild in subnode.Nodes)
                        if (subnodeChild.CheckState != CheckState.Unchecked && subnodeChild.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
                            GetCheckedFolders(subnodeChild);
                }
                if (subnode.CheckState == CheckState.Unchecked && subnode.Nodes.Count > 0)
                {
                    GetCheckedFolders(subnode);
                }
            }
        }

        #endregion

        async Task CreateDummyFolder(CloudFolder folder)
        {
            foreach (var file in folder.Files)
            {
                string filePath = Utility.GetSafeDownloadPath(syncFolderPath, file.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                using var placeholder = File.Create(filePath);
            }

            foreach (var subfolder in folder.Subfolders)
            {
                var cloudSubfolder = subfolder as CloudFolder;
                if (cloudSubfolder == null)
                    continue;

                Directory.CreateDirectory(Utility.GetSafeDownloadPath(syncFolderPath, cloudSubfolder.Path));
                await CreateDummyFolder(cloudSubfolder);
            }
        }

        IProgress<int> ProgressStage = new Progress<int>();

        async Task<bool> LoadPublicFolder(string cloudFolderUrl)
        {
            ProgressStage.Report(1); //set progress to loading web
            usingFogLink = false;
            if (cloudFolderUrl != "")
            {
                if (cloudFolderUrl.IsBase64String() || cloudFolderUrl.Contains(FogLink.ServerAddress.OriginalString))
                {
                    try
                    {
                        Model.CloudServiceType = CloudServiceType.Mega;
                        usingFogLink = true;
                        await Model.LoadMega(await FogLink.GetDecodedAsync(cloudFolderUrl), publicFolderKey_textBox.Text);
                        Model.LoadedFromFile = false;

                        UpdateTreeModel();
                        return true;
                    }
                    catch (Exception ex)
                    {
                        Model.WriteToLog($"\n{DateTime.Now:O}\nFailed to load FogLink: {ex}\n", true);
                        MessageBox.Show(
                            "Cannot load the FogLink: " + ex.Message,
                            "Load failed",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                        return false;
                    }
                    finally
                    {
                        ProgressStage?.Report(2);
                    }
                }

                if (!cloudFolderUrl.Contains("http"))
                    cloudFolderUrl = @"https://" + cloudFolderUrl;

                if (cloudFolderUrl.Contains("rebrand.ly", StringComparison.OrdinalIgnoreCase))
                {
                    string? redirectedUrl = await Utility.GetFinalRedirect(cloudFolderUrl, browserUserAgentString);
                    cloudFolderUrl = redirectedUrl ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(cloudFolderUrl)
                    || cloudFolderUrl.Contains("rebrand.ly", StringComparison.OrdinalIgnoreCase)
                    || cloudFolderUrl.Contains("rebrandly", StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("Timeout or link is dead");
                    ProgressStage?.Report(2);  //set progress to finished
                    return false;
                }

                ICloudProviderDescriptor? provider = CloudProviderRegistry.Default.Resolve(cloudFolderUrl);
                if (provider == null)
                {
                    MessageBox.Show("Unsupported link type");
                    ProgressStage?.Report(2);
                    return false;
                }
                Model.CloudServiceType = provider.ServiceType;

                ProgressStage.Report(0); //set progress to loading treeview

                try
                {
                    Model.CloudPublicFolder = await CloudProviderRegistry.Default.LoadAsync(
                        cloudFolderUrl,
                        new CloudProviderLoadContext(this, this),
                        ProgressStage);
                    Model.CloudPublicFolder.OriginalString = cloudFolderUrl;
                    Model.AllFolders = new List<CloudFolder>();
                    AddSubFolders(Model.CloudPublicFolder);
                    Model.CloudPublicFolder.CalculateFolderSize();
                    UpdateTreeModel();
                    Model.LoadedFromFile = false;
                }
                catch (Exception ex)
                {
                    Model.WriteToLog($"\n{DateTime.Now:O}\nFailed to load {cloudFolderUrl}: {ex}\n", true);
                    MessageBox.Show(
                        "Cannot load the folder: " + ex.Message,
                        "Load failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return false;
                }
                finally
                {
                    ProgressStage?.Report(2);
                }
            }

            return true;
        }

        public Task<CloudFolder> LoadHostIntegratedAsync(
            string providerId,
            string url,
            IProgress<int>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!hostProviderLoaders.TryGetValue(providerId, out HostProviderLoader? loader))
            {
                throw new NotSupportedException(
                    $"No host-integrated loader is registered for provider '{providerId}'.");
            }
            return loader(url, progress, cancellationToken);
        }

        private async Task<CloudFolder> LoadAllsyncProviderAsync(
            string url,
            IProgress<int>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await LoadAllsync(url))
                throw new InvalidOperationException("The AllSync/Qloud share could not be loaded.");
            return Model.CloudPublicFolder;
        }

        public Task<string?> RequestPasswordAsync(
            CloudProviderCredentialRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var passwordForm = new PasswordForm();
            return Task.FromResult(
                passwordForm.ShowDialog(this) == DialogResult.OK
                    ? passwordForm.Password
                    : null);
        }

        public static void SetDoubleBuffered(Control c)
        {
            if (SystemInformation.TerminalServerSession)
                return;
            System.Reflection.PropertyInfo? property = typeof(Control).GetProperty(
                "DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            property?.SetValue(c, true, null);
        }

        Bitmap? mainFormScreenshot;
        void SetProgress(bool waiting = true, bool loadingWeb = true)
        {
            if (!UseProgressPanel)
            {
                if (this.InvokeRequired)
                    MainProgressBar.BeginInvoke(() => Enabled = !waiting);
                else
                    Enabled = !waiting;

                return;
            }

            bool progressChanged = waiting && MainProgressBar.Value == 0;
            Action progressBarAction = () =>
            {
                if (loadingWeb)
                    MainProgressBar.ProgressColor = Color.LimeGreen;
                else
                    MainProgressBar.ProgressColor = Color.DodgerBlue;

                if (waiting && MainProgressBar.Value == 0)
                {
                    MainProgressBar.Value = 75;
                    MainProgressBar.Style = ProgressBarStyle.Marquee;
                    MainProgressBar.AnimationSpeed = 2000;
                }
                else if (!waiting)
                {
                    MainProgressBar.Value = 0;
                    MainProgressBar.Style = ProgressBarStyle.Continuous;
                    MainProgressBar.AnimationSpeed = 0;
                }
            };

            Action progressPanelAction = () =>
            {
                if (waiting && MainProgressBar.Value == 0)
                {
                    Size size = ClientSize;
                    Point origin = PointToScreen(Point.Empty);
                    var screenshot = new Bitmap(
                        Math.Max(1, size.Width),
                        Math.Max(1, size.Height),
                        PixelFormat.Format32bppArgb);
                    using (Graphics graphics = Graphics.FromImage(screenshot))
                    using (var fadeBrush = new SolidBrush(Color.FromArgb(32, Color.Black)))
                    {
                        graphics.CopyFromScreen(origin, Point.Empty, size, CopyPixelOperation.SourceCopy);
                        graphics.FillRectangle(fadeBrush, new Rectangle(Point.Empty, size));
                    }

                    Bitmap? previousScreenshot = mainFormScreenshot;
                    mainFormScreenshot = screenshot;
                    ProgressLoading_panel.BackgroundImage = screenshot;
                    ProgressLoading_panel.Size = size;
                    previousScreenshot?.Dispose();
                    ProgressLoading_panel.Visible = true;
                    ProgressLoading_panel.BringToFront();


                }
                else if (!waiting)
                {
                    ProgressLoading_panel.Visible = false;
                    ProgressLoading_panel.BackgroundImage = null;
                    mainFormScreenshot?.Dispose();
                    mainFormScreenshot = null;
                }

                ProgressLoading_panel.Invalidate();
            };

            Action progressFormAction = () =>
            {
                if (progressChanged)
                {
                    FormBorderStyle = FormBorderStyle.FixedSingle;
                }
                else if (!waiting)
                {
                    FormBorderStyle = FormBorderStyle.Sizable;
                }
            };

            Action SuspendLayoutAction = () =>
            {
                SuspendLayout();
            };

            Action ResumeLayoutAction = () =>
            {
                ResumeLayout();
            };

            //if (InvokeRequired)
            //    BeginInvoke(SuspendLayoutAction);

            if (ProgressLoading_panel.InvokeRequired && waiting && MainProgressBar.Value == 0 || (!waiting && MainProgressBar.Value != 0))
                ProgressLoading_panel.BeginInvoke(progressPanelAction);
            else if (waiting && MainProgressBar.Value == 0 || (!waiting && MainProgressBar.Value != 0))
                progressPanelAction();

            if (MainProgressBar.InvokeRequired)
                MainProgressBar.BeginInvoke(progressBarAction);
            else
                progressBarAction();

            if (InvokeRequired)
                BeginInvoke(progressFormAction);
            else
                progressFormAction();

            //if (InvokeRequired)
            //    BeginInvoke(ResumeLayoutAction);
        }

        private void MainForm_ProgressChanged(object? sender, int e)
        {
            switch (e)
            {
                case 0: //web
                    SetProgress();
                    break;
                case 1: //treeview
                    SetProgress(true, false);
                    break;
                case 2: //completed
                    SetProgress(false, false);
                    break;
            }
        }


        #region LOAD WEB

        #region Allsync  

        async Task<bool> LoadAllsync(string url, bool onlyCheck = false)
        {
            var authentication = new AllsyncAuthenticationService(Model);
            AllsyncLoadResult loadResult = await authentication.LoadAsync(
                url,
                onlyCheck,
                this,
                ProgressStage);
            if (!loadResult.Success)
            {
                if (!loadResult.Cancelled && !string.IsNullOrWhiteSpace(loadResult.ErrorMessage))
                    MessageBox.Show(loadResult.ErrorMessage);
                return false;
            }

            if (onlyCheck)
                return true;

            try
            {
                if (Model.CloudPublicFolder.Name == "")
                {
                    HtmlWeb web = new HtmlWeb();
                    HtmlAgilityPack.HtmlDocument htmlDoc = new HtmlAgilityPack.HtmlDocument();
                    using (var httpClient = AppHttpClientFactory.CreateClient(
                        TimeSpan.FromSeconds(45), routeKey: "Allsync"))
                    {
                        httpClient.DefaultRequestHeaders.UserAgent.Clear();
                        httpClient.DefaultRequestHeaders.Add("User-Agent", browserUserAgentString);
                        using (var request = new HttpRequestMessage(new HttpMethod("GET"), Model.allsyncRootFolderAddress))
                        {
                            if (Model.FlareSolverrSession != null)
                            {
                                request.Headers.Remove("User-Agent");
                                request.Headers.TryAddWithoutValidation(
                                    "User-Agent", Model.FlareSolverrSession.UserAgent);
                                var cookieHeader = Model.FlareSolverrSession.GetCookieHeader(request.RequestUri!);
                                if (!string.IsNullOrWhiteSpace(cookieHeader))
                                    request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                            }
                            var response = await httpClient.SendAsync(request);
                            var ts = await response.Content.ReadAsStringAsync();
                            htmlDoc.LoadHtml(ts);
                        }
                    }
                    var rootFolderName = htmlDoc.DocumentNode.SelectSingleNode("//span[@class='header-appname']");
                    if (rootFolderName == null)
                        Model.CloudPublicFolder.Name = publicFolders_comboBox.Text;
                    else
                    {
                        Model.CloudPublicFolder.Name = rootFolderName.InnerText;
                        Model.CloudPublicFolder.Name = Regex.Replace(Model.CloudPublicFolder.Name, @"\t|\n|\r", "");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Cannot retrieve data from URL");
                Model.WriteToLog(ex.Message, true);
                return false;
            }
        }

        #endregion

        #region MEGA

        public async Task LoginMega(string login, string password)
        {
            try
            {
                if (megaClient.IsLoggedIn)
                    megaClient.Logout();

                var loginToken = await megaClient.LoginAsync(login, password);
                var nodes = await megaClient.GetNodesAsync();
                MegaRootNode = nodes.FirstOrDefault()
                    ?? throw new InvalidDataException("MEGA did not return a root node.");
                await GetMegaInfo();

                Properties.Settings.Default.loginTokenMega = JsonConvert.SerializeObject(loginToken, new JsonSerializerSettings
                {
                    TypeNameHandling = TypeNameHandling.Auto
                });
                Properties.Settings.Default.loginedMega = true;
                Properties.Settings.Default.Save();
                UpdateAccountButtonState();
            }
            catch
            {
                LogoutMega();
                throw;
            }
        }

        public async Task LoginMega(MegaApiClient.LogonSessionToken token)
        {
            try
            {
                await megaClient.LoginAsync(token);
                var nodes = await megaClient.GetNodesAsync();
                MegaRootNode = nodes.FirstOrDefault()
                    ?? throw new InvalidDataException("MEGA did not return a root node.");
                await GetMegaInfo();

                Properties.Settings.Default.loginedMega = true;
                Properties.Settings.Default.Save();
                UpdateAccountButtonState();
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException)
            {
                LogoutMega();
                Model.WriteToLog($"\n{DateTime.Now:O}\nMEGA login failed: {ex}\n", true);
                throw;
            }
        }

        async Task GetMegaInfo()
        {
            var accInfo = await megaClient.GetAccountInformationAsync();
            long totalSpace = (long)(accInfo.TotalQuota * b2Mb),
                usedSpace = (long)(accInfo.UsedQuota * b2Mb);
            freeSpace = accInfo.TotalQuota - accInfo.UsedQuota;

            int progressMaximum = (int)Math.Clamp(totalSpace, 1, int.MaxValue);
            int progressValue = (int)Math.Clamp(usedSpace, 0, progressMaximum);
            yadiskSpace_progressBar.Maximum = progressMaximum;
            yadiskSpace_progressBar.Value = progressValue;

            if (usedSpace >= 0.95 * (totalSpace))
            {
                yadiskSpace_progressBar.ProgressColor = Color.OrangeRed;
            }
            else
            if (usedSpace >= 0.85 * (totalSpace))
                yadiskSpace_progressBar.ProgressColor = Color.Orange;
            else
            if (usedSpace >= 0.75 * (totalSpace))
                yadiskSpace_progressBar.ProgressColor = Color.Yellow;

            yadiskSpace_progressBar.Visible = true;

            double freeGb = Math.Max(0, totalSpace - usedSpace) / 1024.0;
            int freePercent = totalSpace > 0
                ? (int)Math.Clamp((totalSpace - usedSpace) * 100.0 / totalSpace, 0, 100)
                : 0;
            yadiskSpace_progressBar.CustomText = $"Free: {freePercent}% |" +
               $" {Math.Round(freeGb, 2)}" +
               $" GB out of {Math.Max(0, totalSpace) / 1024} GB";
        }
        
        public void LogoutMega()
        {
            try
            {
                megaClient.Logout();
            }
            catch { }

            Properties.Settings.Default.loginTokenMega = "";
            Properties.Settings.Default.loginedMega = false;
            Properties.Settings.Default.Save();

            UpdateAccountButtonState();
            yadiskSpace_progressBar.Visible = false;
        }
        #endregion

        #endregion

        #region LOAD LOCAL     

        void AddSubFolders(CloudFolder? folder)
        {
            if (folder == null)
                return;

            Model.AllFolders.Add(folder);
            foreach (var subfolder in folder.Subfolders)
            {
                AddSubFolders(subfolder as CloudFolder);
            }
        }

        static string appPath = Directory.GetCurrentDirectory();

        async Task LoadFolderJson(bool checkStatus = false)
        {
            syncFolders_button.Enabled = false;
            ProgressStage?.Report(1);
            try
            {
                string key = publicFolderKey_textBox.Text;
                if (key == "")
                    checkStatus = false;

                Model.CloudPublicFolder = new CloudFolder();

                var dir = new DirectoryInfo(Path.Combine(appPath, "jsons"));
                dir.Create();

                if (!key.Contains("http", StringComparison.OrdinalIgnoreCase) && !key.IsBase64String())
                    key = @"https://" + key;

                if (publicFolderKey_textBox.Text.Contains("rebrand.ly", StringComparison.OrdinalIgnoreCase))
                    key = await Utility.GetFinalRedirect(key, browserUserAgentString)
                        ?? throw new HttpRequestException("The saved-share redirect could not be resolved.");

                string fileName = Path.Combine(dir.FullName, Utility.GetHashString(key) + ".json");
                if (!File.Exists(fileName))
                {
                    MessageBox.Show("No saved list was found for this share.");
                    return;
                }

                CloudFolder loadedFolder = await Task.Run(() =>
                {
                    string jsonString = File.ReadAllText(fileName);
                    return JsonConvert.DeserializeObject<CloudFolder>(jsonString, new JsonSerializerSettings
                    {
                        TypeNameHandling = TypeNameHandling.Auto
                    }) ?? throw new JsonException("The saved folder list is empty.");
                });

                Model.CloudPublicFolder = loadedFolder;
                Model.AllFolders = new List<CloudFolder>();
                AddSubFolders(Model.CloudPublicFolder);
                Model.LoadedFromFile = true;
                UpdateTreeModel();

                bool providerAvailable = true;
                if (checkStatus)
                {
                    Model.CloudServiceType = Utility.GetCloudServiceType(key);
                    if (Model.CloudServiceType is CloudServiceType.Allsync or CloudServiceType.QCloud)
                        providerAvailable = await LoadAllsync(key, true);
                }

                flatList_checkBox.Enabled = true;
                syncFolders_button.Enabled = providerAvailable && syncFolder != null;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or HttpRequestException)
            {
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to open saved list: {ex}\n", true);
                MessageBox.Show(
                    "Unable to open the saved list: " + ex.Message,
                    "Open list failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                ProgressStage?.Report(2);
            }
        }

        #endregion

        #region BUILD NODE TREE

        void UpdateTreeModel()
        {
            ProgressStage?.Report(1);
            cloudPublicFolder_model = new TreeModel();
            cloudFlatFolder_model = new TreeModel();
            ColumnNode rootNode = new ColumnNode(HttpUtility.UrlDecode(Model.CloudPublicFolder.Name), Model.CloudPublicFolder.Created, Model.CloudPublicFolder.Modified, Model.CloudPublicFolder.Size);
            rootNode.Tag = Model.CloudPublicFolder;

            cloudPublicFolder_model.Nodes.Add(rootNode);
            BuildSubfolderNodes(rootNode);
            BuildFullFolderStructure(rootNode);

            cloudPublicFolder_treeViewAdv.Model = new SortedTreeModel(cloudPublicFolder_model);
            cloudPublicFolder_treeViewAdv.NodeFilter = filter;

            cloudPublicFolder_treeViewAdv.Columns[0].MinColumnWidth = 100;

            if (syncFolderPath_textBox.Text != "")
            {
                refreshFolder_menuItem.Enabled = true;
                openFolder_menuItem.Enabled = true;
            }
            else
            {
                syncFolders_button.Enabled = false;
            }
            ProgressStage?.Report(2);
        }

        public void BuildSubfolderNodes(ColumnNode node)
        {
            if (node.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder")
            {
                foreach (CloudFolder subfolder in ((CloudFolder)node.Tag).Subfolders)
                {
                    ColumnNode subNode = new ColumnNode(HttpUtility.UrlDecode(subfolder.Name), subfolder.Created, subfolder.Modified, subfolder.Size);
                    subNode.Tag = subfolder;
                    node.Nodes.Add(subNode);
                }
                foreach (CloudFile file in ((CloudFolder)node.Tag).Files)
                {
                    ColumnNode subNode = new ColumnNode(HttpUtility.UrlDecode(file.Name), file.Created, file.Modified, file.Size);
                    subNode.Tag = file;
                    node.Nodes.Add(subNode);
                    cloudFlatFolder_model.Nodes.Add(new ColumnNode(subNode));
                }
            }
            else
            {
                foreach (LocalFolder subfolder in ((LocalFolder)node.Tag).Subfolders)
                {
                    ColumnNode subNode = new ColumnNode(HttpUtility.UrlDecode(subfolder.Name), subfolder.Created, subfolder.Modified, subfolder.Size);
                    subNode.Tag = subfolder;
                    node.Nodes.Add(subNode);
                }
                foreach (FileInfo file in ((LocalFolder)node.Tag).Files)
                {
                    ColumnNode subNode = new ColumnNode(HttpUtility.UrlDecode(file.Name), file.CreationTime, file.LastWriteTime, file.Length);
                    subNode.Tag = file;
                    node.Nodes.Add(subNode);
                }
            }
        }

        private void BuildFullFolderStructure(ColumnNode rootNode)
        {
            foreach (ColumnNode subNode in rootNode.Nodes)
            {
                if (subNode.Tag.GetType().ToString() == "CloudFolderBrowser.CloudFolder"
                    || subNode.Tag.GetType().ToString() == "CloudFolderBrowser.LocalFolder")
                {
                    if (subNode.Nodes.Count == 0)
                        BuildSubfolderNodes(subNode);
                    BuildFullFolderStructure(subNode);
                }
            }
        }

        #endregion

        #region #SYNC        

        async Task SyncFiles()
        {
            if (checkedFolders.Count == 0 && mixedFolders.Count == 0)
            {
                MessageBox.Show("No folders checked!");
                return;
            }

            folderComparisonCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = folderComparisonCancellation.Token;
            syncFolders_button.Enabled = true;
            syncFolders_button.Text = "Cancel compare";
            syncFolders_button.Tag = "danger-subtle";

            var progress = new Progress<SyncPlanProgress>(value =>
            {
                if (value.Total == 0)
                {
                    SetComparisonStatus("Preparing comparison…");
                    return;
                }

                int percentage = (int)Math.Round(value.Processed * 100d / value.Total);
                string detail = string.IsNullOrWhiteSpace(value.CurrentFile)
                    ? $"Compared {value.Total:N0} files"
                    : $"Comparing {value.Processed + 1:N0} of {value.Total:N0}: {value.CurrentFile}";
                SetComparisonStatus(detail, percentage);
            });

            List<SyncPlanItem> plan = await Task.Run(() => Model.BuildSyncPlanAsync(
                checkedFolders,
                mixedFolders,
                syncFolder.Path,
                hideExistingFiles_checkBox.Checked,
                Properties.Settings.Default.verifySha256,
                cancellationToken,
                progress), cancellationToken);
            if (plan.Count == 0)
            {
                SetComparisonStatus("Everything is up to date", 100);
                MessageBox.Show(
                    "No new or changed files were found. The selected local files already match the share.",
                    "Folders match",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            using var preview = new SyncPreviewForm(plan);
            if (preview.ShowDialog(this) != DialogResult.OK)
                return;

            List<CloudFile> selectedFiles = preview.SelectedFiles.ToList();
            if (selectedFiles.Count == 0)
            {
                MessageBox.Show("All files were skipped.");
                return;
            }

            CloudFolder newFilesFolder = new CloudFolder(Model.CloudPublicFolder.Name, Model.CloudPublicFolder.Created, Model.CloudPublicFolder.Modified, Model.CloudPublicFolder.Size);
            newFilesFolder.PublicKey = Model.CloudPublicFolder.PublicKey;
            newFilesFolder.Files.AddRange(selectedFiles);
            newFilesFolder.Size = selectedFiles.Sum(file => file.Size);

            Model.WriteToLog($"\n{DateTime.Now}\n Creating SyncFilesForm for {Model.CloudServiceType}.\n\n");
            SyncFilesForm syncFilesForm = new SyncFilesForm(this, newFilesFolder, Model);
            activeSyncForm = syncFilesForm;
            activeSyncForm.DownloadCompleted += SyncForm_DownloadCompleted;
        }

        private void SetComparisonStatus(string text, int? percentage = null)
        {
            if (comparisonStatus_label != null)
                comparisonStatus_label.Text = text;

            if (percentage.HasValue)
            {
                if (comparisonProgressBar != null)
                    comparisonProgressBar.Value = Math.Clamp(percentage.Value, comparisonProgressBar.Minimum, comparisonProgressBar.Maximum);
                MainProgressBar.Style = ProgressBarStyle.Continuous;
                MainProgressBar.Value = Math.Clamp(percentage.Value, MainProgressBar.Minimum, MainProgressBar.Maximum);
            }
        }

        private async void SyncForm_DownloadCompleted(object? sender, EventArgs e)
        {
            await RefreshSyncFolderAsync(syncFolderPath_textBox.Text);
        }

        private async Task RefreshSyncFolderAsync(string path)
        {
            string requestedPath = path.Trim();
            if (!Directory.Exists(requestedPath))
                return;

            syncFolderLoadCancellation?.Cancel();
            syncFolderLoadCancellation?.Dispose();
            syncFolderLoadCancellation = new CancellationTokenSource();
            CancellationToken cancellationToken = syncFolderLoadCancellation.Token;
            int generation = Interlocked.Increment(ref syncFolderLoadGeneration);

            syncFolders_button.Enabled = false;
            try
            {
                LocalFolder loadedFolder = await Task.Run(() =>
                {
                    var folder = new LocalFolder(new DirectoryInfo(requestedPath), cancellationToken);
                    folder.CalculateFolderSize();
                    return folder;
                }, cancellationToken);

                if (generation != syncFolderLoadGeneration || cancellationToken.IsCancellationRequested)
                    return;

                syncFolder = loadedFolder;
                LoadSyncFolder(requestedPath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (generation != syncFolderLoadGeneration)
                    return;
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to scan local folder {requestedPath}: {ex}\n", true);
                MessageBox.Show(
                    "Unable to scan the local folder: " + ex.Message,
                    "Local folder error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        void LoadSyncFolder(string path)
        {
            if (path != "" && syncFolder != null)
            {
                syncFolder_model = new TreeModel();
                ColumnNode rootNode2 = new ColumnNode(syncFolder.Name, syncFolder.Created, syncFolder.Modified, syncFolder.Size);
                rootNode2.Tag = syncFolder;

                BuildSubfolderNodes(rootNode2);
                BuildFullFolderStructure(rootNode2);
                syncFolder_model.Nodes.Add(rootNode2);

                syncFolder_treeViewAdv.Model = new SortedTreeModel(syncFolder_model);

                if (syncFolder_treeViewAdv.Root.Children.Count > 0)
                    syncFolder_treeViewAdv.Root.Children[0].Expand();

                if (Model.CloudPublicFolder.Path == null)
                    syncFolders_button.Enabled = false;
                else
                    syncFolders_button.Enabled = true;

                openFolder_menuItem.Enabled = true;
                refreshFolder_menuItem.Enabled = true;
            }
        }

        #endregion

        #region #EVENT HANDLERS

        #region #TREEVIEW

        private async void refreshFolder_menuItem_Click(object? sender, EventArgs e)
        {
            await RefreshSyncFolderAsync(syncFolderPath_textBox.Text);
        }
        private void openFolder_menuItem_Click(object? sender, EventArgs e)
        {
            if (Directory.Exists(syncFolderPath_textBox.Text))
                Process.Start(new ProcessStartInfo { FileName = syncFolderPath_textBox.Text, UseShellExecute = true });
        }
        private async void syncFolderPath_textBox_TextChanged(object sender, EventArgs e)
        {
            syncFolderPath = syncFolderPath_textBox.Text.Trim();
            if (!Directory.Exists(syncFolderPath))
            {
                syncFolderLoadCancellation?.Cancel();
                openFolder_menuItem.Enabled = false;
                refreshFolder_menuItem.Enabled = false;
                syncFolders_button.Enabled = false;
                return;
            }

            await RefreshSyncFolderAsync(syncFolderPath);
        }

        private void CollapseAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            cloudPublicFolder_treeViewAdv.Model = new SortedTreeModel(cloudPublicFolder_model);
            if (cloudPublicFolder_treeViewAdv.Root.Children.Count > 0)
                cloudPublicFolder_treeViewAdv.Root.Children[0].Expand();
            cloudPublicFolder_treeViewAdv.AutoSizeColumn(cloudPublicFolder_treeViewAdv.Columns[0]);
        }

        private void ExpandAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            cloudPublicFolder_treeViewAdv.ExpandAll();
            cloudPublicFolder_treeViewAdv.AutoSizeColumn(cloudPublicFolder_treeViewAdv.Columns[0]);
        }

        private void CheckNoneToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (cloudPublicFolder_model.Nodes.Count == 0
                || cloudPublicFolder_model.Nodes[0] is not ColumnNode rootNode)
                return;

            rootNode.IsChecked = false;
            CheckAllSubnodes(rootNode, true);
            checkedFilesSize = checkedFilesNumber = 0;
            checkedFiles_label.Text = $"{checkedFilesNumber} files • {Math.Round(checkedFilesSize * b2Mb, 2)} MB";
            cloudPublicFolder_treeViewAdv.Refresh();
        }

        private void CheckAllToolStripMenuItem_Click(object? sender, EventArgs e)
        {
            if (cloudPublicFolder_model.Nodes.Count == 0
                || cloudPublicFolder_model.Nodes[0] is not ColumnNode rootNode)
                return;

            rootNode.IsChecked = true;
            CheckAllSubnodes(rootNode, false);
            checkedFilesSize = Model.CloudPublicFolder.Size;
            checkedFilesNumber = Model.CloudPublicFolder.FilesNumber;
            checkedFiles_label.Text = $"{checkedFilesNumber} files • {Math.Round(checkedFilesSize * b2Mb, 2)} MB";
            cloudPublicFolder_treeViewAdv.Refresh();
        }


        private void treeViewAdv_Expanded(object sender, TreeViewAdvEventArgs e)
        {
            if (!e.Node.CanExpand)
                return;
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[0]);
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[3]);
            //e.Node.Tree.Columns[0].Width += (int) Math.Round(e.Node.Tree.Columns[0].Width * 0.3, 0);
            //e.Node.Tree.Columns[3].Width += 10;
        }

        private void treeViewAdv_Collapsed(object sender, TreeViewAdvEventArgs e)
        {
            if (!e.Node.CanExpand)
                return;
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[0], false);
            e.Node.Tree.AutoSizeColumn(e.Node.Tree.Columns[3], false);
            //e.Node.Tree.Columns[0].Width += (int)Math.Round(e.Node.Tree.Columns[0].Width * 0.3, 0);
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

        private bool filter(object obj)
        {
            TreeNodeAdv? viewNode = obj as TreeNodeAdv;
            Node? node = viewNode != null ? viewNode.Tag as Node : obj as Node;
            if (node == null)
                return false;

            bool matchesName = node.Text?.Contains(
                filter_textBox.Text,
                StringComparison.CurrentCultureIgnoreCase) == true
                || node.Nodes.Any(filter);

            if (node is not ColumnNode columnNode
                || !DateTime.TryParse(columnNode.NodeControl3, out DateTime modified))
            {
                return matchesName;
            }

            bool matchesDate = modified.Date >= afterDate_dateTimePicker.Value.Date
                && modified.Date <= beforeDate_dateTimePicker.Value.Date;
            return matchesName && matchesDate;
        }

        #endregion

        private void Filter_textBox_TextChanged(object sender, EventArgs e)
        {
            cloudPublicFolder_treeViewAdv.UpdateNodeFilter();
        }

        private void filter_TextChangedComplete(object? sender, EventArgs e)
        {
            cloudPublicFolder_treeViewAdv.UpdateNodeFilter();
        }

        private void PublicFolders_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (publicFolders_comboBox.SelectedItem is not KeyValuePair<string, string> selectedItem)
                return;

            publicFolderKey_textBox.Text = selectedItem.Value;
            hotDictKey = selectedItem.Key;
        }

        private void FlatList_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            if (flatList_checkBox.Checked)
            {
                cloudPublicFolder_treeViewAdv.ShowNodeToolTips = true;
                cloudPublicFolder_treeViewAdv.Model = new SortedTreeModel(cloudFlatFolder_model);
                if (cloudPublicFolder_treeViewAdv.Root.Children.Count > 0)
                    cloudPublicFolder_treeViewAdv.Root.Children[0].Expand();
            }
            else
            {
                cloudPublicFolder_treeViewAdv.ShowNodeToolTips = false;
                //yadiskPublicFolder_treeViewAdv.NodeControls[2].ToolTipProvider = null;
                cloudPublicFolder_treeViewAdv.Model = new SortedTreeModel(cloudPublicFolder_model);
                if (cloudPublicFolder_treeViewAdv.Root.Children.Count > 0)
                    cloudPublicFolder_treeViewAdv.Root.Children[0].Expand();
            }
        }

        #region #BUTTONS     

        private async void LoadPublicFolderKey_button_Click(object sender, EventArgs e)
        {
            if (!loadPublicFolderKey_button.Enabled)
                return;
            loadPublicFolderKey_button.Enabled = false;
            string cloudFolderUrl = publicFolderKey_textBox.Text;

            syncFolders_button.Enabled = false;

            if (flatList_checkBox.Checked)
                flatList_checkBox.Checked = false;

            try
            {
                if (cloudFolderUrl != "")
                {
                    var success = await LoadPublicFolder(cloudFolderUrl);
                    if (success)
                    {
                        syncFolders_button.Enabled = syncFolder != null;
                        flatList_checkBox.Enabled = true;
                        publicFolderKey_textBox.ReadOnly = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to connect to share: {ex}\n", true);
                MessageBox.Show(
                    "Unable to connect to this share: " + ex.Message,
                    "Connection failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                loadPublicFolderKey_button.Enabled = true;
            }
        }

        private void SavePublicFolderKey_button_Click(object sender, EventArgs e)
        {
            if (!publicFolders.Keys.Contains(hotDictKey))
                return;

            string newKey = publicFolders_comboBox.Text.Trim();
            if (!hotDictKey.Equals(newKey, StringComparison.OrdinalIgnoreCase)
                && publicFolders.Keys.Any(existing => existing.Equals(newKey, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Folder name should be unique!");
                return;
            }

            publicFolders.Remove(hotDictKey);
            publicFolders[newKey] = publicFolderKey_textBox.Text;

            publicFolders_comboBox.DataSource = new BindingSource { DataSource = publicFolders };
            publicFolders_comboBox.Update();

            UpdatePublicFoldersSetting();
            publicFolderKey_textBox.ReadOnly = true;
        }

        private void addPublicFolder_button_Click(object sender, EventArgs e)
        {
            var form = new EditLinkForm();
            var dr = form.ShowDialog();
            if (dr != DialogResult.OK)
                return;

            if (publicFolders.Keys.Any(existing => existing.Equals(form.LinkName, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Folder name should be unique!");
                return;
            }

            publicFolders.Add(form.LinkName, form.LinkUrl);

            publicFolders_comboBox.DataSource = new BindingSource { DataSource = publicFolders };
            UpdatePublicFoldersSetting();
        }

        private void editPublicFolderKey_button_Click(object sender, EventArgs e)
        {
            var form = new EditLinkForm(publicFolders_comboBox.Text, publicFolderKey_textBox.Text);
            var dr = form.ShowDialog();
            if (dr != DialogResult.OK)
                return;

            var key = form.LinkName;

            if (!publicFolders.Keys.Contains(hotDictKey))
                return;

            if (!hotDictKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                && publicFolders.Keys.Any(existing => existing.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Folder name should be unique!");
                return;
            }

            if (hotDictKey != key)
            {
                publicFolders.Remove(hotDictKey);
                publicFolders.Add(key, form.LinkUrl);
            }
            else
            {
                publicFolders[key] = form.LinkUrl;
            }

            publicFolders_comboBox.DataSource = new BindingSource { DataSource = publicFolders };
            UpdatePublicFoldersSetting();
        }

        private void deletePublicFolder_button_Click(object sender, EventArgs e)
        {
            DialogResult dialogResult = MessageBox.Show("Delete " + publicFolders_comboBox.Text + "?", "", MessageBoxButtons.YesNo);
            if (dialogResult == DialogResult.Yes)
            {
                publicFolders.Remove(publicFolders_comboBox.Text);
                publicFolders_comboBox.DataSource = new BindingSource { DataSource = publicFolders };
                UpdatePublicFoldersSetting();
            }
        }

        private void loginMega_button_Click(object sender, EventArgs e)
        {
            using var accountManager = new AccountManagerForm(this, accountStore);
            accountManager.ShowDialog(this);
            UpdateAccountButtonState();
        }

        private void showSyncForm_button_Click(object sender, EventArgs e)
        {
            activeSyncForm?.Show();
        }

        private void createArchive_button_Click(object sender, EventArgs e)
        {
        }

        private void appVersion_linkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = ApplicationBrand.LatestReleaseUrl, UseShellExecute = true });
        }

        private void fogLink_button_Click(object sender, EventArgs e)
        {
            var form = new FogLinkForm();
            form.Show();
        }


        private async void syncFolders_button_Click(object sender, EventArgs e)
        {
            if (folderComparisonCancellation != null)
            {
                SetComparisonStatus("Cancelling comparison…");
                syncFolders_button.Enabled = false;
                folderComparisonCancellation.Cancel();
                return;
            }

            if (!syncFolders_button.Enabled)
                return;
            syncFolders_button.Enabled = false;
            activeSyncForm?.CloseForm();
            try
            {
                checkedFolders = new List<CloudFolder>();
                mixedFolders = new List<CloudFolder>();
                if (cloudPublicFolder_model.Nodes.Count == 0
                    || cloudPublicFolder_model.Nodes[0] is not ColumnNode rootNode)
                {
                    MessageBox.Show("Connect a share before comparing folders.");
                    return;
                }

                GetCheckedFolders(rootNode);
                await SyncFiles();
                showSyncForm_button.Enabled = activeSyncForm != null;
            }
            catch (OperationCanceledException)
            {
                SetComparisonStatus("Comparison cancelled");
            }
            catch (Exception ex)
            {
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to compare folders: {ex}\n", true);
                MessageBox.Show(
                    "Unable to compare folders: " + ex.Message,
                    "Comparison failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                folderComparisonCancellation?.Dispose();
                folderComparisonCancellation = null;
                syncFolders_button.Text = "Compare folders";
                syncFolders_button.Tag = "primary";
                syncFolders_button.Enabled = syncFolder != null && Model.CloudPublicFolder.Path != null;
            }
        }

        private void browseSyncFolder_button_Click(object sender, EventArgs e)
        {
            syncFolderPath = GetFolderPath();
            if (syncFolderPath == "")
                return;
            syncFolderPath_textBox.Text = syncFolderPath;
            Properties.Settings.Default.lastSyncFolderPath = syncFolderPath;
            Properties.Settings.Default.Save();
        }

        private void SaveToFile_button_Click(object sender, EventArgs e)
        {
            string sourceUrl = Model.CloudPublicFolder.OriginalString;
            if (string.IsNullOrWhiteSpace(sourceUrl))
                sourceUrl = publicFolderKey_textBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(sourceUrl) || Model.CloudPublicFolder.Path == null)
            {
                MessageBox.Show(
                    "Connect to a cloud share before saving its file list.",
                    "Nothing to save",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Model.CloudPublicFolder.OriginalString = sourceUrl;
                Model.CloudPublicFolder.SaveToJson();
                MessageBox.Show(
                    "The cloud file list was saved.",
                    "List saved",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Model.WriteToLog($"\n{DateTime.Now:O}\nUnable to save folder list: {ex}\n", true);
                MessageBox.Show(
                    "Unable to save the cloud file list: " + ex.Message,
                    "Save failed",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        private async void LoadFromFile_button_Click(object sender, EventArgs e)
        {
            await LoadFolderJson(true);
        }
        #endregion

        #endregion

        private void enableProgressPanel_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            UseProgressPanel = enableProgressPanel_checkBox.Checked;
            Properties.Settings.Default.useProgressBar = UseProgressPanel;
            Properties.Settings.Default.Save();
        }
    }
}

