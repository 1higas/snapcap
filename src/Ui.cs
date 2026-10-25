using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Snapcap
{
    // Windows 11 (Fluent) colors, taken from the system light/dark mode and accent color.
    class Theme
    {
        public readonly bool Dark;
        public readonly Color Back, Text, Secondary, Strong, Disabled, Fill, Subtle, Stroke, Accent, OnAccent, Error;

        // systemMode: follow "Windows mode" (taskbar, flyouts) instead of "App mode" (windows).
        public Theme(bool systemMode)
        {
            Dark = !IsLight(systemMode ? "SystemUsesLightTheme" : "AppsUseLightTheme");
            Color fg = Dark ? Color.White : Color.Black;
            Back = Dark ? (systemMode ? Rgb(0x2C2C2C) : Rgb(0x202020)) : (systemMode ? Rgb(0xF9F9F9) : Rgb(0xF3F3F3));
            Text = Mix(Back, fg, Dark ? 1 : 0.9);
            Secondary = Mix(Back, fg, Dark ? 0.79 : 0.61);
            Strong = Mix(Back, fg, Dark ? 0.54 : 0.45);
            Disabled = Mix(Back, fg, Dark ? 0.36 : 0.37);
            Fill = Mix(Back, Color.White, Dark ? 0.06 : 0.7);
            Subtle = Mix(Back, fg, Dark ? 0.06 : 0.04);
            Stroke = Mix(Back, fg, Dark ? 0.09 : 0.1);
            Accent = AccentShade(Dark ? 1 : 4);
            OnAccent = Dark ? Color.Black : Color.White;
            Error = Dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C);
        }

        public static bool IsLight(string value)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                return k != null && Equals(k.GetValue(value), 1);
        }

        // AccentPalette holds 8 RGBA shades from lightest to darkest; Windows uses
        // "Light 2" (1) on dark backgrounds and "Dark 1" (4) on light ones.
        static Color AccentShade(int index)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
            {
                byte[] p = k == null ? null : k.GetValue("AccentPalette") as byte[];
                if (p == null || p.Length < 32)
                    return index < 3 ? Rgb(0x60CDFF) : Rgb(0x005FB8);
                return Color.FromArgb(p[index * 4], p[index * 4 + 1], p[index * 4 + 2]);
            }
        }

        // Battery power colors, shared by the tray icon and the chart: green in, orange out.
        public static Color Power(bool charging, bool dark)
        {
            return charging ? Rgb(dark ? 0x6CCB5F : 0x0F7B0F) : Rgb(dark ? 0xFF9F45 : 0xBC4B00);
        }

        public static Color Rgb(int rgb) { return Color.FromArgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Mix(Color a, Color b, double t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }
    }

    // A window drawn entirely by hand in the Windows 11 style: a vertical stack of items
    // (text, toggles, sliders, segmented choices, buttons). Painting is GDI+ on a solid background.
    class FluentForm : Form
    {
        const int WM_DPICHANGED = 0x02E0;

        public readonly Theme Theme;
        public float Zoom;
        public Font Body, Strong, Small, Display, Glyphs;
        public Item Hot;
        public Point Mouse;
        protected int ContentWidth = 360;
        readonly List<Item> items = new List<Item>();
        Item pressed;

        public FluentForm(bool systemTheme)
        {
            Theme = new Theme(systemTheme);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Back;
            SetDpi(Native.DpiAt(Cursor.Position));
            Icon = Native.AppIcon(32);
        }

        // Layout and text are both sized in pixels from one DPI, the scale of the window's monitor,
        // so they always grow together (fonts in points would follow GDI's DPI, which can differ).
        void SetDpi(int dpi)
        {
            Zoom = dpi / 96f;
            Font[] old = { Body, Strong, Small, Display, Glyphs };
            Body = MakeFont("Segoe UI Variable Text", "Segoe UI", 10.5f);
            Strong = MakeFont("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 10.5f);
            Small = MakeFont("Segoe UI Variable Small", "Segoe UI", 9f);
            Display = MakeFont("Segoe UI Variable Display Semib", "Segoe UI Semibold", 21f);
            Glyphs = MakeFont("Segoe Fluent Icons", "Segoe MDL2 Assets", 12f);
            Font = Body;
            foreach (Font font in old)
                if (font != null)
                    font.Dispose();
        }

        Font MakeFont(string family, string fallback, float points)
        {
            float pixels = points * 96 / 72 * Zoom;
            try { return new Font(new FontFamily(family), pixels, GraphicsUnit.Pixel); }
            catch (ArgumentException) { return new Font(fallback, pixels, GraphicsUnit.Pixel); }
        }

        // Called after the DPI changed: the window was created on, or moved to, a monitor with
        // another scale, or the user changed the scale while it was open.
        protected virtual void Rescale()
        {
            Relayout();
        }

        void UseDpi(int dpi)
        {
            if (dpi == (int)Math.Round(Zoom * 96))
                return;
            SetDpi(dpi);
            Rescale();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                UseDpi(m.WParam.ToInt32() & 0xFFFF);
                return;
            }
            base.WndProc(ref m);
        }

        public int Px(float dip) { return (int)Math.Round(dip * Zoom); }

        public T Add<T>(T item) where T : Item
        {
            item.Owner = this;
            items.Add(item);
            return item;
        }

        // Stacks the visible items and sizes the window to fit them.
        public void Relayout()
        {
            int pad = Px(20), width = Px(ContentWidth), y = pad;
            bool first = true;
            foreach (Item item in items)
            {
                if (!item.Visible)
                    continue;
                if (!first)
                    y += Px(item.Gap);
                first = false;
                int h = item.Measure(width - 2 * pad);
                item.Bounds = new Rectangle(pad, y, width - 2 * pad, h);
                y += h;
            }
            ClientSize = new Size(width, y + pad);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Item item in items)
                if (item.Visible)
                    item.Paint(e.Graphics);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            foreach (Item item in items)
                if (item.Visible && item.Enabled && item.Bounds.Contains(e.Location) && item.Press(e.Location))
                {
                    pressed = item;
                    Capture = true;
                    Invalidate();
                    return;
                }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            Mouse = e.Location;
            if (pressed != null)
            {
                pressed.Drag(e.Location);
                Invalidate();
                return;
            }
            Item hot = null;
            foreach (Item item in items)
                if (item.Visible && item.Bounds.Contains(e.Location))
                    hot = item;
            if (hot != Hot || (hot != null && hot.TracksMouse))
            {
                Hot = hot;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            Hot = null;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (pressed == null)
                return;
            Item item = pressed;
            pressed = null;
            item.Release(e.Location);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.UseDarkTitleBar(Handle, Theme.Dark, Theme.Back);
            UseDpi(Native.DpiOf(Handle));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Body.Dispose(); Strong.Dispose(); Small.Dispose(); Display.Dispose(); Glyphs.Dispose();
            }
            base.Dispose(disposing);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            if (r.Width <= 0 || r.Height <= 0)
                return;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c))
                g.FillPath(b, p);
        }

        public void StrokeRound(Graphics g, Color c, RectangleF r, float radius)
        {
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, Math.Max(1, Zoom)))
                g.DrawPath(pen, p);
        }

        public void DrawText(Graphics g, string text, Font font, Color color, Rectangle r, TextFormatFlags align)
        {
            TextRenderer.DrawText(g, text, font, r, color, align | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }
    }

    abstract class Item
    {
        public FluentForm Owner;
        public Rectangle Bounds;
        public bool Visible = true, Enabled = true;
        public float Gap = 12;

        protected Theme T { get { return Owner.Theme; } }
        protected Color TextColor { get { return Enabled ? T.Text : T.Disabled; } }

        // Repaint on every mouse move over the item (for hover effects).
        public virtual bool TracksMouse { get { return false; } }

        public abstract int Measure(int width);
        public abstract void Paint(Graphics g);
        public virtual bool Press(Point p) { return false; }
        public virtual void Drag(Point p) { }
        public virtual void Release(Point p) { }
    }

    class TextItem : Item
    {
        public string Text;
        public Font Font;
        public Color? Tint;

        public TextItem(string text, Font font) { Text = text; Font = font; }

        public override int Measure(int width)
        {
            return TextRenderer.MeasureText(string.IsNullOrEmpty(Text) ? " " : Text, Font, new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
        }

        public override void Paint(Graphics g)
        {
            Owner.DrawText(g, Text, Font, Tint ?? (Enabled ? T.Text : T.Disabled), Bounds, TextFormatFlags.WordBreak | TextFormatFlags.VerticalCenter);
        }
    }

    class Divider : Item
    {
        public Divider() { Gap = 16; }
        public override int Measure(int width) { return Math.Max(1, Owner.Px(1)); }
        public override void Paint(Graphics g)
        {
            using (SolidBrush b = new SolidBrush(T.Stroke))
                g.FillRectangle(b, Owner.ClientRectangle.X, Bounds.Y, Owner.ClientSize.Width, Bounds.Height);
        }
    }

    // Label on the left, Windows 11 toggle switch on the right; the whole row is clickable.
    class Toggle : Item
    {
        public string Text;
        public bool On;
        public event Action<bool> Changed;

        public Toggle(string text, bool on) { Text = text; On = on; }

        public override int Measure(int width) { return Owner.Px(24); }

        public override void Paint(Graphics g)
        {
            Owner.DrawText(g, Text, Owner.Body, TextColor, Bounds, TextFormatFlags.VerticalCenter);
            float w = Owner.Px(40), h = Owner.Px(20), x = Bounds.Right - w, y = Bounds.Y + (Bounds.Height - h) / 2;
            RectangleF track = new RectangleF(x, y, w, h);
            if (On)
                Owner.FillRound(g, Enabled ? T.Accent : T.Disabled, track, h / 2);
            else
                Owner.StrokeRound(g, Enabled ? T.Strong : T.Disabled, RectangleF.Inflate(track, -0.5f * Owner.Zoom, -0.5f * Owner.Zoom), h / 2);
            float knob = Owner.Px(12), kx = On ? x + w - h / 2 - knob / 2 : x + h / 2 - knob / 2;
            Owner.FillRound(g, On ? T.OnAccent : (Enabled ? T.Strong : T.Disabled), new RectangleF(kx, y + (h - knob) / 2, knob, knob), knob / 2);
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            if (!Bounds.Contains(p))
                return;
            On = !On;
            if (Changed != null)
                Changed(On);
        }
    }

    // Title row: the app icon or a back arrow on the left, an optional icon button on the right.
    class TitleBar : Item
    {
        public string Text, Glyph, Status;
        public Image Image;
        public bool GlyphOnLeft;
        public event Action Clicked;

        public TitleBar(string text, string glyph, bool glyphOnLeft) { Text = text; Glyph = glyph; GlyphOnLeft = glyphOnLeft; Gap = 0; }

        public override bool TracksMouse { get { return true; } }

        public override int Measure(int width) { return Owner.Px(32); }

        Rectangle Button
        {
            get
            {
                int s = Owner.Px(32), overhang = Owner.Px(8);
                return new Rectangle(GlyphOnLeft ? Bounds.X - overhang : Bounds.Right - s + overhang, Bounds.Y, s, s);
            }
        }

        public override void Paint(Graphics g)
        {
            Rectangle b = Button;
            int x = Bounds.X;
            if (GlyphOnLeft)
                x = b.Right + Owner.Px(4);
            else if (Image != null)
            {
                int s = Owner.Px(20);
                g.DrawImage(Image, x, Bounds.Y + (Bounds.Height - s) / 2, s, s);
                x += Owner.Px(28);
            }
            Owner.DrawText(g, Text, Owner.Strong, T.Text, new Rectangle(x, Bounds.Y, Bounds.Right - x, Bounds.Height), TextFormatFlags.VerticalCenter);
            // A short status beside the button; drawn in space that is already there, so nothing moves.
            if (Status != null && !GlyphOnLeft)
                Owner.DrawText(g, Status, Owner.Small, T.Secondary, new Rectangle(x, Bounds.Y, b.X - Owner.Px(4) - x, Bounds.Height), TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            if (Owner.Hot == this && b.Contains(Owner.Mouse))
                Owner.FillRound(g, T.Subtle, b, Owner.Px(4));
            Owner.DrawText(g, Glyph, Owner.Glyphs, T.Text, b, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        public override bool Press(Point p) { return Button.Contains(p); }

        public override void Release(Point p)
        {
            if (Button.Contains(p) && Clicked != null)
                Clicked();
        }
    }

    // Windows 11 slider with label and value on top; snaps to Step and reports the value on release.
    class Slider : Item
    {
        public string Text;
        public int Min, Max, Step, Value;
        public event Action Changed;

        public Slider(string text, int min, int max, int step, int value) { Text = text; Min = min; Max = max; Step = step; Value = value; }

        public override int Measure(int width) { return Owner.Px(52); }

        RectangleF Track
        {
            get
            {
                float thumb = Owner.Px(20);
                return new RectangleF(Bounds.X + thumb / 2, Bounds.Bottom - thumb / 2 - Owner.Px(2), Bounds.Width - thumb, Owner.Px(4));
            }
        }

        public override void Paint(Graphics g)
        {
            Rectangle top = new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, Owner.Px(20));
            Owner.DrawText(g, Text, Owner.Body, TextColor, top, TextFormatFlags.VerticalCenter);
            Owner.DrawText(g, Value + "%", Owner.Strong, TextColor, top, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            RectangleF t = Track;
            float x = t.X + t.Width * (Value - Min) / (Max - Min), dot = Owner.Px(2);
            Owner.FillRound(g, Enabled ? T.Strong : T.Disabled, t, t.Height / 2);
            Owner.FillRound(g, Enabled ? T.Accent : T.Disabled, new RectangleF(t.X, t.Y, x - t.X, t.Height), t.Height / 2);
            for (int v = Min; v <= Max; v += Step)
                Owner.FillRound(g, T.Stroke, new RectangleF(t.X + t.Width * (v - Min) / (Max - Min) - dot / 2, t.Bottom + Owner.Px(5), dot, dot), dot / 2);

            float outer = Owner.Px(20), inner = Owner.Px(12), y = t.Y + t.Height / 2;
            RectangleF thumb = new RectangleF(x - outer / 2, y - outer / 2, outer, outer);
            Owner.FillRound(g, T.Dark ? Theme.Rgb(0x454545) : Color.White, thumb, outer / 2);
            Owner.StrokeRound(g, T.Stroke, thumb, outer / 2);
            Owner.FillRound(g, Enabled ? T.Accent : T.Disabled, new RectangleF(x - inner / 2, y - inner / 2, inner, inner), inner / 2);
        }

        public override bool Press(Point p) { Drag(p); return true; }

        public override void Drag(Point p)
        {
            RectangleF t = Track;
            Value = Math.Max(Min, Math.Min(Max, Min + (int)Math.Round((p.X - t.X) / t.Width * (Max - Min) / Step) * Step));
        }

        public override void Release(Point p)
        {
            if (Changed != null)
                Changed();
        }
    }

    // Battery power over time around a zero line: green above it (charging), orange below,
    // grey on it. Hovering shows the time and value of the nearest reading in the caption.
    class Chart : Item
    {
        public IEnumerable<Sample> Samples;
        public int Minutes, MaxGapSeconds;

        public Chart() { Gap = 16; }

        public override bool TracksMouse { get { return true; } }

        public override int Measure(int width) { return Owner.Px(121); }

        public static string Watts(int mw)
        {
            return mw == 0 ? "0 W" : string.Format("{0}{1:0.0} W", mw > 0 ? "+" : "−", Math.Abs(mw) / 1000.0);
        }

        public override void Paint(Graphics g)
        {
            DateTime now = DateTime.UtcNow, from = now.AddMinutes(-Minutes);
            List<Sample> shown = new List<Sample>();
            int top = 0, bottom = 0;
            foreach (Sample s in Samples)
                if (s.Time >= from)
                {
                    shown.Add(s);
                    top = Math.Max(top, s.Mw);
                    bottom = Math.Min(bottom, s.Mw);
                }

            RectangleF plot = new RectangleF(Bounds.X, Bounds.Y + Owner.Px(22), Bounds.Width, Bounds.Height - Owner.Px(22));
            // Scale to the data, keeping zero in view; with no flow at all, zero sits in the middle.
            double hi = top > 0 ? top : bottom < 0 ? 0 : 1000, lo = bottom < 0 ? bottom : top > 0 ? 0 : -1000, pad = (hi - lo) * 0.12;
            if (hi > 0) hi += pad;
            if (lo < 0) lo -= pad;
            Func<int, float> y = mw => plot.Bottom - (float)((mw - lo) / (hi - lo)) * plot.Height;
            Func<DateTime, float> x = t => plot.Right - (float)((now - t).TotalSeconds / (Minutes * 60.0)) * plot.Width;
            float zero = y(0);

            int hover = -1;
            Point mouse = Owner.Mouse;
            if (Owner.Hot == this)
                for (int i = 0; i < shown.Count; i++)
                    if (hover < 0 || Math.Abs(x(shown[i].Time) - mouse.X) < Math.Abs(x(shown[hover].Time) - mouse.X))
                        hover = i;

            Rectangle caption = new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, Owner.Px(16));
            string left = Minutes >= 60 ? "Last hour" : "Last " + Minutes + " min";
            string right = "";
            if (hover >= 0)
            {
                left = shown[hover].Time.ToLocalTime().ToString("T");
                right = Watts(shown[hover].Mw);
            }
            Owner.DrawText(g, left, Owner.Small, T.Secondary, caption, TextFormatFlags.VerticalCenter);
            Owner.DrawText(g, right, hover >= 0 ? Owner.Strong : Owner.Small, hover >= 0 ? T.Text : T.Secondary, caption, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);

            Owner.FillRound(g, T.Subtle, plot, Owner.Px(4));
            float width = Math.Max(1, 1.5f * Owner.Zoom);
            using (Pen axis = new Pen(T.Stroke, Math.Max(1, Owner.Zoom)))
                g.DrawLine(axis, plot.X, zero, plot.Right, zero);

            // The line is split where it crosses zero and each piece takes the color of its own sign,
            // decided from the readings rather than from pixels, so 0 W is always grey.
            // Both arrays are indexed by sign + 1; readings at 0 W have a line but no area.
            GraphicsPath[] lines = { new GraphicsPath(), new GraphicsPath(), new GraphicsPath() }, areas = { new GraphicsPath(), null, new GraphicsPath() };
            try
            {
                List<PointF> piece = new List<PointF>();
                int sign = 0;
                for (int i = 1; i < shown.Count; i++)
                {
                    Sample a = shown[i - 1], b = shown[i];
                    PointF p = new PointF(x(a.Time), y(a.Mw)), q = new PointF(x(b.Time), y(b.Mw));
                    if ((b.Time - a.Time).TotalSeconds > MaxGapSeconds)
                    {
                        AddPiece(piece, sign, lines, areas, zero);
                        continue;
                    }
                    int sa = Math.Sign(a.Mw), sb = Math.Sign(b.Mw);
                    if (sa * sb < 0)
                    {
                        PointF cross = new PointF(p.X + (q.X - p.X) * (float)((double)a.Mw / (a.Mw - b.Mw)), zero);
                        Extend(piece, ref sign, sa, p, cross, lines, areas, zero);
                        Extend(piece, ref sign, sb, cross, q, lines, areas, zero);
                    }
                    else
                        Extend(piece, ref sign, sa != 0 ? sa : sb, p, q, lines, areas, zero);
                }
                AddPiece(piece, sign, lines, areas, zero);

                for (int s = -1; s <= 1; s += 2)
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(64, Theme.Power(s > 0, T.Dark))))
                        g.FillPath(brush, areas[s + 1]);
                for (int s = -1; s <= 1; s++)
                    using (Pen pen = new Pen(s == 0 ? T.Strong : Theme.Power(s > 0, T.Dark), width))
                        g.DrawPath(pen, lines[s + 1]);
            }
            finally
            {
                foreach (GraphicsPath path in lines)
                    path.Dispose();
                areas[0].Dispose();
                areas[2].Dispose();
            }

            if (hover >= 0)
            {
                float hx = x(shown[hover].Time), hy = y(shown[hover].Mw), dot = Owner.Px(7);
                using (Pen guide = new Pen(T.Strong, Math.Max(1, Owner.Zoom)))
                    g.DrawLine(guide, hx, plot.Y, hx, plot.Bottom);
                int mw = shown[hover].Mw;
                Owner.FillRound(g, mw == 0 ? T.Strong : Theme.Power(mw > 0, T.Dark), new RectangleF(hx - dot / 2, hy - dot / 2, dot, dot), dot / 2);
            }
            if (shown.Count < 2)
                Owner.DrawText(g, "Collecting data…", Owner.Small, T.Secondary, Rectangle.Round(plot), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // Continues the current piece with a segment of the given sign, or starts a new piece.
        static void Extend(List<PointF> piece, ref int sign, int segmentSign, PointF from, PointF to, GraphicsPath[] lines, GraphicsPath[] areas, float zero)
        {
            if (piece.Count > 0 && segmentSign != sign)
                AddPiece(piece, sign, lines, areas, zero);
            if (piece.Count == 0)
                piece.Add(from);
            piece.Add(to);
            sign = segmentSign;
        }

        // One stretch of a single sign: the line itself, and the area between it and zero.
        static void AddPiece(List<PointF> piece, int sign, GraphicsPath[] lines, GraphicsPath[] areas, float zero)
        {
            if (piece.Count >= 2)
            {
                lines[sign + 1].StartFigure();
                lines[sign + 1].AddLines(piece.ToArray());
                if (sign != 0)
                {
                    piece.Add(new PointF(piece[piece.Count - 1].X, zero));
                    piece.Add(new PointF(piece[0].X, zero));
                    areas[sign + 1].AddPolygon(piece.ToArray());
                }
            }
            piece.Clear();
        }
    }

    // A large value with a line of secondary text beside it, sharing the bottom edge.
    class Headline : Item
    {
        public string Value, Detail;

        public override int Measure(int width)
        {
            return TextRenderer.MeasureText("0", Owner.Display, Size.Empty, TextFormatFlags.NoPadding).Height;
        }

        public override void Paint(Graphics g)
        {
            int w = TextRenderer.MeasureText(g, Value, Owner.Display, Size.Empty, TextFormatFlags.NoPadding).Width + Owner.Px(10);
            Owner.DrawText(g, Value, Owner.Display, T.Text, Bounds, TextFormatFlags.Bottom);
            Rectangle rest = new Rectangle(Bounds.X + w, Bounds.Y, Bounds.Width - w, Bounds.Height - Owner.Px(4));
            Owner.DrawText(g, Detail, Owner.Body, T.Secondary, rest, TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
        }
    }

    // A quiet one-line notice with an action on the right, such as an available update.
    class Notice : Item
    {
        public string Text, Link;
        public event Action Clicked;

        public Notice() { Gap = 16; }

        public override int Measure(int width) { return Owner.Px(36); }

        public override void Paint(Graphics g)
        {
            Owner.FillRound(g, T.Subtle, Bounds, Owner.Px(4));
            Rectangle inner = Rectangle.Inflate(Bounds, -Owner.Px(12), 0);
            Owner.DrawText(g, Text, Owner.Body, T.Text, inner, TextFormatFlags.VerticalCenter);
            Owner.DrawText(g, Link, Owner.Strong, T.Accent, inner, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            if (Bounds.Contains(p) && Clicked != null)
                Clicked();
        }
    }

    // A row of mutually exclusive options; the selected one is filled with the accent color.
    class Segmented : Item
    {
        public string Text;
        public string[] Options;
        public int Selected;
        public event Action<int> Changed;

        public Segmented(string text, string[] options, int selected) { Text = text; Options = options; Selected = selected; }

        public override int Measure(int width) { return Owner.Px(Text == null ? 32 : 60); }

        Rectangle Segment(int i)
        {
            int top = Bounds.Bottom - Owner.Px(32), w = Bounds.Width / Options.Length;
            return new Rectangle(Bounds.X + i * w, top, i == Options.Length - 1 ? Bounds.Right - Bounds.X - i * w : w, Owner.Px(32));
        }

        public override void Paint(Graphics g)
        {
            if (Text != null)
                Owner.DrawText(g, Text, Owner.Body, TextColor, new Rectangle(Bounds.X, Bounds.Y, Bounds.Width, Owner.Px(20)), TextFormatFlags.VerticalCenter);
            RectangleF all = Rectangle.Union(Segment(0), Segment(Options.Length - 1));
            Owner.FillRound(g, T.Fill, all, Owner.Px(4));
            Owner.StrokeRound(g, T.Stroke, all, Owner.Px(4));
            for (int i = 0; i < Options.Length; i++)
            {
                Rectangle r = Segment(i);
                if (i == Selected && Enabled)
                    Owner.FillRound(g, T.Accent, RectangleF.Inflate(r, -Owner.Px(2), -Owner.Px(2)), Owner.Px(3));
                Color c = i == Selected && Enabled ? T.OnAccent : Enabled ? T.Text : T.Disabled;
                Owner.DrawText(g, Options[i], i == Selected ? Owner.Strong : Owner.Body, c, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            for (int i = 0; i < Options.Length; i++)
                if (Segment(i).Contains(p) && i != Selected)
                {
                    Selected = i;
                    if (Changed != null)
                        Changed(i);
                }
        }
    }

    // Right-aligned buttons; the first one uses the accent color.
    class Buttons : Item
    {
        readonly string[] labels;
        readonly Action[] actions;

        public Buttons(string[] labels, Action[] actions) { this.labels = labels; this.actions = actions; Gap = 24; }

        public override int Measure(int width) { return Owner.Px(32); }

        Rectangle Button(int i)
        {
            int w = Owner.Px(120), gap = Owner.Px(8);
            return new Rectangle(Bounds.Right - (labels.Length - i) * w - (labels.Length - 1 - i) * gap, Bounds.Y, w, Bounds.Height);
        }

        public override void Paint(Graphics g)
        {
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = Button(i);
                bool accent = i == 0;
                Owner.FillRound(g, !Enabled ? T.Fill : accent ? T.Accent : T.Fill, r, Owner.Px(4));
                if (!accent || !Enabled)
                    Owner.StrokeRound(g, T.Stroke, r, Owner.Px(4));
                Owner.DrawText(g, labels[i], Owner.Body, !Enabled ? T.Disabled : accent ? T.OnAccent : T.Text, r, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }

        public override bool Press(Point p) { return true; }

        public override void Release(Point p)
        {
            for (int i = 0; i < labels.Length; i++)
                if (Button(i).Contains(p))
                    actions[i]();
        }
    }
}
