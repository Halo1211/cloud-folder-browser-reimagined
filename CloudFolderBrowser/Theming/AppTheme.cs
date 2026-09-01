using Aga.Controls.Tree;
using CloudFolderBrowser.Branding;
using Microsoft.Win32;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CloudFolderBrowser.Theming
{
    public enum AppThemeMode
    {
        System = 0,
        Light = 1,
        Dark = 2
    }

    public sealed record ThemePalette(
        Color Window,
        Color Surface,
        Color SurfaceRaised,
        Color Border,
        Color Text,
        Color MutedText,
        Color Accent,
        Color AccentHover,
        Color Danger,
        Color Success,
        Color Selection,
        Color Input);

    public static class ThemeManager
    {
        private static readonly Font DefaultFont = new("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

        private static readonly ThemePalette LightPalette = new(
            Color.FromArgb(246, 245, 241),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(241, 239, 234),
            Color.FromArgb(211, 207, 198),
            Color.FromArgb(37, 35, 31),
            Color.FromArgb(105, 101, 93),
            Color.FromArgb(154, 91, 19),
            Color.FromArgb(124, 70, 11),
            Color.FromArgb(180, 35, 24),
            Color.FromArgb(40, 122, 75),
            Color.FromArgb(244, 230, 208),
            Color.White);

        private static readonly ThemePalette DarkPalette = new(
            Color.FromArgb(23, 23, 20),
            Color.FromArgb(32, 32, 29),
            Color.FromArgb(42, 41, 37),
            Color.FromArgb(62, 60, 54),
            Color.FromArgb(243, 241, 235),
            Color.FromArgb(171, 166, 156),
            Color.FromArgb(214, 161, 91),
            Color.FromArgb(229, 184, 121),
            Color.FromArgb(232, 91, 80),
            Color.FromArgb(92, 184, 126),
            Color.FromArgb(69, 56, 37),
            Color.FromArgb(25, 25, 22));

        public static AppThemeMode Mode { get; private set; } = AppThemeMode.System;
        public static bool IsDark => ResolveDarkMode();
        public static ThemePalette Palette => IsDark ? DarkPalette : LightPalette;
        public static Color AccentTextColor => IsDark ? Color.FromArgb(7, 25, 34) : Color.White;

        public static event EventHandler? ThemeChanged;

        public static void SetMode(AppThemeMode mode)
        {
            if (!Enum.IsDefined(typeof(AppThemeMode), mode))
                mode = AppThemeMode.System;

            Mode = mode;
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Apply(Form form)
        {
            ThemePalette palette = Palette;
            form.SuspendLayout();
            try
            {
                form.Font = DefaultFont;
                form.BackColor = palette.Window;
                form.ForeColor = palette.Text;
                ApplyTitleBarTheme(form, IsDark);
                StyleControl(form, palette);
            }
            finally
            {
                form.ResumeLayout(true);
                form.Invalidate(true);
            }
        }

        private static bool ResolveDarkMode()
        {
            if (Mode == AppThemeMode.Dark)
                return true;
            if (Mode == AppThemeMode.Light)
                return false;

            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static void StyleControl(Control control, ThemePalette palette)
        {
            control.ForeColor = palette.Text;

            switch (control)
            {
                case Form form:
                    form.BackColor = palette.Window;
                    break;

                case Button button:
                    StyleButton(button, palette);
                    break;

                case TextBoxBase textBox:
                    textBox.BackColor = textBox.ReadOnly ? palette.SurfaceRaised : palette.Input;
                    textBox.ForeColor = palette.Text;
                    textBox.BorderStyle = string.Equals(textBox.Tag?.ToString(), "borderless-input", StringComparison.OrdinalIgnoreCase)
                        ? BorderStyle.None
                        : BorderStyle.FixedSingle;
                    break;

                case ComboBox comboBox:
                    StyleComboBox(comboBox, palette);
                    break;

                case NumericUpDown numeric:
                    numeric.BackColor = palette.Input;
                    numeric.ForeColor = palette.Text;
                    numeric.BorderStyle = BorderStyle.FixedSingle;
                    ApplyNativeControlTheme(numeric);
                    break;

                case DateTimePicker dateTimePicker:
                    dateTimePicker.BackColor = palette.Input;
                    dateTimePicker.ForeColor = palette.Text;
                    dateTimePicker.CalendarMonthBackground = palette.Surface;
                    dateTimePicker.CalendarForeColor = palette.Text;
                    dateTimePicker.CalendarTitleBackColor = palette.Accent;
                    dateTimePicker.CalendarTitleForeColor = Color.White;
                    ApplyNativeControlTheme(dateTimePicker);
                    break;

                case TextProgressBar textProgressBar:
                    StyleProgressBar(textProgressBar, palette);
                    textProgressBar.TextColor = palette.Text;
                    break;

                case ThemedProgressBar progressBar:
                    StyleProgressBar(progressBar, palette);
                    break;

                case ProgressBar progressBar:
                    progressBar.BackColor = palette.SurfaceRaised;
                    progressBar.ForeColor = palette.Accent;
                    ApplyNativeControlTheme(progressBar);
                    break;

                case ScrollBar scrollBar:
                    ApplyNativeControlTheme(scrollBar);
                    break;

                case TreeViewAdv tree:
                    tree.BackColor = palette.Surface;
                    tree.ForeColor = palette.Text;
                    tree.BorderStyle = BorderStyle.None;
                    tree.LineColor = palette.Border;
                    tree.DragDropMarkColor = palette.Accent;
                    tree.FullRowSelectActiveColor = palette.Selection;
                    tree.FullRowSelectInactiveColor = palette.SurfaceRaised;
                    tree.ColumnHeaderHeight = 32;
                    foreach (TreeColumn column in tree.Columns)
                    {
                        column.HeaderBackColor = palette.SurfaceRaised;
                        column.HeaderTextColor = palette.Text;
                    }
                    break;

                case MenuStrip menu:
                    StyleToolStrip(menu, palette);
                    break;

                case StatusStrip status:
                    StyleToolStrip(status, palette);
                    break;

                case ToolStrip strip:
                    StyleToolStrip(strip, palette);
                    break;

                case GroupBox groupBox:
                    groupBox.BackColor = palette.Surface;
                    groupBox.ForeColor = palette.Text;
                    groupBox.Padding = new Padding(12, 8, 12, 12);
                    break;

                case TableLayoutPanel table:
                    table.BackColor = table.Tag?.ToString()?.ToLowerInvariant() switch
                    {
                        "surface" => palette.Surface,
                        "raised" => palette.SurfaceRaised,
                        "input" => palette.Input,
                        "window" => palette.Window,
                        "transparent" => Color.Transparent,
                        _ => palette.Window
                    };
                    break;

                case Panel panel:
                    panel.BackColor = panel.Tag?.ToString()?.ToLowerInvariant() switch
                    {
                        "header" => palette.Surface,
                        "surface" => palette.Surface,
                        "raised" => palette.SurfaceRaised,
                        "accent" => palette.Accent,
                        "modern-card" => palette.Surface,
                        "toolbar" => palette.Surface,
                        "input-shell" => palette.Input,
                        "divider" => palette.Border,
                        "window" => palette.Window,
                        _ => palette.SurfaceRaised
                    };
                    if (string.Equals(panel.Tag?.ToString(), "card", StringComparison.OrdinalIgnoreCase))
                        panel.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case SplitContainer split:
                    split.BackColor = palette.Border;
                    split.Panel1.BackColor = palette.Surface;
                    split.Panel2.BackColor = palette.Surface;
                    break;

                case ListBox listBox:
                    listBox.BackColor = palette.Surface;
                    listBox.ForeColor = palette.Text;
                    listBox.BorderStyle = BorderStyle.FixedSingle;
                    break;

                case DataGridView grid:
                    StyleGrid(grid, palette);
                    break;

                case LinkLabel link:
                    link.BackColor = Color.Transparent;
                    link.ForeColor = palette.MutedText;
                    link.LinkColor = palette.Accent;
                    link.ActiveLinkColor = palette.AccentHover;
                    link.VisitedLinkColor = palette.Accent;
                    break;

                case Label label:
                    label.BackColor = Color.Transparent;
                    label.ForeColor = label.Tag?.ToString()?.ToLowerInvariant() switch
                    {
                        "muted" => palette.MutedText,
                        "accent-text" => AccentTextColor,
                        "accent-foreground" => palette.Accent,
                        "danger-text" => palette.Danger,
                        "success-text" => palette.Success,
                        _ => palette.Text
                    };
                    break;

                case CheckBox checkBox:
                    checkBox.BackColor = Color.Transparent;
                    checkBox.ForeColor = palette.Text;
                    break;

                case RadioButton radioButton:
                    radioButton.BackColor = Color.Transparent;
                    radioButton.ForeColor = palette.Text;
                    break;

                default:
                    if (control is CircularProgressIndicator
                        || control.GetType().FullName?.Contains("CircularProgressBar", StringComparison.Ordinal) == true)
                    {
                        control.BackColor = Color.Transparent;
                        control.ForeColor = palette.Text;
                    }
                    break;
            }

            if (control.ContextMenuStrip != null)
                StyleToolStrip(control.ContextMenuStrip, palette);

            foreach (Control child in control.Controls)
                StyleControl(child, palette);
        }

        private static void StyleButton(Button button, ThemePalette palette)
        {
            string role = button.Tag?.ToString()?.ToLowerInvariant() ?? string.Empty;
            button.EnabledChanged -= Button_EnabledChanged;
            button.EnabledChanged += Button_EnabledChanged;
            button.Paint -= Button_Paint;
            button.Paint += Button_Paint;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.BorderColor = palette.Border;
            button.FlatAppearance.MouseOverBackColor = role switch
            {
                "primary" => palette.AccentHover,
                "danger" or "danger-subtle" => Color.FromArgb(185, 28, 28),
                "nav-active" => palette.Selection,
                "combo-arrow" => palette.Selection,
                _ => palette.Selection
            };
            button.FlatAppearance.MouseDownBackColor = palette.AccentHover;
            button.Cursor = Cursors.Hand;
            button.Padding = new Padding(6, 0, 6, 0);
            button.UseVisualStyleBackColor = false;
            ApplyRoundedButton(button);

            if (!button.Enabled)
            {
                bool primary = role == "primary";
                button.BackColor = primary
                    ? MixColors(palette.Accent, palette.SurfaceRaised, 0.72F)
                    : palette.SurfaceRaised;
                button.ForeColor = primary
                    ? MixColors(AccentTextColor, palette.MutedText, 0.72F)
                    : palette.MutedText;
                button.Cursor = Cursors.Default;
                return;
            }

            switch (role)
            {
                case "primary":
                    button.BackColor = palette.Accent;
                    button.ForeColor = AccentTextColor;
                    break;
                case "danger":
                    button.BackColor = palette.Danger;
                    button.ForeColor = Color.White;
                    break;
                case "danger-subtle":
                    button.BackColor = palette.Surface;
                    button.ForeColor = palette.Danger;
                    button.FlatAppearance.BorderColor = palette.Surface;
                    break;
                case "nav-active":
                    button.BackColor = palette.Selection;
                    button.ForeColor = palette.Accent;
                    button.FlatAppearance.BorderColor = palette.Selection;
                    break;
                case "ghost":
                    button.BackColor = palette.Surface;
                    button.ForeColor = palette.Text;
                    button.FlatAppearance.BorderColor = palette.Surface;
                    break;
                case "combo-arrow":
                    button.BackColor = palette.Input;
                    button.ForeColor = palette.Text;
                    button.FlatAppearance.BorderColor = palette.Input;
                    break;
                default:
                    button.BackColor = palette.SurfaceRaised;
                    button.ForeColor = palette.Text;
                    break;
            }
        }

        private static void StyleProgressBar(ThemedProgressBar progressBar, ThemePalette palette)
        {
            progressBar.BackColor = palette.SurfaceRaised;
            progressBar.ForeColor = palette.Text;
            progressBar.TrackColor = palette.SurfaceRaised;
            progressBar.ProgressColor = palette.Accent;
            progressBar.BorderColor = palette.Border;
            progressBar.Invalidate();
        }

        private static void Button_EnabledChanged(object? sender, EventArgs e)
        {
            if (sender is Button button)
                StyleButton(button, Palette);
        }

        private static void Button_Paint(object? sender, PaintEventArgs e)
        {
            if (sender is not Button button)
                return;

            ThemePalette palette = Palette;
            string role = button.Tag?.ToString()?.ToLowerInvariant() ?? string.Empty;
            bool hovered = button.Enabled && button.ClientRectangle.Contains(button.PointToClient(Cursor.Position));
            bool pressed = hovered && Control.MouseButtons.HasFlag(MouseButtons.Left);

            Color fill = button.Enabled
                ? role switch
                {
                    "primary" => pressed ? palette.AccentHover : hovered ? palette.AccentHover : palette.Accent,
                    "danger" => hovered ? Color.FromArgb(220, 38, 38) : palette.Danger,
                    "danger-subtle" => hovered ? Color.FromArgb(72, 31, 36) : palette.Surface,
                    "ghost" => hovered ? palette.Selection : palette.Surface,
                    "combo-arrow" => hovered ? palette.Selection : palette.Input,
                    "nav-active" => palette.Selection,
                    _ => hovered ? palette.Selection : palette.SurfaceRaised
                }
                : role == "primary"
                    ? MixColors(palette.Accent, palette.SurfaceRaised, 0.72F)
                    : palette.SurfaceRaised;
            Color foreground = button.Enabled
                ? role switch
                {
                    "primary" => AccentTextColor,
                    "danger" => Color.White,
                    "danger-subtle" => hovered ? Color.FromArgb(255, 215, 215) : palette.Danger,
                    "nav-active" => palette.Accent,
                    _ => palette.Text
                }
                : role == "primary"
                    ? MixColors(AccentTextColor, palette.MutedText, 0.72F)
                    : palette.MutedText;
            Color border = button.Enabled && role == "primary" ? palette.AccentHover : palette.Border;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = ModernCard.CreateRoundedPath(
                new Rectangle(0, 0, Math.Max(1, button.Width - 1), Math.Max(1, button.Height - 1)), 8);
            using SolidBrush brush = new(fill);
            using Pen pen = new(border, 1F);
            e.Graphics.FillPath(brush, path);
            if (role is not "ghost" and not "combo-arrow" || hovered || !button.Enabled)
                e.Graphics.DrawPath(pen, path);

            if (string.IsNullOrEmpty(button.Text))
                return;
            int horizontalInset = button.Width <= 48 ? 2 : 10;
            TextRenderer.DrawText(
                e.Graphics,
                button.Text,
                button.Font,
                Rectangle.Inflate(button.ClientRectangle, -horizontalInset, -2),
                foreground,
                GetTextFormatFlags(button.TextAlign)
                    | TextFormatFlags.SingleLine
                    | TextFormatFlags.EndEllipsis
                    | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix);
        }

        private static TextFormatFlags GetTextFormatFlags(ContentAlignment alignment)
        {
            TextFormatFlags horizontal = alignment is ContentAlignment.TopLeft
                or ContentAlignment.MiddleLeft
                or ContentAlignment.BottomLeft
                ? TextFormatFlags.Left
                : alignment is ContentAlignment.TopRight
                    or ContentAlignment.MiddleRight
                    or ContentAlignment.BottomRight
                    ? TextFormatFlags.Right
                    : TextFormatFlags.HorizontalCenter;
            TextFormatFlags vertical = alignment is ContentAlignment.TopLeft
                or ContentAlignment.TopCenter
                or ContentAlignment.TopRight
                ? TextFormatFlags.Top
                : alignment is ContentAlignment.BottomLeft
                    or ContentAlignment.BottomCenter
                    or ContentAlignment.BottomRight
                    ? TextFormatFlags.Bottom
                    : TextFormatFlags.VerticalCenter;
            return horizontal | vertical;
        }

        private static Color MixColors(Color foreground, Color background, float foregroundWeight)
        {
            float weight = Math.Clamp(foregroundWeight, 0F, 1F);
            float backgroundWeight = 1F - weight;
            return Color.FromArgb(
                (int)(foreground.R * weight + background.R * backgroundWeight),
                (int)(foreground.G * weight + background.G * backgroundWeight),
                (int)(foreground.B * weight + background.B * backgroundWeight));
        }

        private static void StyleComboBox(ComboBox comboBox, ThemePalette palette)
        {
            comboBox.BackColor = palette.Input;
            comboBox.ForeColor = palette.Text;
            comboBox.FlatStyle = FlatStyle.Flat;
            comboBox.DrawMode = DrawMode.OwnerDrawFixed;
            comboBox.ItemHeight = 24;
            comboBox.DrawItem -= ComboBox_DrawItem;
            comboBox.DrawItem += ComboBox_DrawItem;
            ApplyNativeControlTheme(comboBox);
        }

        private static void ComboBox_DrawItem(object? sender, DrawItemEventArgs e)
        {
            if (sender is not ComboBox comboBox || e.Index < 0)
                return;

            ThemePalette palette = Palette;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? palette.Selection : palette.Input;
            using SolidBrush brush = new(background);
            e.Graphics.FillRectangle(brush, e.Bounds);
            TextRenderer.DrawText(
                e.Graphics,
                comboBox.GetItemText(comboBox.Items[e.Index]),
                comboBox.Font,
                Rectangle.Inflate(e.Bounds, -8, 0),
                palette.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private static void ApplyNativeControlTheme(Control control)
        {
            if (!OperatingSystem.IsWindows() || !control.IsHandleCreated)
                return;

            SetWindowTheme(control.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
        }

        private static void ApplyRoundedButton(Button button)
        {
            button.Resize -= Button_Resize;
            button.Resize += Button_Resize;
            UpdateButtonRegion(button);
        }

        private static void Button_Resize(object? sender, EventArgs e)
        {
            if (sender is Button button)
                UpdateButtonRegion(button);
        }

        private static void UpdateButtonRegion(Button button)
        {
            if (button.Width <= 0 || button.Height <= 0)
                return;

            Region? oldRegion = button.Region;
            using GraphicsPath path = ModernCard.CreateRoundedPath(
                new Rectangle(0, 0, Math.Max(1, button.Width - 1), Math.Max(1, button.Height - 1)), 8);
            button.Region = new Region(path);
            oldRegion?.Dispose();
        }

        private static void ApplyTitleBarTheme(Form form, bool dark)
        {
            if (!form.IsHandleCreated || !OperatingSystem.IsWindows())
                return;

            int value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string? appName, string? idList);

        private static void StyleToolStrip(ToolStrip strip, ThemePalette palette)
        {
            strip.BackColor = palette.Surface;
            strip.ForeColor = palette.Text;
            strip.RenderMode = ToolStripRenderMode.Professional;
            strip.Renderer = new ToolStripProfessionalRenderer(new AppColorTable(palette));
            foreach (ToolStripItem item in strip.Items)
                StyleToolStripItem(item, palette);
        }

        private static void StyleToolStripItem(ToolStripItem item, ThemePalette palette)
        {
            item.BackColor = palette.Surface;
            item.ForeColor = palette.Text;
            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.BackColor = palette.Surface;
                dropDown.DropDown.ForeColor = palette.Text;
                foreach (ToolStripItem child in dropDown.DropDownItems)
                    StyleToolStripItem(child, palette);
            }
        }

        private static void StyleGrid(DataGridView grid, ThemePalette palette)
        {
            grid.BackgroundColor = palette.Surface;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = palette.Border;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
            grid.ColumnHeadersHeight = 34;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.RowTemplate.Height = 30;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.DefaultCellStyle.BackColor = palette.Surface;
            grid.DefaultCellStyle.ForeColor = palette.Text;
            grid.DefaultCellStyle.SelectionBackColor = palette.Accent;
            grid.DefaultCellStyle.SelectionForeColor = AccentTextColor;
            grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
            grid.AlternatingRowsDefaultCellStyle.BackColor = palette.SurfaceRaised;
            grid.AlternatingRowsDefaultCellStyle.ForeColor = palette.Text;
            grid.ColumnHeadersDefaultCellStyle.BackColor = palette.SurfaceRaised;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = palette.Text;
            grid.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        }

        private sealed class AppColorTable : ProfessionalColorTable
        {
            private readonly ThemePalette palette;

            public AppColorTable(ThemePalette palette) => this.palette = palette;

            public override Color ToolStripDropDownBackground => palette.Surface;
            public override Color ImageMarginGradientBegin => palette.Surface;
            public override Color ImageMarginGradientMiddle => palette.Surface;
            public override Color ImageMarginGradientEnd => palette.Surface;
            public override Color MenuBorder => palette.Border;
            public override Color MenuItemBorder => palette.Accent;
            public override Color MenuItemSelected => palette.Selection;
            public override Color MenuItemSelectedGradientBegin => palette.Selection;
            public override Color MenuItemSelectedGradientEnd => palette.Selection;
            public override Color MenuItemPressedGradientBegin => palette.SurfaceRaised;
            public override Color MenuItemPressedGradientMiddle => palette.SurfaceRaised;
            public override Color MenuItemPressedGradientEnd => palette.SurfaceRaised;
            public override Color ToolStripGradientBegin => palette.Surface;
            public override Color ToolStripGradientMiddle => palette.Surface;
            public override Color ToolStripGradientEnd => palette.Surface;
            public override Color SeparatorDark => palette.Border;
            public override Color SeparatorLight => palette.SurfaceRaised;
        }
    }

    public class ThemedForm : Form
    {
        public ThemedForm()
        {
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            ThemeManager.ThemeChanged += ThemeManager_ThemeChanged;
        }

        protected override void OnLoad(EventArgs e)
        {
            Icon = ApplicationBrand.CreateIcon();
            base.OnLoad(e);
            ThemeManager.Apply(this);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ThemeManager.ThemeChanged -= ThemeManager_ThemeChanged;
            base.OnHandleDestroyed(e);
        }

        private void ThemeManager_ThemeChanged(object? sender, EventArgs e)
        {
            if (IsDisposed)
                return;

            if (InvokeRequired)
                BeginInvoke(new Action(() => ThemeManager.Apply(this)));
            else
                ThemeManager.Apply(this);
        }
    }
}
