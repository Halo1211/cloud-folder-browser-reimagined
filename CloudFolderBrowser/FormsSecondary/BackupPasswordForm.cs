using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser.FormsSecondary;

internal sealed class BackupPasswordForm : ThemedForm
{
    private readonly TextBox _password = new();
    private readonly TextBox _confirmation = new();
    private readonly bool _confirmPassword;

    public string Password => _password.Text;

    public BackupPasswordForm(bool confirmPassword)
    {
        _confirmPassword = confirmPassword;
        Text = confirmPassword ? "Protect encrypted backup" : "Open encrypted backup";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(480, confirmPassword ? 230 : 185);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BuildUi();
    }

    private void BuildUi()
    {
        int rows = _confirmPassword ? 5 : 4;
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(20)
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        if (_confirmPassword)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var note = new Label
        {
            Text = _confirmPassword
                ? "Accounts and passwords are encrypted with this password. It cannot be recovered."
                : "Enter the password used when this backup was exported.",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Tag = "muted"
        };
        root.SetColumnSpan(note, 2);
        root.Controls.Add(note, 0, 0);
        AddPasswordRow(root, "Password", _password, 1);
        int actionsRow;
        if (_confirmPassword)
        {
            AddPasswordRow(root, "Confirm", _confirmation, 2);
            actionsRow = 4;
        }
        else
        {
            actionsRow = 3;
        }

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false
        };
        var proceed = new Button { Text = _confirmPassword ? "Export" : "Open", Tag = "primary", Width = 92, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 92, Height = 32 };
        proceed.Click += (_, _) => ValidateAndClose();
        actions.Controls.Add(proceed);
        actions.Controls.Add(cancel);
        root.SetColumnSpan(actions, 2);
        root.Controls.Add(actions, 0, actionsRow);
        Controls.Add(root);
        AcceptButton = proceed;
        CancelButton = cancel;
    }

    private static void AddPasswordRow(TableLayoutPanel root, string label, TextBox input, int row)
    {
        root.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, row);
        input.UseSystemPasswordChar = true;
        input.Dock = DockStyle.Fill;
        input.Margin = new Padding(0, 4, 0, 7);
        root.Controls.Add(input, 1, row);
    }

    private void ValidateAndClose()
    {
        if (_password.Text.Length < 8)
        {
            MessageBox.Show(this, "Use at least 8 characters.", "Password too short", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_confirmPassword && !string.Equals(_password.Text, _confirmation.Text, StringComparison.Ordinal))
        {
            MessageBox.Show(this, "The passwords do not match.", "Check password", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
