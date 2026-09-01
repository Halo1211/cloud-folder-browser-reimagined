using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Theming;
using CloudFolderBrowser.Providers;

namespace CloudFolderBrowser.FormsSecondary
{
    public partial class SyncSettingsForm : Theming.ThemedForm
    {
        private readonly CheckBox verifySha256_checkBox = new();
        private readonly ComboBox preferredDebridRoute_comboBox = new();
        private readonly Button manageDebridAccounts_button = new();
        private readonly NumericUpDown bandwidthLimit_numericUpDown = new();
        private readonly NumericUpDown maximumSegments_numericUpDown = new();
        private readonly CheckBox segmentedDownloads_checkBox = new();
        private readonly NumericUpDown maximumDownloadsPerHost_numericUpDown = new();
        private readonly CheckBox archivePostProcessing_checkBox = new();
        private readonly CheckBox par2Repair_checkBox = new();
        private readonly TextBox archiveToolPath_textBox = new();
        private readonly TextBox par2ToolPath_textBox = new();
        private readonly TextBox archivePassword_textBox = new();
        private readonly ComboBox dnsProvider_comboBox = new();
        private readonly ComboBox dnsTransport_comboBox = new();
        private readonly TextBox customDnsServer_textBox = new();
        private readonly CheckBox dnsFallback_checkBox = new();
        private readonly CheckBox preferIpv6_checkBox = new();
        private readonly ComboBox proxyMode_comboBox = new();
        private readonly TextBox proxyUrl_textBox = new();
        private readonly TextBox proxyUsername_textBox = new();
        private readonly TextBox proxyPassword_textBox = new();
        private readonly CheckBox proxyBypassLocal_checkBox = new();
        private readonly Button manageProxyRules_button = new();
        private readonly NumericUpDown connectTimeout_numericUpDown = new();
        private readonly Button testNetwork_button = new();
        private readonly Button providerHealth_button = new();
        private readonly Button clearDnsCache_button = new();
        private readonly Label networkStatus_label = new();
        private readonly SyncFilesForm? _parentForm;

        private sealed class DebridRouteSetting
        {
            public string RouteId { get; init; } = DownloadRouteIds.Direct;
            public Guid? AccountId { get; init; }
            public string DisplayName { get; init; } = string.Empty;
            public override string ToString() => DisplayName;
        }
        public SyncSettingsForm(SyncFilesForm? parentForm)
        {
            _parentForm = parentForm;

            InitializeComponent();

            overwriteMode_comboBox.DataSource = new BindingSource { DataSource = overwriteModes };
            overwriteMode_comboBox.DisplayMember = "Value";
            overwriteMode_comboBox.ValueMember = "Key";

            StartPosition = FormStartPosition.CenterParent;
            ConfigureModernUi();

            toolTip1.SetToolTip(checkDownloadedFileSize_checkBox, "Check file size on disk and in cloud if checked. Redownload if mismatch");
            toolTip1.SetToolTip(verifySha256_checkBox, "Store SHA-256 after download and detect later local corruption");
            toolTip1.SetToolTip(checkFileSizeError_numericUpDown, "Margin of error between file sizes. Value of 1.0 means sizes should be identical");
            toolTip1.SetToolTip(retryDelay_numericUpDown, "Delay in ms between download retries");
            toolTip1.SetToolTip(maxDownloadRetries_numericUpDown, "Maximum amount of download retries before file is skipped");
            toolTip1.SetToolTip(folderNewFiles_checkBox, "Will download files into separate <New Files DATE> folder");
            toolTip1.SetToolTip(overwriteMode_comboBox, "Default behavior if file with same name already exists on disk");
            toolTip1.SetToolTip(flareSolverrEnabled_checkBox, "Use FlareSolverr to obtain Cloudflare cookies and a matching browser User-Agent");
            toolTip1.SetToolTip(flareSolverrUrl_textBox, "FlareSolverr base URL; /v1 is added automatically");
            toolTip1.SetToolTip(preferredDebridRoute_comboBox, "Default route used in Sync results. It can still be changed for each transfer.");
            toolTip1.SetToolTip(bandwidthLimit_numericUpDown, "Global download limit in KiB/s. Set to 0 for unlimited.");
            toolTip1.SetToolTip(maximumSegments_numericUpDown, "Maximum HTTP range connections per large file. Automatic fallback uses one connection when ranges are unsafe.");
            toolTip1.SetToolTip(dnsProvider_comboBox, "Resolver used by app HTTP connections. System keeps the Windows DNS configuration.");
            toolTip1.SetToolTip(dnsTransport_comboBox, "Plain UDP, encrypted DNS-over-HTTPS, or encrypted DNS-over-TLS.");
            toolTip1.SetToolTip(customDnsServer_textBox, "DNS server IP with an optional port, for example 192.168.1.1 or 127.0.0.1:5353.");
            toolTip1.SetToolTip(proxyUrl_textBox, "HTTP(S) or SOCKS proxy URL. Supported examples: http://127.0.0.1:8080 and socks5://127.0.0.1:1080.");
            toolTip1.SetToolTip(clearDnsCache_button, "Clear cached custom DNS results.");
            toolTip1.SetToolTip(testNetwork_button, "Resolve example.com and test HTTPS through the selected DNS and proxy route.");
            toolTip1.SetToolTip(providerHealth_button, "Check each provider endpoint through its configured DNS and proxy route.");
            manageDebridAccounts_button.Click += ManageDebridAccounts_Click;
            dnsProvider_comboBox.SelectedIndexChanged += (_, _) => UpdateNetworkControls();
            dnsTransport_comboBox.SelectedIndexChanged += (_, _) => UpdateNetworkControls();
            proxyMode_comboBox.SelectedIndexChanged += (_, _) => UpdateNetworkControls();
            segmentedDownloads_checkBox.CheckedChanged += (_, _) => UpdateAdvancedControls();
            archivePostProcessing_checkBox.CheckedChanged += (_, _) => UpdateAdvancedControls();
            par2Repair_checkBox.CheckedChanged += (_, _) => UpdateAdvancedControls();
            testNetwork_button.Click += TestNetwork_Click;
            providerHealth_button.Click += ProviderHealth_Click;
            manageProxyRules_button.Click += (_, _) =>
            {
                using var rules = new ProviderProxyRulesForm();
                rules.ShowDialog(this);
            };
            clearDnsCache_button.Click += (_, _) =>
            {
                CustomDnsResolver.ClearCache();
                SecureDnsResolver.ClearCache();
                networkStatus_label.Text = "DNS cache cleared";
                networkStatus_label.Tag = "success-text";
                networkStatus_label.ForeColor = ThemeManager.Palette.Success;
            };
        }

        Dictionary<int, string> overwriteModes = new Dictionary<int, string>()
        {
            {0, "None" },
            {1, "Overwrite all" },
            {2, "Overwrite older"},
            {3, "Ask" }
        };

        private void maximumDownloads_numericUpDown_ValueChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void overwriteMode_comboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void folderNewFiles_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void maxDownloadRetries_numericUpDown_ValueChanged(object sender, EventArgs e)
        {
           
            Properties.Settings.Default.Save();
        }

        private void retryDelay_numericUpDown_ValueChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void checkFileSizeError_numericUpDown_ValueChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void checkDownloadedFileSize_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            
            Properties.Settings.Default.Save();
        }

        private void SyncSettingsForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            try
            {
                PersistSettingsFromControls();
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                e.Cancel = true;
                MessageBox.Show(this, ex.Message, "Invalid network settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        private void PersistSettingsFromControls()
        {
            NetworkConfiguration networkConfiguration = BuildNetworkConfigurationFromControls();
            networkConfiguration.Validate();
            Properties.Settings.Default.maximumDownloads = (int)maximumDownloads_numericUpDown.Value;
            Properties.Settings.Default.maximumSegmentsPerFile = (int)maximumSegments_numericUpDown.Value;
            Properties.Settings.Default.segmentedDownloadsEnabled = segmentedDownloads_checkBox.Checked;
            Properties.Settings.Default.maximumDownloadsPerHost = (int)maximumDownloadsPerHost_numericUpDown.Value;
            Properties.Settings.Default.archivePostProcessingEnabled = archivePostProcessing_checkBox.Checked;
            Properties.Settings.Default.par2RepairEnabled = par2Repair_checkBox.Checked;
            Properties.Settings.Default.archiveToolPath = archiveToolPath_textBox.Text.Trim();
            Properties.Settings.Default.par2ToolPath = par2ToolPath_textBox.Text.Trim();
            Properties.Settings.Default.protectedArchivePassword =
                ProxySecretProtector.Protect(archivePassword_textBox.Text);
            Properties.Settings.Default.overwriteMode = (int)overwriteMode_comboBox.SelectedIndex;
            Properties.Settings.Default.folderNewFiles = folderNewFiles_checkBox.Checked;
            Properties.Settings.Default.retryMax = (int)maxDownloadRetries_numericUpDown.Value;
            Properties.Settings.Default.checkFileSizeError = (double)checkFileSizeError_numericUpDown.Value;
            Properties.Settings.Default.checkDownloadedFileSize = checkDownloadedFileSize_checkBox.Checked;
            Properties.Settings.Default.retryDelay = (int)retryDelay_numericUpDown.Value;
            Properties.Settings.Default.verifySha256 = verifySha256_checkBox.Checked;
            Properties.Settings.Default.flareSolverrEnabled = flareSolverrEnabled_checkBox.Checked;
            Properties.Settings.Default.flareSolverrUrl = flareSolverrUrl_textBox.Text.Trim();
            Properties.Settings.Default.flareSolverrTimeoutSeconds = (int)flareSolverrTimeout_numericUpDown.Value;
            Properties.Settings.Default.preferredDebridAccountId =
                preferredDebridRoute_comboBox.SelectedItem is DebridRouteSetting route
                    ? route.RouteId
                    : DownloadRouteIds.Automatic;
            Properties.Settings.Default.dnsProvider = (int)networkConfiguration.DnsProvider;
            Properties.Settings.Default.dnsTransport = (int)networkConfiguration.DnsTransport;
            Properties.Settings.Default.customDnsServer = networkConfiguration.CustomDnsServer;
            Properties.Settings.Default.dnsFallbackToSystem = networkConfiguration.FallbackToSystemDns;
            Properties.Settings.Default.preferIpv6 = networkConfiguration.PreferIpv6;
            Properties.Settings.Default.proxyMode = (int)networkConfiguration.ProxyMode;
            Properties.Settings.Default.proxyUrl = networkConfiguration.ProxyUrl;
            Properties.Settings.Default.proxyUsername = networkConfiguration.ProxyUsername;
            Properties.Settings.Default.protectedProxyPassword = ProxySecretProtector.Protect(networkConfiguration.ProxyPassword);
            Properties.Settings.Default.proxyBypassLocal = networkConfiguration.BypassProxyForLocal;
            Properties.Settings.Default.connectTimeoutSeconds = (int)networkConfiguration.ConnectTimeout.TotalSeconds;
            Properties.Settings.Default.bandwidthLimitKibPerSecond = (int)bandwidthLimit_numericUpDown.Value;
            Properties.Settings.Default.Save();
            FogLink.ReloadNetworkSettings();
            _parentForm?.UpdateSettings();
        }

        private void ExportBackup_Click(object? sender, EventArgs e)
        {
            try
            {
                PersistSettingsFromControls();
                using var password = new BackupPasswordForm(confirmPassword: true);
                if (password.ShowDialog(this) != DialogResult.OK)
                    return;
                using var dialog = new SaveFileDialog
                {
                    Title = "Export encrypted settings backup",
                    Filter = "Cloud Folder Browser backup (*.cfbackup)|*.cfbackup",
                    DefaultExt = "cfbackup",
                    AddExtension = true,
                    FileName = $"CloudFolderBrowser-backup-{DateTime.Now:yyyyMMdd}.cfbackup"
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                byte[] encrypted = SettingsBackupService.Encrypt(SettingsBackupService.Capture(), password.Password);
                File.WriteAllBytes(dialog.FileName, encrypted);
                MessageBox.Show(this, "Encrypted backup exported successfully.", "Backup complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show(this, ex.Message, "Could not export backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ImportBackup_Click(object? sender, EventArgs e)
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Import encrypted settings backup",
                Filter = "Cloud Folder Browser backup (*.cfbackup)|*.cfbackup|All files (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != DialogResult.OK)
                return;
            using var password = new BackupPasswordForm(confirmPassword: false);
            if (password.ShowDialog(this) != DialogResult.OK)
                return;
            try
            {
                SettingsBackupDocument backup = SettingsBackupService.Decrypt(File.ReadAllBytes(dialog.FileName), password.Password);
                DialogResult answer = MessageBox.Show(
                    this,
                    $"Import settings and merge {backup.Accounts?.Count ?? 0} account(s)? Existing accounts with matching IDs will be updated.",
                    "Import encrypted backup",
                    MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question);
                if (answer != DialogResult.OK)
                    return;
                SettingsBackupService.Apply(backup);
                ThemeManager.SetMode(Enum.IsDefined(typeof(AppThemeMode), Properties.Settings.Default.themeMode)
                    ? (AppThemeMode)Properties.Settings.Default.themeMode
                    : AppThemeMode.System);
                LoadDebridRoutes();
                LoadNetworkSettings();
                SyncSettingsForm_Load(this, EventArgs.Empty);
                ThemeManager.Apply(this);
                FogLink.ReloadNetworkSettings();
                _parentForm?.UpdateSettings();
                MessageBox.Show(this, "Backup imported. Settings and account list are now active.", "Import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                MessageBox.Show(this, ex.Message, "Could not import backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void SyncSettingsForm_Load(object sender, EventArgs e)
        {
            overwriteMode_comboBox.SelectedIndex = Math.Clamp(Properties.Settings.Default.overwriteMode, 0, overwriteMode_comboBox.Items.Count - 1);
            SetNumericValue(maximumDownloads_numericUpDown,
                Properties.Settings.Default.maximumDownloads > 0
                    ? Properties.Settings.Default.maximumDownloads
                    : 4);
            SetNumericValue(maximumSegments_numericUpDown,
                Properties.Settings.Default.maximumSegmentsPerFile > 0
                    ? Properties.Settings.Default.maximumSegmentsPerFile
                    : 4);
            segmentedDownloads_checkBox.Checked = Properties.Settings.Default.segmentedDownloadsEnabled;
            SetNumericValue(maximumDownloadsPerHost_numericUpDown,
                Properties.Settings.Default.maximumDownloadsPerHost > 0
                    ? Properties.Settings.Default.maximumDownloadsPerHost
                    : 2);
            archivePostProcessing_checkBox.Checked = Properties.Settings.Default.archivePostProcessingEnabled;
            par2Repair_checkBox.Checked = Properties.Settings.Default.par2RepairEnabled;
            archiveToolPath_textBox.Text = Properties.Settings.Default.archiveToolPath ?? string.Empty;
            par2ToolPath_textBox.Text = Properties.Settings.Default.par2ToolPath ?? string.Empty;
            archivePassword_textBox.Text = ProxySecretProtector.Unprotect(
                Properties.Settings.Default.protectedArchivePassword);

            checkDownloadedFileSize_checkBox.Checked = Properties.Settings.Default.checkDownloadedFileSize;
            verifySha256_checkBox.Checked = Properties.Settings.Default.verifySha256;
            SetNumericValue(checkFileSizeError_numericUpDown,
                Properties.Settings.Default.checkFileSizeError > 0
                    ? (decimal)Properties.Settings.Default.checkFileSizeError
                    : 0.999m);
            SetNumericValue(maxDownloadRetries_numericUpDown,
                Properties.Settings.Default.retryMax > 0
                    ? Properties.Settings.Default.retryMax
                    : 4);
            SetNumericValue(retryDelay_numericUpDown,
                Properties.Settings.Default.retryDelay > 0
                    ? Properties.Settings.Default.retryDelay
                    : 300);

            folderNewFiles_checkBox.Checked = Properties.Settings.Default.folderNewFiles;
            flareSolverrEnabled_checkBox.Checked = Properties.Settings.Default.flareSolverrEnabled;
            flareSolverrUrl_textBox.Text = string.IsNullOrWhiteSpace(Properties.Settings.Default.flareSolverrUrl)
                ? "http://127.0.0.1:8191"
                : Properties.Settings.Default.flareSolverrUrl;
            SetNumericValue(flareSolverrTimeout_numericUpDown, Properties.Settings.Default.flareSolverrTimeoutSeconds);
            LoadDebridRoutes();
            LoadNetworkSettings();
            UpdateFlareSolverrControls();
            UpdateAdvancedControls();
        }

        private void UpdateAdvancedControls()
        {
            maximumSegments_numericUpDown.Enabled = segmentedDownloads_checkBox.Checked;
            archiveToolPath_textBox.Enabled = archivePostProcessing_checkBox.Checked;
            archivePassword_textBox.Enabled = archivePostProcessing_checkBox.Checked;
            par2Repair_checkBox.Enabled = archivePostProcessing_checkBox.Checked;
            par2ToolPath_textBox.Enabled = archivePostProcessing_checkBox.Checked
                && par2Repair_checkBox.Checked;
        }

        private void LoadDebridRoutes()
        {
            var routes = new List<DebridRouteSetting>
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
            routes.AddRange(CloudAccountStore.Default.GetAll()
                .Where(account => account.IsActive
                    && CloudProviderRegistry.Default.SupportsLinkResolver(account.Provider))
                .OrderBy(account => account.ProviderName)
                .Select(account => new DebridRouteSetting
                {
                    AccountId = account.Id,
                    RouteId = DownloadRouteIds.ForAccount(account.Id),
                    DisplayName = $"{account.ProviderName} — {account.DisplayName}"
                }));
            preferredDebridRoute_comboBox.DataSource = routes;

            string saved = Properties.Settings.Default.preferredDebridAccountId;
            string selectedRouteId = string.IsNullOrWhiteSpace(saved)
                ? DownloadRouteIds.Automatic
                : Guid.TryParse(saved, out Guid legacyId)
                    ? DownloadRouteIds.ForAccount(legacyId)
                    : saved;
            int index = routes.FindIndex(route =>
                route.RouteId.Equals(selectedRouteId, StringComparison.OrdinalIgnoreCase));
            preferredDebridRoute_comboBox.SelectedIndex = index >= 0 ? index : 0;
        }

        private void ManageDebridAccounts_Click(object? sender, EventArgs e)
        {
            MainForm? mainForm = Owner as MainForm ?? _parentForm?.HostMainForm;
            if (mainForm == null)
            {
                MessageBox.Show(this, "Open Accounts from the main window to configure a debrid provider.");
                return;
            }

            using var accounts = new AccountManagerForm(mainForm, CloudAccountStore.Default);
            accounts.ShowDialog(this);
            LoadDebridRoutes();
        }

        private static void SetNumericValue(NumericUpDown control, decimal value)
        {
            control.Value = Math.Clamp(value, control.Minimum, control.Maximum);
        }

        private void flareSolverrEnabled_checkBox_CheckedChanged(object sender, EventArgs e)
        {
            UpdateFlareSolverrControls();
        }

        private void UpdateFlareSolverrControls()
        {
            var enabled = flareSolverrEnabled_checkBox.Checked;
            flareSolverrUrl_textBox.Enabled = enabled;
            flareSolverrTimeout_numericUpDown.Enabled = enabled;
            flareSolverrTest_button.Enabled = enabled;
            if (!enabled)
            {
                flareSolverrStatus_label.Tag = "muted";
                flareSolverrStatus_label.ForeColor = ThemeManager.Palette.MutedText;
                flareSolverrStatus_label.Text = "Direct mode is active";
            }
        }

        private async void flareSolverrTest_button_Click(object sender, EventArgs e)
        {
            flareSolverrTest_button.Enabled = false;
            flareSolverrStatus_label.Tag = "muted";
            flareSolverrStatus_label.ForeColor = ThemeManager.Palette.MutedText;
            flareSolverrStatus_label.Text = "Testing...";

            try
            {
                using var client = new FlareSolverrClient(
                    flareSolverrUrl_textBox.Text,
                    TimeSpan.FromSeconds((int)flareSolverrTimeout_numericUpDown.Value));
                var session = await client.GetSessionAsync(new Uri("https://example.com/"), CancellationToken.None);
                flareSolverrStatus_label.Tag = "success-text";
                flareSolverrStatus_label.ForeColor = ThemeManager.Palette.Success;
                flareSolverrStatus_label.Text = $"Ready ({session.Cookies.Count} cookies)";
            }
            catch (Exception ex)
            {
                flareSolverrStatus_label.Tag = "danger-text";
                flareSolverrStatus_label.ForeColor = ThemeManager.Palette.Danger;
                flareSolverrStatus_label.Text = "Failed: " + ex.Message;
                toolTip1.SetToolTip(flareSolverrStatus_label, ex.Message);
            }
            finally
            {
                flareSolverrTest_button.Enabled = flareSolverrEnabled_checkBox.Checked;
            }
        }

        private void LoadNetworkSettings()
        {
            dnsProvider_comboBox.Items.Clear();
            dnsProvider_comboBox.Items.AddRange(new object[]
            {
                "System (Windows)", "Cloudflare (1.1.1.1)", "Google (8.8.8.8)",
                "Quad9 (9.9.9.9)", "AdGuard (94.140.14.14)", "Custom"
            });
            dnsTransport_comboBox.Items.Clear();
            dnsTransport_comboBox.Items.AddRange(new object[]
            {
                "DNS (UDP)", "DNS-over-HTTPS", "DNS-over-TLS"
            });
            proxyMode_comboBox.Items.Clear();
            proxyMode_comboBox.Items.AddRange(new object[] { "Direct (no proxy)", "System proxy", "Custom proxy" });

            NetworkConfiguration configuration = NetworkConfiguration.FromSettings();
            dnsProvider_comboBox.SelectedIndex = (int)configuration.DnsProvider;
            dnsTransport_comboBox.SelectedIndex = (int)configuration.DnsTransport;
            customDnsServer_textBox.Text = string.IsNullOrWhiteSpace(configuration.CustomDnsServer)
                ? "1.1.1.1"
                : configuration.CustomDnsServer;
            dnsFallback_checkBox.Checked = configuration.FallbackToSystemDns;
            preferIpv6_checkBox.Checked = configuration.PreferIpv6;
            proxyMode_comboBox.SelectedIndex = (int)configuration.ProxyMode;
            proxyUrl_textBox.Text = configuration.ProxyUrl;
            proxyUsername_textBox.Text = configuration.ProxyUsername;
            proxyPassword_textBox.Text = configuration.ProxyPassword;
            proxyBypassLocal_checkBox.Checked = configuration.BypassProxyForLocal;
            SetNumericValue(connectTimeout_numericUpDown, (decimal)configuration.ConnectTimeout.TotalSeconds);
            SetNumericValue(bandwidthLimit_numericUpDown, Properties.Settings.Default.bandwidthLimitKibPerSecond);
            UpdateNetworkControls();
        }

        private NetworkConfiguration BuildNetworkConfigurationFromControls() => new()
        {
            DnsProvider = (DnsProviderMode)Math.Max(0, dnsProvider_comboBox.SelectedIndex),
            DnsTransport = (DnsTransport)Math.Max(0, dnsTransport_comboBox.SelectedIndex),
            CustomDnsServer = customDnsServer_textBox.Text.Trim(),
            FallbackToSystemDns = dnsFallback_checkBox.Checked,
            PreferIpv6 = preferIpv6_checkBox.Checked,
            ProxyMode = (ProxyMode)Math.Max(0, proxyMode_comboBox.SelectedIndex),
            ProxyUrl = proxyUrl_textBox.Text.Trim(),
            ProxyUsername = proxyUsername_textBox.Text.Trim(),
            ProxyPassword = proxyPassword_textBox.Text,
            BypassProxyForLocal = proxyBypassLocal_checkBox.Checked,
            ConnectTimeout = TimeSpan.FromSeconds((int)connectTimeout_numericUpDown.Value)
        };

        private void UpdateNetworkControls()
        {
            customDnsServer_textBox.Enabled = dnsProvider_comboBox.SelectedIndex == (int)DnsProviderMode.Custom;
            dnsTransport_comboBox.Enabled = dnsProvider_comboBox.SelectedIndex != (int)DnsProviderMode.System;
            customDnsServer_textBox.PlaceholderText = (DnsTransport)Math.Max(0, dnsTransport_comboBox.SelectedIndex) switch
            {
                DnsTransport.Https => "https://dns.example/dns-query",
                DnsTransport.Tls => "dns.example:853",
                _ => "IP address or IP:port"
            };
            bool customProxy = proxyMode_comboBox.SelectedIndex == (int)ProxyMode.Custom;
            proxyUrl_textBox.Enabled = customProxy;
            proxyUsername_textBox.Enabled = customProxy;
            proxyPassword_textBox.Enabled = customProxy;
            proxyBypassLocal_checkBox.Enabled = customProxy;
        }

        private async void TestNetwork_Click(object? sender, EventArgs e)
        {
            testNetwork_button.Enabled = false;
            networkStatus_label.Tag = "muted";
            networkStatus_label.ForeColor = ThemeManager.Palette.MutedText;
            networkStatus_label.Text = "Resolving example.com and testing HTTPS...";
            try
            {
                NetworkConfiguration configuration = BuildNetworkConfigurationFromControls();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                NetworkDiagnosticResult result = await AppHttpClientFactory.TestAsync(configuration, cts.Token);
                string addresses = string.Join(", ", result.Addresses.Take(3));
                networkStatus_label.Tag = "success-text";
                networkStatus_label.ForeColor = ThemeManager.Palette.Success;
                networkStatus_label.Text =
                    $"Ready — DNS {result.DnsElapsed.TotalMilliseconds:0} ms; HTTPS {(int)result.StatusCode} in {result.HttpElapsed.TotalMilliseconds:0} ms";
                using var report = new NetworkDiagnosticsForm(result);
                report.ShowDialog(this);
            }
            catch (Exception ex)
            {
                networkStatus_label.Tag = "danger-text";
                networkStatus_label.ForeColor = ThemeManager.Palette.Danger;
                networkStatus_label.Text = "Failed: " + ex.Message;
                toolTip1.SetToolTip(networkStatus_label, ex.ToString());
            }
            finally
            {
                testNetwork_button.Enabled = true;
            }
        }

        private void ProviderHealth_Click(object? sender, EventArgs e)
        {
            try
            {
                PersistSettingsFromControls();
                using var health = new ProviderHealthForm();
                health.ShowDialog(this);
            }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            {
                MessageBox.Show(this, ex.Message, "Invalid network settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
