using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// Display scaling. Every size in the UI is written in 96-dpi design pixels and passed
    /// through Px(), because .NET Framework WinForms does no automatic scaling for a form
    /// built in code: AutoScaleDimensions stays {0,0} and Control.DeviceDpi reports 96 even
    /// when the screen is running at 168. Fonts are therefore specified in pixels too, so the
    /// ratio between text and the box around it stays fixed at any scale.
    /// </summary>
    static class Ui
    {
        public static readonly float Scale = MeasureScale();

        static float MeasureScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    float s = g.DpiX / 96f;
                    if (s < 0.5f || s > 6f) return 1f;
                    return s;
                }
            }
            catch { return 1f; }
        }

        public static int Px(int designPixels)
        {
            return (int)Math.Round(designPixels * Scale);
        }

        public static Size Sz(int w, int h) { return new Size(Px(w), Px(h)); }
        public static Point Pt(int x, int y) { return new Point(Px(x), Px(y)); }
        public static Padding Pad(int l, int t, int r, int b)
        {
            return new Padding(Px(l), Px(t), Px(r), Px(b));
        }
    }

    /// <summary>The palette, fonts and the small set of styled controls the UI is built from.</summary>
    static class Theme
    {
        public enum Palette { Dark, Light }

        public static Palette Active = Palette.Dark;
        static bool Dark { get { return Active == Palette.Dark; } }

        /// <summary>Resolves the stored setting ("dark", "light" or "auto") into a palette.</summary>
        public static void Use(string setting)
        {
            if (setting == "light") Active = Palette.Light;
            else if (setting == "dark") Active = Palette.Dark;
            else Active = WindowsPrefersLight() ? Palette.Light : Palette.Dark;
        }

        /// <summary>Reads the personalisation setting Windows exposes for app themes.</summary>
        public static bool WindowsPrefersLight()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (v is int) return (int)v != 0;
            }
            catch { }
            return false;
        }

        static Color C(int dr, int dg, int db, int lr, int lg, int lb)
        {
            return Dark ? Color.FromArgb(dr, dg, db) : Color.FromArgb(lr, lg, lb);
        }

        public static Color Bg { get { return C(18, 20, 25, 240, 242, 246); } }
        public static Color Sidebar { get { return C(13, 15, 19, 255, 255, 255); } }
        public static Color Card { get { return C(28, 32, 41, 255, 255, 255); } }
        public static Color CardHi { get { return C(44, 50, 63, 226, 231, 239); } }
        public static Color Border { get { return C(58, 66, 81, 199, 207, 220); } }
        public static Color Text { get { return C(245, 247, 250, 14, 19, 31); } }
        public static Color Muted { get { return C(176, 185, 201, 72, 83, 102); } }
        public static Color InputBg { get { return C(13, 15, 20, 255, 255, 255); } }
        public static Color Accent { get { return C(72, 140, 255, 27, 86, 214); } }
        public static Color OnAccent { get { return Color.White; } }
        public static Color Green { get { return C(46, 214, 110, 17, 116, 56); } }
        public static Color Amber { get { return C(251, 176, 52, 161, 74, 8); } }
        public static Color Red { get { return C(250, 90, 90, 198, 30, 30); } }
        public static Color Danger { get { return C(126, 44, 44, 253, 226, 226); } }

        // ------------------------------------------------------------------ fonts

        [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int AddFontResourceEx(string path, uint flags, IntPtr reserved);

        const uint FR_PRIVATE = 0x10;

        static readonly PrivateFontCollection Bundled = new PrivateFontCollection();
        static readonly List<string> BundledFamilies = new List<string>();

        /// <summary>
        /// Loads the .ttf files shipped in the app's fonts folder for this process only, so the
        /// font does not have to be installed. Both loaders are needed: AddFontResourceEx makes
        /// it visible to GDI, which is what TextRenderer draws every label with, and the private
        /// collection is what lets us read the family name back.
        /// </summary>
        static Theme()
        {
            try
            {
                LoadFrom(Path.Combine(AppPaths.ExeDir, "fonts"));
                // The exe gets copied around on its own, so the font also travels inside it and
                // is unpacked next to the log the first time it is needed.
                if (BundledFamilies.Count == 0) LoadFrom(ExtractEmbeddedFonts());
                foreach (FontFamily f in Bundled.Families) BundledFamilies.Add(f.Name);
            }
            catch { /* a missing font must never stop the app starting */ }
        }

        static void LoadFrom(string dir)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir, "*.ttf"))
            {
                try
                {
                    // Both loaders are needed: AddFontResourceEx registers the face with GDI,
                    // which is what TextRenderer draws every label with, and the private
                    // collection is what lets us build a Font that keeps the family name.
                    if (AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero) == 0) continue;
                    Bundled.AddFontFile(file);
                }
                catch { }
            }
        }

        static string ExtractEmbeddedFonts()
        {
            try
            {
                System.Reflection.Assembly asm = System.Reflection.Assembly.GetExecutingAssembly();
                // Temp, not the data folder: the installer shares this code and must not
                // leave a stray folder wherever it was downloaded to.
                string dir = Path.Combine(Path.GetTempPath(), "VMTun-fonts");
                bool any = false;
                foreach (string name in asm.GetManifestResourceNames())
                {
                    if (!name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)) continue;
                    string target = Path.Combine(dir, Path.GetFileName(name));
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    if (!File.Exists(target))
                    {
                        using (System.IO.Stream src = asm.GetManifestResourceStream(name))
                        using (FileStream dst = File.Create(target))
                        {
                            byte[] buffer = new byte[64 * 1024];
                            int n;
                            while ((n = src.Read(buffer, 0, buffer.Length)) > 0) dst.Write(buffer, 0, n);
                        }
                    }
                    any = true;
                }
                return any ? dir : null;
            }
            catch { return null; }
        }

        public static bool HasBundled(string family)
        {
            return BundledFamily(family) != null;
        }

        /// <summary>
        /// The FontFamily object from the private collection. Fonts must be built from this
        /// rather than from the family name: GDI+ resolves a name against installed families
        /// only, so new Font("B Nazanin", ...) silently falls back to Microsoft Sans Serif.
        /// Built from the family object the name survives into ToLogFont(), which is how
        /// TextRenderer then finds the font that AddFontResourceEx registered with GDI.
        /// </summary>
        static FontFamily BundledFamily(string name)
        {
            try
            {
                foreach (FontFamily f in Bundled.Families)
                    if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) return f;
            }
            catch { }
            return null;
        }

        public const string PersianFamily = "B Nazanin";

        public static string FamilyName
        {
            get
            {
                if (!Lang.Fa) return "Segoe UI";
                // Tahoma is the fallback when the bundled font is missing.
                return HasBundled(PersianFamily) ? PersianFamily : "Tahoma";
            }
        }

        /// <summary>
        /// B Nazanin draws much smaller than Tahoma at the same em size, so its sizes are
        /// scaled up to keep the text the same optical size as the rest of the layout.
        /// </summary>
        static float FamilyScale
        {
            get { return FamilyName == PersianFamily ? 1.34f : 1f; }
        }

        // Font sizes are design pixels: the em size in pixels at 100% scaling.
        public const int FTiny = 12;
        public const int FSmall = 13;
        public const int FBody = 14;
        public const int FH3 = 16;
        public const int FH2 = 19;
        public const int FH1 = 25;

        public static Font F(int designPx) { return MakeFont(designPx, FontStyle.Regular); }
        public static Font FB(int designPx) { return MakeFont(designPx, FontStyle.Bold); }

        /// <summary>
        /// Always a Latin face, whatever the interface language. B Nazanin maps the ASCII
        /// digits to Persian ones, which is fine for prose but turns an address into
        /// ۱۲۷.۰.۰.۱:۱۰۸۰۸ — unreadable when you are checking a port or comparing an IP.
        /// Used for addresses, ports, versions and anything else the user has to type or match.
        /// </summary>
        public static Font FLatin(int designPx) { return MakeLatin(designPx, FontStyle.Regular); }
        public static Font FLatinB(int designPx) { return MakeLatin(designPx, FontStyle.Bold); }

        static Font MakeLatin(int designPx, FontStyle style)
        {
            try { return new Font("Segoe UI", designPx * Ui.Scale, style, GraphicsUnit.Pixel); }
            catch { return new Font("Tahoma", designPx * Ui.Scale, style, GraphicsUnit.Pixel); }
        }

        static readonly Dictionary<string, int> LineHeights = new Dictionary<string, int>();

        /// <summary>
        /// Rendered line height in real pixels for one of the design sizes. Boxes are sized
        /// from this rather than from a fixed number, because fonts differ wildly here:
        /// B Nazanin needs about half again the line box of Tahoma at the same optical size,
        /// and a hardcoded height would clip it.
        /// </summary>
        public static int TextH(int designPx)
        {
            string key = FamilyName + "|" + designPx;
            int cached;
            if (LineHeights.TryGetValue(key, out cached)) return cached;

            int h;
            using (Font f = F(designPx))
            {
                // A Latin ascender and a Persian descender, to catch both extremes.
                h = TextRenderer.MeasureText("Agبج", f).Height;
            }
            LineHeights[key] = h;
            return h;
        }

        static Font MakeFont(int designPx, FontStyle style)
        {
            float px = designPx * FamilyScale * Ui.Scale;
            if (Lang.Fa)
            {
                FontFamily fam = BundledFamily(PersianFamily);
                if (fam != null)
                {
                    try
                    {
                        if (!fam.IsStyleAvailable(style)) style = FontStyle.Regular;
                        return new Font(fam, px, style, GraphicsUnit.Pixel);
                    }
                    catch { }
                }
            }
            try { return new Font(FamilyName, px, style, GraphicsUnit.Pixel); }
            catch { return new Font("Tahoma", designPx * Ui.Scale, style, GraphicsUnit.Pixel); }
        }

        public static Color StatusColor(CheckStatus s)
        {
            switch (s)
            {
                case CheckStatus.Ok: return Green;
                case CheckStatus.Warn: return Amber;
                case CheckStatus.Fail: return Red;
                case CheckStatus.Running: return Accent;
                default: return Muted;
            }
        }

        // ------------------------------------------------------------------ window chrome

        [DllImport("dwmapi.dll", PreserveSig = true)]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Asks Windows for a dark title bar so the frame matches the window.</summary>
        public static void ApplyTitleBar(Form form)
        {
            try
            {
                int dark = Dark ? 1 : 0;
                // 20 on current builds, 19 on Windows 10 1809-1903. Both are safe to try.
                if (DwmSetWindowAttribute(form.Handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref dark, sizeof(int));
            }
            catch { /* older Windows simply keeps the default frame */ }
        }

        // ------------------------------------------------------------------ controls

        /// <summary>A flat button with a hover tint. Width and height are design pixels.</summary>
        public static Button Button(string text, Color back, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Size = Ui.Sz(w, h);
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderSize = 0;
            b.BackColor = back;
            b.ForeColor = Contrast(back);
            b.Font = F(FSmall);
            b.UseVisualStyleBackColor = false;
            b.Cursor = Cursors.Hand;
            b.TextAlign = ContentAlignment.MiddleCenter;
            b.AutoEllipsis = false;

            Color normal = back;
            Color hover = Shift(back, Dark ? 18 : -16);
            b.MouseEnter += delegate { if (b.Enabled) b.BackColor = hover; };
            b.MouseLeave += delegate { b.BackColor = normal; };
            b.EnabledChanged += delegate
            {
                b.BackColor = normal;
                b.ForeColor = b.Enabled ? Contrast(normal) : Muted;
            };
            return b;
        }

        /// <summary>Readable foreground for a given background.</summary>
        public static Color Contrast(Color back)
        {
            double luma = (0.299 * back.R + 0.587 * back.G + 0.114 * back.B) / 255.0;
            return luma > 0.55 ? Color.FromArgb(14, 19, 31) : Color.FromArgb(245, 247, 250);
        }

        public static Color Shift(Color c, int amount)
        {
            return Color.FromArgb(
                Math.Max(0, Math.Min(255, c.R + amount)),
                Math.Max(0, Math.Min(255, c.G + amount)),
                Math.Max(0, Math.Min(255, c.B + amount)));
        }

        public static Label Label(string text, int fontPx, Color color, bool bold)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.ForeColor = color;
            l.Font = bold ? FB(fontPx) : F(fontPx);
            l.BackColor = Color.Transparent;
            l.UseCompatibleTextRendering = false;
            return l;
        }

        public static void StyleInput(Control c)
        {
            c.BackColor = InputBg;
            c.ForeColor = Text;
            // Latin face: these hold addresses, ports and MTUs the user types and compares.
            c.Font = FLatin(FBody);
            // Host names, ports and MTUs are Latin: keep them left-aligned in either language.
            c.RightToLeft = RightToLeft.No;
            TextBox tb = c as TextBox;
            if (tb != null) tb.BorderStyle = BorderStyle.FixedSingle;

            // A single-line text box on a form with no accept button answers Enter with the
            // Windows alert sound, and a NumericUpDown beeps at any character it will not take.
            // Swallow those keys so typing a value stays silent.
            bool numeric = c is NumericUpDown;
            c.KeyPress += delegate(object sender, KeyPressEventArgs e)
            {
                if (e.KeyChar == (char)Keys.Return || e.KeyChar == (char)Keys.Escape) e.Handled = true;
                else if (numeric && !char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true;
            };
        }

        /// <summary>
        /// Text direction for Persian prose. The window itself is never mirrored, so controls
        /// stay exactly where they are in both languages; only the reading order of the text
        /// inside them follows the language.
        /// </summary>
        public static RightToLeft TextDirection
        {
            get { return Lang.Fa ? RightToLeft.Yes : RightToLeft.No; }
        }

        /// <summary>
        /// Alignment that renders against the leading edge on screen. WinForms mirrors
        /// ContentAlignment when a control is right-to-left, so the value has to be flipped
        /// back to keep a column visually where it was.
        /// </summary>
        public static ContentAlignment VisualLeft
        {
            get { return Lang.Fa ? ContentAlignment.MiddleRight : ContentAlignment.MiddleLeft; }
        }

        public static ContentAlignment VisualRight
        {
            get { return Lang.Fa ? ContentAlignment.MiddleLeft : ContentAlignment.MiddleRight; }
        }

        /// <summary>
        /// A themed replacement for MessageBox. Windows plays MessageBeep for every MessageBox,
        /// including MessageBoxIcon.None — the icon only selects *which* sound — so the only way
        /// to have a silent confirmation is not to use MessageBox at all.
        /// </summary>
        public static bool Ask(IWin32Window owner, string message, string yes, string no)
        {
            return ShowDialog(owner, message, yes, no) == DialogResult.Yes;
        }

        public static void Tell(IWin32Window owner, string message)
        {
            ShowDialog(owner, message, Lang.T("باشه", "OK"), null);
        }

        static DialogResult ShowDialog(IWin32Window owner, string message, string yes, string no)
        {
            using (Form f = new Form())
            {
                f.Text = "VMTun";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = owner == null ? FormStartPosition.CenterScreen : FormStartPosition.CenterParent;
                f.BackColor = Card;
                f.ForeColor = Text;
                f.AutoScaleMode = AutoScaleMode.None;
                f.RightToLeft = TextDirection;
                f.RightToLeftLayout = false;
                f.KeyPreview = true;

                int pad = Ui.Px(22);
                int textWidth = Ui.Px(420);
                Size measured;
                using (Font mf = F(FBody))
                {
                    measured = TextRenderer.MeasureText(message, mf,
                        new Size(textWidth, 0), TextFormatFlags.WordBreak);
                }

                Label text = new Label();
                text.Text = message;
                text.Font = F(FBody);
                text.ForeColor = Text;
                text.BackColor = Color.Transparent;
                text.AutoSize = false;
                text.Location = new Point(pad, pad);
                text.Size = new Size(textWidth, measured.Height + Ui.Px(6));
                f.Controls.Add(text);

                int by = text.Bottom + Ui.Px(20);
                int bw = Ui.Px(116), bh = Ui.Px(36), gap = Ui.Px(10);

                Button ok = Button(yes, Accent, 116, 36);
                ok.Font = FB(FSmall);
                ok.DialogResult = DialogResult.Yes;
                ok.Location = new Point(pad + textWidth - bw, by);
                f.Controls.Add(ok);

                if (!string.IsNullOrEmpty(no))
                {
                    Button cancel = Button(no, CardHi, 116, 36);
                    cancel.DialogResult = DialogResult.No;
                    cancel.Location = new Point(pad + textWidth - bw * 2 - gap, by);
                    f.Controls.Add(cancel);
                    f.CancelButton = cancel;
                }
                else
                {
                    f.CancelButton = ok;
                }
                f.AcceptButton = ok;   // also stops Enter from ringing the alert sound

                f.ClientSize = new Size(textWidth + pad * 2, by + bh + pad);
                f.HandleCreated += delegate { ApplyTitleBar(f); };
                return f.ShowDialog(owner);
            }
        }

        /// <summary>A panel with rounded corners and a hairline border.</summary>
        public class CardPanel : Panel
        {
            public int Radius = 8;
            public Color Fill = Card;
            public Color Line = Border;
            public bool DrawBorder = true;

            public CardPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Transparent;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // The parent colour has to be painted first: the panel is transparent only in
                // name, WinForms does not composite it for us.
                Color under = Parent != null ? Parent.BackColor : Bg;
                using (SolidBrush b = new SolidBrush(under)) g.FillRectangle(b, ClientRectangle);

                Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = Round(r, Ui.Px(Radius)))
                {
                    using (SolidBrush b = new SolidBrush(Fill)) g.FillPath(b, path);
                    if (DrawBorder)
                        using (Pen p = new Pen(Line, Math.Max(1f, Ui.Scale * 0.8f))) g.DrawPath(p, path);
                }
                base.OnPaint(e);
            }
        }

        public static GraphicsPath Round(Rectangle r, int radius)
        {
            int d = Math.Max(2, radius * 2);
            GraphicsPath path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>
        /// The pass/warn/fail badge, drawn as vectors. Font glyphs such as U+2714 are missing
        /// or differently proportioned depending on the family, which is what made the icons
        /// look misaligned; a drawn circle is identical everywhere and crisp at any scale.
        /// </summary>
        public class StatusIcon : Control
        {
            CheckStatus _status = CheckStatus.Info;

            public StatusIcon(CheckStatus status, int designSize)
            {
                _status = status;
                Size = Ui.Sz(designSize, designSize);
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            public CheckStatus Status
            {
                get { return _status; }
                set { _status = value; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color under = Parent != null ? Parent.BackColor : Bg;
                Theme.CardPanel card = Parent as Theme.CardPanel;
                if (card != null) under = card.Fill;
                using (SolidBrush b = new SolidBrush(under)) g.FillRectangle(b, ClientRectangle);

                Color tint = StatusColor(_status);
                int inset = Math.Max(1, (int)Ui.Scale);
                Rectangle r = new Rectangle(inset, inset, Width - inset * 2 - 1, Height - inset * 2 - 1);
                using (SolidBrush b = new SolidBrush(tint)) g.FillEllipse(b, r);

                float w = Math.Max(1.6f, r.Width * 0.14f);
                using (Pen p = new Pen(Color.White, w))
                {
                    p.StartCap = LineCap.Round;
                    p.EndCap = LineCap.Round;
                    float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, s = r.Width;

                    if (_status == CheckStatus.Ok)
                    {
                        g.DrawLines(p, new PointF[] {
                            new PointF(cx - s * 0.21f, cy + s * 0.02f),
                            new PointF(cx - s * 0.05f, cy + s * 0.17f),
                            new PointF(cx + s * 0.22f, cy - s * 0.17f) });
                    }
                    else if (_status == CheckStatus.Fail)
                    {
                        g.DrawLine(p, cx - s * 0.17f, cy - s * 0.17f, cx + s * 0.17f, cy + s * 0.17f);
                        g.DrawLine(p, cx + s * 0.17f, cy - s * 0.17f, cx - s * 0.17f, cy + s * 0.17f);
                    }
                    else if (_status == CheckStatus.Warn)
                    {
                        g.DrawLine(p, cx, cy - s * 0.20f, cx, cy + s * 0.04f);
                        using (SolidBrush dot = new SolidBrush(Color.White))
                            g.FillEllipse(dot, cx - w / 2f, cy + s * 0.13f, w, w);
                    }
                    else
                    {
                        using (SolidBrush dot = new SolidBrush(Color.White))
                            g.FillEllipse(dot, cx - s * 0.11f, cy - s * 0.11f, s * 0.22f, s * 0.22f);
                    }
                }
            }
        }

        /// <summary>
        /// A progress bar we draw ourselves. The system ProgressBar ignores BackColor under
        /// visual styles and stays bright white in the dark theme.
        /// </summary>
        public class ProgressStrip : Control
        {
            int _value;

            public ProgressStrip()
            {
                // SupportsTransparentBackColor must be set before BackColor is assigned, or
                // Control rejects Color.Transparent outright.
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                         ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            public int Value
            {
                get { return _value; }
                set
                {
                    int v = Math.Max(0, Math.Min(100, value));
                    if (v == _value) return;
                    _value = v;
                    Invalidate();
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                Color under = Parent != null ? Parent.BackColor : Bg;
                using (SolidBrush b = new SolidBrush(under)) g.FillRectangle(b, ClientRectangle);

                int radius = Height / 2;
                Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath path = Round(track, radius))
                using (SolidBrush b = new SolidBrush(CardHi))
                    g.FillPath(b, path);

                int filled = (int)(track.Width * (_value / 100.0));
                if (filled < 2) return;
                Rectangle done = new Rectangle(0, 0, filled, track.Height);
                using (GraphicsPath path = Round(done, Math.Min(radius, filled / 2)))
                using (SolidBrush b = new SolidBrush(Accent))
                    g.FillPath(b, path);
            }
        }

        /// <summary>A panel that paints a single hairline along one edge, used as a divider.</summary>
        public class EdgePanel : Panel
        {
            public Color LineColor = Border;
            public DockStyle LineEdge = DockStyle.Bottom;

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (Pen p = new Pen(LineColor, Math.Max(1f, Ui.Scale * 0.8f)))
                {
                    if (LineEdge == DockStyle.Bottom) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
                    else if (LineEdge == DockStyle.Top) e.Graphics.DrawLine(p, 0, 0, Width, 0);
                    else if (LineEdge == DockStyle.Left) e.Graphics.DrawLine(p, 0, 0, 0, Height);
                    else if (LineEdge == DockStyle.Right) e.Graphics.DrawLine(p, Width - 1, 0, Width - 1, Height);
                }
            }
        }

        /// <summary>
        /// A row of mutually exclusive buttons, used instead of a combo box: a themed combo
        /// cannot be drawn reliably, and for a handful of options showing them all at once is
        /// clearer than hiding them behind a drop-down.
        /// </summary>
        public class Segmented : Panel
        {
            readonly List<Button> _buttons = new List<Button>();
            readonly List<string> _values = new List<string>();
            string _value = "";

            public event EventHandler ValueChanged;

            /// <param name="values">Stored values.</param>
            /// <param name="captions">What the user sees; null reuses the values.</param>
            /// <param name="segmentWidth">Design pixels per segment.</param>
            public Segmented(string[] values, string[] captions, int segmentWidth)
            {
                Height = Math.Max(Ui.Px(30), TextH(FSmall) + Ui.Px(9));
                BackColor = Color.Transparent;
                int gap = Ui.Px(4);
                int w = Ui.Px(segmentWidth);
                int x = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    string value = values[i];
                    Button b = new Button();
                    b.Text = (captions != null && i < captions.Length) ? captions[i] : values[i];
                    b.Location = new Point(x, 0);
                    b.Size = new Size(w, Height);
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 1;
                    b.FlatAppearance.BorderColor = Border;
                    b.Font = F(FSmall);
                    b.Cursor = Cursors.Hand;
                    b.TabStop = false;
                    b.UseVisualStyleBackColor = false;
                    b.Click += delegate { Value = value; };
                    Controls.Add(b);
                    _buttons.Add(b);
                    _values.Add(value);
                    x += w + gap;
                }
                Width = Math.Max(0, x - gap);
                if (values.Length > 0) Value = values[0];
            }

            public string Value
            {
                get { return _value; }
                set
                {
                    if (_values.Count == 0) return;
                    int index = _values.IndexOf(value);
                    if (index < 0) index = 0;
                    _value = _values[index];
                    for (int i = 0; i < _buttons.Count; i++)
                    {
                        bool on = (i == index);
                        _buttons[i].BackColor = on ? Accent : Card;
                        _buttons[i].ForeColor = on ? OnAccent : Muted;
                        _buttons[i].FlatAppearance.BorderColor = on ? Accent : Border;
                        _buttons[i].Font = on ? FB(FSmall) : F(FSmall);
                    }
                    EventHandler h = ValueChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }

            /// <summary>Sets the value without firing ValueChanged, for loading stored settings.</summary>
            public void SetQuiet(string value)
            {
                EventHandler saved = ValueChanged;
                ValueChanged = null;
                Value = value;
                ValueChanged = saved;
            }
        }
    }
}
