using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CG.Web.MegaApiClient;
using Newtonsoft.Json;

namespace CloudFolderBrowser
{
    public partial class LoginMegaForm : Theming.ThemedForm
    {
        private readonly MainForm _mainForm;
        public LoginMegaForm(MainForm parentForm)
        {
            _mainForm = parentForm;
            InitializeComponent();
            ConfigureModernUi();
            CenterToParent();
        }

        private void ConfigureModernUi()
        {
            SuspendLayout();
            Controls.Clear();
            ClientSize = new Size(620, 330);
            MinimumSize = new Size(620, 330);
            MaximumSize = Size.Empty;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(28, 22, 28, 22),
                Tag = "window"
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));

            warning_label.Text = "Use a separate MEGA account for safer imports.";
            warning_label.Dock = DockStyle.Fill;
            warning_label.TextAlign = ContentAlignment.MiddleLeft;
            warning_label.Tag = "muted";
            root.Controls.Add(warning_label, 0, 0);
            root.Controls.Add(new Label { Text = "Email", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 1);
            login_textBox.Dock = DockStyle.Fill;
            login_textBox.Margin = new Padding(0, 4, 0, 4);
            root.Controls.Add(login_textBox, 0, 2);
            root.Controls.Add(new Label { Text = "Password", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 3);
            password_textBox.Dock = DockStyle.Fill;
            password_textBox.Margin = new Padding(0, 4, 0, 4);
            password_textBox.UseSystemPasswordChar = true;
            root.Controls.Add(password_textBox, 0, 4);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136F));
            savePassword_checkBox.Text = "Remember password";
            savePassword_checkBox.AutoSize = true;
            savePassword_checkBox.Anchor = AnchorStyles.Left;
            footer.Controls.Add(savePassword_checkBox, 0, 0);
            signin_button.Text = "Sign in";
            signin_button.Tag = "primary";
            signin_button.Dock = DockStyle.Fill;
            signin_button.Margin = new Padding(0);
            footer.Controls.Add(signin_button, 1, 0);
            root.Controls.Add(footer, 0, 6);
            Controls.Add(root);
            AcceptButton = signin_button;
            ResumeLayout(true);
        }

        private void LoginMegaForm_Load(object sender, EventArgs e)
        {
            login_textBox.Text = Properties.Settings.Default.megaLogin;
            password_textBox.Text = Properties.Settings.Default.megaPassword;
        }

        private async void signIn_button_Click(object sender, EventArgs e)
        {
            Properties.Settings.Default.megaLogin = login_textBox.Text;
            if (savePassword_checkBox.Checked)
            {
                Properties.Settings.Default.megaPassword = password_textBox.Text;
                Properties.Settings.Default.Save();
            }
            else
            {
                Properties.Settings.Default.megaPassword = "";
                Properties.Settings.Default.Save();
            }


            try
            {
                signin_button.Enabled = false;
                await _mainForm.LoginMega(login_textBox.Text, password_textBox.Text);
            }
            catch
            {
                MessageBox.Show("Failed to sign in.");
                return;
            }
            finally
            {
                signin_button.Enabled = true;
            }
            Close();            
        }
    }
}
