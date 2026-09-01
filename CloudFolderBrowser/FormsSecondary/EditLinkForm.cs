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
    public partial class EditLinkForm : Theming.ThemedForm
    {
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string LinkName {get;set;}

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public string LinkUrl { get; set; }
        public EditLinkForm(string name, string url)
        {
            InitializeComponent();
            ConfigureModernUi();
            name_textBox.Text = name;
            url_textBox.Text = url;
            CenterToParent();
        }

        public EditLinkForm()
        {
            InitializeComponent();
            ConfigureModernUi();
            CenterToParent();
        }

        private void ConfigureModernUi()
        {
            ClientSize = new Size(620, 260);
            MinimumSize = new Size(560, 250);
            ok_button.DialogResult = DialogResult.None;
            ok_button.Text = "Save";
            ok_button.Tag = "primary";
            ok_button.Size = new Size(112, 38);
            ok_button.Location = new Point(ClientSize.Width - 136, ClientSize.Height - 58);
            ok_button.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            name_textBox.Width = ClientSize.Width - 48;
            name_textBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            url_textBox.Width = ClientSize.Width - 48;
            url_textBox.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;

            var cancel = new Button
            {
                Text = "Cancel",
                Tag = "subtle",
                DialogResult = DialogResult.Cancel,
                Size = new Size(104, 38),
                Location = new Point(ok_button.Left - 112, ok_button.Top),
                Anchor = AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(cancel);
            AcceptButton = ok_button;
            CancelButton = cancel;
        }

        private void ok_button_Click(object sender, EventArgs e)
        {
            string name = name_textBox.Text.Trim();
            string url = url_textBox.Text.Trim();
            if (name.Length == 0 || url.Length == 0)
            {
                MessageBox.Show("Enter both a name and a share URL.", "Incomplete cloud share", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            LinkName = name;
            LinkUrl = url;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
