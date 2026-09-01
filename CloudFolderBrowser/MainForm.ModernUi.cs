using Aga.Controls.Tree;
using CloudFolderBrowser.Branding;
using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser
{
    public partial class MainForm
    {
        private Label? comparisonStatus_label;
        private ThemedProgressBar? comparisonProgressBar;

        private void ConfigureModernUi()
        {
            SuspendLayout();
            try
            {
                Controls.Clear();
                ClientSize = GetInitialClientSize();
                MinimumSize = new Size(1060, 700);
                StartPosition = FormStartPosition.CenterScreen;
                Text = ApplicationBrand.ProductName;

                var root = NewTable(1, 5);
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 154F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78F));

                appHeader_panel = BuildHeader();
                root.Controls.Add(appHeader_panel, 0, 0);
                root.Controls.Add(BuildSourceBar(), 0, 1);
                root.Controls.Add(BuildFilterBar(), 0, 2);
                root.Controls.Add(BuildFileWorkspace(), 0, 3);
                root.Controls.Add(BuildCompareBar(), 0, 4);
                Controls.Add(root);

                ProgressLoading_panel.Dock = DockStyle.Fill;
                ProgressLoading_panel.Margin = new Padding(0);
                Controls.Add(ProgressLoading_panel);
                ProgressLoading_panel.BringToFront();

                loadPublicFolderKey_button.Tag = "primary";
                syncFolders_button.Tag = "primary";
                deletePublicFolder_button.Tag = "danger-subtle";
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private static Size GetInitialClientSize()
        {
            Rectangle area = Screen.FromPoint(Cursor.Position).WorkingArea;
            int width = Math.Min(1440, Math.Max(1060, area.Width - 56));
            int height = Math.Min(900, Math.Max(700, area.Height - 72));
            return new Size(width, height);
        }

        private Panel BuildHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "header",
                Padding = new Padding(20, 10, 16, 10),
                Margin = new Padding(0)
            };
            var row = NewTable(8, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            row.Controls.Add(new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                Text = "Cloud Folder Browser",
                Font = new Font("Segoe UI Semibold", 13F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);

            ConfigureHeaderButton(LoadFromFile_button, "Open list", 112);
            ConfigureHeaderButton(SaveToFile_button, "Save list");
            ConfigureHeaderButton(fogLink_button, "FogLink");
            ConfigureHeaderButton(networkSettings_button, "Settings");
            ConfigureHeaderButton(
                loginMega_button,
                "Accounts",
                142);
            loginMega_button.Tag = "primary";

            appVersion_linkLabel.AutoSize = true;
            appVersion_linkLabel.TextAlign = ContentAlignment.MiddleCenter;
            appVersion_linkLabel.Anchor = AnchorStyles.None;
            appVersion_linkLabel.Margin = new Padding(8, 0, 2, 0);

            themeMode_comboBox = new ComboBox
            {
                Name = "themeMode_comboBox",
                DropDownStyle = ComboBoxStyle.DropDownList,
                Size = new Size(124, 30),
                Anchor = AnchorStyles.None,
                Margin = new Padding(10, 0, 0, 0)
            };
            themeMode_comboBox.Items.AddRange(new object[] { "System", "Light", "Dark" });
            int savedTheme = Math.Clamp(Properties.Settings.Default.themeMode, 0, 2);
            ThemeManager.SetMode((AppThemeMode)savedTheme);
            themeMode_comboBox.SelectedIndex = savedTheme;
            themeMode_comboBox.SelectedIndexChanged += themeMode_comboBox_SelectedIndexChanged;

            row.Controls.Add(appVersion_linkLabel, 1, 0);
            row.Controls.Add(LoadFromFile_button, 2, 0);
            row.Controls.Add(SaveToFile_button, 3, 0);
            row.Controls.Add(fogLink_button, 4, 0);
            row.Controls.Add(networkSettings_button, 5, 0);
            row.Controls.Add(loginMega_button, 6, 0);
            row.Controls.Add(themeMode_comboBox, 7, 0);
            header.Controls.Add(row);
            return header;
        }

        private Control BuildSourceBar()
        {
            var sourceBar = NewTable(3, 1);
            sourceBar.Tag = "window";
            sourceBar.Padding = new Padding(20, 9, 20, 9);
            sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16F));
            sourceBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            sourceBar.Controls.Add(BuildCloudSource(), 0, 0);
            sourceBar.Controls.Add(BuildLocalSource(), 2, 0);
            return sourceBar;
        }

        private Control BuildCloudSource()
        {
            var box = BuildSection("Cloud share");
            var body = (TableLayoutPanel)box.Controls[0];
            var fields = NewTable(1, 3);
            fields.Margin = new Padding(0, 1, 0, 1);
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 8F));
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            var savedShare = NewTable(4, 1);
            savedShare.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            savedShare.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            savedShare.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72F));
            savedShare.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            publicFolders_comboBox.Dock = DockStyle.Fill;
            publicFolders_comboBox.Margin = new Padding(0, 0, 8, 0);
            ConfigureSmallButton(addNewPublicFolder_button, "Add");
            ConfigureSmallButton(editPublicFolderKey_button, "Edit");
            ConfigureSmallButton(deletePublicFolder_button, "Delete");
            savedShare.Controls.Add(publicFolders_comboBox, 0, 0);
            savedShare.Controls.Add(addNewPublicFolder_button, 1, 0);
            savedShare.Controls.Add(editPublicFolderKey_button, 2, 0);
            savedShare.Controls.Add(deletePublicFolder_button, 3, 0);
            fields.Controls.Add(savedShare, 0, 0);
            fields.Controls.Add(BuildPathField(publicFolderKey_textBox, loadPublicFolderKey_button, "Connect", true, 104), 0, 2);
            body.Controls.Add(fields, 0, 1);
            return box;
        }

        private Control BuildLocalSource()
        {
            var box = BuildSection("Local folder");
            // The local card has one field instead of two. Balance its unused
            // space around the complete title/field block rather than leaving
            // the title visually pinned to the top edge.
            box.Padding = new Padding(12, 17, 12, 1);
            var body = (TableLayoutPanel)box.Controls[0];
            var centeredField = NewTable(1, 3);
            centeredField.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            centeredField.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            centeredField.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            centeredField.Controls.Add(
                BuildPathField(syncFolderPath_textBox, browseSyncFolder_button, "Browse", true, 112),
                0,
                1);
            body.Controls.Add(centeredField, 0, 1);
            return box;
        }

        private static Panel BuildSection(string title)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "surface",
                Padding = new Padding(12, 8, 12, 10),
                Margin = new Padding(0)
            };
            var body = NewTable(1, 2);
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0)
            }, 0, 0);
            panel.Controls.Add(body);
            return panel;
        }

        private static Control BuildPathField(
            System.Windows.Forms.TextBox textBox,
            Button button,
            string text,
            bool primary,
            int buttonWidth = 104)
        {
            var row = NewTable(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, buttonWidth));
            textBox.Dock = DockStyle.Fill;
            textBox.BorderStyle = BorderStyle.FixedSingle;
            textBox.Margin = new Padding(0, 0, 8, 0);
            button.Dock = DockStyle.Fill;
            button.Text = text;
            button.Tag = primary ? "primary" : "subtle";
            button.Margin = new Padding(0);
            row.Controls.Add(textBox, 0, 0);
            row.Controls.Add(button, 1, 0);
            return row;
        }

        private Control BuildFilterBar()
        {
            var bar = NewTable(8, 1);
            bar.Tag = "surface";
            bar.Padding = new Padding(20, 8, 20, 8);
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 12F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            filter_textBox.Dock = DockStyle.Fill;
            filter_textBox.BorderStyle = BorderStyle.FixedSingle;
            filter_textBox.Margin = new Padding(0, 0, 16, 0);
            beforeDate_dateTimePicker.Format = DateTimePickerFormat.Custom;
            beforeDate_dateTimePicker.CustomFormat = "yyyy-MM-dd";
            beforeDate_dateTimePicker.Dock = DockStyle.Fill;
            beforeDate_dateTimePicker.Margin = new Padding(6, 0, 12, 0);
            afterDate_dateTimePicker.Format = DateTimePickerFormat.Custom;
            afterDate_dateTimePicker.CustomFormat = "yyyy-MM-dd";
            afterDate_dateTimePicker.Dock = DockStyle.Fill;
            afterDate_dateTimePicker.Margin = new Padding(6, 0, 12, 0);
            flatList_checkBox.AutoSize = true;
            flatList_checkBox.Margin = new Padding(0, 6, 16, 0);
            checkedFiles_label.AutoSize = true;
            checkedFiles_label.Tag = "muted";
            checkedFiles_label.Margin = new Padding(0, 6, 0, 0);

            bar.Controls.Add(filter_textBox, 0, 0);
            bar.Controls.Add(NewInlineLabel("From"), 1, 0);
            bar.Controls.Add(afterDate_dateTimePicker, 2, 0);
            bar.Controls.Add(NewInlineLabel("To"), 3, 0);
            bar.Controls.Add(beforeDate_dateTimePicker, 4, 0);
            bar.Controls.Add(flatList_checkBox, 5, 0);
            bar.Controls.Add(checkedFiles_label, 7, 0);
            return bar;
        }

        private Control BuildFileWorkspace()
        {
            var workspace = NewTable(3, 1);
            workspace.Tag = "window";
            workspace.Padding = new Padding(20, 0, 20, 0);
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            workspace.Controls.Add(BuildFilePane("Cloud files", cloudPublicFolder_treeViewAdv), 0, 0);
            workspace.Controls.Add(BuildFilePane("Local files", syncFolder_treeViewAdv), 2, 0);
            return workspace;
        }

        private static Control BuildFilePane(string title, TreeViewAdv tree)
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "surface",
                Margin = new Padding(0)
            };
            var layout = NewTable(1, 2);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = title,
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0),
                Margin = new Padding(0)
            }, 0, 0);
            tree.Dock = DockStyle.Fill;
            tree.Margin = new Padding(0);
            tree.BorderStyle = BorderStyle.None;
            tree.ColumnHeaderHeight = 28;
            ConfigureTreeColumns(tree);
            layout.Controls.Add(tree, 0, 1);
            panel.Controls.Add(layout);
            return panel;
        }

        private Control BuildCompareBar()
        {
            var bar = NewTable(6, 2);
            bar.Tag = "surface";
            bar.Padding = new Padding(20, 10, 20, 10);
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 144F));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 174F));
            bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            bar.RowStyles.Add(new RowStyle(SizeType.Absolute, 4F));

            comparisonStatus_label = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Choose a cloud share and local folder",
                Tag = "muted",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Margin = new Padding(0, 0, 12, 0)
            };
            hideExistingFiles_checkBox.AutoSize = true;
            hideExistingFiles_checkBox.Text = "Only changed files";
            hideExistingFiles_checkBox.Margin = new Padding(0, 11, 16, 0);
            enableProgressPanel_checkBox.AutoSize = true;
            enableProgressPanel_checkBox.Text = "Progress overlay";
            enableProgressPanel_checkBox.Margin = new Padding(0, 11, 16, 0);
            downloadManager_button.Text = "Download history";
            downloadManager_button.Tag = "primary";
            downloadManager_button.Dock = DockStyle.Fill;
            downloadManager_button.Margin = new Padding(0, 0, 8, 0);
            downloadManager_button.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
            downloadManager_button.Click -= downloadManager_button_Click;
            downloadManager_button.Click += downloadManager_button_Click;
            showSyncForm_button.Text = "Open results";
            showSyncForm_button.Tag = "subtle";
            showSyncForm_button.Dock = DockStyle.Fill;
            showSyncForm_button.Margin = new Padding(0, 0, 8, 0);
            showSyncForm_button.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);
            syncFolders_button.Text = "Compare folders";
            syncFolders_button.Tag = "primary";
            syncFolders_button.Dock = DockStyle.Fill;
            syncFolders_button.Margin = new Padding(0);
            syncFolders_button.Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold);

            comparisonProgressBar = new ThemedProgressBar
            {
                Dock = DockStyle.Fill,
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Style = ProgressBarStyle.Continuous,
                Margin = new Padding(0)
            };
            bar.Controls.Add(comparisonStatus_label, 0, 0);
            bar.Controls.Add(enableProgressPanel_checkBox, 1, 0);
            bar.Controls.Add(hideExistingFiles_checkBox, 2, 0);
            bar.Controls.Add(downloadManager_button, 3, 0);
            bar.Controls.Add(showSyncForm_button, 4, 0);
            bar.Controls.Add(syncFolders_button, 5, 0);
            bar.Controls.Add(comparisonProgressBar, 0, 1);
            bar.SetColumnSpan(comparisonProgressBar, 6);
            return bar;
        }

        private static void ConfigureTreeColumns(TreeViewAdv tree)
        {
            if (tree.Columns.Count < 4)
                return;
            tree.Columns[0].Width = 300;
            tree.Columns[1].Width = 112;
            tree.Columns[2].Width = 112;
            tree.Columns[3].Width = 90;
            tree.Columns[3].TextAlign = HorizontalAlignment.Right;
        }

        private static Label NewInlineLabel(string text) => new()
        {
            AutoSize = true,
            Text = text,
            Tag = "muted",
            Margin = new Padding(0, 6, 0, 0)
        };

        private static void ConfigureHeaderButton(Button button, string text, int width = 96)
        {
            button.Text = text;
            button.Tag = "ghost";
            int measuredWidth = TextRenderer.MeasureText(
                text,
                button.Font,
                Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + 32;
            int safeWidth = Math.Max(width, measuredWidth);
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Size = new Size(safeWidth, 38);
            button.MinimumSize = new Size(safeWidth, 38);
            button.Anchor = AnchorStyles.None;
            button.TextAlign = ContentAlignment.MiddleCenter;
            button.Margin = new Padding(6, 0, 0, 0);
        }

        private static void ConfigureSmallButton(Button button, string text)
        {
            button.Text = text;
            button.Tag = "subtle";
            button.Dock = DockStyle.Fill;
            button.Margin = new Padding(0, 0, 6, 0);
        }

        private static TableLayoutPanel NewTable(int columns, int rows)
        {
            return new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = columns,
                RowCount = rows,
                Margin = new Padding(0),
                Padding = new Padding(0),
                Tag = "surface"
            };
        }
    }
}
