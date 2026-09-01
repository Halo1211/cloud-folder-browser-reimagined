using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudFolderBrowser
{
    public partial class PasswordForm : Theming.ThemedForm
    {
        public string Password = "";

        public PasswordForm()
        {
            InitializeComponent();
            ConfigureModernUi();
            CenterToParent();
        }

        private void ConfigureModernUi()
        {
            SuspendLayout();
            Controls.Clear();
            ClientSize = new Size(560, 230);
            MinimumSize = new Size(520, 220);
            MaximumSize = Size.Empty;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                Padding = new Padding(24, 20, 24, 20),
                Tag = "window"
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(new Label
            {
                Text = "This share requires a password.",
                Dock = DockStyle.Fill,
                Tag = "muted",
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            root.Controls.Add(new Label { Text = "Password", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 1);
            password_textBox.Dock = DockStyle.Fill;
            password_textBox.Margin = new Padding(0, 4, 0, 4);
            password_textBox.UseSystemPasswordChar = true;
            root.Controls.Add(password_textBox, 0, 2);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 14, 0, 0)
            };
            confirmPassword_button.Text = "Continue";
            confirmPassword_button.Tag = "primary";
            confirmPassword_button.Size = new Size(124, 40);
            confirmPassword_button.DialogResult = DialogResult.None;
            button1.Text = "Cancel";
            button1.Tag = "subtle";
            button1.Size = new Size(104, 40);
            button1.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(confirmPassword_button);
            footer.Controls.Add(button1);
            root.Controls.Add(footer, 0, 3);
            Controls.Add(root);
            AcceptButton = confirmPassword_button;
            CancelButton = button1;
            ResumeLayout(true);
        }

        private void confirmPassword_button_Click(object sender, EventArgs e)
        {
            if (password_textBox.Text != "")
            {
                Password = password_textBox.Text;
                DialogResult = DialogResult.OK;
                Close();
            }
            else
            {
                MessageBox.Show("Enter the share password.", "Password required", MessageBoxButtons.OK, MessageBoxIcon.Information);
                password_textBox.Focus();
            }
        }

        private void cancelPassword_button_Click(object sender, EventArgs e)
        {
            Password = "";
            DialogResult = DialogResult.Cancel;
            Close();
        }
    }
}
