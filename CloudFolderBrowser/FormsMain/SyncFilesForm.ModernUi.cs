using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser
{
    public partial class SyncFilesForm
    {
        private Label? transferSummaryLabel;
        private ComboBox? downloadRouteComboBox;

        private void NormalizeSyncLayout()
        {
            PerformLayoutRecursively(this);
        }

        private static void PerformLayoutRecursively(Control control)
        {
            control.PerformLayout();
            foreach (Control child in control.Controls)
                PerformLayoutRecursively(child);
        }

        private void ConfigureModernUi()
        {
            SuspendLayout();
            try
            {
                Controls.Clear();
                // This form is rebuilt entirely in logical layout units. The
                // legacy designer's font autoscale metadata would otherwise
                // scale fixed columns a second time and clip the action pane.
                AutoScaleMode = AutoScaleMode.None;
                ClientSize = new Size(1120, 720);
                MinimumSize = new Size(960, 620);
                StartPosition = FormStartPosition.CenterParent;
                Text = "Sync results";

                var root = NewSyncTable(1, 2);
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                root.Controls.Add(BuildSyncHeader(), 0, 0);
                root.Controls.Add(BuildSyncWorkspace(), 0, 1);
                Controls.Add(root);
            }
            finally
            {
                ResumeLayout(true);
            }
        }

        private Panel BuildSyncHeader()
        {
            var header = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "header",
                Padding = new Padding(18, 8, 16, 8),
                Margin = new Padding(0)
            };
            var row = NewSyncTable(2, 1);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124F));
            row.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Sync results",
                Font = new Font("Segoe UI Semibold", 12F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            var settings = new Button
            {
                Text = "Settings",
                Tag = "subtle",
                Dock = DockStyle.Fill,
                Margin = new Padding(0)
            };
            settings.Click += settingsToolStripMenuItem_Click;
            row.Controls.Add(settings, 1, 0);
            header.Controls.Add(row);
            return header;
        }

        private Control BuildSyncWorkspace()
        {
            var workspace = NewSyncTable(3, 1);
            workspace.Tag = "window";
            workspace.Padding = new Padding(16);
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 16F));
            workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36F));
            workspace.Controls.Add(BuildResultsPanel(), 0, 0);
            workspace.Controls.Add(BuildActionPanel(), 2, 0);
            return workspace;
        }

        private Control BuildResultsPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "surface",
                Margin = new Padding(0)
            };
            var layout = NewSyncTable(1, 3);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Files to transfer",
                Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(12, 0, 0, 0)
            }, 0, 0);

            newFilesTreeViewAdv.Dock = DockStyle.Fill;
            newFilesTreeViewAdv.Margin = new Padding(0);
            newFilesTreeViewAdv.BorderStyle = BorderStyle.None;
            newFilesTreeViewAdv.ColumnHeaderHeight = 28;
            if (newFilesTreeViewAdv.Columns.Count >= 4)
            {
                newFilesTreeViewAdv.Columns[0].Width = 300;
                newFilesTreeViewAdv.Columns[1].Width = 120;
                newFilesTreeViewAdv.Columns[2].Width = 120;
                newFilesTreeViewAdv.Columns[3].Width = 100;
            }
            layout.Controls.Add(newFilesTreeViewAdv, 0, 1);

            var filter = NewSyncTable(1, 1);
            filter.Padding = new Padding(10, 8, 10, 8);
            filter.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            filter_textBox.Dock = DockStyle.Fill;
            filter_textBox.BorderStyle = BorderStyle.FixedSingle;
            filter_textBox.Margin = new Padding(0);
            filter.Controls.Add(filter_textBox, 0, 0);
            layout.Controls.Add(filter, 0, 2);
            panel.Controls.Add(layout);
            panel.Resize += (_, _) =>
            {
                layout.Bounds = panel.DisplayRectangle;
                layout.PerformLayout();
            };
            return panel;
        }

        private Control BuildActionPanel()
        {
            var panel = new Panel
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "surface",
                Padding = new Padding(12),
                Margin = new Padding(0)
            };
            var layout = NewSyncTable(1, 12);
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32F));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Transfer actions",
                Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            transferSummaryLabel = new Label
            {
                Dock = DockStyle.Fill,
                Text = "Preparing transfer summary…",
                Tag = "muted",
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 2, 4, 2)
            };
            layout.Controls.Add(transferSummaryLabel, 0, 1);

            flatList2_checkBox.AutoSize = true;
            flatList2_checkBox.Visible = true;
            flatList2_checkBox.Text = "Flat file list";
            flatList2_checkBox.Dock = DockStyle.None;
            flatList2_checkBox.Anchor = AnchorStyles.Left;
            flatList2_checkBox.Margin = new Padding(4, 6, 0, 4);
            layout.Controls.Add(flatList2_checkBox, 0, 2);

            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Download route",
                Font = new Font("Segoe UI Semibold", 8.8F, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft,
                Padding = new Padding(4, 0, 0, 0)
            }, 0, 3);
            downloadRouteComboBox = new ComboBox
            {
                Name = "downloadRouteComboBox",
                Dock = DockStyle.Fill,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(0, 3, 0, 5)
            };
            layout.Controls.Add(downloadRouteComboBox, 0, 4);

            ConfigureSyncButton(getJdLinks_button, "Export JDownloader links", "subtle");
            ConfigureSyncButton(importMega_button, "Import to cloud storage", "subtle");
            ConfigureSyncButton(downloadFiles_button, "Download selected files", "primary");
            layout.Controls.Add(getJdLinks_button, 0, 5);
            layout.Controls.Add(importMega_button, 0, 6);
            layout.Controls.Add(downloadFiles_button, 0, 7);

            layout.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "Transfer progress",
                Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold),
                TextAlign = ContentAlignment.BottomLeft
            }, 0, 8);
            DownloadProgress_label.Dock = DockStyle.Fill;
            DownloadProgress_label.Text = "No transfer started yet";
            DownloadProgress_label.Tag = "muted";
            DownloadProgress_label.TextAlign = ContentAlignment.MiddleLeft;
            DownloadProgress_label.Visible = true;
            layout.Controls.Add(DownloadProgress_label, 0, 9);
            layout.Controls.Add(BuildProgressList(), 0, 10);

            stopDownload_button.Text = "Pause downloads";
            stopDownload_button.Tag = "danger-subtle";
            stopDownload_button.Dock = DockStyle.Fill;
            stopDownload_button.Margin = new Padding(0, 6, 0, 0);
            layout.Controls.Add(stopDownload_button, 0, 11);
            panel.Controls.Add(layout);
            panel.Resize += (_, _) =>
            {
                // WinForms can retain the action table's pre-autoscale width
                // after its percentage column shrinks. Rebind it to the padded
                // display rectangle so controls cannot extend past the right edge.
                layout.Bounds = panel.DisplayRectangle;
                layout.PerformLayout();
            };
            return panel;
        }

        private Control BuildProgressList()
        {
            var list = NewSyncTable(1, 8);
            list.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            list.Margin = new Padding(0, 4, 0, 0);
            Label[] labels = { label1, label2, label3, label4 };
            ProgressBar[] bars = { progressBar1, progressBar2, progressBar3, progressBar4 };
            for (int index = 0; index < labels.Length; index++)
            {
                list.RowStyles.Add(new RowStyle(SizeType.Absolute, 23F));
                list.RowStyles.Add(new RowStyle(SizeType.Absolute, 18F));
                labels[index].Dock = DockStyle.Fill;
                labels[index].Tag = "muted";
                labels[index].AutoEllipsis = true;
                labels[index].Visible = false;
                labels[index].Text = string.Empty;
                bars[index].Dock = DockStyle.Fill;
                bars[index].Margin = new Padding(0, 2, 0, 5);
                bars[index].Style = ProgressBarStyle.Continuous;
                bars[index].Visible = false;
                list.Controls.Add(labels[index], 0, index * 2);
                list.Controls.Add(bars[index], 0, index * 2 + 1);
            }
            return list;
        }

        private static void ConfigureSyncButton(Button button, string text, string role)
        {
            button.Text = text;
            button.Tag = role;
            button.Dock = DockStyle.Fill;
            // Keep the themed outline and rounded corners away from the parent
            // clipping boundary, including at 125–200% Windows DPI scaling.
            button.Margin = new Padding(10, 4, 10, 4);
        }

        private static TableLayoutPanel NewSyncTable(int columns, int rows) => new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = columns,
            RowCount = rows,
            Tag = "surface",
            Margin = new Padding(0),
            Padding = new Padding(0)
        };
    }
}
