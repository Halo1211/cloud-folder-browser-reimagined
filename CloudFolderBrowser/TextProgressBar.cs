using System;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using CloudFolderBrowser.Theming;

//https://github.com/ukushu/TextProgressBar
namespace CloudFolderBrowser
{
    public enum ProgressBarDisplayMode
    {
        NoText,
        Percentage,
        CurrProgress,
        CustomText,
        TextAndPercentage,
        TextAndCurrProgress
    }

    public class TextProgressBar : ThemedProgressBar
    {
        [Description("Font of the text on ProgressBar"), Category("Additional Options"),
         DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Font TextFont { get; set; } = new Font(FontFamily.GenericSerif, 11, FontStyle.Bold | FontStyle.Italic);

        private SolidBrush _textColourBrush = new(Color.Black);
        [Category("Additional Options"), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public Color TextColor
        {
            get
            {
                return _textColourBrush.Color;
            }
            set
            {
                _textColourBrush.Dispose();
                _textColourBrush = new SolidBrush(value);
                Invalidate();
            }
        }

        private ProgressBarDisplayMode _visualMode = ProgressBarDisplayMode.CurrProgress;
        [Category("Additional Options"), Browsable(true),
         DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public ProgressBarDisplayMode VisualMode
        {
            get
            {
                return _visualMode;
            }
            set
            {
                _visualMode = value;
                Invalidate();//redraw component after change value from VS Properties section
            }
        }

        private string _text = string.Empty;

        [Description("If it's empty, % will be shown"), Category("Additional Options"), Browsable(true),
         EditorBrowsable(EditorBrowsableState.Always),
         DesignerSerializationVisibility(DesignerSerializationVisibility.Visible)]
        public string CustomText
        {
            get
            {
                return _text;
            }
            set
            {
                _text = value;
                Invalidate();//redraw component after change value from VS Properties section
            }
        }

        private string _textToDraw
        {
            get
            {
                string text = CustomText;

                switch (VisualMode)
                {
                    case (ProgressBarDisplayMode.Percentage):
                        text = _percentageStr;
                        break;
                    case (ProgressBarDisplayMode.CurrProgress):
                        text = _currProgressStr;
                        break;
                    case (ProgressBarDisplayMode.TextAndCurrProgress):
                        text = $"{CustomText}: {_currProgressStr}";
                        break;
                    case (ProgressBarDisplayMode.TextAndPercentage):
                        text = $"{CustomText}: {_percentageStr}";
                        break;
                }

                return text;
            }
            set { }
        }

        private string _percentageStr
        {
            get
            {
                int range = Maximum - Minimum;
                int percentage = range <= 0 ? 0 : (int)((float)(Value - Minimum) / range * 100);
                return $"{percentage} %";
            }
        }

        private string _currProgressStr
        {
            get
            {
                return $"{Value}/{Maximum}";
            }
        }

        public TextProgressBar()
        {
            Value = Minimum;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            DrawStringIfNeeded(e.Graphics);
        }

        private void DrawStringIfNeeded(Graphics g)
        {
            if (VisualMode != ProgressBarDisplayMode.NoText)
            {

                string text = _textToDraw;

                SizeF len = g.MeasureString(text, TextFont);

                Point location = new Point(((Width / 2) - (int)len.Width / 2), ((Height / 2) - (int)len.Height / 2));

                g.DrawString(text, TextFont, _textColourBrush, location);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _textColourBrush.Dispose();
            base.Dispose(disposing);
        }
    }
}


