using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudFolderBrowser
{
    public partial class FogLinkForm : Theming.ThemedForm
    {
        public FogLinkForm()
        {
            InitializeComponent();
            serverAddress_textBox.Text = FogLink.ServerAddress.OriginalString;
            label1.Text = "FogLink server address";
            saveServerAddress_button.Text = "Save";
            saveServerAddress_button.Tag = "subtle";
            encrypt_button.Text = "Encrypt URL";
            encrypt_button.Tag = "primary";
            encrypt_button.Width = 124;
            progressBar1.Left = encrypt_button.Right + 10;
            progressBar1.Width = Math.Max(100, ClientSize.Width - progressBar1.Left - 18);
            progressBar1.Visible = false;
        }

        private async void encrypt_button_Click(object sender, EventArgs e)
        {
            if (!Uri.TryCreate(in_textBox.Text.Trim(), UriKind.Absolute, out _))
            {
                MessageBox.Show("Enter a valid URL to encrypt.", "Invalid URL", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            encrypt_button.Enabled = false;
            progressBar1.Visible = true;
            progressBar1.Style = ProgressBarStyle.Marquee;
            progressBar1.MarqueeAnimationSpeed = 30;
            try
            {
                out_textBox.Text = await FogLink.GetEncodedAsync(in_textBox.Text.Trim());
            }
            catch (Exception ex)
            {
                out_textBox.Text = string.Empty;
                MessageBox.Show("FogLink request failed: " + ex.Message, "FogLink error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                encrypt_button.Enabled = true;
                progressBar1.Visible = false;
            }
        }

        private void saveServerAddress_button_Click(object sender, EventArgs e)
        {
            string value = serverAddress_textBox.Text.Trim();
            if (value == "")
                value = @"https://foglink.onrender.com/";
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? serverUri)
                || serverUri.Scheme is not ("http" or "https"))
            {
                MessageBox.Show("Enter a valid HTTP or HTTPS FogLink server address.", "Invalid server address", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            FogLink.ServerAddress = serverUri;
            serverAddress_textBox.Text = FogLink.ServerAddress.OriginalString;
            Properties.Settings.Default.fogLinkAddress = FogLink.ServerAddress.OriginalString;
            Properties.Settings.Default.Save();
        }
    }
}
