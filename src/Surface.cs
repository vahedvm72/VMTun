using System;
using System.Drawing;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// Flat painting helpers. Every surface in the interface is opaque, so a control that needs
    /// to look transparent only has to reproduce the nearest opaque ancestor behind it.
    /// </summary>
    static class Surface
    {
        /// <summary>The colour a control is sitting on, resolved through the parent chain.</summary>
        public static Color Behind(Control control)
        {
            int guard = 0;
            for (Control p = control == null ? null : control.Parent;
                 p != null && guard < 32; p = p.Parent, guard++)
            {
                Theme.CardPanel card = p as Theme.CardPanel;
                if (card != null) return Opaque(card.Fill);

                Color back = p.BackColor;
                if (back.A == 255) return back;
            }
            return Theme.Bg;
        }

        public static Color Opaque(Color c)
        {
            return c.A == 255 ? c : Color.FromArgb(255, c);
        }

        public static void PaintBase(Graphics g, Control target, Rectangle bounds)
        {
            using (SolidBrush b = new SolidBrush(Behind(target))) g.FillRectangle(b, bounds);
        }
    }
}
