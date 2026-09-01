using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CloudFolderBrowser.Theming
{
    public class ModernCard : Panel
    {
        private int cornerRadius = 12;

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public int CornerRadius
        {
            get => cornerRadius;
            set
            {
                cornerRadius = Math.Max(0, value);
                UpdateRegion();
                Invalidate();
            }
        }

        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public bool DrawBorder { get; set; } = true;

        public ModernCard()
        {
            DoubleBuffered = true;
            Tag = "modern-card";
            Margin = new Padding(0);
            Padding = new Padding(0);
            Resize += (_, _) => UpdateRegion();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!DrawBorder || Width < 2 || Height < 2)
                return;

            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), cornerRadius);
            using Pen pen = new(ThemeManager.Palette.Border, 1F);
            e.Graphics.DrawPath(pen, path);
        }

        private void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0)
                return;

            Region? oldRegion = Region;
            using GraphicsPath path = CreateRoundedPath(new Rectangle(0, 0, Width, Height), cornerRadius);
            Region = new Region(path);
            oldRegion?.Dispose();
        }

        internal static GraphicsPath CreateRoundedPath(Rectangle rectangle, int radius)
        {
            var path = new GraphicsPath();
            int diameter = Math.Min(Math.Max(radius * 2, 1), Math.Min(rectangle.Width, rectangle.Height));
            if (diameter <= 2)
            {
                path.AddRectangle(rectangle);
                path.CloseFigure();
                return path;
            }

            var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));
            path.AddArc(arc, 180, 90);
            arc.X = rectangle.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rectangle.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rectangle.Left;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public sealed class ModernComboField : ModernCard
    {
        private readonly ComboBox source;
        private readonly Button openButton;

        public ModernComboField(ComboBox sourceComboBox)
        {
            source = sourceComboBox;
            CornerRadius = 8;
            Tag = "input-shell";
            DrawBorder = true;
            Padding = new Padding(1);
            Margin = new Padding(0);

            var host = new Panel
            {
                Dock = DockStyle.Fill,
                Tag = "input",
                Margin = new Padding(0)
            };
            source.Dock = DockStyle.Fill;
            source.Margin = new Padding(0);
            source.FlatStyle = FlatStyle.Flat;

            openButton = new Button
            {
                Text = "\u25BE",
                Tag = "combo-arrow",
                Margin = new Padding(0),
                Padding = new Padding(0),
                TabStop = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom
            };
            openButton.Click += (_, _) =>
            {
                source.Focus();
                source.DroppedDown = true;
            };
            host.Resize += (_, _) => PositionOpenButton(host);
            host.Controls.Add(source);
            host.Controls.Add(openButton);
            openButton.BringToFront();
            Controls.Add(host);
            PositionOpenButton(host);
        }

        private void PositionOpenButton(Control host)
        {
            const int buttonWidth = 30;
            openButton.Bounds = new Rectangle(
                Math.Max(0, host.ClientSize.Width - buttonWidth),
                0,
                Math.Min(buttonWidth, host.ClientSize.Width),
                host.ClientSize.Height);
            openButton.BringToFront();
        }
    }

    public sealed class ModernDateField : ModernCard
    {
        private readonly DateTimePicker source;
        private readonly Label valueLabel;

        public ModernDateField(DateTimePicker sourcePicker)
        {
            source = sourcePicker;
            CornerRadius = 8;
            Tag = "input-shell";
            DrawBorder = true;
            Size = new Size(150, 34);
            Margin = new Padding(0);
            Padding = new Padding(10, 0, 4, 0);

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Tag = "input",
                Margin = new Padding(0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30F));

            valueLabel = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(0),
                Cursor = Cursors.Hand
            };
            var openButton = new Button
            {
                Dock = DockStyle.Fill,
                Text = "\u25BE",
                Tag = "ghost",
                Margin = new Padding(0, 3, 0, 3),
                Padding = new Padding(0),
                TabStop = false
            };
            valueLabel.Click += (_, _) => ShowCalendar();
            openButton.Click += (_, _) => ShowCalendar();
            source.ValueChanged += (_, _) => UpdateText();
            layout.Controls.Add(valueLabel, 0, 0);
            layout.Controls.Add(openButton, 1, 0);
            Controls.Add(layout);
            UpdateText();
        }

        private void UpdateText() => valueLabel.Text = source.Value.ToString("dd MMM yy");

        private void ShowCalendar()
        {
            var calendar = new MonthCalendar
            {
                MaxSelectionCount = 1,
                SelectionStart = source.Value,
                SelectionEnd = source.Value,
                MinDate = source.MinDate,
                MaxDate = source.MaxDate
            };
            var dropDown = new ToolStripDropDown
            {
                AutoClose = true,
                Padding = new Padding(1),
                Margin = new Padding(0)
            };
            var host = new ToolStripControlHost(calendar)
            {
                AutoSize = false,
                Size = calendar.Size,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            dropDown.Items.Add(host);
            calendar.DateSelected += (_, e) =>
            {
                source.Value = e.Start;
                dropDown.Close();
            };
            dropDown.Show(this, new Point(0, Height + 2));
        }
    }
}
