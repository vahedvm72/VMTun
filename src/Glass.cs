using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The frosted surface every panel in this app is made of.
    ///
    /// Glass needs three things to read as glass: what is behind it, blurred; a pale tint over
    /// that; and a bright edge where light catches it. WinForms has no backdrop-filter, so the
    /// blur is produced by rendering the backdrop into a bitmap an eighth the size and scaling
    /// it back up — bilinear upscaling averages a wide neighbourhood into every output pixel,
    /// which is what a blur is.
    ///
    /// The expensive half of that is done exactly once. An earlier version had every panel
    /// stretch the small bitmap to full size behind its own clip, which is a high-quality
    /// resample per panel per repaint; with a sidebar, a header and a page of rows that is
    /// dozens of them, and switching pages took seconds. Now the upscale is cached and a panel
    /// copies the rectangle under itself, which is a straight blit.
    /// </summary>
    static class Glass
    {
        /// <summary>The control the cached image is aligned to. Panels locate themselves in it.</summary>
        public static Control Source;

        static Bitmap _blur;
        public const int Divisor = 8;

        /// <summary>The blurred backdrop at full size, or null before the first build.</summary>
        public static Bitmap Blur { get { return _blur; } }

        /// <summary>
        /// Renders the scene small, scales it up once, and keeps the result. `scene` draws into
        /// whatever size it is handed, so the same code produces both.
        /// </summary>
        public static void Rebuild(Control source, int width, int height,
                                   Action<Graphics, int, int> scene)
        {
            if (width <= 0 || height <= 0) return;

            int w = Math.Max(1, width / Divisor);
            int h = Math.Max(1, height / Divisor);

            Bitmap big = null;
            try
            {
                using (Bitmap small = new Bitmap(w, h))
                {
                    using (Graphics g = Graphics.FromImage(small))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        scene(g, w, h);
                    }

                    big = new Bitmap(width, height);
                    using (Graphics g = Graphics.FromImage(big))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // Drawn one pixel proud on every side: bilinear sampling at the border
                        // otherwise pulls in the bitmap's transparent surround and leaves a
                        // pale frame around the window.
                        g.DrawImage(small, new Rectangle(-1, -1, width + 2, height + 2));
                    }
                }
            }
            catch
            {
                if (big != null) { try { big.Dispose(); } catch { } }
                return;
            }

            Bitmap old = _blur;
            _blur = big;
            Source = source;
            if (old != null) { try { old.Dispose(); } catch { } }
        }

        /// <summary>
        /// Paints what is behind a control: the blurred backdrop, then the tint of every glass
        /// ancestor between it and the backdrop.
        ///
        /// That second part is what stops a rounded button showing a hard dark square at its
        /// corners. A control cannot see through its parent in WinForms, so it has to reproduce
        /// the parent's surface itself before drawing its own shape on top.
        /// </summary>
        public static bool PaintBase(Graphics g, Control target, Rectangle bounds)
        {
            Bitmap blur = _blur;
            Control source = Source;
            if (blur == null || source == null || source.IsDisposed) return false;
            if (target.IsDisposed || !target.IsHandleCreated || !source.IsHandleCreated) return false;

            Point origin;
            try { origin = source.PointToClient(target.PointToScreen(Point.Empty)); }
            catch { return false; }

            Rectangle src = new Rectangle(origin.X, origin.Y, bounds.Width, bounds.Height);
            if (src.Width <= 0 || src.Height <= 0) return false;

            InterpolationMode was = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;   // 1:1, no resampling
            try { g.DrawImage(blur, bounds, src, GraphicsUnit.Pixel); }
            catch { g.InterpolationMode = was; return false; }
            g.InterpolationMode = was;

            // Every frosted ancestor has already tinted what this control sits on.
            ApplyAncestorTints(g, target, bounds);
            return true;
        }

        static void ApplyAncestorTints(Graphics g, Control target, Rectangle bounds)
        {
            // Outermost first, so the tints stack in the order they were painted.
            int depth = 0;
            for (Control p = target.Parent; p != null && p != Source && depth < 8; p = p.Parent) depth++;

            for (int level = depth; level >= 1; level--)
            {
                Control p = target.Parent;
                for (int i = 1; i < level && p != null; i++) p = p.Parent;

                Theme.CardPanel card = p as Theme.CardPanel;
                if (card == null || !card.Frosted) continue;
                using (SolidBrush b = new SolidBrush(card.Fill)) g.FillRectangle(b, bounds);
            }
        }

        /// <summary>
        /// The full frosted pane: backdrop, this control's own tint, the sheen along the top and
        /// the rim. Returns false when there is nothing to sample, so callers fall back to a
        /// flat fill rather than painting nothing.
        /// </summary>
        public static bool Paint(Graphics g, Control target, GraphicsPath path, Rectangle bounds,
                                 Color tint, int radius)
        {
            if (_blur == null) return false;

            GraphicsState state = g.Save();
            try
            {
                g.SetClip(path);
                if (!PaintBase(g, target, bounds)) { g.Restore(state); return false; }

                using (SolidBrush b = new SolidBrush(tint)) g.FillRectangle(b, bounds);

                // Light falls from above, so the top of the pane carries a sheen that fades out
                // before the middle. Without it the panel reads as a flat translucent rectangle.
                int sheen = Math.Max(Ui.Px(6), bounds.Height / 2);
                using (LinearGradientBrush lg = new LinearGradientBrush(
                    new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, sheen + 1),
                    Color.FromArgb(Theme.GlassSheen, Color.White),
                    Color.FromArgb(0, Color.White), 90f))
                    g.FillRectangle(lg, new Rectangle(bounds.X, bounds.Y, bounds.Width, sheen));
            }
            finally { g.Restore(state); }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(Theme.GlassRim, Color.White), Math.Max(1f, Ui.Scale)))
                g.DrawPath(p, path);

            // A brighter line along the top edge only, where the light would catch.
            using (GraphicsPath arc = new GraphicsPath())
            {
                int d = Math.Max(2, radius * 2);
                arc.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
                arc.AddLine(bounds.X + radius, bounds.Y, bounds.Right - radius, bounds.Y);
                arc.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
                using (Pen p = new Pen(Color.FromArgb(Theme.GlassRimTop, Color.White),
                                       Math.Max(1f, Ui.Scale)))
                    g.DrawPath(p, arc);
            }
            return true;
        }

        public static void Release()
        {
            Bitmap old = _blur;
            _blur = null;
            if (old != null) { try { old.Dispose(); } catch { } }
        }
    }
}
