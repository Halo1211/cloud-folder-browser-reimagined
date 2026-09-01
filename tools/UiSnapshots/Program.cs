using System.Drawing.Imaging;
using CloudFolderBrowser;
using CloudFolderBrowser.FormsSecondary;
using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Theming;
using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        ThemeManager.SetMode(AppThemeMode.Dark);

        Environment.SetEnvironmentVariable("CFB_UI_SNAPSHOT", "1");

        string outputDirectory = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.GetFullPath(Path.Combine("artifacts", "ui-snapshots"));
        Directory.CreateDirectory(outputDirectory);

        using var main = new MainForm();
        SetText(main, "publicFolderKey_textBox", "https://example.invalid/public-share");
        SetText(main, "syncFolderPath_textBox", @"C:\Downloads\Cloud files");
        Capture(main, outputDirectory, "01-main-window.png");
        ThemeManager.SetMode(AppThemeMode.Light);
        Capture(main, outputDirectory, "01b-main-window-light.png");
        ThemeManager.SetMode(AppThemeMode.Dark);

        using var settings = new SyncSettingsForm(null);
        Capture(settings, outputDirectory, "02-download-network-settings.png");
        TabControl? settingsTabs = FindControls<TabControl>(settings).FirstOrDefault();
        if (settingsTabs != null && settingsTabs.TabPages.Count > 1)
        {
            settingsTabs.SelectedIndex = 1;
            Capture(settings, outputDirectory, "02b-dns-proxy-settings.png");
        }
        ThemeManager.SetMode(AppThemeMode.Light);
        using (var lightSettings = new SyncSettingsForm(null))
        {
            Capture(lightSettings, outputDirectory, "02c-download-network-settings-light.png");
            TabControl? lightSettingsTabs = FindControls<TabControl>(lightSettings).FirstOrDefault();
            if (lightSettingsTabs != null && lightSettingsTabs.TabPages.Count > 1)
            {
                lightSettingsTabs.SelectedIndex = 1;
                Capture(lightSettings, outputDirectory, "02d-dns-proxy-settings-light.png");
            }
        }
        ThemeManager.SetMode(AppThemeMode.Dark);

        using var proxyRules = new ProviderProxyRulesForm();
        Capture(proxyRules, outputDirectory, "02e-provider-proxy-rules.png");
        using var diagnostics = new NetworkDiagnosticsForm(new NetworkDiagnosticResult(
            new[] { IPAddress.Parse("93.184.216.34"), IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946") },
            TimeSpan.FromMilliseconds(18), TimeSpan.FromMilliseconds(142), HttpStatusCode.OK,
            HttpVersion.Version20, "system proxy", "Cloudflare via Https", false,
            SslProtocols.Tls13, TlsCipherSuite.TLS_AES_128_GCM_SHA256, "CN=example.com",
            "CN=Example Certificate Authority", DateTime.UtcNow.AddDays(60)));
        Capture(diagnostics, outputDirectory, "02f-network-diagnostics.png");
        ThemeManager.SetMode(AppThemeMode.Light);
        using (var lightProxyRules = new ProviderProxyRulesForm())
            Capture(lightProxyRules, outputDirectory, "02g-provider-proxy-rules-light.png");
        using (var lightDiagnostics = new NetworkDiagnosticsForm(new NetworkDiagnosticResult(
            new[] { IPAddress.Parse("93.184.216.34") }, TimeSpan.FromMilliseconds(18), TimeSpan.FromMilliseconds(142),
            HttpStatusCode.OK, HttpVersion.Version20, "direct", "Windows system resolver", false,
            SslProtocols.Tls13, TlsCipherSuite.TLS_AES_128_GCM_SHA256, "CN=example.com",
            "CN=Example Certificate Authority", DateTime.UtcNow.AddDays(60))))
            Capture(lightDiagnostics, outputDirectory, "02h-network-diagnostics-light.png");
        ThemeManager.SetMode(AppThemeMode.Dark);
        using var providerHealth = new ProviderHealthForm();
        Capture(providerHealth, outputDirectory, "02i-provider-health.png");
        ThemeManager.SetMode(AppThemeMode.Light);
        using (var lightProviderHealth = new ProviderHealthForm())
            Capture(lightProviderHealth, outputDirectory, "02j-provider-health-light.png");
        ThemeManager.SetMode(AppThemeMode.Dark);

        using var fogLink = new FogLinkForm();
        SetText(fogLink, "in_textBox", "https://example.invalid/source-link");
        SetText(fogLink, "out_textBox", "Encoded links appear here");
        Capture(fogLink, outputDirectory, "03-foglink.png");

        using var megaLogin = new LoginMegaForm(main);
        SetText(megaLogin, "login_textBox", string.Empty);
        SetText(megaLogin, "password_textBox", string.Empty);
        Capture(megaLogin, outputDirectory, "04-mega-login.png");

        string accountPreviewDirectory = Path.Combine(Path.GetTempPath(), "cfb-ui-accounts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(accountPreviewDirectory);
        var accountStore = new CloudAccountStore(Path.Combine(accountPreviewDirectory, "accounts.json"));
        accountStore.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.Mega,
            DisplayName = "Primary MEGA",
            UserName = "user@example.com",
            Secret = "sample-session",
            SecretKind = "mega-session"
        });
        accountStore.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.WebDav,
            DisplayName = "Team Nextcloud",
            UserName = "albert",
            ServerUrl = "https://cloud.example.com/remote.php/dav/files/albert/",
            Secret = "sample-password",
            SecretKind = "password"
        });
        accountStore.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.AllDebrid,
            DisplayName = "Fast downloads",
            Secret = "sample-api-key",
            SecretKind = "api-token"
        });
        accountStore.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.Premiumize,
            DisplayName = "Premiumize route",
            Secret = "sample-api-key",
            SecretKind = "api-token"
        });
        accountStore.Upsert(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "TeraBox route",
            Secret = "sample-api-key",
            SecretKind = "api-token"
        });
        using var accounts = new AccountManagerForm(main, accountStore);
        Capture(accounts, outputDirectory, "05-cloud-accounts.png");

        using var accountEditor = new CloudAccountEditorForm(new CloudAccountProfile
        {
            Provider = CloudAccountProvider.TorBox,
            DisplayName = "TeraBox route",
            Secret = "sample-token",
            SecretKind = "oauth-token"
        });
        Capture(accountEditor, outputDirectory, "06-cloud-account-editor.png");

        using var downloads = new DownloadManagerForm();
        Capture(downloads, outputDirectory, "07-download-history.png");

        CloudFolder previewFolder = BuildPreviewFolder();
        using var compareReview = new SyncPreviewForm(BuildPreviewItems(previewFolder));
        Capture(compareReview, outputDirectory, "08-compare-review.png");

        var incomingFolder = new CloudFolder("Incoming", DateTime.Now, DateTime.Now, 0)
        {
            Path = "/Incoming/"
        };
        var previewModel = new MainFormModel
        {
            CloudServiceType = CloudServiceType.Other,
            CloudPublicFolder = previewFolder,
            AllFolders = new List<CloudFolder> { previewFolder, incomingFolder }
        };
        using var syncResults = new SyncFilesForm(
            main,
            previewFolder,
            previewModel,
            Path.GetTempPath(),
            accountStore);
        SetComboSelection(syncResults, "downloadRouteComboBox", 1);
        Capture(syncResults, outputDirectory, "09-compare-sync-results.png");
        SetActiveTransferPreview(syncResults);
        Capture(syncResults, outputDirectory, "15-active-transfer.png");
        syncResults.CloseForm();

        using var editShare = new EditLinkForm("Example share", "https://example.invalid/public-share");
        Capture(editShare, outputDirectory, "10-edit-cloud-share.png");

        using var password = new PasswordForm();
        Capture(password, outputDirectory, "11-share-password.png");

        using var completed = new DownloadsFinishedForm(
            @"C:\Downloads\Cloud files",
            "Downloads completed",
            "1 file could not be downloaded.");
        Capture(completed, outputDirectory, "12-download-complete.png");

        using var error = new ErrorForm("Connection error", "The selected cloud share could not be opened. Check the URL and try again.");
        Capture(error, outputDirectory, "13-error-dialog.png");

        using var errorLog = new ErrorLogForm();
        errorLog.AddErrorLine("Example provider response could not be parsed.");
        Capture(errorLog, outputDirectory, "14-error-log.png");

        try
        {
            Directory.Delete(accountPreviewDirectory, true);
        }
        catch
        {
        }
    }

    private static IReadOnlyList<SyncPlanItem> BuildPreviewItems(CloudFolder folder)
    {
        CloudFile[] files = folder.Files.ToArray();
        string localRoot = Path.Combine(Path.GetTempPath(), "Cloud files", "Incoming");
        return new[]
        {
            new SyncPlanItem(files[0], SyncDifference.Missing, SyncPlanAction.Download,
                "File is missing locally", Path.Combine(localRoot, files[0].Name)),
            new SyncPlanItem(files[1], SyncDifference.SizeMismatch, SyncPlanAction.Overwrite,
                "Size differs: local 6,100,000, cloud 6,700,000 bytes", Path.Combine(localRoot, files[1].Name)),
            new SyncPlanItem(files[2], SyncDifference.UpToDate, SyncPlanAction.Skip,
                "File size matches", Path.Combine(localRoot, files[2].Name))
        };
    }

    private static CloudFolder BuildPreviewFolder()
    {
        DateTime now = DateTime.Now;
        var root = new CloudFolder("Example share", now, now, 0)
        {
            Path = "/",
            OriginalString = "https://example.invalid/public-share"
        };

        AddPreviewFile(root, "Project archive.zip", 48_300_000, now.AddHours(-2));
        AddPreviewFile(root, "Reference manual.pdf", 6_700_000, now.AddDays(-1));
        AddPreviewFile(root, "Photos backup.tar", 128_000_000, now.AddDays(-3));
        root.SizeTopDirectoryOnly = root.Files.Sum(file => file.Size);
        root.CalculateFolderSize();
        return root;
    }

    private static void AddPreviewFile(CloudFolder folder, string name, long size, DateTime modified)
    {
        folder.AddFile(new CloudFile(name, modified.AddDays(-5), modified, size)
        {
            Path = "/Incoming/" + name,
            PublicUrl = new Uri("https://example.invalid/files/" + Uri.EscapeDataString(name)),
            PlannedAction = SyncPlanAction.Download
        });
    }

    private static void SetText(Control root, string controlName, string value)
    {
        Control? control = root.Controls.Find(controlName, true).FirstOrDefault();
        if (control != null)
            control.Text = value;
    }

    private static void SetComboSelection(Control root, string controlName, int index)
    {
        if (root.Controls.Find(controlName, true).FirstOrDefault() is ComboBox combo
            && index >= 0
            && index < combo.Items.Count)
        {
            combo.SelectedIndex = index;
        }
    }

    private static void SetActiveTransferPreview(Control root)
    {
        SetText(root, "DownloadProgress_label", "0/3 files finished");
        SetText(root, "downloadFiles_button", "Downloading…");
        if (root.Controls.Find("downloadFiles_button", true).FirstOrDefault() is Button download)
            download.Enabled = false;
        if (root.Controls.Find("stopDownload_button", true).FirstOrDefault() is Button pause)
        {
            pause.Text = "Pause downloads";
            pause.Visible = true;
            pause.Enabled = true;
        }

        string[] labels =
        {
            "62.0% • 4.8 MB/s • ETA 0:04 • Project archive.zip",
            "18.0% • 860 KB/s • ETA 0:06 • Reference manual.pdf",
            "Resolving with TorBox: Photos backup.tar"
        };
        int[] values = { 62, 18, 0 };
        for (int index = 0; index < labels.Length; index++)
        {
            if (root.Controls.Find($"label{index + 1}", true).FirstOrDefault() is Label label)
            {
                label.Text = labels[index];
                label.Visible = true;
            }
            if (root.Controls.Find($"progressBar{index + 1}", true).FirstOrDefault() is ProgressBar progress)
            {
                progress.Value = values[index];
                progress.Visible = true;
            }
        }
    }

    private static void Capture(Form form, string outputDirectory, string fileName)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-32000, -32000);
        form.ShowInTaskbar = false;
        if (!form.Visible)
            form.Show();
        ThemeManager.Apply(form);
        CreateHandles(form);
        form.PerformLayout();
        Application.DoEvents();

        if (form is LoginMegaForm)
        {
            SetText(form, "login_textBox", string.Empty);
            SetText(form, "password_textBox", string.Empty);
        }
        if (form is DownloadManagerForm)
        {
            DataGridView? grid = FindControls<DataGridView>(form).FirstOrDefault();
            if (grid != null)
                grid.DataSource = null;
        }
        form.PerformLayout();
        form.Refresh();
        Application.DoEvents();

        Size size = form.Size;
        using var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
        bitmap.Save(Path.Combine(outputDirectory, fileName), ImageFormat.Png);
    }

    private static IEnumerable<TControl> FindControls<TControl>(Control root)
        where TControl : Control
    {
        foreach (Control child in root.Controls)
        {
            if (child is TControl match)
                yield return match;
            foreach (TControl descendant in FindControls<TControl>(child))
                yield return descendant;
        }
    }

    private static void CreateHandles(Control control)
    {
        control.CreateControl();
        foreach (Control child in control.Controls)
            CreateHandles(child);
    }
}
