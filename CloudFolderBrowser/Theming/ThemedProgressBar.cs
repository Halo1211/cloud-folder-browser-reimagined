using System.ComponentModel;

namespace CloudFolderBrowser.Theming
{
    /// <summary>
    /// A deterministic, theme-aware progress bar. The native Windows progress
    /// control renders small values in theme-dependent chunks, which makes bars
    /// with similar percentages appear to have different sizes.
    /// </summary>
    public class ThemedProgressBar : ProgressBar
    {
        private readonly System.Windows.Forms.Timer marqueeTimer;
        private int marqueeOffset;
        private Color trackColor = Color.Empty;
        private Color progressColor = Color.Empty;
        private Color borderColor = Color.Empty;

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color TrackColor
        {
            get => trackColor.IsEmpty ? ThemeManager.Palette.SurfaceRaised : trackColor;
            set
            {
                trackColor = value;
                Invalidate();
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color ProgressColor
        {
            get => progressColor.IsEmpty ? ThemeManager.Palette.Accent : progressColor;
            set
            {
                progressColor = value;
                Invalidate();
            }
        }

        [Category("Appearance")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color BorderColor
        {
            get => borderColor.IsEmpty ? ThemeManager.Palette.Border : borderColor;
            set
            {
                borderColor = value;
                Invalidate();
            }
        }

        public ThemedProgressBar()
        {
            SetStyle(
                ControlStyles.UserPaint
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer,
                true);

            marqueeTimer = new System.Windows.Forms.Timer { Interval = 30 };
            marqueeTimer.Tick += (_, _) =>
            {
                marqueeOffset = (marqueeOffset + 5) % Math.Max(1, Width + 1);
                Invalidate();
            };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            DrawProgressBar(e.Graphics);
            UpdateMarqueeTimer();
        }

        protected virtual void DrawProgressBar(Graphics graphics)
        {
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            using SolidBrush trackBrush = new(TrackColor);
            using SolidBrush progressBrush = new(ProgressColor);
            using Pen borderPen = new(BorderColor);
            graphics.FillRectangle(trackBrush, bounds);

            Rectangle content = Rectangle.Inflate(bounds, -1, -1);
            if (content.Width > 0 && content.Height > 0)
            {
                if (Style == ProgressBarStyle.Marquee)
                {
                    int segmentWidth = Math.Max(12, content.Width / 4);
                    int travelWidth = content.Width + segmentWidth;
                    int x = content.Left + (marqueeOffset % Math.Max(1, travelWidth)) - segmentWidth;
                    Rectangle segment = Rectangle.Intersect(content, new Rectangle(x, content.Top, segmentWidth, content.Height));
                    if (!segment.IsEmpty)
                        graphics.FillRectangle(progressBrush, segment);
                }
                else
                {
                    int range = Maximum - Minimum;
                    double ratio = range <= 0 ? 0 : (double)(Value - Minimum) / range;
                    ratio = Math.Clamp(ratio, 0d, 1d);
                    int fillWidth = (int)Math.Round(content.Width * ratio, MidpointRounding.AwayFromZero);
                    if (ratio > 0 && fillWidth == 0)
                        fillWidth = 1;
                    if (fillWidth > 0)
                        graphics.FillRectangle(progressBrush, content.Left, content.Top, fillWidth, content.Height);
                }
            }

            graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            UpdateMarqueeTimer();
        }

        private void UpdateMarqueeTimer()
        {
            bool shouldRun = Visible && Style == ProgressBarStyle.Marquee && MarqueeAnimationSpeed > 0;
            if (marqueeTimer.Enabled != shouldRun)
                marqueeTimer.Enabled = shouldRun;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                marqueeTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
