using CloudFolderBrowser.Networking;
using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser.FormsSecondary;

public sealed class ProviderProxyRulesForm : ThemedForm
{
    private readonly ListView _list = new();
    private readonly List<ProviderProxyRule> _rules;

    public ProviderProxyRulesForm()
    {
        Text = "Provider proxy rules";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 430);
        MinimumSize = new Size(680, 380);
        _rules = ProviderProxyRuleStore.Load().Select(Clone).ToList();
        BuildUi();
        RefreshList();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        root.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "A provider rule overrides the global proxy. Providers without a rule keep the global setting.",
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "muted"
        }, 0, 0);

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("Provider", 190);
        _list.Columns.Add("Mode", 110);
        _list.Columns.Add("Proxy", 275);
        _list.Columns.Add("Auth", 120);
        _list.DoubleClick += (_, _) => EditSelected();
        root.Controls.Add(_list, 0, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 9, 0, 0)
        };
        Button done = CreateButton("Done", 96, "primary");
        done.Click += (_, _) => SaveAndClose();
        Button remove = CreateButton("Remove", 92);
        remove.Click += (_, _) => RemoveSelected();
        Button edit = CreateButton("Edit", 82);
        edit.Click += (_, _) => EditSelected();
        Button add = CreateButton("Add rule", 96);
        add.Click += (_, _) => AddRule();
        buttons.Controls.Add(done);
        buttons.Controls.Add(remove);
        buttons.Controls.Add(edit);
        buttons.Controls.Add(add);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = done;
    }

    private static Button CreateButton(string text, int width, string? role = null) => new()
    {
        Text = text,
        Width = width,
        Height = 32,
        Tag = role,
        Margin = new Padding(8, 0, 0, 0)
    };

    private void AddRule()
    {
        using var editor = new ProviderProxyRuleEditorForm(null, _rules.Select(rule => rule.RouteKey));
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;
        _rules.RemoveAll(rule => rule.RouteKey.Equals(editor.Rule.RouteKey, StringComparison.OrdinalIgnoreCase));
        _rules.Add(editor.Rule);
        RefreshList(editor.Rule.RouteKey);
    }

    private void EditSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not ProviderProxyRule selected)
            return;
        using var editor = new ProviderProxyRuleEditorForm(selected, _rules
            .Where(rule => !ReferenceEquals(rule, selected))
            .Select(rule => rule.RouteKey));
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;
        int index = _rules.IndexOf(selected);
        _rules[index] = editor.Rule;
        RefreshList(editor.Rule.RouteKey);
    }

    private void RemoveSelected()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not ProviderProxyRule selected)
            return;
        _rules.Remove(selected);
        RefreshList();
    }

    private void RefreshList(string? selectedKey = null)
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (ProviderProxyRule rule in _rules.OrderBy(rule => ProviderRouteCatalog.GetDisplayName(rule.RouteKey)))
        {
            var item = new ListViewItem(ProviderRouteCatalog.GetDisplayName(rule.RouteKey)) { Tag = rule };
            item.SubItems.Add(rule.Mode.ToString());
            item.SubItems.Add(rule.Mode == ProxyMode.Custom ? rule.ProxyUrl : "—");
            item.SubItems.Add(string.IsNullOrWhiteSpace(rule.Username) ? "No" : "Yes");
            _list.Items.Add(item);
            if (rule.RouteKey.Equals(selectedKey, StringComparison.OrdinalIgnoreCase))
                item.Selected = true;
        }
        _list.EndUpdate();
    }

    private void SaveAndClose()
    {
        try
        {
            ProviderProxyRuleStore.Save(_rules);
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid proxy rule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static ProviderProxyRule Clone(ProviderProxyRule rule) => new()
    {
        RouteKey = rule.RouteKey,
        Mode = rule.Mode,
        ProxyUrl = rule.ProxyUrl,
        Username = rule.Username,
        ProtectedPassword = rule.ProtectedPassword,
        BypassLocal = rule.BypassLocal
    };
}

internal sealed class ProviderProxyRuleEditorForm : ThemedForm
{
    private readonly ComboBox _provider = new();
    private readonly ComboBox _mode = new();
    private readonly TextBox _url = new();
    private readonly TextBox _username = new();
    private readonly TextBox _password = new();
    private readonly CheckBox _bypassLocal = new();
    private readonly HashSet<string> _unavailable;
    public ProviderProxyRule Rule { get; private set; } = new();

    public ProviderProxyRuleEditorForm(ProviderProxyRule? existing, IEnumerable<string> unavailable)
    {
        _unavailable = new HashSet<string>(unavailable, StringComparer.OrdinalIgnoreCase);
        Text = existing == null ? "Add proxy rule" : "Edit proxy rule";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(520, 350);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        BuildUi();
        LoadValues(existing);
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 7, Padding = new Padding(20) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 135));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        AddRow(root, "Provider", _provider, 0);
        AddRow(root, "Mode", _mode, 1);
        AddRow(root, "Proxy URL", _url, 2);
        AddRow(root, "Username", _username, 3);
        _password.UseSystemPasswordChar = true;
        AddRow(root, "Password", _password, 4);
        _bypassLocal.Text = "Bypass proxy for local addresses";
        _bypassLocal.AutoSize = true;
        _bypassLocal.Margin = new Padding(0, 10, 0, 0);
        root.Controls.Add(_bypassLocal, 1, 5);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 38 };
        var save = new Button { Text = "Save", Tag = "primary", Width = 88, Height = 32 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 88, Height = 32 };
        save.Click += (_, _) => SaveRule();
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        root.SetColumnSpan(actions, 2);
        root.Controls.Add(actions, 0, 6);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
        _mode.SelectedIndexChanged += (_, _) => UpdateEnabledState();
    }

    private static void AddRow(TableLayoutPanel root, string label, Control control, int row)
    {
        root.Controls.Add(new Label { Text = label, AutoSize = true, Margin = new Padding(0, 10, 0, 0) }, 0, row);
        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 0, 7);
        root.Controls.Add(control, 1, row);
    }

    private void LoadValues(ProviderProxyRule? existing)
    {
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.DataSource = ProviderRouteCatalog.Options.ToList();
        _provider.DisplayMember = "Value";
        _provider.ValueMember = "Key";
        _mode.DropDownStyle = ComboBoxStyle.DropDownList;
        _mode.DataSource = new[] { ProxyMode.Direct, ProxyMode.System, ProxyMode.Custom };
        if (existing != null)
        {
            _provider.SelectedValue = existing.RouteKey;
            _provider.Enabled = false;
            _mode.SelectedItem = existing.Mode;
            _url.Text = existing.ProxyUrl;
            _username.Text = existing.Username;
            _password.Text = existing.Password;
            _bypassLocal.Checked = existing.BypassLocal;
        }
        else
        {
            _mode.SelectedItem = ProxyMode.Custom;
            _bypassLocal.Checked = true;
        }
        UpdateEnabledState();
    }

    private void UpdateEnabledState()
    {
        bool custom = _mode.SelectedItem is ProxyMode.Custom;
        _url.Enabled = custom;
        _username.Enabled = custom;
        _password.Enabled = custom;
        _bypassLocal.Enabled = custom;
    }

    private void SaveRule()
    {
        string routeKey = _provider.SelectedValue?.ToString() ?? string.Empty;
        if (_provider.Enabled && _unavailable.Contains(routeKey))
        {
            MessageBox.Show(this, "That provider already has a proxy rule.");
            return;
        }
        var rule = new ProviderProxyRule
        {
            RouteKey = routeKey,
            Mode = _mode.SelectedItem is ProxyMode mode ? mode : ProxyMode.System,
            ProxyUrl = _url.Text.Trim(),
            Username = _username.Text.Trim(),
            BypassLocal = _bypassLocal.Checked
        };
        rule.Password = _password.Text;
        try
        {
            ProviderProxyRuleStore.Validate(rule);
            Rule = rule;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            MessageBox.Show(this, ex.Message, "Invalid proxy rule", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
