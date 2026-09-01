using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CloudFolderBrowser.Theming;

public sealed class CircularProgressIndicator : Control
{
    private readonly System.Windows.Forms.Timer animationTimer;
    private int value;
    private int animationAngle;
    private ProgressBarStyle style = ProgressBarStyle.Continuous;
    private int animationSpeed = 1000;

    public CircularProgressIndicator()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor
            | ControlStyles.UserPaint,
            true);
        BackColor = Color.Transparent;
        ProgressColor = Color.DodgerBlue;
        TrackColor = Color.FromArgb(55, 90, 110, 135);
        animationTimer = new System.Windows.Forms.Timer { Interval = 33 };
        animationTimer.Tick += (_, _) =>
        {
            int angleStep = Math.Max(2, (int)Math.Round(11_880.0 / Math.Max(250, AnimationSpeed)));
            animationAngle = (animationAngle + angleStep) % 360;
            Invalidate();
        };
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Minimum { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Maximum { get; set; } = 100;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, Minimum, Math.Max(Minimum, Maximum));
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int AnimationSpeed
    {
        get => animationSpeed;
        set => animationSpeed = Math.Max(0, value);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public Color ProgressColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public Color TrackColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public int ProgressWidth { get; set; } = 12;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
    public ProgressBarStyle Style
    {
        get => style;
        set
        {
            style = value;
            UpdateAnimationState();
            Invalidate();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateAnimationState();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        int strokeWidth = Math.Clamp(ProgressWidth, 2, Math.Max(2, Math.Min(Width, Height) / 3));
        var bounds = new RectangleF(
            strokeWidth / 2F + 1,
            strokeWidth / 2F + 1,
            Math.Max(1, Width - strokeWidth - 2),
            Math.Max(1, Height - strokeWidth - 2));
        using var trackPen = new Pen(TrackColor, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        using var progressPen = new Pen(ProgressColor, strokeWidth)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        e.Graphics.DrawArc(trackPen, bounds, 0, 360);

        if (Style == ProgressBarStyle.Marquee)
        {
            e.Graphics.DrawArc(progressPen, bounds, animationAngle - 90, 92);
            return;
        }

        int range = Math.Max(1, Maximum - Minimum);
        float sweep = 360F * (Value - Minimum) / range;
        if (sweep > 0)
            e.Graphics.DrawArc(progressPen, bounds, -90, sweep);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            animationTimer.Dispose();
        base.Dispose(disposing);
    }

    private void UpdateAnimationState()
    {
        animationTimer.Enabled = Visible && Style == ProgressBarStyle.Marquee && AnimationSpeed > 0;
    }
}
