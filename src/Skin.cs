using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The drawn parts of the interface: the backdrop, the top navigation, the status dots,
    /// the rounded buttons and the line icons.
    ///
    /// All of it is painted rather than assembled from images, for the same reason the rest of
    /// this app measures everything in design pixels: the machine runs at 175% and a bitmap
    /// would be soft at every size that is not the one it was cut at. A GraphicsPath is sharp
    /// wherever it lands.
    /// </summary>
    static class Skin
    {
        // Corner radii, in design pixels.
        public const int RCard = 10;
        public const int RButton = 999;

        // ------------------------------------------------------------------ backdrop

        /// <summary>
        /// The window background. Flat, like everything else in this design: the depth comes
        /// from the white surfaces and their hairlines, not from colour behind them.
        /// </summary>
        public class Backdrop : Panel
        {
            public Backdrop()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg;
            }
        }

        /// <summary>
        /// A plain container that paints the flat surface behind it.
        ///
        /// WinForms' own transparency walks the parent chain and asks each ancestor to repaint
        /// into the child's clip, which on a page of nested panels is a lot of painting for a
        /// background. One solid fill is cheaper and does not flicker while a page is built.
        /// </summary>
        public class Sheet : Panel
        {
            public Sheet()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaintBackground(PaintEventArgs e) { }

            protected override void OnPaint(PaintEventArgs e)
            {
                Surface.PaintBase(e.Graphics, this, ClientRectangle);
                base.OnPaint(e);
            }
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>
        /// One page in the top bar: a word with a hairline underline while it is the page on
        /// screen. Text only, so navigation costs a row of type instead of a column of chrome.
        /// </summary>
        public class TopTab : Control
        {
            bool _active, _hot;

            public TopTab(string text)
            {
                Text = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Font = Theme.F(Theme.FSmall);
                Cursor = Cursors.Hand;
                TabStop = false;
                RightToLeft = Theme.TextDirection;
                Height = Ui.Px(56);
                Width = Measure();
            }

            public bool Active
            {
                get { return _active; }
                set { if (_active != value) { _active = value; Invalidate(); } }
            }

            public int Measure()
            {
                return TextRenderer.MeasureText(Text, Font).Width + Ui.Px(26);
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);

                Color ink = _active ? Theme.Text : (_hot ? Theme.Text : Theme.Muted);
                TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                        TextFormatFlags.NoPrefix;
                if (RightToLeft == RightToLeft.Yes) flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, Text, Font, ClientRectangle, ink, flags);

                if (_active)
                {
                    int line = Math.Max(2, (int)Ui.Scale * 2);
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillRectangle(b, Ui.Px(10), Height - line, Math.Max(0, Width - Ui.Px(20)), line);
                }
            }
        }

        // ------------------------------------------------------------------ status dot

        /// <summary>A small filled circle: the lightest way to carry state in a list.</summary>
        public class Dot : Control
        {
            Color _fill = Theme.Muted;

            public Dot(int designSize)
            {
                Size = Ui.Sz(designSize, designSize);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            public Color Fill
            {
                get { return _fill; }
                set { _fill = value; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);
                int inset = Math.Max(0, (int)Math.Round(Ui.Scale * 0.5f));
                Rectangle r = new Rectangle(inset, inset, Width - inset * 2, Height - inset * 2);
                if (r.Width > 0 && r.Height > 0)
                    using (SolidBrush b = new SolidBrush(_fill)) g.FillEllipse(b, r);
            }
        }

        // ------------------------------------------------------------------ icons

        public enum Icon { None, Activity, Gear, Shield, Wrench, Document, Refresh, Bolt, Theme }

        /// <summary>Draws one icon into a box. Used by the navigation and by buttons.</summary>
        public static void DrawIcon(Graphics g, Icon icon, RectangleF box, Color colour, float stroke)
        {
            Icons.Draw(g, icon, box, colour, stroke);
        }

        /// <summary>
        /// Line icons drawn as paths. Six shapes at a size nothing else needs, which is less
        /// code than shipping an icon font and cannot render as a missing-glyph box on a
        /// machine that happens not to have one.
        /// </summary>
        internal static class Icons
        {
            public static void Draw(Graphics g, Icon icon, RectangleF box, Color colour, float stroke)
            {
                if (icon == Icon.None) return;
                SmoothingMode was = g.SmoothingMode;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (Pen p = new Pen(colour, stroke))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    p.LineJoin = LineJoin.Round;

                    switch (icon)
                    {
                        case Icon.Activity: Activity(g, p, box); break;
                        case Icon.Gear: Gear(g, p, box, colour); break;
                        case Icon.Shield: Shield(g, p, box); break;
                        case Icon.Wrench: Wrench(g, p, box); break;
                        case Icon.Document: Document(g, p, box); break;
                        case Icon.Refresh: Refresh(g, p, box); break;
                        case Icon.Bolt: Bolt(g, p, box, colour); break;
                        case Icon.Theme: Sun(g, p, box); break;
                    }
                }
                g.SmoothingMode = was;
            }

            static PointF P(RectangleF b, float x, float y)
            {
                return new PointF(b.X + b.Width * x, b.Y + b.Height * y);
            }

            static void Activity(Graphics g, Pen p, RectangleF b)
            {
                g.DrawLines(p, new PointF[] {
                    P(b,0.04f,0.55f), P(b,0.26f,0.55f), P(b,0.40f,0.16f),
                    P(b,0.56f,0.86f), P(b,0.70f,0.45f), P(b,0.96f,0.45f) });
            }

            static void Gear(Graphics g, Pen p, RectangleF b, Color colour)
            {
                float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
                float ring = b.Width * 0.27f;
                g.DrawEllipse(p, cx - ring, cy - ring, ring * 2, ring * 2);

                // Eight teeth as short radial strokes: a real cog outline is unreadable at 18px.
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * 2 * i / 8;
                    float x1 = cx + (float)Math.Cos(a) * ring * 1.02f;
                    float y1 = cy + (float)Math.Sin(a) * ring * 1.02f;
                    float x2 = cx + (float)Math.Cos(a) * ring * 1.44f;
                    float y2 = cy + (float)Math.Sin(a) * ring * 1.44f;
                    g.DrawLine(p, x1, y1, x2, y2);
                }
            }

            static void Shield(Graphics g, Pen p, RectangleF b)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddLines(new PointF[] {
                        P(b,0.50f,0.06f), P(b,0.88f,0.22f), P(b,0.88f,0.52f),
                        P(b,0.50f,0.94f), P(b,0.12f,0.52f), P(b,0.12f,0.22f) });
                    path.CloseFigure();
                    g.DrawPath(p, path);
                }
            }

            static void Wrench(Graphics g, Pen p, RectangleF b)
            {
                // A ring with a bite taken out of it, and a shaft running away from the bite.
                float cx = b.X + b.Width * 0.68f, cy = b.Y + b.Height * 0.32f;
                float r = b.Width * 0.22f;
                g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, 40, 280);
                g.DrawLine(p, P(b, 0.56f, 0.44f), P(b, 0.14f, 0.86f));
            }

            static void Document(Graphics g, Pen p, RectangleF b)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddLines(new PointF[] {
                        P(b,0.22f,0.06f), P(b,0.62f,0.06f), P(b,0.80f,0.26f),
                        P(b,0.80f,0.94f), P(b,0.22f,0.94f) });
                    path.CloseFigure();
                    g.DrawPath(p, path);
                }
                g.DrawLine(p, P(b, 0.62f, 0.06f), P(b, 0.62f, 0.26f));
                g.DrawLine(p, P(b, 0.62f, 0.26f), P(b, 0.80f, 0.26f));
                g.DrawLine(p, P(b, 0.34f, 0.50f), P(b, 0.68f, 0.50f));
                g.DrawLine(p, P(b, 0.34f, 0.68f), P(b, 0.68f, 0.68f));
            }

            static void Refresh(Graphics g, Pen p, RectangleF b)
            {
                float inset = b.Width * 0.16f;
                RectangleF r = new RectangleF(b.X + inset, b.Y + inset,
                                              b.Width - inset * 2, b.Height - inset * 2);
                g.DrawArc(p, r, 40, 280);
                // The arrow head, at the open end of the arc.
                PointF tip = P(b, 0.78f, 0.30f);
                g.DrawLines(p, new PointF[] {
                    new PointF(tip.X - b.Width * 0.02f, tip.Y - b.Height * 0.20f),
                    tip,
                    new PointF(tip.X + b.Width * 0.16f, tip.Y - b.Height * 0.02f) });
            }

            static void Bolt(Graphics g, Pen p, RectangleF b, Color colour)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    path.AddLines(new PointF[] {
                        P(b,0.58f,0.04f), P(b,0.24f,0.54f), P(b,0.47f,0.54f),
                        P(b,0.40f,0.96f), P(b,0.76f,0.44f), P(b,0.53f,0.44f) });
                    path.CloseFigure();
                    using (SolidBrush br = new SolidBrush(colour)) g.FillPath(br, path);
                }
            }

            static void Sun(Graphics g, Pen p, RectangleF b)
            {
                float cx = b.X + b.Width / 2f, cy = b.Y + b.Height / 2f;
                float ring = b.Width * 0.18f;
                g.DrawEllipse(p, cx - ring, cy - ring, ring * 2, ring * 2);
                for (int i = 0; i < 8; i++)
                {
                    double a = Math.PI * 2 * i / 8;
                    float x1 = cx + (float)Math.Cos(a) * ring * 1.55f;
                    float y1 = cy + (float)Math.Sin(a) * ring * 1.55f;
                    float x2 = cx + (float)Math.Cos(a) * ring * 2.15f;
                    float y2 = cy + (float)Math.Sin(a) * ring * 2.15f;
                    g.DrawLine(p, x1, y1, x2, y2);
                }
            }
        }
    }
}
