using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CloudFolderBrowser
{
    public partial class DownloadsFinishedForm : Theming.ThemedForm
    {
        string downloadFolder;

        public DownloadsFinishedForm(string directoryInfo, string message, string message2 = "")
        {            
            downloadFolder = directoryInfo;
            InitializeComponent();
            ClientSize = new Size(500, 160);
            MinimumSize = new Size(500, 160);
            MaximumSize = Size.Empty;
            message_label.Text = message;
            failed_label.Text = message2;
            OK_button.Text = "Close";
            OK_button.Tag = "subtle";
            OK_button.Width = 104;
            OK_button.Location = new Point(ClientSize.Width - 244, ClientSize.Height - 54);
            OK_button.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            opneFolder_button.Text = "Open folder";
            opneFolder_button.Tag = "primary";
            opneFolder_button.Width = 124;
            opneFolder_button.Location = new Point(ClientSize.Width - 132, ClientSize.Height - 54);
            opneFolder_button.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            CenterToParent();
        }

        private void OK_button_Click(object sender, EventArgs e)
        {
            Close();
            Dispose();
        }

        private void ppenFolder_button_Click(object sender, EventArgs e)
        {
            Process.Start(new ProcessStartInfo { FileName = downloadFolder, UseShellExecute = true });
            Close();
            Dispose();
        }
    }
}
