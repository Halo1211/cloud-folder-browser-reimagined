using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Theming;
using System.Net;

namespace CloudFolderBrowser.FormsSecondary;

public sealed class ProviderHealthForm : ThemedForm
{
    private readonly DataGridView _grid = new();
    private readonly Label _status = new();
    private readonly Button _run = new();
    private readonly ProviderHealthCheckService _service = new();
    private CancellationTokenSource? _runCancellation;

    public ProviderHealthForm()
    {
        Text = "Provider health";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(920, 520);
        MinimumSize = new Size(760, 420);
        BuildUi();
        if (string.Equals(Environment.GetEnvironmentVariable("CFB_UI_SNAPSHOT"), "1", StringComparison.Ordinal))
            LoadPreviewRows();
        else
            Shown += async (_, _) => await RunChecksAsync();
        Shown += (_, _) => BeginInvoke(new Action(() =>
        {
            _grid.ClearSelection();
            _grid.CurrentCell = null;
        }));
        FormClosing += (_, _) => _runCancellation?.Cancel();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Checks endpoint reachability through each provider DNS/proxy route. Credentials are not sent.",
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "muted"
        }, 0, 0);

        ConfigureGrid();
        root.Controls.Add(_grid, 0, 1);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Tag = "muted";
        footer.Controls.Add(_status, 0, 0);
        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var close = new Button
        {
            Text = "Close",
            Width = 64,
            Height = 28,
            Margin = new Padding(4, 0, 0, 0),
            DialogResult = DialogResult.OK
        };
        _run.Text = "Retest";
        _run.Width = 72;
        _run.Height = 28;
        _run.Margin = new Padding(4, 0, 0, 0);
        _run.Tag = "primary";
        _run.Click += async (_, _) => await RunChecksAsync();
        actions.Controls.Add(close);
        actions.Controls.Add(_run);
        footer.Controls.Add(actions, 1, 0);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);
        AcceptButton = close;
    }

    private void ConfigureGrid()
    {
        _grid.Dock = DockStyle.Fill;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCellsExceptHeaders;
        _grid.AutoGenerateColumns = false;
        _grid.MultiSelect = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.TabStop = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Provider",
            HeaderText = "Provider",
            Width = 180,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "State", HeaderText = "State", Width = 110 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Latency", HeaderText = "Latency", Width = 100 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Http", HeaderText = "HTTP", Width = 64 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Route",
            HeaderText = "Route",
            Width = 190,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        });
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Detail",
            HeaderText = "Detail",
            AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            MinimumWidth = 180,
            DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True }
        });
    }

    private async Task RunChecksAsync()
    {
        _runCancellation?.Cancel();
        _runCancellation?.Dispose();
        _runCancellation = new CancellationTokenSource();
        CancellationToken cancellationToken = _runCancellation.Token;
        _run.Enabled = false;
        _grid.Rows.Clear();
        IReadOnlyList<ProviderHealthTarget> targets = ProviderHealthCheckService.CreateTargets(
            CloudAccountStore.Default.GetAll(),
            Properties.Settings.Default.fogLinkAddress,
            Properties.Settings.Default.flareSolverrEnabled,
            Properties.Settings.Default.flareSolverrUrl);
        int completed = 0;
        _status.Text = $"Checking 0/{targets.Count} providers…";
        var progress = new Progress<ProviderHealthResult>(result =>
        {
            AddResult(result);
            completed++;
            _status.Text = $"Checking {completed}/{targets.Count} providers…";
        });
        try
        {
            IReadOnlyList<ProviderHealthResult> results = await _service.CheckAllAsync(targets, progress, cancellationToken);
            int online = results.Count(result => result.State == ProviderHealthState.Online);
            int degraded = results.Count(result => result.State == ProviderHealthState.Degraded);
            int unreachable = results.Count(result => result.State == ProviderHealthState.Unreachable);
            _status.Text = $"{online} online • {degraded} degraded • {unreachable} unreachable";
            _status.Tag = unreachable > 0 ? "danger-text" : degraded > 0 ? "muted" : "success-text";
            _status.ForeColor = unreachable > 0
                ? ThemeManager.Palette.Danger
                : degraded > 0 ? ThemeManager.Palette.MutedText : ThemeManager.Palette.Success;
            _grid.ClearSelection();
            _grid.CurrentCell = null;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Health check cancelled";
        }
        finally
        {
            if (!IsDisposed)
                _run.Enabled = true;
        }
    }

    private void AddResult(ProviderHealthResult result)
    {
        string state = result.State switch
        {
            ProviderHealthState.Online => "Online",
            ProviderHealthState.Degraded => "Degraded",
            _ => "Offline"
        };
        int rowIndex = _grid.Rows.Add(
            result.Target.DisplayName,
            state,
            result.Elapsed.TotalSeconds >= 10
                ? $"{result.Elapsed.TotalSeconds:0.0} s"
                : $"{result.Elapsed.TotalMilliseconds:0} ms",
            result.StatusCode.HasValue ? ((int)result.StatusCode.Value).ToString() : "—",
            result.RouteDescription,
            result.Detail);
        DataGridViewRow row = _grid.Rows[rowIndex];
        row.Cells["State"].Style.ForeColor = result.State switch
        {
            ProviderHealthState.Online => ThemeManager.Palette.Success,
            ProviderHealthState.Degraded => ThemeManager.Palette.Accent,
            _ => ThemeManager.Palette.Danger
        };
    }

    private void LoadPreviewRows()
    {
        AddResult(new ProviderHealthResult(
            new ProviderHealthTarget("Mega", "MEGA", new Uri("https://g.api.mega.co.nz/")),
            ProviderHealthState.Online, TimeSpan.FromMilliseconds(84), HttpStatusCode.OK,
            "Direct", "Endpoint responded normally"));
        AddResult(new ProviderHealthResult(
            new ProviderHealthTarget("GoogleDrive", "Google Drive", new Uri("https://www.googleapis.com/")),
            ProviderHealthState.Online, TimeSpan.FromMilliseconds(132), HttpStatusCode.Forbidden,
            "System proxy", "Reachable; authentication required"));
        AddResult(new ProviderHealthResult(
            new ProviderHealthTarget("Premiumize", "Premiumize.me", new Uri("https://www.premiumize.me/")),
            ProviderHealthState.Degraded, TimeSpan.FromMilliseconds(428), HttpStatusCode.TooManyRequests,
            "Custom proxy (127.0.0.1:8080)", "Provider is rate limiting requests"));
        AddResult(new ProviderHealthResult(
            new ProviderHealthTarget("WebDav", "WebDAV — Team storage", new Uri("https://cloud.example.test/")),
            ProviderHealthState.Unreachable, TimeSpan.FromSeconds(15), null,
            "Direct", "Timed out after 15 seconds"));
        _status.Text = "2 online • 1 degraded • 1 unreachable";
        _grid.ClearSelection();
        _grid.CurrentCell = null;
    }
}
