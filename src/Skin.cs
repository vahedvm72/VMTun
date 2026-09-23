using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The drawn parts of the interface: the window's backdrop, the navigation pills, the
    /// status chips, the rounded buttons and the line icons.
    ///
    /// All of it is painted rather than assembled from images, for the same reason the rest of
    /// this app measures everything in design pixels: the machine runs at 175% and a bitmap
    /// would be soft at every size that is not the one it was cut at. A GraphicsPath is sharp
    /// wherever it lands.
    /// </summary>
    static class Skin
    {
        // Corner radii, in design pixels.
        public const int RCard = 18;
        public const int RPill = 14;
        public const int RButton = 14;

        // ------------------------------------------------------------------ backdrop

        /// <summary>
        /// The window background, and the thing every glass panel samples.
        ///
        /// The scene is a handful of wide colour blobs on a dark base. It is painted twice: once
        /// at full size for the window, and once into a bitmap an eighth as large, which the
        /// panels stretch back up to get their blur. Drawing it rather than shipping a wallpaper
        /// means it recolours with the theme and costs nothing to resize.
        ///
        /// Blobs are deliberately large and few. Glass blurs whatever is behind it, and a busy
        /// backdrop blurs into flat grey — the colour only survives if there is a lot of each.
        /// </summary>
        public class Backdrop : Panel
        {
            public Backdrop()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (Width <= 0 || Height <= 0) return;
                Scene(e.Graphics, Width, Height, 1f);
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Rebuild();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Rebuild();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && Glass.Source == this) { Glass.Source = null; Glass.Release(); }
                base.Dispose(disposing);
            }

            /// <summary>Re-renders the small copy the glass panels sample.</summary>
            void Rebuild()
            {
                if (Width <= 0 || Height <= 0) return;
                int w = Math.Max(1, Width / Glass.Divisor);
                int h = Math.Max(1, Height / Glass.Divisor);

                Bitmap small = new Bitmap(w, h);
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Scene(g, w, h, 1f / Glass.Divisor);
                }

                Glass.Release();
                Glass.Small = small;
                Glass.Source = this;
                Invalidate(true);
            }

            /// <summary>
            /// The scene itself. `scale` lets the small copy place its blobs in the same relative
            /// spots, so what a panel samples lines up with what is actually behind it.
            /// </summary>
            static void Scene(Graphics g, int w, int h, float scale)
            {
                using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, 0, 0, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Blob(g, w * 0.72f, h * -0.12f, Math.Max(w, h) * 0.78f, Theme.GlowA);
                Blob(g, w * 0.08f, h * 0.06f, Math.Max(w, h) * 0.62f, Theme.GlowB);
                Blob(g, w * 0.36f, h * 1.02f, Math.Max(w, h) * 0.70f, Theme.GlowC);
                Blob(g, w * 1.05f, h * 0.68f, Math.Max(w, h) * 0.55f, Theme.GlowB);
            }

            static void Blob(Graphics g, float cx, float cy, float radius, Color tint)
            {
                if (radius <= 1) return;
                RectangleF area = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(area);
                    using (PathGradientBrush br = new PathGradientBrush(p))
                    {
                        br.CenterColor = tint;
                        br.SurroundColors = new Color[] { Color.FromArgb(0, tint) };
                        br.CenterPoint = new PointF(cx, cy);
                        g.FillPath(br, p);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>
        /// One item in the sidebar: an icon, a label, and a pill behind them that fills in when
        /// the page is the one being shown.
        ///
        /// It is a Control rather than a Button because a Button insists on its own focus
        /// rectangle and hover shading, and the whole point here is to control both.
        /// </summary>
        public class NavItem : Control
        {
            readonly Icon _icon;
            bool _active, _hot;

            public NavItem(string text, Icon icon)
            {
                _icon = icon;
                Text = text;
                // The transparent style has to be set before the colour: a plain Control
                // refuses Color.Transparent without it.
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                TabStop = false;
                // Single words in both languages, so the pill never changes side when the
                // interface language does; only the word inside it is translated.
                RightToLeft = RightToLeft.No;
            }

            public bool Active
            {
                get { return _active; }
                set { if (_active != value) { _active = value; Invalidate(); } }
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color under = Parent != null ? Parent.BackColor : Theme.Sidebar;
                Theme.CardPanel card = Parent as Theme.CardPanel;
                if (card != null) under = card.Fill;
                using (SolidBrush b = new SolidBrush(under)) g.FillRectangle(b, ClientRectangle);

                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                if (_active || _hot)
                {
                    using (GraphicsPath p = Theme.Round(r, Ui.Px(RPill)))
                    {
                        Color fill = _active ? Theme.NavActive : Theme.NavHover;
                        using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                        if (_active)
                            using (Pen pen = new Pen(Theme.NavActiveLine, Math.Max(1f, Ui.Scale)))
                                g.DrawPath(pen, p);
                    }
                }

                Color ink = _active ? Theme.Text : (_hot ? Theme.Text : Theme.Muted);
                int pad = Ui.Px(14);
                int size = Ui.Px(18);
                RectangleF icon = new RectangleF(pad, (Height - size) / 2f, size, size);
                Icons.Draw(g, _icon, icon, _active ? Theme.Accent : ink, Math.Max(1.4f, Ui.Scale * 1.25f));

                Rectangle text = new Rectangle(pad + size + Ui.Px(12), 0,
                                               Width - pad - size - Ui.Px(12), Height);
                TextRenderer.DrawText(g, Text, _active ? Theme.FB(Theme.FH3) : Theme.F(Theme.FH3),
                    text, ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                              TextFormatFlags.NoPrefix);
            }
        }

        // ------------------------------------------------------------------ chips

        /// <summary>
        /// A small rounded label for one fact about the connection. Optionally preceded by a
        /// coloured dot, which is what carries the state at a glance.
        /// </summary>
        public class Chip : Control
        {
            public Color Dot = Color.Empty;

            public Chip(string text)
            {
                Text = text;
                // The transparent style has to be set before the colour: a plain Control
                // refuses Color.Transparent without it.
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
                RightToLeft = RightToLeft.No;    // these hold names and numbers, never prose
            }

            /// <summary>Width that fits the text, so a row of chips can be laid out by hand.</summary>
            public int Measure()
            {
                Size s = TextRenderer.MeasureText(Text, Theme.FLatin(Theme.FTiny));
                int lead = Dot.IsEmpty ? Ui.Px(14) : Ui.Px(26);
                return s.Width + lead + Ui.Px(14);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color under = Parent != null ? Parent.BackColor : Theme.Card;
                Theme.CardPanel card = Parent as Theme.CardPanel;
                if (card != null) under = card.Fill;
                using (SolidBrush b = new SolidBrush(under)) g.FillRectangle(b, ClientRectangle);

                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath p = Theme.Round(r, r.Height / 2))
                {
                    using (SolidBrush b = new SolidBrush(Theme.ChipFill)) g.FillPath(b, p);
                    using (Pen pen = new Pen(Theme.ChipLine, Math.Max(1f, Ui.Scale * 0.8f)))
                        g.DrawPath(pen, p);
                }

                int left = Ui.Px(7);
                if (!Dot.IsEmpty)
                {
                    int d = Ui.Px(7);
                    using (SolidBrush b = new SolidBrush(Dot))
                        g.FillEllipse(b, left + Ui.Px(4), (Height - d) / 2, d, d);
                    left += Ui.Px(19);
                }

                Rectangle text = new Rectangle(left, 0, Width - left - Ui.Px(7), Height);
                TextRenderer.DrawText(g, Text, Theme.FLatin(Theme.FTiny), text, Theme.Muted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        // ------------------------------------------------------------------ icons

        public enum Icon { None, Activity, Gear, Shield, Wrench, Document, Refresh, Bolt }

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
        }
    }
}
