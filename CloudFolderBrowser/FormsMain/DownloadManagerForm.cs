using CloudFolderBrowser.Theming;
using System.ComponentModel;
using System.Diagnostics;

namespace CloudFolderBrowser;

public sealed class DownloadManagerForm : ThemedForm
{
    private readonly BindingList<DownloadHistoryEntry> _entries = new();
    private readonly DataGridView _grid;

    public event EventHandler<IReadOnlyList<DownloadHistoryEntry>>? RetryRequested;

    public DownloadManagerForm()
    {
        Text = "Download manager";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(900, 520);
        ClientSize = new Size(1080, 640);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            Tag = "window"
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 65F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));

        var header = new Panel { Dock = DockStyle.Fill, Tag = "window" };
        header.Controls.Add(new Label
        {
            Text = "Persistent download history",
            AutoSize = true,
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            Location = new Point(0, 0)
        });
        header.Controls.Add(new Label
        {
            Text = "Interrupted HTTP downloads keep their .part file and can be resumed.",
            AutoSize = true,
            Tag = "muted",
            Location = new Point(2, 36)
        });
        root.Controls.Add(header, 0, 0);

        _grid = BuildGrid();
        _grid.DataSource = _entries;
        root.Controls.Add(_grid, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0),
            Tag = "window"
        };
        var close = new Button { Text = "Close", Width = 100, Height = 38, Tag = "subtle", DialogResult = DialogResult.OK };
        var retry = new Button { Text = "Resume / retry", Width = 138, Height = 38, Tag = "primary" };
        retry.Click += Retry_Click;
        var open = new Button { Text = "Open folder", Width = 118, Height = 38, Tag = "subtle" };
        open.Click += Open_Click;
        var clear = new Button { Text = "Clear completed", Width = 160, Height = 38, Tag = "subtle" };
        clear.Click += async (_, _) =>
        {
            await DownloadHistoryStore.Default.RemoveCompletedAsync();
            await RefreshEntriesAsync();
        };
        var refresh = new Button { Text = "Refresh", Width = 96, Height = 38, Tag = "subtle" };
        refresh.Click += async (_, _) => await RefreshEntriesAsync();
        footer.Controls.Add(close);
        footer.Controls.Add(retry);
        footer.Controls.Add(open);
        footer.Controls.Add(clear);
        footer.Controls.Add(refresh);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        AcceptButton = close;

        Shown += async (_, _) => await RefreshEntriesAsync();
    }

    private static DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true
        };
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", DataPropertyName = nameof(DownloadHistoryEntry.Status), Width = 95 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provider", DataPropertyName = nameof(DownloadHistoryEntry.CloudService), Width = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Route", DataPropertyName = nameof(DownloadHistoryEntry.EffectiveRouteName), Width = 125 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "File", DataPropertyName = nameof(DownloadHistoryEntry.FileName), Width = 190 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Local path", DataPropertyName = nameof(DownloadHistoryEntry.SavePath), AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Bytes", DataPropertyName = nameof(DownloadHistoryEntry.BytesOnDisk), Width = 105, DefaultCellStyle = new DataGridViewCellStyle { Format = "N0" } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Updated", DataPropertyName = nameof(DownloadHistoryEntry.UpdatedUtc), Width = 145, DefaultCellStyle = new DataGridViewCellStyle { Format = "g" } });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Error", DataPropertyName = nameof(DownloadHistoryEntry.Error), Width = 210 });
        return grid;
    }

    private async Task RefreshEntriesAsync()
    {
        try
        {
            IReadOnlyList<DownloadHistoryEntry> values = await DownloadHistoryStore.Default.GetAllAsync();
            _entries.RaiseListChangedEvents = false;
            _entries.Clear();
            foreach (DownloadHistoryEntry value in values)
                _entries.Add(value);
            _entries.RaiseListChangedEvents = true;
            _entries.ResetBindings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                "Unable to read download history: " + ex.Message,
                "Download history",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void Retry_Click(object? sender, EventArgs e)
    {
        DownloadHistoryEntry[] selected = _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem as DownloadHistoryEntry)
            .Where(entry => entry != null
                && entry.Status is DownloadJobStatus.Paused or DownloadJobStatus.Failed or DownloadJobStatus.Queued)
            .Cast<DownloadHistoryEntry>()
            .ToArray();
        if (selected.Length == 0)
        {
            MessageBox.Show("Select one or more paused, failed, or queued downloads.");
            return;
        }

        RetryRequested?.Invoke(this, selected);
        DialogResult = DialogResult.OK;
        Close();
    }

    private void Open_Click(object? sender, EventArgs e)
    {
        if (_grid.CurrentRow?.DataBoundItem is not DownloadHistoryEntry entry)
            return;
        string? directory = Path.GetDirectoryName(entry.SavePath);
        if (directory != null && Directory.Exists(directory))
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }
}
