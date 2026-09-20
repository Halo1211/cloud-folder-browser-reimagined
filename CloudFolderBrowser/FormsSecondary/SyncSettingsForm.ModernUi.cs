using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser.FormsSecondary
{
    public partial class SyncSettingsForm
    {
        private void ConfigureModernUi()
        {
            SuspendLayout();
            try
            {
                Controls.Clear();
                ClientSize = new Size(1040, 820);
                MinimumSize = new Size(980, 760);
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = false;
                Text = "Download and network settings";

                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount = 4,
                    Margin = new Padding(0)
                };
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 86F));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
                root.Controls.Add(BuildSettingsHeader(), 0, 0);
                root.Controls.Add(BuildDebridRouteBar(), 0, 1);
                root.Controls.Add(BuildSettingsContent(), 0, 2);
                root.Controls.Add(BuildSettingsFooter(), 0, 3);
                Controls.Add(root);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private Control BuildDebridRouteBar()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "surface",
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(20, 10, 20, 10),
                Margin = new Padding(16, 6, 16, 6)
            };
            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 4,
                RowCount = 1,
                Margin = new Padding(0)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 196F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 188F));
            row.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Default download route",
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            preferredDebridRoute_comboBox.Dock = DockStyle.Fill;
            preferredDebridRoute_comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            preferredDebridRoute_comboBox.Margin = new Padding(0, 6, 0, 6);
            row.Controls.Add(preferredDebridRoute_comboBox, 1, 0);
            manageDebridAccounts_button.Text = "Manage accounts";
            manageDebridAccounts_button.Tag = "primary";
            manageDebridAccounts_button.Dock = DockStyle.Fill;
            manageDebridAccounts_button.Margin = new Padding(0, 4, 0, 4);
            row.Controls.Add(manageDebridAccounts_button, 3, 0);
            panel.Controls.Add(row);
            return panel;
        }

        private static Panel BuildSettingsHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "header",
                Padding = new Padding(20, 0, 0, 0),
                Margin = new Padding(0)
            };
            header.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
                Location = new Point(20, 14),
                Text = "Download and network settings"
            });
            return header;
        }

        private Control BuildSettingsContent()
        {
            var tabs = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(14, 6),
                Margin = new Padding(16, 8, 16, 8)
            };
            var generalPage = new TabPage("Downloads") { Padding = new Padding(0), Tag = "surface" };
            generalPage.Controls.Add(BuildDownloadSettingsGrid());
            var connectionPage = new TabPage("DNS and proxy") { Padding = new Padding(0), Tag = "surface" };
            connectionPage.Controls.Add(BuildNetworkSettingsGrid());
            var advancedPage = new TabPage("Advanced") { Padding = new Padding(0), Tag = "surface" };
            advancedPage.Controls.Add(BuildAdvancedSettingsGrid());
            tabs.TabPages.Add(generalPage);
            tabs.TabPages.Add(connectionPage);
            tabs.TabPages.Add(advancedPage);
            return tabs;
        }

        private Control BuildAdvancedSettingsGrid()
        {
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(16, 16, 16, 12),
                Margin = new Padding(0)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            ModernCard transfer = CreateSettingsCard("Transfer engine", "Download concurrency and automatic reliability");
            transfer.Margin = new Padding(0, 0, 6, 0);
            transfer.Controls.Add(BuildAdvancedTransferBody());

            ModernCard postProcessing = CreateSettingsCard("Post-processing", "Optional extraction and recovery tools");
            postProcessing.Margin = new Padding(6, 0, 0, 0);
            postProcessing.Controls.Add(BuildPostProcessingBody());

            content.Controls.Add(transfer, 0, 0);
            content.Controls.Add(postProcessing, 1, 0);
            return content;
        }

        private Control BuildAdvancedTransferBody()
        {
            var body = CreateCardBody(3);
            AddAdvancedToggle(body, segmentedDownloads_checkBox, "Enable segmented downloads", 0);

            body.Controls.Add(CreateFieldLabel("Downloads per host"), 0, 1);
            maximumDownloadsPerHost_numericUpDown.Minimum = 1;
            maximumDownloadsPerHost_numericUpDown.Maximum = 32;
            maximumDownloadsPerHost_numericUpDown.Dock = DockStyle.Fill;
            maximumDownloadsPerHost_numericUpDown.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(maximumDownloadsPerHost_numericUpDown, 1, 1);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(390, 0),
                Tag = "muted",
                Margin = new Padding(0, 10, 0, 0),
                Text = "Resume data, checksum validation, route scoring, adaptive workers, and fair scheduling are applied automatically."
            };
            body.SetColumnSpan(note, 2);
            body.Controls.Add(note, 0, 2);
            return body;
        }

        private Control BuildPostProcessingBody()
        {
            var body = CreateCardBody(7);
            AddAdvancedToggle(body, archivePostProcessing_checkBox, "Extract completed archives", 0);
            AddAdvancedToggle(body, par2Repair_checkBox, "Run PAR2 repair before extraction", 1);

            body.Controls.Add(CreateFieldLabel("7-Zip executable"), 0, 2);
            archiveToolPath_textBox.Dock = DockStyle.Fill;
            archiveToolPath_textBox.PlaceholderText = "Optional; ZIP has a built-in fallback";
            archiveToolPath_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(archiveToolPath_textBox, 1, 2);

            body.Controls.Add(CreateFieldLabel("PAR2 executable"), 0, 3);
            par2ToolPath_textBox.Dock = DockStyle.Fill;
            par2ToolPath_textBox.PlaceholderText = "par2.exe or par2cmdline.exe";
            par2ToolPath_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(par2ToolPath_textBox, 1, 3);

            body.Controls.Add(CreateFieldLabel("Archive password"), 0, 4);
            archivePassword_textBox.Dock = DockStyle.Fill;
            archivePassword_textBox.UseSystemPasswordChar = true;
            archivePassword_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(archivePassword_textBox, 1, 4);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(390, 0),
                Tag = "muted",
                Margin = new Padding(0, 12, 0, 0),
                Text = "Passwords are protected for this Windows user. RAR and 7z require 7-Zip; PAR2 requires an external executable."
            };
            body.SetColumnSpan(note, 2);
            body.Controls.Add(note, 0, 5);
            return body;
        }

        private static void AddAdvancedToggle(
            TableLayoutPanel body,
            CheckBox checkBox,
            string text,
            int row)
        {
            checkBox.AutoSize = true;
            checkBox.Text = text;
            checkBox.Margin = new Padding(0, 7, 0, 7);
            body.SetColumnSpan(checkBox, 2);
            body.Controls.Add(checkBox, 0, row);
        }

        private Control BuildDownloadSettingsGrid()
        {
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(16, 12, 16, 12),
                Margin = new Padding(0)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));

            ModernCard download = CreateSettingsCard("Download behavior", "How files are written to disk");
            download.Margin = new Padding(0, 0, 6, 6);
            download.Controls.Add(BuildDownloadBody());

            ModernCard integrity = CreateSettingsCard("File verification", "Detect incomplete or mismatched files");
            integrity.Margin = new Padding(6, 0, 0, 6);
            integrity.Controls.Add(BuildIntegrityBody());

            ModernCard retry = CreateSettingsCard("Recovery policy", "Retries used for interrupted transfers");
            retry.Margin = new Padding(0, 6, 6, 0);
            retry.Controls.Add(BuildRetryBody());

            ModernCard flare = CreateSettingsCard("Cloudflare access", "Optional FlareSolverr browser session");
            flare.Margin = new Padding(6, 6, 0, 0);
            flare.Controls.Add(BuildFlareBody());

            content.Controls.Add(download, 0, 0);
            content.Controls.Add(integrity, 1, 0);
            content.Controls.Add(retry, 0, 1);
            content.Controls.Add(flare, 1, 1);
            return content;
        }

        private Control BuildNetworkSettingsGrid()
        {
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(16, 16, 16, 12),
                Margin = new Padding(0)
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            content.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));

            ModernCard dns = CreateSettingsCard("DNS resolver", "Choose a preset or enter your own DNS server");
            dns.Margin = new Padding(0, 0, 6, 8);
            dns.Controls.Add(BuildDnsBody());

            ModernCard proxy = CreateSettingsCard("Proxy", "Direct, Windows system proxy, HTTP(S), or SOCKS");
            proxy.Margin = new Padding(6, 0, 0, 8);
            proxy.Controls.Add(BuildProxyBody());

            var diagnostics = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "surface",
                BorderStyle = BorderStyle.FixedSingle,
                Padding = new Padding(12, 7, 12, 7),
                Margin = new Padding(0)
            };
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(1, 4, 1, 4)
            };
            clearDnsCache_button.Text = "Clear cache";
            clearDnsCache_button.Size = new Size(108, 32);
            clearDnsCache_button.Margin = new Padding(0, 0, 8, 0);
            providerHealth_button.Text = "Providers";
            providerHealth_button.Size = new Size(96, 32);
            providerHealth_button.Margin = new Padding(0, 0, 8, 0);
            testNetwork_button.Text = "Run test";
            testNetwork_button.Tag = "primary";
            testNetwork_button.Size = new Size(88, 32);
            actions.Controls.Add(clearDnsCache_button);
            actions.Controls.Add(providerHealth_button);
            actions.Controls.Add(testNetwork_button);
            networkStatus_label.Dock = DockStyle.Fill;
            networkStatus_label.AutoEllipsis = true;
            networkStatus_label.TextAlign = ContentAlignment.MiddleLeft;
            networkStatus_label.Tag = "muted";
            networkStatus_label.Text = "Test DNS and HTTPS with these settings.";
            diagnostics.Controls.Add(networkStatus_label);
            diagnostics.Controls.Add(actions);

            content.Controls.Add(dns, 0, 0);
            content.Controls.Add(proxy, 1, 0);
            content.SetColumnSpan(diagnostics, 2);
            content.Controls.Add(diagnostics, 0, 1);
            return content;
        }

        private Control BuildDnsBody()
        {
            var body = CreateCardBody(6);
            body.RowStyles.Clear();
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

            body.Controls.Add(CreateFieldLabel("Resolver"), 0, 0);
            dnsProvider_comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            dnsProvider_comboBox.Dock = DockStyle.Fill;
            dnsProvider_comboBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(dnsProvider_comboBox, 1, 0);

            body.Controls.Add(CreateFieldLabel("Protocol"), 0, 1);
            dnsTransport_comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            dnsTransport_comboBox.Dock = DockStyle.Fill;
            dnsTransport_comboBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(dnsTransport_comboBox, 1, 1);

            body.Controls.Add(CreateFieldLabel("Custom endpoint"), 0, 2);
            customDnsServer_textBox.Dock = DockStyle.Fill;
            customDnsServer_textBox.PlaceholderText = "IP address or IP:port";
            customDnsServer_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(customDnsServer_textBox, 1, 2);

            dnsFallback_checkBox.AutoSize = true;
            dnsFallback_checkBox.Text = "Fall back to Windows DNS if unavailable";
            dnsFallback_checkBox.Margin = new Padding(0, 9, 0, 6);
            body.SetColumnSpan(dnsFallback_checkBox, 2);
            body.Controls.Add(dnsFallback_checkBox, 0, 3);

            preferIpv6_checkBox.AutoSize = true;
            preferIpv6_checkBox.Text = "Prefer IPv6 addresses";
            preferIpv6_checkBox.Margin = new Padding(0, 9, 0, 6);
            body.SetColumnSpan(preferIpv6_checkBox, 2);
            body.Controls.Add(preferIpv6_checkBox, 0, 4);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(390, 0),
                Tag = "muted",
                Margin = new Padding(0, 12, 0, 0),
                Text = "Encrypted DNS uses verified TLS. Results are cached using the server-provided TTL."
            };
            body.SetColumnSpan(note, 2);
            body.Controls.Add(note, 0, 5);
            return body;
        }

        private Control BuildProxyBody()
        {
            var body = CreateCardBody(8);
            body.RowStyles.Clear();
            for (int i = 0; i < 5; i++)
                body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40F));

            body.Controls.Add(CreateFieldLabel("Mode"), 0, 0);
            proxyMode_comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
            proxyMode_comboBox.Dock = DockStyle.Fill;
            proxyMode_comboBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(proxyMode_comboBox, 1, 0);

            body.Controls.Add(CreateFieldLabel("Proxy URL"), 0, 1);
            proxyUrl_textBox.Dock = DockStyle.Fill;
            proxyUrl_textBox.PlaceholderText = "http://host:port or socks5://host:port";
            proxyUrl_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(proxyUrl_textBox, 1, 1);

            body.Controls.Add(CreateFieldLabel("Username"), 0, 2);
            proxyUsername_textBox.Dock = DockStyle.Fill;
            proxyUsername_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(proxyUsername_textBox, 1, 2);

            body.Controls.Add(CreateFieldLabel("Password"), 0, 3);
            proxyPassword_textBox.Dock = DockStyle.Fill;
            proxyPassword_textBox.UseSystemPasswordChar = true;
            proxyPassword_textBox.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(proxyPassword_textBox, 1, 3);

            body.Controls.Add(CreateFieldLabel("Connect timeout"), 0, 4);
            connectTimeout_numericUpDown.Minimum = 5;
            connectTimeout_numericUpDown.Maximum = 120;
            connectTimeout_numericUpDown.Dock = DockStyle.Fill;
            connectTimeout_numericUpDown.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(connectTimeout_numericUpDown, 1, 4);

            proxyBypassLocal_checkBox.AutoSize = true;
            proxyBypassLocal_checkBox.Text = "Bypass proxy for local addresses";
            proxyBypassLocal_checkBox.Margin = new Padding(0, 9, 0, 6);
            body.SetColumnSpan(proxyBypassLocal_checkBox, 2);
            body.Controls.Add(proxyBypassLocal_checkBox, 0, 5);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(390, 0),
                Tag = "muted",
                Margin = new Padding(0, 10, 0, 0),
                Text = "Passwords are encrypted for this Windows user."
            };
            body.SetColumnSpan(note, 2);
            body.Controls.Add(note, 0, 6);

            manageProxyRules_button.Text = "Proxy rules...";
            manageProxyRules_button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            manageProxyRules_button.Size = new Size(176, 32);
            manageProxyRules_button.Margin = new Padding(0, 4, 0, 0);
            body.SetColumnSpan(manageProxyRules_button, 2);
            body.Controls.Add(manageProxyRules_button, 0, 7);
            return body;
        }

        private static ModernCard CreateSettingsCard(string title, string subtitle)
        {
            var card = new ModernCard
            {
                Dock = DockStyle.Fill,
                CornerRadius = 8,
                Padding = new Padding(16, 76, 16, 14)
            };
            card.Controls.Add(new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                Location = new Point(16, 13),
                Text = title
            });
            card.Controls.Add(new Label
            {
                AutoSize = true,
                Tag = "muted",
                Location = new Point(17, 36),
                Text = subtitle
            });
            return card;
        }

        private Control BuildDownloadBody()
        {
            var body = CreateCardBody(5);
            body.Controls.Add(CreateFieldLabel("Concurrent downloads", 2), 0, 0);
            maximumDownloads_numericUpDown.Dock = DockStyle.Fill;
            maximumDownloads_numericUpDown.Margin = new Padding(8, 0, 0, 1);
            body.Controls.Add(maximumDownloads_numericUpDown, 1, 0);

            body.Controls.Add(CreateFieldLabel("Segments per large file", 2), 0, 1);
            maximumSegments_numericUpDown.Minimum = 1;
            maximumSegments_numericUpDown.Maximum = 8;
            maximumSegments_numericUpDown.Dock = DockStyle.Fill;
            maximumSegments_numericUpDown.Margin = new Padding(8, 0, 0, 1);
            body.Controls.Add(maximumSegments_numericUpDown, 1, 1);

            body.Controls.Add(CreateFieldLabel("Existing file", 2), 0, 2);
            overwriteMode_comboBox.Dock = DockStyle.Fill;
            overwriteMode_comboBox.Margin = new Padding(8, 0, 0, 0);
            body.Controls.Add(overwriteMode_comboBox, 1, 2);

            folderNewFiles_checkBox.AutoSize = true;
            folderNewFiles_checkBox.Text = "Use a dated “New Files” folder";
            folderNewFiles_checkBox.Margin = new Padding(0);
            body.SetColumnSpan(folderNewFiles_checkBox, 2);
            body.Controls.Add(folderNewFiles_checkBox, 0, 3);

            body.Controls.Add(CreateFieldLabel("Speed limit (KiB/s)", 2), 0, 4);
            bandwidthLimit_numericUpDown.Minimum = 0;
            bandwidthLimit_numericUpDown.Maximum = 1_000_000;
            bandwidthLimit_numericUpDown.ThousandsSeparator = true;
            bandwidthLimit_numericUpDown.Dock = DockStyle.Fill;
            bandwidthLimit_numericUpDown.Margin = new Padding(8, 0, 0, 0);
            body.Controls.Add(bandwidthLimit_numericUpDown, 1, 4);
            return body;
        }

        private Control BuildIntegrityBody()
        {
            var body = CreateCardBody(3);
            checkDownloadedFileSize_checkBox.AutoSize = true;
            checkDownloadedFileSize_checkBox.Text = "Verify downloaded file size";
            checkDownloadedFileSize_checkBox.Margin = new Padding(0, 7, 0, 7);
            body.SetColumnSpan(checkDownloadedFileSize_checkBox, 2);
            body.Controls.Add(checkDownloadedFileSize_checkBox, 0, 0);

            verifySha256_checkBox.AutoSize = true;
            verifySha256_checkBox.Text = "Store and verify SHA-256 integrity manifest";
            verifySha256_checkBox.Margin = new Padding(0, 7, 0, 7);
            body.SetColumnSpan(verifySha256_checkBox, 2);
            body.Controls.Add(verifySha256_checkBox, 0, 1);

            body.Controls.Add(CreateFieldLabel("Accepted size ratio"), 0, 2);
            checkFileSizeError_numericUpDown.Dock = DockStyle.Fill;
            checkFileSizeError_numericUpDown.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(checkFileSizeError_numericUpDown, 1, 2);
            return body;
        }

        private Control BuildRetryBody()
        {
            var body = CreateCardBody(3);
            body.Controls.Add(CreateFieldLabel("Maximum retries"), 0, 0);
            maxDownloadRetries_numericUpDown.Dock = DockStyle.Fill;
            maxDownloadRetries_numericUpDown.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(maxDownloadRetries_numericUpDown, 1, 0);

            body.Controls.Add(CreateFieldLabel("Base delay (ms)"), 0, 1);
            retryDelay_numericUpDown.Dock = DockStyle.Fill;
            retryDelay_numericUpDown.Margin = new Padding(8, 4, 0, 7);
            body.Controls.Add(retryDelay_numericUpDown, 1, 1);

            var note = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(300, 0),
                Tag = "muted",
                Text = "Retries use exponential backoff. Partial .part files remain available for HTTP Range resume.",
                Margin = new Padding(0, 10, 0, 0)
            };
            body.SetColumnSpan(note, 2);
            body.Controls.Add(note, 0, 2);
            return body;
        }

        private Control BuildFlareBody()
        {
            var body = CreateCardBody(5);
            body.RowStyles.Clear();
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            flareSolverrEnabled_checkBox.AutoSize = true;
            flareSolverrEnabled_checkBox.Text = "Use FlareSolverr when challenged";
            flareSolverrEnabled_checkBox.Margin = new Padding(0, 4, 0, 6);
            body.SetColumnSpan(flareSolverrEnabled_checkBox, 2);
            body.Controls.Add(flareSolverrEnabled_checkBox, 0, 0);

            body.Controls.Add(CreateFieldLabel("Server URL"), 0, 1);
            flareSolverrUrl_textBox.Dock = DockStyle.Fill;
            flareSolverrUrl_textBox.Margin = new Padding(8, 3, 0, 7);
            body.Controls.Add(flareSolverrUrl_textBox, 1, 1);

            body.Controls.Add(CreateFieldLabel("Timeout (seconds)"), 0, 2);
            flareSolverrTimeout_numericUpDown.Dock = DockStyle.Fill;
            flareSolverrTimeout_numericUpDown.Margin = new Padding(8, 3, 0, 7);
            body.Controls.Add(flareSolverrTimeout_numericUpDown, 1, 2);

            flareSolverrTest_button.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            flareSolverrTest_button.Size = new Size(168, 34);
            flareSolverrTest_button.Margin = new Padding(0, 2, 0, 2);
            flareSolverrTest_button.Text = "Test connection";
            body.SetColumnSpan(flareSolverrTest_button, 2);
            body.Controls.Add(flareSolverrTest_button, 0, 3);

            flareSolverrStatus_label.AutoSize = true;
            flareSolverrStatus_label.Tag = "muted";
            flareSolverrStatus_label.Margin = new Padding(0, 8, 0, 0);
            body.SetColumnSpan(flareSolverrStatus_label, 2);
            body.Controls.Add(flareSolverrStatus_label, 0, 4);
            return body;
        }

        private Panel BuildSettingsFooter()
        {
            var footer = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "header",
                Padding = new Padding(18, 10, 18, 10),
                Margin = new Padding(0)
            };
            var done = new Button
            {
                Text = "Done",
                Tag = "primary",
                DialogResult = DialogResult.OK,
                Dock = DockStyle.Right,
                Width = 112
            };
            var export = new Button
            {
                Text = "Export backup",
                Dock = DockStyle.Left,
                Width = 140,
                Margin = new Padding(0, 0, 8, 0)
            };
            var import = new Button
            {
                Text = "Import backup",
                Dock = DockStyle.Left,
                Width = 140,
                Margin = new Padding(0, 0, 8, 0)
            };
            export.Click += ExportBackup_Click;
            import.Click += ImportBackup_Click;
            footer.Controls.Add(done);
            footer.Controls.Add(import);
            footer.Controls.Add(export);
            AcceptButton = done;
            return footer;
        }

        private static TableLayoutPanel CreateCardBody(int rows)
        {
            var body = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = rows,
                Tag = "surface",
                Margin = new Padding(0)
            };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55F));
            for (int i = 0; i < rows; i++)
                body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return body;
        }

        private static Label CreateFieldLabel(string text, int topMargin = 8)
        {
            return new Label
            {
                AutoSize = true,
                Text = text,
                Margin = new Padding(0, topMargin, 0, 0)
            };
        }
    }
}
