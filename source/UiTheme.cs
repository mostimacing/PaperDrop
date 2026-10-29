using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaperDrop
{
    internal static class UiTheme
    {
        public static readonly Color Canvas = Color.FromArgb(244, 246, 245);
        public static readonly Color Ink = Color.FromArgb(30, 44, 49);
        public static readonly Color Muted = Color.FromArgb(100, 113, 118);
        public static readonly Color Accent = Color.FromArgb(23, 110, 94);
        public static readonly Color Border = Color.FromArgb(226, 232, 229);
        public static readonly Color Soft = Color.FromArgb(247, 249, 248);
        public static GraphicsPath Rounded(RectangleF rectangle, float radius)
        {
            var path = new GraphicsPath();
            radius = Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2);
            float d = radius * 2;
            if (d <= 0) { path.AddRectangle(rectangle); return path; }
            path.AddArc(rectangle.Left, rectangle.Top, d, d, 180, 90);
            path.AddArc(rectangle.Right - d, rectangle.Top, d, d, 270, 90);
            path.AddArc(rectangle.Right - d, rectangle.Bottom - d, d, d, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class Surface : Panel
    {
        public Color SurfaceColor { get; set; }
        public int CornerRadius { get; set; }
        public Surface()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            SurfaceColor = Color.White;
            CornerRadius = 14;
            ResizeRedraw = true;
        }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent == null ? UiTheme.Canvas : Parent.BackColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = e.Graphics.DpiX / 96F;
            using (GraphicsPath shape = UiTheme.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), CornerRadius * scale))
            using (var fill = new SolidBrush(SurfaceColor))
            using (var edge = new Pen(UiTheme.Border))
            {
                e.Graphics.FillPath(fill, shape);
                e.Graphics.DrawPath(edge, shape);
            }
        }
    }

    internal enum ButtonTone { Primary, Secondary, Quiet, Segment }

    internal sealed class ActionButton : Button
    {
        private bool hovered, pressed, selected;
        public ButtonTone Tone { get; set; }
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }
        public ActionButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.White;
            ForeColor = UiTheme.Ink;
            Dock = DockStyle.Fill;
            Margin = new Padding(0);
            Height = 38;
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Parent == null ? UiTheme.Canvas : Parent.BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = g.DpiX / 96F;
            bool primary = Tone == ButtonTone.Primary;
            Color fill = primary ? UiTheme.Accent : Tone == ButtonTone.Segment && selected ? Color.White : Parent == null ? Color.White : Parent.BackColor;
            Color text = primary ? Color.White : selected ? UiTheme.Ink : UiTheme.Muted;
            Color edge = Tone == ButtonTone.Secondary || (Tone == ButtonTone.Segment && selected) ? UiTheme.Border : Color.Transparent;
            if (Tone == ButtonTone.Secondary) { fill = Color.White; text = UiTheme.Ink; }
            if (hovered && Enabled) { fill = primary ? Color.FromArgb(17, 92, 80) : Color.FromArgb(235, 241, 237); text = primary ? Color.White : UiTheme.Ink; }
            if (pressed && Enabled) fill = primary ? Color.FromArgb(13, 78, 67) : Color.FromArgb(225, 235, 229);
            if (!Enabled) { fill = primary ? Color.FromArgb(199, 217, 209) : fill; text = primary ? Color.White : Color.FromArgb(172, 181, 178); }
            using (GraphicsPath shape = UiTheme.Rounded(new RectangleF(.5F, .5F, Width - 1F, Height - 1F), 8 * scale))
            using (var brush = new SolidBrush(fill))
            {
                g.FillPath(brush, shape);
                if (edge != Color.Transparent) using (var pen = new Pen(edge)) g.DrawPath(pen, shape);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
            {
                var focus = ClientRectangle; focus.Inflate(-(int)(4 * scale), -(int)(4 * scale));
                ControlPaint.DrawFocusRectangle(g, focus, primary ? Color.White : UiTheme.Accent, fill);
            }
        }
    }

    internal sealed class EmptyState : Control
    {
        public string Heading { get; set; }
        public string Hint { get; set; }
        public bool IsCitation { get; set; }
        public EmptyState()
        {
            Dock = DockStyle.Fill;
            BackColor = Color.White;
            ForeColor = UiTheme.Muted;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            float s = e.Graphics.DpiX / 96F;
            float x = Width / 2F, y = Math.Max(45 * s, Height / 2F - 58 * s);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath shape = UiTheme.Rounded(new RectangleF(x - 30 * s, y - 25 * s, 60 * s, 60 * s), 18 * s))
            using (var brush = new SolidBrush(Color.FromArgb(239, 244, 241))) g.FillPath(brush, shape);
            using (var pen = new Pen(Color.FromArgb(97, 129, 115), 1.7F * s))
            {
                if (IsCitation)
                {
                    g.DrawLines(pen, new[] { new PointF(x - 11 * s, y - 6 * s), new PointF(x - 17 * s, y + 2 * s), new PointF(x - 11 * s, y + 10 * s) });
                    g.DrawLines(pen, new[] { new PointF(x + 11 * s, y - 6 * s), new PointF(x + 17 * s, y + 2 * s), new PointF(x + 11 * s, y + 10 * s) });
                    g.DrawLine(pen, x + 3 * s, y - 10 * s, x - 3 * s, y + 14 * s);
                }
                else
                {
                    g.DrawLines(pen, new[] { new PointF(x - 11 * s, y - 12 * s), new PointF(x + 5 * s, y - 12 * s), new PointF(x + 12 * s, y - 5 * s), new PointF(x + 12 * s, y + 18 * s), new PointF(x - 11 * s, y + 18 * s), new PointF(x - 11 * s, y - 12 * s) });
                    g.DrawLine(pen, x - 5 * s, y + 2 * s, x + 6 * s, y + 2 * s);
                    g.DrawLine(pen, x - 5 * s, y + 8 * s, x + 3 * s, y + 8 * s);
                }
            }
            using (var title = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
                TextRenderer.DrawText(g, Heading ?? "", title, new Rectangle(0, (int)(y + 52 * s), Width, (int)(28 * s)), UiTheme.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            using (var hint = new Font("Microsoft YaHei UI", 9F))
                TextRenderer.DrawText(g, Hint ?? "", hint, new Rectangle((int)(12 * s), (int)(y + 88 * s), Width - (int)(24 * s), (int)(48 * s)), UiTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
    }

    internal sealed class BrandMark : Control
    {
        public BrandMark() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(Math.Min(Width, Height) / 44F, Math.Min(Width, Height) / 44F);
            using (GraphicsPath path = UiTheme.Rounded(new RectangleF(0, 0, 43, 43), 11))
            using (var fill = new SolidBrush(UiTheme.Accent)) g.FillPath(fill, path);
            using (var pen = new Pen(Color.White, 1.8F))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                g.DrawLines(pen, new[] { new PointF(14, 10), new PointF(25, 10), new PointF(31, 16), new PointF(31, 33), new PointF(13, 33), new PointF(13, 10) });
                g.DrawLine(pen, 22, 17, 22, 27);
                g.DrawLines(pen, new[] { new PointF(18, 23), new PointF(22, 27), new PointF(26, 23) });
            }
        }
    }
}
