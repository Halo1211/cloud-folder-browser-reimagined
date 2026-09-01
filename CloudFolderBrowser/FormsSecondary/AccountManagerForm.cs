using CloudFolderBrowser.Accounts;
using CloudFolderBrowser.Theming;

namespace CloudFolderBrowser;

public sealed class AccountManagerForm : ThemedForm
{
    private readonly MainForm _mainForm;
    private readonly CloudAccountStore _store;
    private readonly DataGridView _accountsGrid = new();
    private readonly Label _selectionInfo = new();
    private readonly Button _activateButton = new();
    private readonly Button _editButton = new();
    private readonly Button _removeButton = new();

    public AccountManagerForm(MainForm mainForm, CloudAccountStore store)
    {
        _mainForm = mainForm;
        _store = store;
        Text = "Cloud accounts";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(900, 570);
        MinimumSize = new Size(780, 500);
        BuildUi();
        RefreshAccounts();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(22, 18, 22, 18),
            Margin = new Padding(0),
            Tag = "window"
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));

        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
        heading.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Cloud accounts",
            Font = new Font("Segoe UI Semibold", 15F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "Cloud storage and debrid accounts stay separate, encrypted, and easy to switch.",
            Tag = "muted",
            TextAlign = ContentAlignment.TopLeft
        }, 0, 1);
        root.Controls.Add(heading, 0, 0);

        _accountsGrid.Dock = DockStyle.Fill;
        _accountsGrid.Margin = new Padding(0, 8, 0, 8);
        _accountsGrid.AllowUserToAddRows = false;
        _accountsGrid.AllowUserToDeleteRows = false;
        _accountsGrid.AllowUserToResizeRows = false;
        _accountsGrid.AutoGenerateColumns = false;
        _accountsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _accountsGrid.BackgroundColor = ThemeManager.Palette.Surface;
        _accountsGrid.BorderStyle = BorderStyle.FixedSingle;
        _accountsGrid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _accountsGrid.ColumnHeadersHeight = 38;
        _accountsGrid.RowHeadersVisible = false;
        _accountsGrid.RowTemplate.Height = 38;
        _accountsGrid.MultiSelect = false;
        _accountsGrid.ReadOnly = true;
        _accountsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _accountsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Status", FillWeight = 20 });
        _accountsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Provider", FillWeight = 34 });
        _accountsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Account name", FillWeight = 38 });
        _accountsGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Account / server", FillWeight = 54 });
        _accountsGrid.SelectionChanged += (_, _) => UpdateSelection();
        _accountsGrid.CellDoubleClick += async (_, _) => await ActivateSelectedAsync();
        root.Controls.Add(_accountsGrid, 0, 1);

        _selectionInfo.Dock = DockStyle.Fill;
        _selectionInfo.Tag = "muted";
        _selectionInfo.TextAlign = ContentAlignment.MiddleLeft;
        _selectionInfo.Padding = new Padding(10, 0, 10, 0);
        _selectionInfo.Text = "Select an account to activate, edit, or remove it.";
        root.Controls.Add(_selectionInfo, 0, 2);

        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 1, Margin = new Padding(0) };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112F));

        Button add = NewButton("Add account", "primary");
        add.Click += async (_, _) => await AddAccountAsync();
        _editButton.Text = "Edit";
        ConfigureButton(_editButton, "subtle");
        _editButton.Click += async (_, _) => await EditSelectedAsync();
        _activateButton.Text = "Activate";
        ConfigureButton(_activateButton, "primary");
        _activateButton.Click += async (_, _) => await ActivateSelectedAsync();
        _removeButton.Text = "Remove";
        ConfigureButton(_removeButton, "danger-subtle");
        _removeButton.Click += (_, _) => RemoveSelected();
        Button close = NewButton("Close", "subtle");
        close.Click += (_, _) => Close();

        actions.Controls.Add(add, 0, 0);
        actions.Controls.Add(_editButton, 1, 0);
        actions.Controls.Add(_activateButton, 2, 0);
        actions.Controls.Add(_removeButton, 3, 0);
        actions.Controls.Add(close, 5, 0);
        root.Controls.Add(actions, 0, 3);
        Controls.Add(root);
    }

    private static Button NewButton(string text, string tag)
    {
        var button = new Button { Text = text };
        ConfigureButton(button, tag);
        return button;
    }

    private static void ConfigureButton(Button button, string tag)
    {
        button.Tag = tag;
        button.Dock = DockStyle.Fill;
        button.Margin = new Padding(0, 5, 10, 5);
    }

    private void RefreshAccounts(Guid? selectId = null)
    {
        _accountsGrid.Rows.Clear();
        foreach (CloudAccountProfile account in _store.GetAll()
            .OrderBy(profile => profile.ProviderName)
            .ThenBy(profile => profile.DisplayName))
        {
            string location = account.Provider == CloudAccountProvider.WebDav
                ? GetServerDisplay(account.ServerUrl)
                : account.UserName;
            int rowIndex = _accountsGrid.Rows.Add(
                account.IsActive ? "Active" : "Saved",
                account.ProviderName,
                account.DisplayName,
                string.IsNullOrWhiteSpace(location) ? "—" : location);
            _accountsGrid.Rows[rowIndex].Tag = account;
            if (account.IsActive)
                _accountsGrid.Rows[rowIndex].Cells[0].Style.ForeColor = ThemeManager.Palette.Success;
            if (selectId == account.Id)
                _accountsGrid.Rows[rowIndex].Selected = true;
        }
        UpdateSelection();
    }

    private CloudAccountProfile? SelectedAccount => _accountsGrid.SelectedRows.Count == 1
        ? _accountsGrid.SelectedRows[0].Tag as CloudAccountProfile
        : null;

    private void UpdateSelection()
    {
        CloudAccountProfile? selected = SelectedAccount;
        bool hasSelection = selected != null;
        _activateButton.Enabled = hasSelection && !selected!.IsActive;
        _editButton.Enabled = hasSelection;
        _removeButton.Enabled = hasSelection;
        string endpoint = selected?.Provider == CloudAccountProvider.WebDav
            && !string.IsNullOrWhiteSpace(selected.ServerUrl)
            ? Environment.NewLine + selected.ServerUrl
            : string.Empty;
        _selectionInfo.Text = selected == null
            ? "Select an account to activate, edit, or remove it."
            : selected.IsActive
                ? $"{selected.ProviderName} • active • credentials are encrypted for this Windows user.{endpoint}"
                : $"{selected.ProviderName} • saved • activate it to verify the connection.{endpoint}";
    }

    private static string GetServerDisplay(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ? uri.Host : value;

    private async Task AddAccountAsync()
    {
        using var editor = new CloudAccountEditorForm();
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Profile == null)
            return;
        await SaveAsync(editor.Profile);
    }

    private async Task EditSelectedAsync()
    {
        CloudAccountProfile? selected = SelectedAccount;
        if (selected == null)
            return;
        using var editor = new CloudAccountEditorForm(selected);
        if (editor.ShowDialog(this) != DialogResult.OK || editor.Profile == null)
            return;
        await SaveAsync(editor.Profile);
    }

    private async Task SaveAsync(CloudAccountProfile profile)
    {
        try
        {
            SetBusy(true, "Verifying credentials…");
            await _mainForm.SaveAndActivateAccountAsync(profile);
            RefreshAccounts(profile.Id);
            _selectionInfo.Text = $"Connected to {profile.ProviderName}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Account connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task ActivateSelectedAsync()
    {
        CloudAccountProfile? selected = SelectedAccount;
        if (selected == null || selected.IsActive)
            return;
        try
        {
            SetBusy(true, $"Connecting to {selected.ProviderName}…");
            await _mainForm.ActivateAccountAsync(selected);
            RefreshAccounts(selected.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Account connection failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void RemoveSelected()
    {
        CloudAccountProfile? selected = SelectedAccount;
        if (selected == null)
            return;
        if (MessageBox.Show(
            this,
            $"Remove {selected.DisplayName}? The encrypted credential will also be deleted.",
            "Remove account",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning) != DialogResult.Yes)
        {
            return;
        }
        _mainForm.RemoveAccount(selected);
        RefreshAccounts();
    }

    private void SetBusy(bool busy, string? message = null)
    {
        UseWaitCursor = busy;
        _accountsGrid.Enabled = !busy;
        foreach (Control button in Controls.Cast<Control>().SelectMany(Descendants).Where(control => control is Button))
            button.Enabled = !busy;
        if (!string.IsNullOrWhiteSpace(message))
            _selectionInfo.Text = message;
        if (!busy)
            UpdateSelection();
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child))
                yield return nested;
        }
    }
}

public sealed class CloudAccountEditorForm : ThemedForm
{
    private readonly ComboBox _provider = new();
    private readonly TextBox _displayName = new();
    private readonly TextBox _userName = new();
    private readonly TextBox _serverUrl = new();
    private readonly TextBox _secret = new();
    private readonly Label _userLabel = new();
    private readonly Label _serverLabel = new();
    private readonly Label _secretLabel = new();
    private readonly Label _help = new();
    private readonly Button _credentialPageButton = new();
    private readonly CloudAccountProfile? _existing;
    private TableLayoutPanel? _layout;

    public CloudAccountProfile? Profile { get; private set; }

    public CloudAccountEditorForm(CloudAccountProfile? existing = null)
    {
        _existing = existing;
        Text = existing == null ? "Add cloud account" : "Edit cloud account";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(650, 560);
        MinimumSize = new Size(620, 540);
        BuildUi();
        LoadValues();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 13,
            Padding = new Padding(26, 20, 26, 20),
            Margin = new Padding(0),
            Tag = "window"
        };
        _layout = root;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54F));
        for (int index = 1; index <= 8; index++)
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, index % 2 == 1 ? 27F : 42F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 27F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48F));

        root.Controls.Add(new Label
        {
            Text = Text,
            Dock = DockStyle.Fill,
            Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        AddField(root, 1, "Provider", _provider);
        _provider.DropDownStyle = ComboBoxStyle.DropDownList;
        _provider.FormattingEnabled = true;
        _provider.Items.AddRange(Enum.GetValues<CloudAccountProvider>().Cast<object>().ToArray());
        if (_provider.Items.Count > 0)
            _provider.SelectedIndex = 0;
        _provider.Format += (_, e) =>
        {
            if (e.ListItem is CloudAccountProvider provider)
                e.Value = ProviderDisplayName(provider);
        };
        _provider.SelectedIndexChanged += (_, _) => UpdateProviderFields();

        AddField(root, 3, "Account name", _displayName);
        _userLabel.Text = "Email / username";
        AddField(root, 5, _userLabel, _userName);
        _serverLabel.Text = "Server URL";
        AddField(root, 7, _serverLabel, _serverUrl);
        _secretLabel.Text = "Password / access token";
        AddField(root, 9, _secretLabel, _secret);
        _secret.UseSystemPasswordChar = true;

        _help.Dock = DockStyle.Fill;
        _help.Tag = "muted";
        _help.Padding = new Padding(4, 8, 4, 4);
        _help.TextAlign = ContentAlignment.TopLeft;
        root.Controls.Add(_help, 0, 11);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0) };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 118F));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 152F));
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Dock = DockStyle.Fill, Tag = "subtle", Margin = new Padding(0, 4, 10, 4) };
        var save = new Button { Text = "Save & connect", Dock = DockStyle.Fill, Tag = "primary", Margin = new Padding(0, 4, 0, 4) };
        _credentialPageButton.Text = "Get API key";
        _credentialPageButton.Tag = "subtle";
        _credentialPageButton.Anchor = AnchorStyles.Left;
        _credentialPageButton.Size = new Size(140, 38);
        _credentialPageButton.Click += CredentialPageButton_Click;
        save.Click += Save_Click;
        footer.Controls.Add(_credentialPageButton, 0, 0);
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(save, 2, 0);
        root.Controls.Add(footer, 0, 12);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    private static void AddField(TableLayoutPanel root, int labelRow, string label, Control field)
    {
        AddField(root, labelRow, new Label { Text = label }, field);
    }

    private static void AddField(TableLayoutPanel root, int labelRow, Label label, Control field)
    {
        label.Dock = DockStyle.Fill;
        label.TextAlign = ContentAlignment.BottomLeft;
        field.Dock = DockStyle.Fill;
        field.Margin = new Padding(0, 3, 0, 5);
        root.Controls.Add(label, 0, labelRow);
        root.Controls.Add(field, 0, labelRow + 1);
    }

    private void LoadValues()
    {
        if (_existing != null)
        {
            CloudAccountProvider[] providers = Enum.GetValues<CloudAccountProvider>();
            _provider.SelectedIndex = Array.IndexOf(providers, _existing.Provider);
            _provider.Enabled = false;
            _displayName.Text = _existing.DisplayName;
            _userName.Text = _existing.UserName;
            _serverUrl.Text = _existing.ServerUrl;
        }
        UpdateProviderFields();
    }

    private void UpdateProviderFields()
    {
        if (_provider.SelectedItem is not CloudAccountProvider provider)
            return;
        _serverUrl.Visible = _serverLabel.Visible = provider == CloudAccountProvider.WebDav;
        _userName.Visible = _userLabel.Visible = provider is CloudAccountProvider.Mega or CloudAccountProvider.WebDav;
        if (_layout != null)
        {
            bool showUser = provider is CloudAccountProvider.Mega or CloudAccountProvider.WebDav;
            bool showServer = provider == CloudAccountProvider.WebDav;
            _layout.RowStyles[5].Height = showUser ? 27F : 0F;
            _layout.RowStyles[6].Height = showUser ? 42F : 0F;
            _layout.RowStyles[7].Height = showServer ? 27F : 0F;
            _layout.RowStyles[8].Height = showServer ? 42F : 0F;
        }
        _userLabel.Text = provider == CloudAccountProvider.Mega ? "MEGA email" : "Username";
        _secretLabel.Text = provider switch
        {
            CloudAccountProvider.Mega => _existing == null ? "MEGA password" : "New password (leave blank to keep session)",
            CloudAccountProvider.WebDav => _existing == null ? "Password" : "New password (leave blank to keep current)",
            CloudAccountProvider.AllDebrid or CloudAccountProvider.RealDebrid or CloudAccountProvider.DebridLink
                or CloudAccountProvider.Premiumize or CloudAccountProvider.TorBox
                => _existing == null ? "API key / access token" : "New API key / token (leave blank to keep current)",
            _ => _existing == null ? "OAuth access token" : "New access token (leave blank to keep current)"
        };
        _help.Text = provider switch
        {
            CloudAccountProvider.Mega => "The password is exchanged for a MEGA session. Only the encrypted session is saved.",
            CloudAccountProvider.YandexDisk => "Paste a Yandex OAuth token. The connection is verified before saving.",
            CloudAccountProvider.WebDav => "Works with standards-compatible servers, including Nextcloud and ownCloud WebDAV endpoints.",
            CloudAccountProvider.Dropbox => "Public Dropbox links work without an account. Account access uses an OAuth access token.",
            CloudAccountProvider.GoogleDrive => "Public Google Drive file links work without an account. Account access uses an OAuth access token.",
            CloudAccountProvider.AllDebrid => "Paste an AllDebrid API key to resolve supported MEGA and hoster links into temporary direct links.",
            CloudAccountProvider.RealDebrid => "Paste a Real-Debrid API token. It is verified with the official account endpoint before saving.",
            CloudAccountProvider.DebridLink => "Paste a Debrid-Link API key or access token. Its downloader supports MEGA and other hosters.",
            CloudAccountProvider.Premiumize => "Paste a Premiumize.me API key. Direct-download support is checked by Premiumize for each submitted hoster link.",
            CloudAccountProvider.TorBox => "Paste a TorBox API token. Web Downloads can prepare supported hoster links, including TeraBox when the host is available.",
            _ => string.Empty
        };
        _credentialPageButton.Visible = provider is CloudAccountProvider.AllDebrid
            or CloudAccountProvider.RealDebrid
            or CloudAccountProvider.DebridLink
            or CloudAccountProvider.Premiumize
            or CloudAccountProvider.TorBox;
    }

    private void CredentialPageButton_Click(object? sender, EventArgs e)
    {
        if (_provider.SelectedItem is not CloudAccountProvider provider)
            return;
        string? url = provider switch
        {
            CloudAccountProvider.AllDebrid => "https://alldebrid.com/apikeys",
            CloudAccountProvider.RealDebrid => "https://real-debrid.com/apitoken",
            CloudAccountProvider.DebridLink => "https://debrid-link.com/webapp/apikey",
            CloudAccountProvider.Premiumize => "https://www.premiumize.me/account",
            CloudAccountProvider.TorBox => "https://torbox.app/settings",
            _ => null
        };
        if (url != null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void Save_Click(object? sender, EventArgs e)
    {
        if (_provider.SelectedItem is not CloudAccountProvider provider)
            return;
        string displayName = _displayName.Text.Trim();
        string secret = _secret.Text;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            MessageBox.Show(this, "Enter an account name.", "Missing account name", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (_existing == null && string.IsNullOrWhiteSpace(secret))
        {
            MessageBox.Show(this, "Enter the password or access token.", "Missing credential", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (provider == CloudAccountProvider.Mega && string.IsNullOrWhiteSpace(_userName.Text))
        {
            MessageBox.Show(this, "Enter the MEGA email address.", "Missing email", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (provider == CloudAccountProvider.WebDav
            && (!Uri.TryCreate(_serverUrl.Text.Trim(), UriKind.Absolute, out Uri? uri)
                || uri.Scheme is not ("http" or "https")))
        {
            MessageBox.Show(this, "Enter a valid HTTP or HTTPS WebDAV URL.", "Invalid server URL", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Profile = new CloudAccountProfile
        {
            Id = _existing?.Id ?? Guid.NewGuid(),
            Provider = provider,
            DisplayName = displayName,
            UserName = _userName.Text.Trim(),
            ServerUrl = _serverUrl.Text.Trim(),
            Secret = string.IsNullOrEmpty(secret) ? _existing?.Secret ?? string.Empty : secret,
            SecretKind = provider switch
            {
                CloudAccountProvider.Mega when !string.IsNullOrEmpty(secret) => "password",
                CloudAccountProvider.Mega => _existing?.SecretKind ?? "mega-session",
                CloudAccountProvider.WebDav => "password",
                _ => "oauth-token"
            },
            IsActive = _existing?.IsActive ?? false
        };
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string ProviderDisplayName(CloudAccountProvider provider) => provider switch
    {
        CloudAccountProvider.Mega => "MEGA",
        CloudAccountProvider.YandexDisk => "Yandex Disk",
        CloudAccountProvider.WebDav => "WebDAV / Nextcloud / ownCloud",
        CloudAccountProvider.Dropbox => "Dropbox",
        CloudAccountProvider.GoogleDrive => "Google Drive",
        CloudAccountProvider.AllDebrid => "AllDebrid",
        CloudAccountProvider.RealDebrid => "Real-Debrid",
        CloudAccountProvider.DebridLink => "Debrid-Link",
        CloudAccountProvider.Premiumize => "Premiumize.me",
        CloudAccountProvider.TorBox => "TorBox",
        _ => provider.ToString()
    };
}
