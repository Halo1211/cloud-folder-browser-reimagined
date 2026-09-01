using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser.FormsSecondary;

public sealed class NetworkDiagnosticsForm : ThemedForm
{
    private readonly string _report;

    public NetworkDiagnosticsForm(NetworkDiagnosticResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        _report = result.ToReport();
        Text = "Network diagnostics";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(680, 500);
        MinimumSize = new Size(560, 420);
        BuildUi();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            RowCount = 2,
            ColumnCount = 1,
            Padding = new Padding(16)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        var reportBox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            TabStop = false,
            Font = new Font(FontFamily.GenericMonospace, 9.5F),
            Text = _report
        };
        root.Controls.Add(reportBox, 0, 0);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 8, 0, 0)
        };
        var close = new Button { Text = "Close", Tag = "primary", Width = 90, Height = 32, DialogResult = DialogResult.OK };
        var copy = new Button { Text = "Copy", Width = 88, Height = 32 };
        copy.Click += (_, _) =>
        {
            Clipboard.SetText(_report);
            copy.Text = "Copied";
        };
        actions.Controls.Add(close);
        actions.Controls.Add(copy);
        root.Controls.Add(actions, 0, 1);
        Controls.Add(root);
        AcceptButton = close;
        Shown += (_, _) =>
        {
            reportBox.SelectionStart = 0;
            reportBox.SelectionLength = 0;
            close.Select();
        };
    }
}
