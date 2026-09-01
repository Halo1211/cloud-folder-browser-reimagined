namespace CloudFolderBrowser.FormsSecondary
{
    partial class SyncSettingsForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            checkDownloadedFileSize_checkBox = new CheckBox();
            maxDownloadRetries_numericUpDown = new NumericUpDown();
            folderNewFiles_checkBox = new CheckBox();
            overwriteMode_comboBox = new ComboBox();
            maximumDownloads_numericUpDown = new NumericUpDown();
            groupBox1 = new GroupBox();
            groupBox2 = new GroupBox();
            retryDelay_numericUpDown = new NumericUpDown();
            groupBox3 = new GroupBox();
            groupBox4 = new GroupBox();
            groupBox5 = new GroupBox();
            checkFileSizeError_numericUpDown = new NumericUpDown();
            groupBox6 = new GroupBox();
            groupBox7 = new GroupBox();
            flareSolverr_groupBox = new GroupBox();
            flareSolverrEnabled_checkBox = new CheckBox();
            flareSolverrUrl_label = new Label();
            flareSolverrUrl_textBox = new TextBox();
            flareSolverrTimeout_label = new Label();
            flareSolverrTimeout_numericUpDown = new NumericUpDown();
            flareSolverrTest_button = new Button();
            flareSolverrStatus_label = new Label();
            toolTip1 = new ToolTip(components);
            ((System.ComponentModel.ISupportInitialize)maxDownloadRetries_numericUpDown).BeginInit();
            ((System.ComponentModel.ISupportInitialize)maximumDownloads_numericUpDown).BeginInit();
            groupBox1.SuspendLayout();
            groupBox2.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)retryDelay_numericUpDown).BeginInit();
            groupBox3.SuspendLayout();
            groupBox4.SuspendLayout();
            groupBox5.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)checkFileSizeError_numericUpDown).BeginInit();
            groupBox6.SuspendLayout();
            groupBox7.SuspendLayout();
            flareSolverr_groupBox.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)flareSolverrTimeout_numericUpDown).BeginInit();
            SuspendLayout();
            // 
            // checkDownloadedFileSize_checkBox
            // 
            checkDownloadedFileSize_checkBox.AutoSize = true;
            checkDownloadedFileSize_checkBox.FlatStyle = FlatStyle.Flat;
            checkDownloadedFileSize_checkBox.Location = new Point(26, 92);
            checkDownloadedFileSize_checkBox.Name = "checkDownloadedFileSize_checkBox";
            checkDownloadedFileSize_checkBox.Size = new Size(170, 19);
            checkDownloadedFileSize_checkBox.TabIndex = 0;
            checkDownloadedFileSize_checkBox.Text = "Check Downloaded File Size";
            checkDownloadedFileSize_checkBox.UseVisualStyleBackColor = true;
            checkDownloadedFileSize_checkBox.CheckedChanged += checkDownloadedFileSize_checkBox_CheckedChanged;
            // 
            // maxDownloadRetries_numericUpDown
            // 
            maxDownloadRetries_numericUpDown.Location = new Point(6, 22);
            maxDownloadRetries_numericUpDown.Maximum = new decimal(new int[] { 5, 0, 0, 0 });
            maxDownloadRetries_numericUpDown.Name = "maxDownloadRetries_numericUpDown";
            maxDownloadRetries_numericUpDown.Size = new Size(120, 23);
            maxDownloadRetries_numericUpDown.TabIndex = 1;
            maxDownloadRetries_numericUpDown.ValueChanged += maxDownloadRetries_numericUpDown_ValueChanged;
            // 
            // folderNewFiles_checkBox
            // 
            folderNewFiles_checkBox.FlatStyle = FlatStyle.Flat;
            folderNewFiles_checkBox.Location = new Point(252, 167);
            folderNewFiles_checkBox.Margin = new Padding(4, 3, 4, 3);
            folderNewFiles_checkBox.Name = "folderNewFiles_checkBox";
            folderNewFiles_checkBox.Size = new Size(188, 19);
            folderNewFiles_checkBox.TabIndex = 23;
            folderNewFiles_checkBox.Text = "Download to \"New Files\" folder";
            folderNewFiles_checkBox.UseVisualStyleBackColor = true;
            folderNewFiles_checkBox.CheckedChanged += folderNewFiles_checkBox_CheckedChanged;
            // 
            // overwriteMode_comboBox
            // 
            overwriteMode_comboBox.FlatStyle = FlatStyle.Flat;
            overwriteMode_comboBox.FormattingEnabled = true;
            overwriteMode_comboBox.Location = new Point(7, 23);
            overwriteMode_comboBox.Margin = new Padding(4, 3, 4, 3);
            overwriteMode_comboBox.Name = "overwriteMode_comboBox";
            overwriteMode_comboBox.Size = new Size(151, 23);
            overwriteMode_comboBox.TabIndex = 26;
            overwriteMode_comboBox.SelectedIndexChanged += overwriteMode_comboBox_SelectedIndexChanged;
            // 
            // maximumDownloads_numericUpDown
            // 
            maximumDownloads_numericUpDown.BorderStyle = BorderStyle.FixedSingle;
            maximumDownloads_numericUpDown.Location = new Point(7, 22);
            maximumDownloads_numericUpDown.Margin = new Padding(4, 3, 4, 3);
            maximumDownloads_numericUpDown.Maximum = new decimal(new int[] { 4, 0, 0, 0 });
            maximumDownloads_numericUpDown.Minimum = new decimal(new int[] { 1, 0, 0, 0 });
            maximumDownloads_numericUpDown.Name = "maximumDownloads_numericUpDown";
            maximumDownloads_numericUpDown.Size = new Size(151, 23);
            maximumDownloads_numericUpDown.TabIndex = 24;
            maximumDownloads_numericUpDown.Value = new decimal(new int[] { 4, 0, 0, 0 });
            maximumDownloads_numericUpDown.ValueChanged += maximumDownloads_numericUpDown_ValueChanged;
            // 
            // groupBox1
            // 
            groupBox1.Controls.Add(maxDownloadRetries_numericUpDown);
            groupBox1.Location = new Point(19, 27);
            groupBox1.Name = "groupBox1";
            groupBox1.Size = new Size(145, 58);
            groupBox1.TabIndex = 28;
            groupBox1.TabStop = false;
            groupBox1.Text = "Max Retries";
            // 
            // groupBox2
            // 
            groupBox2.Controls.Add(retryDelay_numericUpDown);
            groupBox2.Location = new Point(19, 91);
            groupBox2.Name = "groupBox2";
            groupBox2.Size = new Size(145, 58);
            groupBox2.TabIndex = 28;
            groupBox2.TabStop = false;
            groupBox2.Text = "Retry Delay";
            // 
            // retryDelay_numericUpDown
            // 
            retryDelay_numericUpDown.Location = new Point(6, 22);
            retryDelay_numericUpDown.Maximum = new decimal(new int[] { 900, 0, 0, 0 });
            retryDelay_numericUpDown.Minimum = new decimal(new int[] { 100, 0, 0, 0 });
            retryDelay_numericUpDown.Name = "retryDelay_numericUpDown";
            retryDelay_numericUpDown.Size = new Size(120, 23);
            retryDelay_numericUpDown.TabIndex = 1;
            retryDelay_numericUpDown.Value = new decimal(new int[] { 100, 0, 0, 0 });
            retryDelay_numericUpDown.ValueChanged += retryDelay_numericUpDown_ValueChanged;
            // 
            // groupBox3
            // 
            groupBox3.Controls.Add(overwriteMode_comboBox);
            groupBox3.Location = new Point(245, 96);
            groupBox3.Name = "groupBox3";
            groupBox3.Size = new Size(170, 65);
            groupBox3.TabIndex = 29;
            groupBox3.TabStop = false;
            groupBox3.Text = "File Overwrite";
            // 
            // groupBox4
            // 
            groupBox4.Controls.Add(maximumDownloads_numericUpDown);
            groupBox4.Location = new Point(245, 27);
            groupBox4.Name = "groupBox4";
            groupBox4.Size = new Size(170, 58);
            groupBox4.TabIndex = 28;
            groupBox4.TabStop = false;
            groupBox4.Text = "Max concurrent downloads";
            // 
            // groupBox5
            // 
            groupBox5.Controls.Add(checkFileSizeError_numericUpDown);
            groupBox5.Location = new Point(20, 28);
            groupBox5.Name = "groupBox5";
            groupBox5.Size = new Size(145, 58);
            groupBox5.TabIndex = 28;
            groupBox5.TabStop = false;
            groupBox5.Text = "File Size Error Margin";
            // 
            // checkFileSizeError_numericUpDown
            // 
            checkFileSizeError_numericUpDown.DecimalPlaces = 4;
            checkFileSizeError_numericUpDown.Increment = new decimal(new int[] { 1, 0, 0, 196608 });
            checkFileSizeError_numericUpDown.Location = new Point(6, 22);
            checkFileSizeError_numericUpDown.Maximum = new decimal(new int[] { 1, 0, 0, 0 });
            checkFileSizeError_numericUpDown.Minimum = new decimal(new int[] { 99, 0, 0, 131072 });
            checkFileSizeError_numericUpDown.Name = "checkFileSizeError_numericUpDown";
            checkFileSizeError_numericUpDown.Size = new Size(120, 23);
            checkFileSizeError_numericUpDown.TabIndex = 1;
            checkFileSizeError_numericUpDown.Value = new decimal(new int[] { 999, 0, 0, 196608 });
            checkFileSizeError_numericUpDown.ValueChanged += checkFileSizeError_numericUpDown_ValueChanged;
            // 
            // groupBox6
            // 
            groupBox6.Controls.Add(groupBox1);
            groupBox6.Controls.Add(groupBox2);
            groupBox6.Location = new Point(13, 181);
            groupBox6.Name = "groupBox6";
            groupBox6.Size = new Size(213, 166);
            groupBox6.TabIndex = 30;
            groupBox6.TabStop = false;
            groupBox6.Text = "Download Retries";
            // 
            // groupBox7
            // 
            groupBox7.Controls.Add(groupBox5);
            groupBox7.Controls.Add(checkDownloadedFileSize_checkBox);
            groupBox7.Location = new Point(12, 27);
            groupBox7.Name = "groupBox7";
            groupBox7.Size = new Size(214, 134);
            groupBox7.TabIndex = 31;
            groupBox7.TabStop = false;
            groupBox7.Text = "Filesize Check";
            //
            // flareSolverr_groupBox
            //
            flareSolverr_groupBox.Controls.Add(flareSolverrStatus_label);
            flareSolverr_groupBox.Controls.Add(flareSolverrTest_button);
            flareSolverr_groupBox.Controls.Add(flareSolverrTimeout_numericUpDown);
            flareSolverr_groupBox.Controls.Add(flareSolverrTimeout_label);
            flareSolverr_groupBox.Controls.Add(flareSolverrUrl_textBox);
            flareSolverr_groupBox.Controls.Add(flareSolverrUrl_label);
            flareSolverr_groupBox.Controls.Add(flareSolverrEnabled_checkBox);
            flareSolverr_groupBox.Location = new Point(245, 197);
            flareSolverr_groupBox.Name = "flareSolverr_groupBox";
            flareSolverr_groupBox.Size = new Size(268, 160);
            flareSolverr_groupBox.TabIndex = 32;
            flareSolverr_groupBox.TabStop = false;
            flareSolverr_groupBox.Text = "Cloudflare / FlareSolverr (optional)";
            //
            // flareSolverrEnabled_checkBox
            //
            flareSolverrEnabled_checkBox.AutoSize = true;
            flareSolverrEnabled_checkBox.Location = new Point(12, 23);
            flareSolverrEnabled_checkBox.Name = "flareSolverrEnabled_checkBox";
            flareSolverrEnabled_checkBox.Size = new Size(126, 19);
            flareSolverrEnabled_checkBox.TabIndex = 0;
            flareSolverrEnabled_checkBox.Text = "Use FlareSolverr";
            flareSolverrEnabled_checkBox.UseVisualStyleBackColor = true;
            flareSolverrEnabled_checkBox.CheckedChanged += flareSolverrEnabled_checkBox_CheckedChanged;
            //
            // flareSolverrUrl_label
            //
            flareSolverrUrl_label.AutoSize = true;
            flareSolverrUrl_label.Location = new Point(12, 47);
            flareSolverrUrl_label.Name = "flareSolverrUrl_label";
            flareSolverrUrl_label.Size = new Size(57, 15);
            flareSolverrUrl_label.TabIndex = 1;
            flareSolverrUrl_label.Text = "Server URL";
            //
            // flareSolverrUrl_textBox
            //
            flareSolverrUrl_textBox.Location = new Point(12, 64);
            flareSolverrUrl_textBox.Name = "flareSolverrUrl_textBox";
            flareSolverrUrl_textBox.PlaceholderText = "http://127.0.0.1:8191";
            flareSolverrUrl_textBox.Size = new Size(244, 23);
            flareSolverrUrl_textBox.TabIndex = 2;
            //
            // flareSolverrTimeout_label
            //
            flareSolverrTimeout_label.AutoSize = true;
            flareSolverrTimeout_label.Location = new Point(12, 99);
            flareSolverrTimeout_label.Name = "flareSolverrTimeout_label";
            flareSolverrTimeout_label.Size = new Size(49, 15);
            flareSolverrTimeout_label.TabIndex = 3;
            flareSolverrTimeout_label.Text = "Timeout";
            //
            // flareSolverrTimeout_numericUpDown
            //
            flareSolverrTimeout_numericUpDown.Location = new Point(67, 96);
            flareSolverrTimeout_numericUpDown.Maximum = new decimal(new int[] { 300, 0, 0, 0 });
            flareSolverrTimeout_numericUpDown.Minimum = new decimal(new int[] { 10, 0, 0, 0 });
            flareSolverrTimeout_numericUpDown.Name = "flareSolverrTimeout_numericUpDown";
            flareSolverrTimeout_numericUpDown.Size = new Size(60, 23);
            flareSolverrTimeout_numericUpDown.TabIndex = 4;
            flareSolverrTimeout_numericUpDown.Value = new decimal(new int[] { 60, 0, 0, 0 });
            //
            // flareSolverrTest_button
            //
            flareSolverrTest_button.Location = new Point(139, 95);
            flareSolverrTest_button.Name = "flareSolverrTest_button";
            flareSolverrTest_button.Size = new Size(117, 25);
            flareSolverrTest_button.TabIndex = 5;
            flareSolverrTest_button.Text = "Test connection";
            flareSolverrTest_button.UseVisualStyleBackColor = true;
            flareSolverrTest_button.Click += flareSolverrTest_button_Click;
            //
            // flareSolverrStatus_label
            //
            flareSolverrStatus_label.AutoEllipsis = true;
            flareSolverrStatus_label.Location = new Point(12, 128);
            flareSolverrStatus_label.Name = "flareSolverrStatus_label";
            flareSolverrStatus_label.Size = new Size(244, 20);
            flareSolverrStatus_label.TabIndex = 6;
            flareSolverrStatus_label.Tag = "muted";
            flareSolverrStatus_label.Text = "Direct mode is active";
            // 
            // SyncSettingsForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(526, 382);
            Controls.Add(flareSolverr_groupBox);
            Controls.Add(groupBox7);
            Controls.Add(groupBox6);
            Controls.Add(groupBox3);
            Controls.Add(groupBox4);
            Controls.Add(folderNewFiles_checkBox);
            Name = "SyncSettingsForm";
            Text = "Sync Settings";
            FormClosing += SyncSettingsForm_FormClosing;
            Load += SyncSettingsForm_Load;
            ((System.ComponentModel.ISupportInitialize)maxDownloadRetries_numericUpDown).EndInit();
            ((System.ComponentModel.ISupportInitialize)maximumDownloads_numericUpDown).EndInit();
            groupBox1.ResumeLayout(false);
            groupBox2.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)retryDelay_numericUpDown).EndInit();
            groupBox3.ResumeLayout(false);
            groupBox4.ResumeLayout(false);
            groupBox5.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)checkFileSizeError_numericUpDown).EndInit();
            groupBox6.ResumeLayout(false);
            groupBox7.ResumeLayout(false);
            groupBox7.PerformLayout();
            flareSolverr_groupBox.ResumeLayout(false);
            flareSolverr_groupBox.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)flareSolverrTimeout_numericUpDown).EndInit();
            ResumeLayout(false);
        }

        #endregion

        private CheckBox checkDownloadedFileSize_checkBox;
        private NumericUpDown maxDownloadRetries_numericUpDown;
        private CheckBox folderNewFiles_checkBox;
        private ComboBox overwriteMode_comboBox;
        private NumericUpDown maximumDownloads_numericUpDown;
        private GroupBox groupBox1;
        private GroupBox groupBox2;
        private NumericUpDown retryDelay_numericUpDown;
        private GroupBox groupBox3;
        private GroupBox groupBox4;
        private GroupBox groupBox5;
        private NumericUpDown checkFileSizeError_numericUpDown;
        private GroupBox groupBox6;
        private GroupBox groupBox7;
        private ToolTip toolTip1;
        private GroupBox flareSolverr_groupBox;
        private CheckBox flareSolverrEnabled_checkBox;
        private Label flareSolverrUrl_label;
        private TextBox flareSolverrUrl_textBox;
        private Label flareSolverrTimeout_label;
        private NumericUpDown flareSolverrTimeout_numericUpDown;
        private Button flareSolverrTest_button;
        private Label flareSolverrStatus_label;
    }
}
