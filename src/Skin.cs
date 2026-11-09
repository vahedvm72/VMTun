using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The drawn parts of the interface: the backdrop, the connect orb, the status capsule,
    /// the floating navigation, the check chips and the line icons.
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
        public const int RButton = 999;

        // ------------------------------------------------------------------ backdrop

        /// <summary>The window background: one flat, warm near-black.</summary>
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

        /// <summary>
        /// A page surface that scrolls its one child by moving it, with no scroll bar anywhere.
        ///
        /// WinForms' AutoScroll reserves a strip for the bar and, when the content is even a few
        /// pixels wide, answers with a second, horizontal bar under it. This design has neither:
        /// the offset is kept here, the mouse wheel drives it through the window's message filter,
        /// and the content always fits the width it is given.
        /// </summary>
        public class ScrollArea : Sheet
        {
            public Control Body;
            int _offset;

            public int Offset { get { return _offset; } }

            public int MaxOffset
            {
                get
                {
                    if (Body == null) return 0;
                    return Math.Max(0, Body.Height - ClientSize.Height);
                }
            }

            /// <summary>Scrolls by a pixel amount, stopping at both ends.</summary>
            public void ScrollBy(int delta)
            {
                if (Body == null) return;
                int next = Math.Max(0, Math.Min(MaxOffset, _offset + delta));
                if (next == _offset) return;
                _offset = next;
                Body.Top = -_offset;
            }

            public void ScrollTo(int offset)
            {
                if (Body == null) return;
                _offset = Math.Max(0, Math.Min(MaxOffset, offset));
                Body.Top = -_offset;
            }

            /// <summary>Pulls the offset back after the content has shrunk, or the page grew.</summary>
            public void Clamp()
            {
                if (Body == null) return;
                int max = MaxOffset;
                if (_offset <= max) return;
                _offset = max;
                Body.Top = -_offset;
            }
        }

        static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        static GraphicsPath Pill(RectangleF r, float radius)
        {
            radius = Math.Max(0.5f, Math.Min(radius, Math.Min(r.Width, r.Height) / 2f));
            float d = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>A status dot with a soft halo, the way state is shown everywhere in the app.</summary>
        public static void DrawStatusDot(Graphics g, RectangleF r, Color fill, Color behind)
        {
            float halo = Math.Max(1.5f, Ui.Scale * 2f);
            using (SolidBrush b = new SolidBrush(Mix(behind, fill, 0.22f)))
                g.FillEllipse(b, r.X - halo, r.Y - halo, r.Width + halo * 2, r.Height + halo * 2);
            using (SolidBrush b = new SolidBrush(fill))
                g.FillEllipse(b, r);
            // A small specular spot so the dot reads as a lamp rather than a flat disc.
            float s = r.Width * 0.34f;
            using (SolidBrush b = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                g.FillEllipse(b, r.X + r.Width * 0.2f, r.Y + r.Height * 0.16f, s, s);
        }

        /// <summary>A small status lamp: the lightest way to carry state in a list.</summary>
        public class Dot : Control
        {
            Color _fill = Theme.Muted;

            public Dot(int designSize)
            {
                Size = Ui.Sz(designSize + 4, designSize + 4);
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
                float inset = Ui.Px(2);
                RectangleF r = new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2);
                if (r.Width > 0 && r.Height > 0) DrawStatusDot(g, r, _fill, Surface.Behind(this));
            }
        }

        // ------------------------------------------------------------------ brand tile

        /// <summary>The square brand mark: a bolt on an orange gradient tile.</summary>
        public class LogoTile : Control
        {
            public LogoTile(int designSize)
            {
                Size = Ui.Sz(designSize, designSize);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);

                RectangleF r = new RectangleF(0, 0, Width - 1, Height - 1);
                using (GraphicsPath p = Pill(r, Width * 0.32f))
                using (LinearGradientBrush b = new LinearGradientBrush(
                           new PointF(0, 0), new PointF(Width, Height),
                           Theme.Accent, Color.FromArgb(122, 40, 0)))
                    g.FillPath(b, p);

                float inset = Width * 0.25f;
                DrawIcon(g, Icon.Bolt, new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2),
                         Color.FromArgb(255, 233, 214), 1f);
            }
        }

        // ------------------------------------------------------------------ status capsule

        /// <summary>
        /// The connection state in the header: a lamp and one word in a rounded capsule. It
        /// sizes itself to its text, so the caller only positions it.
        /// </summary>
        public class Capsule : Control
        {
            Color _dot = Theme.Muted;

            public Capsule()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
                Font = Lang.Fa ? Theme.FB(Theme.FSmall) : Theme.FLatinB(Theme.FSmall);
                Height = Ui.Px(34);
            }

            public Color DotColor
            {
                get { return _dot; }
                set { _dot = value; Invalidate(); }
            }

            string Shown { get { return Lang.Fa ? Text : (Text ?? "").ToUpperInvariant(); } }

            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                AccessibleName = Text;
                Width = TextRenderer.MeasureText(Shown, Font).Width + Ui.Px(12 + 9 + 8 + 14);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);

                RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath p = Pill(r, Height / 2f))
                {
                    using (SolidBrush b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                    using (Pen pen = new Pen(Theme.BorderHi, Math.Max(1f, Ui.Scale))) g.DrawPath(pen, p);
                }

                float dot = Ui.Px(9);
                // The lamp sits on the reading side of the word.
                float dotX = Lang.Fa ? Width - Ui.Px(12) - dot : Ui.Px(12);
                DrawStatusDot(g, new RectangleF(dotX, (Height - dot) / 2f, dot, dot), _dot, Theme.Card);

                Rectangle text = Lang.Fa
                    ? new Rectangle(Ui.Px(14), 0, Width - Ui.Px(14 + 12 + 8) - (int)dot, Height)
                    : new Rectangle(Ui.Px(12 + 8) + (int)dot, 0, Width - Ui.Px(12 + 8 + 14) - (int)dot, Height);
                TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter |
                                        TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
                if (Lang.Fa) flags |= TextFormatFlags.RightToLeft;
                TextRenderer.DrawText(g, Shown, Font, text, Theme.AccentSoft, flags);
            }
        }

        // ------------------------------------------------------------------ connect orb

        /// <summary>
        /// The main action: a lit orange sphere inside two recessed rings, with an icon and a
        /// word on it. Only the sphere itself is the button — clicks on the rings around it do
        /// nothing — and it answers Space and Enter like any other button.
        /// </summary>
        public class Orb : Control
        {
            // A small parallax: the orb leans toward the pointer and eases back when it leaves.
            const float MaxDrift = 9f;
            float _x, _y, _targetX, _targetY;
            readonly System.Windows.Forms.Timer _ease;
            bool _hot, _down;


            /// <summary>Raised when the sphere is clicked or activated from the keyboard.</summary>
            public event EventHandler Pressed;

            /// <summary>Diameter of the sphere in design pixels; the rings scale with it.</summary>
            public int Diameter = 168;

            public Orb()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
                BackColor = Color.Transparent;
                TabStop = true;
                AccessibleRole = AccessibleRole.PushButton;
                Font = Theme.FDisplay(25);

                _ease = new System.Windows.Forms.Timer();
                _ease.Interval = 16;
                _ease.Tick += delegate { Ease(); };
            }

            /// <summary>
            /// The sphere drifts a few pixels toward the pointer and eases back when it leaves.
            /// Only the drawing moves: the control's bounds and hit test stay where they are, so
            /// the rings around it are still not clickable and the sphere still clicks exactly
            /// where it looks.
            /// </summary>
            void Follow(Point? at)
            {
                float max = Math.Max(2f, Ui.Px((int)MaxDrift));
                if (at.HasValue)
                {
                    _targetX = Math.Max(-max, Math.Min(max, (at.Value.X - Width / 2f) * 0.10f));
                    _targetY = Math.Max(-max, Math.Min(max, (at.Value.Y - Height / 2f) * 0.08f));
                }
                else
                {
                    _targetX = 0;
                    _targetY = 0;
                }
                if (!_ease.Enabled) _ease.Start();
            }

            void Ease()
            {
                _x += (_targetX - _x) * 0.18f;
                _y += (_targetY - _y) * 0.18f;
                if (Math.Abs(_targetX - _x) < 0.4f && Math.Abs(_targetY - _y) < 0.4f)
                {
                    _x = _targetX;
                    _y = _targetY;
                    _ease.Stop();
                }
                Invalidate();
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (Enabled) Follow(e.Location);
                SetHot(Enabled && Inside(e.Location));
                base.OnMouseMove(e);
            }


            protected override void OnTextChanged(EventArgs e)
            {
                base.OnTextChanged(e);
                AccessibleName = Text;
                Invalidate();
            }

            float Radius { get { return Ui.Px(Diameter) / 2f; } }

            bool Inside(Point p)
            {
                float dx = p.X - Width / 2f, dy = p.Y - Height / 2f;
                return dx * dx + dy * dy <= Radius * Radius;
            }

            void SetHot(bool hot)
            {
                if (_hot == hot) return;
                _hot = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }

            void Raise()
            {
                if (!Enabled) return;
                EventHandler h = Pressed;
                if (h != null) h(this, EventArgs.Empty);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _down = false;
                SetHot(false);
                Follow(null);
                base.OnMouseLeave(e);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    if (_ease != null)
                    {
                        _ease.Stop();
                        _ease.Dispose();
                    }
                    foreach (Font f in _labelFonts.Values) f.Dispose();
                    _labelFonts.Clear();
                }
                base.Dispose(disposing);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left && Enabled && Inside(e.Location))
                {
                    _down = true;
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (_down)
                {
                    _down = false;
                    Invalidate();
                    if (e.Button == MouseButtons.Left && Inside(e.Location)) Raise();
                }
                base.OnMouseUp(e);
            }

            protected override bool IsInputKey(Keys keyData)
            {
                if (keyData == Keys.Enter || keyData == Keys.Space) return true;
                return base.IsInputKey(keyData);
            }

            protected override void OnKeyUp(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { Raise(); e.Handled = true; }
                base.OnKeyUp(e);
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                if (!Enabled) { _hot = false; _down = false; Cursor = Cursors.Default; }
                Invalidate();
                base.OnEnabledChanged(e);
            }

            protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
            protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Surface.PaintBase(g, this, ClientRectangle);

                float cx = Width / 2f, cy = Height / 2f;
                float r = Radius;
                Color bg = Theme.Bg;

                // Outer hairline ring and the recessed well the sphere sits in.
                float r1 = r * 1.53f, r2 = r * 1.305f;
                using (Pen p = new Pen(Mix(bg, Theme.AccentSoft, 0.06f), Math.Max(1f, Ui.Scale)))
                    g.DrawEllipse(p, cx - r1, cy - r1, r1 * 2, r1 * 2);
                using (GraphicsPath well = new GraphicsPath())
                {
                    well.AddEllipse(cx - r2, cy - r2, r2 * 2, r2 * 2);
                    using (PathGradientBrush b = new PathGradientBrush(well))
                    {
                        b.CenterColor = Color.FromArgb(10, 6, 4);
                        b.SurroundColors = new Color[] { Color.FromArgb(21, 14, 10) };
                        g.FillPath(b, well);
                    }
                    using (Pen p = new Pen(Mix(bg, Theme.AccentSoft, 0.08f), Math.Max(1f, Ui.Scale)))
                        g.DrawPath(p, well);
                }

                // From here on the sphere follows the pointer; the rings above stay put.
                cx += _x;
                cy += _y;

                // No halo around the sphere: it sits in its well as one clean disc, and the
                // rings around it do the framing.
                //
                // The sphere: a radial gradient lit from the upper left.
                float d = r * 2f * (_down ? 0.96f : 1f);
                RectangleF orb = new RectangleF(cx - d / 2f, cy - d / 2f, d, d);
                // Deep enough that white type carries on it: the lighter end of this ramp used
                // to wash the label out where it crossed the middle of the sphere.
                Color[] stops =
                {
                    Color.FromArgb(38, 12, 0), Color.FromArgb(84, 28, 0), Color.FromArgb(122, 44, 2),
                    Color.FromArgb(152, 64, 10), Color.FromArgb(180, 94, 32)
                };
                for (int i = 0; i < stops.Length; i++)
                {
                    if (!Enabled) stops[i] = Mix(stops[i], Color.FromArgb(40, 30, 24), 0.55f);
                    else if (_hot) stops[i] = Theme.Shift(stops[i], 14);
                }
                using (GraphicsPath body = new GraphicsPath())
                {
                    body.AddEllipse(orb);
                    using (PathGradientBrush b = new PathGradientBrush(body))
                    {
                        b.CenterPoint = new PointF(orb.X + orb.Width * 0.34f, orb.Y + orb.Height * 0.26f);
                        ColorBlend cb = new ColorBlend(stops.Length);
                        cb.Colors = stops;
                        cb.Positions = new float[] { 0f, 0.24f, 0.5f, 0.78f, 1f };
                        b.InterpolationColors = cb;
                        g.FillPath(b, body);
                    }
                }

                // A soft specular highlight, tilted with the light.
                GraphicsState saved = g.Save();
                RectangleF spec = new RectangleF(orb.X + orb.Width * 0.22f, orb.Y + orb.Height * 0.11f,
                                                 orb.Width * 0.42f, orb.Height * 0.22f);
                float sx = spec.X + spec.Width / 2f, sy = spec.Y + spec.Height / 2f;
                g.TranslateTransform(sx, sy);
                g.RotateTransform(-20f);
                using (GraphicsPath hl = new GraphicsPath())
                {
                    hl.AddEllipse(-spec.Width / 2f, -spec.Height / 2f, spec.Width, spec.Height);
                    using (PathGradientBrush b = new PathGradientBrush(hl))
                    {
                        b.CenterColor = Color.FromArgb(Enabled ? 56 : 20, 255, 220, 190);
                        b.SurroundColors = new Color[] { Color.FromArgb(0, 255, 220, 190) };
                        g.FillPath(b, hl);
                    }
                }
                g.Restore(saved);

                if (Focused && ShowFocusCues)
                {
                    float f = Ui.Px(5);
                    using (Pen p = new Pen(Theme.AccentSoft, Math.Max(1.2f, Ui.Scale * 1.2f)))
                        g.DrawEllipse(p, orb.X - f, orb.Y - f, orb.Width + f * 2, orb.Height + f * 2);
                }

                // No icon: the label is the two words of the action, stacked and centred, in the
                // same warm tone as the small Connect button underneath the orb.
                Color ink = Enabled ? Theme.AccentSoft : Mix(Theme.AccentSoft, Theme.Bg, 0.55f);
                string[] words = (Text ?? "").Trim()
                    .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) words = new string[] { Text ?? "" };

                TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix |
                                        TextFormatFlags.SingleLine;
                if (Lang.Fa) flags |= TextFormatFlags.RightToLeft;

                int boxW = (int)(orb.Width * 0.86f);
                int left = (int)cx - boxW / 2;
                Font labelFont = FitWords(words, boxW);
                int lineH = TextRenderer.MeasureText("Ag", labelFont).Height;
                int gap = Ui.Px(2);
                int totalH = words.Length * lineH + (words.Length - 1) * gap;
                int top = (int)(cy - totalH / 2f);

                for (int i = 0; i < words.Length; i++)
                {
                    Rectangle line = new Rectangle(left, top + i * (lineH + gap), boxW, lineH);
                    TextRenderer.DrawText(g, words[i], labelFont, line, ink, flags);
                }
            }

            readonly Dictionary<int, Font> _labelFonts = new Dictionary<int, Font>();

            Font LabelFontAt(int designPx)
            {
                Font f;
                if (_labelFonts.TryGetValue(designPx, out f)) return f;
                f = Theme.FDisplay(designPx);
                _labelFonts[designPx] = f;
                return f;
            }

            /// <summary>
            /// The largest size at which every word of the label still fits the width of the
            /// sphere, so the two lines stay the same size and neither is clipped.
            /// </summary>
            Font FitWords(string[] words, int boxWidth)
            {
                Font best = LabelFontAt(25);
                for (int px = 25; px >= 13; px -= 2)
                {
                    Font f = LabelFontAt(px);
                    best = f;
                    bool fits = true;
                    foreach (string word in words)
                    {
                        if (TextRenderer.MeasureText(word, f).Width > boxWidth) { fits = false; break; }
                    }
                    if (fits) break;
                }
                return best;
            }
        }

        // ------------------------------------------------------------------ navigation

        /// <summary>
        /// One page in the floating bottom bar: an icon, with an orange pill behind it while it
        /// is the page on screen. The control's region is the pill itself, so it never paints a
        /// square corner over the rounded bar it sits in.
        /// </summary>
        /// <summary>
        /// The rounded bar the page buttons sit in. It paints the surface and the orange pill in
        /// its own background, which is what lets the pill slide underneath the buttons: a
        /// transparent child asks its parent to repaint its background, so the pill shows through
        /// wherever a button does not draw.
        /// </summary>
        public class NavBar : Control
        {
            public Rectangle Highlight = Rectangle.Empty;
            public Color Fill = Theme.Card;
            public Color Line = Theme.BorderHi;

            public NavBar()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
            }

            public void SetHighlight(Rectangle r)
            {
                if (Highlight == r) return;
                Highlight = r;
                Invalidate();
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                using (SolidBrush b = new SolidBrush(Surface.Behind(this)))
                    g.FillRectangle(b, e.ClipRectangle);

                RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath p = Pill(r, Height / 2f))
                {
                    using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, p);
                    if (Highlight != Rectangle.Empty)
                    {
                        RectangleF h = new RectangleF(Highlight.X, Highlight.Y,
                                                      Highlight.Width, Highlight.Height);
                        using (GraphicsPath hp = Pill(h, h.Height / 2f))
                        using (SolidBrush b = new SolidBrush(Theme.Accent))
                            g.FillPath(b, hp);
                    }
                    using (Pen pen = new Pen(Line, Math.Max(1f, Ui.Scale)))
                        g.DrawPath(pen, p);
                }
            }

            protected override void OnPaint(PaintEventArgs e) { }
        }

        public class NavButton : Control
        {
            bool _hot;
            readonly Icon _icon;

            public NavButton(Icon icon, string name)
            {
                _icon = icon;
                Text = name;
                AccessibleName = name;
                AccessibleRole = AccessibleRole.PageTab;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                Cursor = Cursors.Hand;
                TabStop = false;
                Size = Ui.Sz(52, 44);
            }
    
            public Icon Icon { get; set; }

            /// <summary>
            /// The orange pill, in the coordinates of the bar the button sits in. Every button
            /// paints its own share of this one rectangle while the bar animates, which is what
            /// lets the pill slide across two buttons without either painting over the other —
            /// a control cannot be transparent over its siblings in WinForms.
            /// </summary>
            public Rectangle Highlight = Rectangle.Empty;

            /// <summary>True while the orange pill covers the middle of this button.</summary>
            public bool Covered
            {
                get
                {
                    return Highlight != Rectangle.Empty &&
                           Highlight.Contains(Left + Width / 2, Top + Height / 2);
                }
            }

            /// <summary>Moves the pill and repaints, without touching the control's own bounds.</summary>
            public void SetHighlight(Rectangle highlight)
            {
                if (Highlight == highlight) return;
                Highlight = highlight;
                Invalidate();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                if (Width <= 0 || Height <= 0) return;
                // The button keeps a pill-shaped region, so its own base fill cannot square off
                // the rounded end of the bar it sits in.
                using (GraphicsPath p = Pill(new RectangleF(0, 0, Width, Height), Height / 2f))
                    Region = new Region(p);
            }

            protected override void OnMouseEnter(EventArgs e) { _hot = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hot = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);

                // The bar paints the pill as well, which is what carries it across the gaps
                // between buttons; each button repaints its own share so the straight sections
                // line up exactly with it.
                if (Highlight != Rectangle.Empty)
                {
                    RectangleF local = new RectangleF(Highlight.X - Left, Highlight.Y - Top,
                                                      Highlight.Width, Highlight.Height);
                    using (GraphicsPath p = Pill(local, local.Height / 2f))
                    using (SolidBrush b = new SolidBrush(Theme.Accent))
                        g.FillPath(b, p);
                }

                Color ink = Covered ? Theme.OnAccent : (_hot ? Theme.Text : Theme.Muted);

                float size = Ui.Px(20);
                DrawIcon(g, _icon, new RectangleF((Width - size) / 2f, (Height - size) / 2f, size, size),
                         ink, Math.Max(1.6f, Ui.Scale * 1.5f));
            }
        }

        // ------------------------------------------------------------------ check chip

        /// <summary>
        /// One pre-connection check. A passed check is a compact chip with a lamp and its name;
        /// a warning or a failure takes a full-width card with its detail and a tag, so the one
        /// thing that needs attention is the one that stands out.
        /// </summary>
        public class Chip : Control
        {
            readonly CheckResult _check;
            readonly bool _wide;
            readonly Font _title, _detail, _tag;

            public Chip(CheckResult check, bool wide)
            {
                _check = check;
                _wide = wide;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
                _title = wide ? Theme.FB(Theme.FSmall) : Theme.F(Theme.FSmall);
                _detail = Theme.FLatin(Theme.FTiny);
                _tag = Theme.FB(Theme.FTiny);
                AccessibleName = check.Title;
                AccessibleDescription = check.Detail;

                if (wide)
                    Height = Ui.Px(10) + Theme.TextH(Theme.FSmall) + Ui.Px(2) + Theme.TextH(Theme.FTiny) + Ui.Px(10);
                else
                {
                    Height = Ui.Px(34);
                    Width = TextRenderer.MeasureText(check.Title ?? "", _title).Width + Ui.Px(10 + 11 + 8 + 14);
                }
            }

            public bool Wide { get { return _wide; } }

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _title.Dispose(); _detail.Dispose(); _tag.Dispose(); }
                base.Dispose(disposing);
            }

            /// <summary>The word shown at the trailing edge of a wide chip.</summary>
            public string StatusTag
            {
                get
                {
                    if (_check.Status == CheckStatus.Fail) return Lang.T("خطا", "Error");
                    if (_check.Status == CheckStatus.Warn) return Lang.T("هشدار", "Warning");
                    return "";
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Surface.PaintBase(g, this, ClientRectangle);

                Color fill = _wide ? Theme.StatusFill(_check.Status) : Theme.Card;
                Color line = _wide ? Theme.StatusLine(_check.Status) : Theme.Border;
                RectangleF r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
                using (GraphicsPath p = Pill(r, _wide ? Ui.Px(16) : Height / 2f))
                {
                    using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                    using (Pen pen = new Pen(line, Math.Max(1f, Ui.Scale))) g.DrawPath(pen, p);
                }

                bool rtl = Lang.Fa;
                float dot = Ui.Px(11);
                int side = _wide ? Ui.Px(12) : Ui.Px(10);
                int lineH = Theme.TextH(Theme.FSmall);
                float dotTop = _wide ? Ui.Px(10) + (lineH - dot) / 2f : (Height - dot) / 2f;
                float dotX = rtl ? Width - side - dot : side;
                DrawStatusDot(g, new RectangleF(dotX, dotTop, dot, dot), Theme.StatusColor(_check.Status), fill);

                TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                        TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
                if (rtl) flags |= TextFormatFlags.RightToLeft | TextFormatFlags.Right;

                int textStart = side + (int)dot + Ui.Px(8);
                if (!_wide)
                {
                    Rectangle t = rtl
                        ? new Rectangle(Ui.Px(12), 0, Width - textStart - Ui.Px(12), Height)
                        : new Rectangle(textStart, 0, Width - textStart - Ui.Px(12), Height);
                    TextRenderer.DrawText(g, _check.Title ?? "", _title, t, Theme.Text, flags);
                    return;
                }

                string tag = StatusTag;
                int tagW = tag.Length == 0 ? 0 : TextRenderer.MeasureText(tag, _tag).Width + Ui.Px(8);
                int textW = Width - textStart - Ui.Px(14) - tagW;
                int y = Ui.Px(10);
                Rectangle title = rtl ? new Rectangle(Ui.Px(14) + tagW, y, textW, lineH)
                                      : new Rectangle(textStart, y, textW, lineH);
                TextRenderer.DrawText(g, _check.Title ?? "", _title, title, Theme.Text, flags);

                int detailH = Theme.TextH(Theme.FTiny);
                Rectangle detail = new Rectangle(title.X, y + lineH + Ui.Px(2), textW + tagW, detailH);
                if (rtl) detail.X = Ui.Px(14);
                TextRenderer.DrawText(g, _check.Detail ?? "", _detail, detail, Theme.Muted, flags);

                if (tagW > 0)
                {
                    Rectangle tg = rtl ? new Rectangle(Ui.Px(14), y, tagW, lineH)
                                       : new Rectangle(Width - Ui.Px(14) - tagW, y, tagW, lineH);
                    TextFormatFlags tf = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine |
                                         TextFormatFlags.VerticalCenter |
                                         (rtl ? TextFormatFlags.Left : TextFormatFlags.Right);
                    TextRenderer.DrawText(g, tag, _tag, tg, Theme.StatusColor(_check.Status), tf);
                }
            }
        }

        // ------------------------------------------------------------------ icons

        public enum Icon { None, Activity, Gear, Shield, Wrench, Document, Refresh, Bolt, Theme, Power }

        /// <summary>Draws one icon into a box. Used by the navigation and by buttons.</summary>
        public static void DrawIcon(Graphics g, Icon icon, RectangleF box, Color colour, float stroke)
        {
            Icons.Draw(g, icon, box, colour, stroke);
        }

        /// <summary>
        /// Line icons drawn as paths. A handful of shapes at a size nothing else needs, which is
        /// less code than shipping an icon font and cannot render as a missing-glyph box on a
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
                        case Icon.Power: Power(g, p, box); break;
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
                // An open-end spanner: a half head with two jaws, and a handle off its back.
                // The head used to be a nearly closed circle, which at twenty pixels read as a
                // circle with a piece missing rather than a tool, so the jaws are drawn as two
                // straight prongs instead.
                float cx = b.X + b.Width * 0.64f, cy = b.Y + b.Height * 0.36f;
                float r = b.Width * 0.21f;

                // n runs across the shaft, f along it: the jaws open along f, up and to the right.
                float nx = 0.7071f, ny = 0.7071f;
                float fx = 0.7071f, fy = -0.7071f;

                PointF p1 = new PointF(cx + nx * r, cy + ny * r);
                PointF p2 = new PointF(cx - nx * r, cy - ny * r);

                PointF[] arc = new PointF[9];
                for (int i = 0; i < arc.Length; i++)
                {
                    // 45 to 225 degrees, through the back of the head (down and to the left).
                    double a = Math.PI * 0.25 + Math.PI * (i / 8.0);
                    arc[i] = new PointF(cx + (float)Math.Cos(a) * r, cy + (float)Math.Sin(a) * r);
                }
                g.DrawLines(p, arc);

                PointF t1 = new PointF(p1.X + fx * b.Width * 0.26f, p1.Y + fy * b.Height * 0.26f);
                PointF t2 = new PointF(p2.X + fx * b.Width * 0.18f, p2.Y + fy * b.Height * 0.18f);
                g.DrawLine(p, p1, t1);
                g.DrawLine(p, p2, t2);

                g.DrawLine(p,
                    new PointF(cx - fx * r * 0.6f, cy - fy * r * 0.6f),
                    P(b, 0.16f, 0.86f));
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

            static void Power(Graphics g, Pen p, RectangleF b)
            {
                // A ring open at the top, and the stem dropping into the opening.
                float inset = b.Width * 0.12f;
                RectangleF r = new RectangleF(b.X + inset, b.Y + inset + b.Height * 0.04f,
                                              b.Width - inset * 2, b.Height - inset * 2);
                g.DrawArc(p, r, 300, 300);
                g.DrawLine(p, P(b, 0.5f, 0.06f), P(b, 0.5f, 0.46f));
            }
        }
    }
}
