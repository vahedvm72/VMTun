using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The frosted-glass surface every panel in this app is made of.
    ///
    /// Glass needs three things to read as glass: what is behind it, blurred; a pale tint over
    /// that; and a bright edge where the light catches it. WinForms gives none of them, and
    /// there is no backdrop-filter to borrow, so the blur is produced honestly — the backdrop
    /// draws its scene a second time into a small bitmap, and a panel stretches the region under
    /// itself back up to size. Bilinear upscaling from an eighth of the resolution is a blur:
    /// every output pixel is a weighted average of a wide neighbourhood, which is what a blur
    /// is, and it costs one StretchBlt instead of a convolution per frame.
    ///
    /// The tint and the highlight are then painted on top, and the result refracts the colour
    /// behind it and moves when the window moves, because it is genuinely sampling it.
    /// </summary>
    static class Glass
    {
        /// <summary>The backdrop currently on screen, and the small copy panels sample.</summary>
        public static Control Source;
        public static Bitmap Small;
        public static int Divisor = 8;

        /// <summary>
        /// Paints the frosted surface of a control into a rounded path: blurred backdrop, tint,
        /// specular highlight, rim. Returns false when there is no backdrop to sample, so the
        /// caller can fall back to a flat fill.
        /// </summary>
        public static bool Paint(Graphics g, Control target, GraphicsPath path, Rectangle bounds,
                                 Color tint, int radius)
        {
            Bitmap small = Small;
            Control source = Source;
            if (small == null || source == null || source.IsDisposed || target.IsDisposed) return false;
            if (!target.IsHandleCreated || !source.IsHandleCreated) return false;

            Point origin;
            try { origin = source.PointToClient(target.PointToScreen(Point.Empty)); }
            catch { return false; }

            GraphicsState state = g.Save();
            try
            {
                g.SetClip(path);

                // The whole small bitmap is stretched to where the backdrop is, shifted so the
                // part under this control lands on it. Clipping keeps the rest off screen.
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Rectangle dest = new Rectangle(-origin.X, -origin.Y,
                                               small.Width * Divisor, small.Height * Divisor);
                g.DrawImage(small, dest);

                using (SolidBrush b = new SolidBrush(tint)) g.FillRectangle(b, bounds);

                // Light falls from above, so the top of the pane carries a sheen that fades out
                // before the middle. Without it the panel reads as a flat translucent rectangle.
                int sheen = Math.Max(Ui.Px(6), bounds.Height / 2);
                Rectangle top = new Rectangle(bounds.X, bounds.Y, bounds.Width, sheen);
                if (top.Height > 0 && top.Width > 0)
                {
                    using (LinearGradientBrush lg = new LinearGradientBrush(
                        new Rectangle(top.X, top.Y - 1, top.Width, top.Height + 1),
                        Color.FromArgb(Theme.GlassSheen, Color.White),
                        Color.FromArgb(0, Color.White), 90f))
                        g.FillRectangle(lg, top);
                }
            }
            finally { g.Restore(state); }

            // The rim, drawn unclipped so it stays crisp: brighter along the top edge where the
            // light hits, fainter round the rest.
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen p = new Pen(Color.FromArgb(Theme.GlassRim, Color.White),
                                   Math.Max(1f, Ui.Scale)))
                g.DrawPath(p, path);

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

        /// <summary>Releases the cached copy, so a resize does not leak the old one.</summary>
        public static void Release()
        {
            Bitmap old = Small;
            Small = null;
            if (old != null) { try { old.Dispose(); } catch { } }
        }
    }
}
