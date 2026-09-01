using CloudFolderBrowser.Sync;
using CloudFolderBrowser.Theming;
using System.ComponentModel;

namespace CloudFolderBrowser;

public sealed class SyncPreviewForm : ThemedForm
{
    private readonly BindingList<PreviewRow> _rows;
    private readonly DataGridView _grid;
    private readonly Label _summary;

    public IReadOnlyList<CloudFile> SelectedFiles { get; private set; } = Array.Empty<CloudFile>();

    public SyncPreviewForm(IEnumerable<SyncPlanItem> items)
    {
        _rows = new BindingList<PreviewRow>(items.Select(item => new PreviewRow(item)).ToList());
        Text = "Sync preview";
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(1120, 620);
        ClientSize = new Size(1420, 760);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16),
            Tag = "window"
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 82F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));

        var header = new Panel { Dock = DockStyle.Fill, Tag = "window" };
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Review synchronization actions",
            Font = new Font("Segoe UI Semibold", 16F, FontStyle.Bold),
            Location = new Point(0, 4)
        });
        _summary = new Label { AutoSize = true, Tag = "muted", Location = new Point(2, 48) };
        header.Controls.Add(_summary);
        root.Controls.Add(header, 0, 0);

        _grid = BuildGrid();
        _grid.DataSource = _rows;
        _grid.CellValueChanged += (_, _) => UpdateSummary();
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        root.Controls.Add(_grid, 0, 1);

        var footer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Tag = "window",
            Margin = new Padding(0, 10, 0, 0)
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var downloadAll = new Button { Text = "Transfer all", Width = 132, Height = 38, Tag = "subtle", Dock = DockStyle.Fill };
        downloadAll.Click += (_, _) => SetAllSuggestedTransfers();
        var suggested = new Button { Text = "Reset suggested", Width = 164, Height = 38, Tag = "subtle", Dock = DockStyle.Fill };
        suggested.Click += (_, _) =>
        {
            foreach (PreviewRow row in _rows)
                row.Reset();
            _grid.Refresh();
            UpdateSummary();
        };
        var cancel = new Button { Text = "Cancel", Width = 100, Height = 38, Tag = "subtle", DialogResult = DialogResult.Cancel };
        var apply = new Button { Text = "Continue", Width = 120, Height = 38, Tag = "primary" };
        apply.Click += Apply_Click;

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Dock = DockStyle.Right
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(apply);
        footer.Controls.Add(downloadAll, 0, 0);
        footer.Controls.Add(suggested, 1, 0);
        footer.Controls.Add(actions, 3, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        AcceptButton = apply;
        CancelButton = cancel;
        UpdateSummary();
    }

    private static DataGridView BuildGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AutoGenerateColumns = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = true
        };
        grid.Columns.Add(new DataGridViewComboBoxColumn
        {
            HeaderText = "Action",
            DataPropertyName = nameof(PreviewRow.Action),
            DataSource = Enum.GetValues<SyncPlanAction>(),
            Width = 140
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Target name",
            DataPropertyName = nameof(PreviewRow.TargetName),
            Width = 250
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Cloud path",
            DataPropertyName = nameof(PreviewRow.CloudPath),
            Width = 320,
            ReadOnly = true
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Difference",
            DataPropertyName = nameof(PreviewRow.Difference),
            Width = 175,
            ReadOnly = true
        });
        grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = "Reason",
            DataPropertyName = nameof(PreviewRow.Reason),
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            ReadOnly = true
        });
        return grid;
    }

    private void SetAll(SyncPlanAction action)
    {
        foreach (PreviewRow row in _rows)
            row.Action = action;
        _grid.Refresh();
        UpdateSummary();
    }

    private void SetAllSuggestedTransfers()
    {
        foreach (PreviewRow row in _rows)
            row.Action = row.Difference == SyncDifference.Missing
                ? SyncPlanAction.Download
                : SyncPlanAction.Overwrite;
        _grid.Refresh();
        UpdateSummary();
    }

    private void Apply_Click(object? sender, EventArgs e)
    {
        _grid.EndEdit();
        foreach (PreviewRow row in _rows.Where(row => row.Action == SyncPlanAction.Rename))
        {
            if (string.IsNullOrWhiteSpace(row.TargetName)
                || string.IsNullOrWhiteSpace(row.GetSafeTargetName()))
            {
                MessageBox.Show("A renamed file must have a target name.");
                return;
            }
        }

        var duplicateTargets = _rows
            .Where(row => row.Action != SyncPlanAction.Skip)
            .GroupBy(row => row.GetTargetPath(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateTargets != null)
        {
            MessageBox.Show($"Multiple files map to the same target: {duplicateTargets.Key}");
            return;
        }

        SelectedFiles = _rows
            .Where(row => row.Action != SyncPlanAction.Skip)
            .Select(row => row.Apply())
            .ToArray();
        DialogResult = DialogResult.OK;
        Close();
    }

    private void UpdateSummary()
    {
        int selected = _rows.Count(row => row.Action != SyncPlanAction.Skip);
        long bytes = _rows.Where(row => row.Action != SyncPlanAction.Skip).Sum(row => row.Size);
        int skipped = _rows.Count - selected;
        _summary.Text = $"{selected:N0} transfer(s), {skipped:N0} skipped, {bytes / 1024d / 1024d:N2} MB selected";
    }

    private sealed class PreviewRow
    {
        private readonly SyncPlanItem _item;
        private readonly SyncPlanAction _suggestedAction;

        public SyncPlanAction Action { get; set; }
        public string TargetName { get; set; }
        public string CloudPath => _item.File.Path;
        public SyncDifference Difference => _item.Difference;
        public string Reason => _item.Reason;
        public long Size => _item.File.Size;

        public PreviewRow(SyncPlanItem item)
        {
            _item = item;
            _suggestedAction = item.Action;
            Action = item.Action;
            TargetName = item.TargetName;
        }

        public void Reset()
        {
            Action = _suggestedAction;
            TargetName = _item.File.Name;
        }

        public string GetTargetPath()
        {
            _item.Action = Action;
            _item.TargetName = TargetName;
            return _item.GetTargetCloudPath();
        }

        public string GetSafeTargetName()
        {
            _item.TargetName = TargetName;
            return _item.GetSafeTargetName();
        }

        public CloudFile Apply()
        {
            _item.Action = Action;
            _item.TargetName = TargetName;
            return _item.Apply();
        }
    }
}
