using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace VMTun
{
    partial class MainForm : Form
    {
        readonly Settings _settings;
        readonly TunnelService _tunnel;

        /// <summary>Set when the theme or language changes; Program rebuilds the window so
        /// colours and fonts are applied cleanly instead of patched at runtime.</summary>
        public bool RestartRequested { get; private set; }

        /// <summary>Page to reopen after a rebuild, so a settings change does not lose your place.</summary>
        public int RestartPage { get; private set; }

        NotifyIcon _tray;
        ToolStripMenuItem _miToggle;

        // header
        Skin.Capsule _capsule;
        Skin.NavBar _navBar;
        ToolTip _tips;

        // status hero: the orb is Pro Connect (and Disconnect while connected), the small
        // button under it is the plain Connect that changes nothing outside this app
        Skin.Orb _orb;
        Label _lblVerifCaption, _lblVerif, _stateDetail;
        Theme.RoundButton _btnToggle;

        // navigation
        readonly List<Skin.NavButton> _navButtons = new List<Skin.NavButton>();
        readonly List<Panel> _pages = new List<Panel>();
        int _currentPage;

        // The orange pill in the bottom bar slides between pages instead of jumping: the
        // rectangle is tweened and every button repaints its share of it on each tick.
        readonly List<Rectangle> _navRects = new List<Rectangle>();
        System.Windows.Forms.Timer _navTween;
        Rectangle _navFrom, _navTo, _navNow;
        DateTime _navStarted;
        int _navTarget;
        const int NavSlideMs = 220;

        // status page
        Skin.ScrollArea _statusPage;
        Skin.Sheet _statusBody, _checkHost;
        Skin.ScrollArea _activeScroller;
        WheelRouter _wheel;
        readonly List<Theme.CardPanel> _factPills = new List<Theme.CardPanel>();
        Label _sumProxy, _sumDns, _sumRouting, _sumAdapter, _sumExit, _sumGuard, _lblChecks, _lblPhase;
        Theme.RoundButton _btnRecheck;
        bool _layingOutStatus;

        // settings page
        TextBox _txtHost, _txtDns, _txtProDns, _txtExtraDirect;
        NumericUpDown _numPort, _numMtu;
        Theme.Segmented _segType, _segStack, _segDnsMode, _segLang;
        RadioButton _rbFull, _rbIran;
        CheckBox _chkKill, _chkIpv6, _chkQuic, _chkAuto, _chkStartup, _chkTray, _chkVerbose, _chkUpdate, _chkNotify;

        // log page
        RichTextBox _log;

        bool _reallyExit;
        bool _loading = true;

        // ---- design geometry, in 96-dpi pixels; everything goes through Ui.Px -------------
        // A narrow, tray-sized window: one column, the pages switched from a floating bar.
        const int WinW = 400, WinH = 860;
        const int PagePad = 16;      // left and right margin of every page
        const int CardPad = 16;      // inner margin of a section card
        // Width a wrapping option can use inside a card on a scrolling page: the window,
        // less the page margins, the card's own margins and room for the scroll bar.
        const int OptionWrap = WinW - PagePad * 2 - CardPad * 2 - 20;
        const int OrbZone = 262;     // height of the orb and its rings

        /// <summary>
        /// Width available to page content: the window less the page margins. No scroll bar takes
        /// any of it — the pages scroll by moving their body, so nothing is ever reserved for one.
        /// </summary>
        static int ContentWidth
        {
            get { return Ui.Px(WinW - PagePad * 2); }
        }

        // Nav order, named so a new page cannot silently renumber the jumps below.
        const int PageStatus = 0, PageSettings = 1, PagePrivacy = 2, PageTools = 3, PageLog = 4;

        public MainForm(Settings settings, bool startInTray, int startPage, TunnelService tunnel)
        {
            _settings = settings;
            _tunnel = tunnel;
            Lang.Fa = _settings.Lang != "en";
            Theme.Use(_settings.Theme);

            BuildUi();
            LoadSettingsIntoUi();
            _loading = false;
            ShowPage(startPage);
            RefreshHeader();
            RefreshSummary();

            Log.Line += OnLogLine;
            _tunnel.StateChanged += OnTunnelState;
            _tunnel.ChecksUpdated += OnChecksUpdated;

            if (startInTray) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }

            HandleCreated += delegate { Theme.ApplyTitleBar(this); };

            Shown += delegate
            {
                Theme.ApplyTitleBar(this);
                if (startInTray) Hide();
                // Only when nothing is running: the window is rebuilt on a theme or language
                // change while the tunnel stays up, and this would kill its own core.
                if (_tunnel.State == TunnelState.Disconnected)
                {
                    string notes = TunnelService.CleanupStale();
                    if (!string.IsNullOrEmpty(notes)) AppendLog(LogLevel.Warn, notes.Trim());
                }
                AppendLog(LogLevel.Info, "DPI " + (int)(Ui.Scale * 96) + " (" +
                    (int)Math.Round(Ui.Scale * 100) + "%)  •  " +
                    Lang.T("پوشه داده: ", "Data folder: ") + AppPaths.DataDir);
                StartLogPump();
                StartUpdateTimer();
                RunPreflightAsync();
                if (_settings.AutoConnect) BeginConnect();
            };
        }

        // =================================================================== chrome

        void BuildUi()
        {
            Text = "VMTun";
            // No automatic scaling: the layout is scaled explicitly through Ui.Px, and letting
            // WinForms scale on top of that would apply the factor twice.
            AutoScaleMode = AutoScaleMode.None;
            Font = Theme.F(Theme.FBody);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = LoadAppIcon(false);

            // The window is never mirrored: RightToLeftLayout stays off, so the sidebar, the
            // connect button and every card keep the same place in both languages. Only the
            // reading order of the text inside the controls follows the language.
            RightToLeft = Theme.TextDirection;
            RightToLeftLayout = false;

            // A fixed, narrow window like a tray popover. Every page is one column laid out
            // for this width, so the window does not offer to grow sideways.
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(
                Math.Min(Ui.Px(WinW), (int)(work.Width * 0.94)),
                Math.Min(Ui.Px(WinH), (int)(work.Height * 0.94)));

            _tips = new ToolTip();

            // The backdrop is the parent of everything, not a sibling behind it. A transparent
            // WinForms control paints its *parent's* background, so a wash sitting beside the
            // layout rather than under it composites against nothing and the panels above it
            // come out empty.
            Skin.Backdrop back = new Skin.Backdrop();
            back.Dock = DockStyle.Fill;
            Controls.Add(back);

            // The filler is added first and the edges after it: docking resolves from the
            // highest z-order down, so the later header and nav claim their edges and the
            // filler gets what is left.
            Panel main = new Panel();
            main.Dock = DockStyle.Fill;
            main.BackColor = Color.Transparent;
            back.Controls.Add(main);

            BuildNav(back);
            BuildHeader(back);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.Transparent;
            content.Padding = Ui.Pad(PagePad, 4, PagePad, 0);
            main.Controls.Add(content);

            _pages.Add(BuildStatusPage());
            _pages.Add(BuildSettingsPage());
            _pages.Add(BuildPrivacyPage());
            _pages.Add(BuildToolsPage());
            _pages.Add(BuildLogPage());
            foreach (Panel p in _pages)
            {
                p.Dock = DockStyle.Fill;
                p.Visible = false;
                content.Controls.Add(p);
            }

            BuildTray();

            _wheel = new WheelRouter(this);
            Application.AddMessageFilter(_wheel);

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
                {
                    Hide(); ShowInTaskbar = false;
                }
            };
            FormClosing += OnFormClosing;
        }

        /// <summary>
        /// The header: the brand on the right and the connection capsule on the left. Like
        /// every other control in the window, these keep their places in both languages.
        /// </summary>
        void BuildHeader(Control host)
        {
            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(58);
            host.Controls.Add(bar);

            Skin.LogoTile tile = new Skin.LogoTile(32);
            bar.Controls.Add(tile);

            Label logo = Theme.Label("VMTun", 18, Theme.Text, true);
            logo.Font = Theme.FLatinB(18);
            bar.Controls.Add(logo);

            _capsule = new Skin.Capsule();
            _capsule.Text = Lang.T("قطع", "Disconnected");
            _tips.SetToolTip(_capsule, "v" + Integration.Version);
            bar.Controls.Add(_capsule);

            EventHandler layout = delegate
            {
                int h = bar.ClientSize.Height;
                int w = bar.ClientSize.Width;
                if (h <= 0 || w <= 0) return;
                tile.Location = new Point(w - Ui.Px(20) - tile.Width, (h - tile.Height) / 2);
                logo.Location = new Point(tile.Left - Ui.Px(10) - logo.PreferredWidth,
                                          (h - logo.PreferredHeight) / 2);
                _capsule.Location = new Point(Ui.Px(20), (h - _capsule.Height) / 2);
            };
            bar.SizeChanged += layout;
            _capsule.SizeChanged += layout;
            layout(bar, EventArgs.Empty);
        }

        /// <summary>The floating page bar at the bottom of the window: one icon per page.</summary>
        void BuildNav(Control host)
        {
            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Ui.Px(80);
            host.Controls.Add(bar);

            Skin.NavBar pill = new Skin.NavBar();
            bar.Controls.Add(pill);
            _navBar = pill;

            Skin.Icon[] icons =
            {
                Skin.Icon.Activity, Skin.Icon.Gear, Skin.Icon.Shield, Skin.Icon.Wrench, Skin.Icon.Document
            };
            string[] names =
            {
                Lang.T("وضعیت", "Status"),
                Lang.T("تنظیمات", "Settings"),
                Lang.T("حریم خصوصی", "Privacy"),
                Lang.T("ابزارها", "Tools"),
                Lang.T("گزارش", "Log")
            };

            int pad = Ui.Px(6), gap = Ui.Px(6);
            int x = pad;
            for (int i = 0; i < icons.Length; i++)
            {
                Skin.NavButton b = new Skin.NavButton(icons[i], names[i]);
                b.Location = new Point(x, pad);
                _navRects.Add(b.Bounds);
                int index = i;
                b.Click += delegate { ShowPage(index); };
                _tips.SetToolTip(b, names[i]);
                pill.Controls.Add(b);
                _navButtons.Add(b);
                x += b.Width + gap;
            }
            pill.Size = new Size(x - gap + pad, Ui.Px(44) + pad * 2);
            if (_navButtons.Count > 0) SetNavHighlight(0);

            EventHandler layout = delegate
            {
                pill.Location = new Point(Math.Max(0, (bar.ClientSize.Width - pill.Width) / 2),
                                          Math.Max(0, (bar.ClientSize.Height - pill.Height) / 2 - Ui.Px(2)));
            };
            bar.SizeChanged += layout;
            layout(bar, EventArgs.Empty);
        }

        /// <summary>Puts the pill on one page with no animation, for the first paint.</summary>
        void SetNavHighlight(int index)
        {
            if (index < 0 || index >= _navRects.Count) return;
            _navNow = _navRects[index];
            if (_navBar != null) _navBar.SetHighlight(_navNow);
            for (int i = 0; i < _navButtons.Count; i++) _navButtons[i].SetHighlight(_navNow);
        }

        /// <summary>Slides the pill to another page over a couple of hundred milliseconds.</summary>
        void SlideNav(int index)
        {
            if (index < 0 || index >= _navRects.Count) return;
            if (_navTween != null) { _navTween.Stop(); _navTween.Dispose(); _navTween = null; }

            _navTarget = index;
            _navFrom = _navNow == Rectangle.Empty ? _navRects[index] : _navNow;
            _navTo = _navRects[index];
            _navStarted = DateTime.UtcNow;
            for (int i = 0; i < _navButtons.Count; i++)
                _navButtons[i].SetHighlight(i == index ? _navTo : Rectangle.Empty);

            _navTween = new System.Windows.Forms.Timer();
            _navTween.Interval = 15;
            _navTween.Tick += delegate { StepNav(); };
            _navTween.Start();
            StepNav();
        }

        void StepNav()
        {
            float t = (float)(DateTime.UtcNow - _navStarted).TotalMilliseconds / NavSlideMs;
            if (t >= 1f) t = 1f;
            float ease = 1f - (1f - t) * (1f - t) * (1f - t);      // ease-out cubic

            _navNow = new Rectangle(
                _navFrom.X + (int)Math.Round((_navTo.X - _navFrom.X) * ease),
                _navFrom.Y,
                _navFrom.Width + (int)Math.Round((_navTo.Width - _navFrom.Width) * ease),
                _navFrom.Height);

            // The bar draws the pill; the buttons only need to know which of them it covers,
            // so their icon picks the dark or the muted ink.
            if (_navBar != null) _navBar.SetHighlight(_navNow);
            for (int i = 0; i < _navButtons.Count; i++) _navButtons[i].SetHighlight(_navNow);

            if (t < 1f) return;
            if (_navTween != null) { _navTween.Stop(); _navTween.Dispose(); _navTween = null; }
            SetNavHighlight(_navTarget);
        }

        /// <summary>
        /// A page column of at most designWidth, centred in its host. Widths are capped rather
        /// than fixed so a narrow window does not push the column off the right edge.
        /// </summary>
        Skin.Sheet CenteredColumn(Panel host, int designWidth)
        {
            Skin.Sheet inner = new Skin.Sheet();
            inner.Anchor = AnchorStyles.Top | AnchorStyles.Bottom;
            host.Controls.Add(inner);

            EventHandler layout = delegate
            {
                int available = host.ClientSize.Width;
                int w = Math.Min(Ui.Px(designWidth), Math.Max(Ui.Px(260), available));
                inner.Width = w;
                inner.Left = Math.Max(0, (host.ClientSize.Width - w) / 2);
                inner.Top = 0;
                inner.Height = host.ClientSize.Height;
            };
            host.SizeChanged += layout;
            layout(host, EventArgs.Empty);
            return inner;
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Font = Theme.F(Theme.FSmall);
            ToolStripMenuItem show = new ToolStripMenuItem(Lang.T("نمایش پنجره", "Show window"));
            show.Click += delegate { RestoreWindow(); };
            _miToggle = new ToolStripMenuItem("");
            _miToggle.Click += delegate { ToggleTunnel(); };
            ToolStripMenuItem update = new ToolStripMenuItem(
                Lang.T("بررسی به‌روزرسانی", "Check for updates"));
            update.Click += delegate { RestoreWindow(); CheckForUpdate(true); };

            ToolStripMenuItem exit = new ToolStripMenuItem(Lang.T("خروج", "Exit"));
            exit.Click += delegate { _reallyExit = true; Close(); };

            menu.Items.Add(show);
            menu.Items.Add(_miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(update);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exit);

            _tray = new NotifyIcon();
            _tray.Icon = LoadAppIcon(true);
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreWindow(); };
        }

        /// <summary>
        /// The mark for this window or for the tray, read from the .ico beside the exe at the
        /// size that surface actually shows. Asking for the size matters: a tray icon taken from
        /// the largest frame is soft, and the icon file carries a hand-tuned frame for each size
        /// Windows asks for. When the file is missing — a bare exe copied out of the installer —
        /// the exe's own embedded icon is the fallback.
        /// </summary>
        static Icon LoadAppIcon(bool small)
        {
            try
            {
                string ico = Path.Combine(AppPaths.ExeDir, "VMTun.ico");
                if (File.Exists(ico))
                {
                    Size want = small ? SystemInformation.SmallIconSize : SystemInformation.IconSize;
                    return new Icon(ico, want.Width, want.Height);
                }
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); }
            catch { return SystemIcons.Shield; }
        }

        void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) index = 0;
            _currentPage = index;
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = (i == index);
            SlideNav(index);

            // A page is opened at its top, and the wheel then drives whichever page is on screen.
            _activeScroller = _pages[index] as Skin.ScrollArea;
            if (_activeScroller != null) _activeScroller.ScrollTo(0);
        }

        /// <summary>
        /// Gives a page its one scrolling body: a column of the design width, centred, moved up
        /// and down by the wheel. Nothing here ever shows a scroll bar.
        /// </summary>
        Skin.Sheet ScrollBody(Skin.ScrollArea page, int width)
        {
            Skin.Sheet body = new Skin.Sheet();
            body.Width = width;
            page.Body = body;
            page.Controls.Add(body);

            EventHandler layout = delegate
            {
                body.Left = Math.Max(0, (page.ClientSize.Width - body.Width) / 2);
                page.Clamp();
            };
            page.SizeChanged += layout;
            layout(page, EventArgs.Empty);
            return body;
        }

        /// <summary>
        /// Routes the mouse wheel to the page on screen, wherever the pointer is inside the
        /// window. Without a scroll bar there is nothing for WinForms to give the wheel to, and
        /// the wheel only reaches a control that already has focus.
        /// </summary>
        class WheelRouter : IMessageFilter
        {
            readonly MainForm _form;

            public WheelRouter(MainForm form) { _form = form; }

            public bool PreFilterMessage(ref Message m)
            {
                const int WM_MOUSEWHEEL = 0x020A;
                if (m.Msg != WM_MOUSEWHEEL) return false;
                return _form.ScrollFromWheel(m.WParam);
            }
        }

        /// <summary>Applies one wheel notch to the page on screen. False leaves the wheel alone.</summary>
        bool ScrollFromWheel(IntPtr wParam)
        {
            if (!Visible || WindowState == FormWindowState.Minimized) return false;
            if (!Bounds.Contains(Cursor.Position)) return false;

            int delta = (short)((long)wParam >> 16);
            if (delta == 0) return false;

            if (_activeScroller != null)
            {
                int before = _activeScroller.Offset;
                _activeScroller.ScrollBy(-delta / 2);
                return _activeScroller.Offset != before;
            }

            // The log is the one page that is not a scroller: the text is the page, and it has
            // no scroll bar either, so the wheel is delivered to the box directly. Without this
            // the wheel only reached the log while it held the focus, and after a couple of
            // seconds of logging the reader could not look back at all.
            if (_log != null && !_log.IsDisposed && _log.Visible)
            {
                SendMessage(_log.Handle, EM_LINESCROLL, IntPtr.Zero,
                            (IntPtr)(delta > 0 ? -LogWheelLines : LogWheelLines));
                return true;
            }
            return false;
        }

        /// <summary>Window messages the log box needs, since it has no scroll bar to drag.</summary>
        const int EM_GETLINECOUNT = 0x00BA;
        const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        const int EM_LINESCROLL = 0x00B6;
        const int LogWheelLines = 3;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// True while the log is showing its newest line, which is the only time a new line
        /// should drag the view down with it. Once the reader has scrolled back to look at
        /// something, the tail must keep growing underneath without pulling the page away.
        /// </summary>
        bool LogAtTail()
        {
            if (_log == null || _log.IsDisposed || !_log.IsHandleCreated) return true;
            int first = (int)SendMessage(_log.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            int total = (int)SendMessage(_log.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
            int rows = Math.Max(1, _log.ClientSize.Height / Math.Max(1, _log.Font.Height));
            return first + rows >= total - 1;
        }

        // =================================================================== status page

        /// <summary>
        /// The status page, top to bottom: the orb (Pro Connect), the verification line, the
        /// plain Connect button, the state message, three rows of facts and the checks. It is
        /// one scrolling column, laid out by LayoutStatus whenever its width or content changes.
        /// </summary>
        Panel BuildStatusPage()
        {
            // Everything sits on one body panel inside a scroller. The page itself draws no
            // scroll bar: the body is moved, and its width is fixed, so nothing can ever be
            // squeezed sideways onto a second bar.
            Skin.ScrollArea page = new Skin.ScrollArea();
            _statusPage = page;
            _statusBody = ScrollBody(page, ContentWidth);

            // ---- hero ------------------------------------------------------------------
            _orb = new Skin.Orb();
            _orb.Height = Ui.Px(OrbZone);
            _orb.Pressed += delegate { OnOrbPressed(); };
            _statusBody.Controls.Add(_orb);

            _lblVerifCaption = Theme.Label(Lang.T("تأیید", "Verification"), Theme.FSmall, Theme.Muted, false);
            _statusBody.Controls.Add(_lblVerifCaption);
            _lblVerif = Theme.Label(Lang.T("تأیید نشده", "Not verified"), Theme.FSmall, Theme.Text, true);
            _statusBody.Controls.Add(_lblVerif);

            // The plain connect stays the one that changes nothing outside this app, so it is
            // always offered beside the orb rather than folded into it.
            _btnToggle = Theme.Button(Lang.T("اتصال", "Connect"), Theme.AccentWash, 140, 42);
            _btnToggle.Font = Theme.FB(Theme.FSmall);
            _btnToggle.Icon = Skin.Icon.Power;
            _btnToggle.LineColor = Theme.AccentLine;
            _btnToggle.Ink = Theme.AccentSoft;
            _btnToggle.Click += delegate { if (_tunnel.State != TunnelState.Connected) BeginConnect(); };
            _statusBody.Controls.Add(_btnToggle);

            _stateDetail = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            _stateDetail.TextAlign = ContentAlignment.MiddleCenter;
            _stateDetail.TextChanged += delegate { LayoutStatus(); };
            _statusBody.Controls.Add(_stateDetail);

            // ---- facts -----------------------------------------------------------------
            // The first fact of each row sits on the right, as the reading starts there.
            _factPills.Add(FactPill(_statusBody, Lang.T("پروکسی بالادست", "Upstream proxy"), Lang.T("DNS", "DNS"),
                                    out _sumProxy, out _sumDns));
            _factPills.Add(FactPill(_statusBody, Lang.T("مسیریابی", "Routing"), Lang.T("آداپتور", "Adapter"),
                                    out _sumRouting, out _sumAdapter));
            _factPills.Add(FactPill(_statusBody, Lang.T("IP خروجی", "Exit IP"), Lang.T("کیل‌سوئیچ", "Kill switch"),
                                    out _sumExit, out _sumGuard));

            // ---- checks ----------------------------------------------------------------
            _lblChecks = Theme.Label(Lang.T("بررسی‌ها", "Checks"), 15, Theme.Text, true);
            _statusBody.Controls.Add(_lblChecks);

            _lblPhase = Theme.Label("", Theme.FTiny, Theme.Muted, false);
            _lblPhase.AutoEllipsis = true;
            _statusBody.Controls.Add(_lblPhase);

            _btnRecheck = Theme.Button(Lang.T("بررسی مجدد", "Re-run"), Theme.Card, 96, 30);
            _btnRecheck.Font = Theme.FB(Theme.FTiny);
            _btnRecheck.Icon = Skin.Icon.Refresh;
            _btnRecheck.LineColor = Theme.BorderHi;
            _btnRecheck.Ink = Theme.AccentSoft;
            _btnRecheck.FitWidth(90);
            _btnRecheck.Click += delegate { RunPreflightAsync(); };
            _statusBody.Controls.Add(_btnRecheck);

            _checkHost = new Skin.Sheet();
            _statusBody.Controls.Add(_checkHost);

            page.SizeChanged += delegate { LayoutStatus(); };
            LayoutStatus();
            return page;
        }

        /// <summary>
        /// A rounded row holding two facts side by side, with a hairline between them. The
        /// labels sit inside the card's straight section, clear of its rounded ends, so the
        /// card's corners are never painted over.
        /// </summary>
        Theme.CardPanel FactPill(Panel host, string captionRight, string captionLeft,
                                 out Label valueRight, out Label valueLeft)
        {
            Theme.CardPanel card = new Theme.CardPanel();
            card.Fill = Theme.Card;
            card.Line = Theme.Border;
            card.Radius = 22;
            card.Height = Ui.Px(58);

            Label kr = FactLabel(captionRight, Theme.F(Theme.FTiny), Theme.Muted);
            Label vr = FactLabel("—", Theme.FLatinB(Theme.FSmall), Theme.Text);
            Label kl = FactLabel(captionLeft, Theme.F(Theme.FTiny), Theme.Muted);
            Label vl = FactLabel("—", Theme.FLatinB(Theme.FSmall), Theme.Text);
            card.Controls.Add(kr);
            card.Controls.Add(vr);
            card.Controls.Add(kl);
            card.Controls.Add(vl);

            card.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.CardPanel c = (Theme.CardPanel)sender;
                using (Pen p = new Pen(Theme.Border, Math.Max(1f, Ui.Scale)))
                    e.Graphics.DrawLine(p, c.Width / 2, Ui.Px(1), c.Width / 2, c.Height - Ui.Px(2));
            };

            card.SizeChanged += delegate
            {
                int half = card.Width / 2;
                int pad = Ui.Px(18);
                int capH = Theme.TextH(Theme.FTiny);
                int valH = Theme.TextH(Theme.FSmall);
                int top = Math.Max(Ui.Px(8), (card.Height - capH - valH - Ui.Px(2)) / 2);
                int cellW = Math.Max(Ui.Px(40), half - pad * 2);

                kl.SetBounds(pad, top, cellW, capH);
                vl.SetBounds(pad, top + capH + Ui.Px(2), cellW, valH);
                kr.SetBounds(half + pad, top, cellW, capH);
                vr.SetBounds(half + pad, top + capH + Ui.Px(2), cellW, valH);
            };

            host.Controls.Add(card);
            valueRight = vr;
            valueLeft = vl;
            return card;
        }

        static Label FactLabel(string text, Font font, Color colour)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = font;
            l.ForeColor = colour;
            l.BackColor = Color.Transparent;
            l.AutoSize = false;
            l.AutoEllipsis = true;
            l.UseMnemonic = false;
            l.RightToLeft = Theme.TextDirection;
            l.TextAlign = Theme.VisualRight;
            return l;
        }

        /// <summary>
        /// Places the status column top to bottom for the page's current width and tells the
        /// page how tall the content is, so it scrolls when the window is shorter than that.
        /// </summary>
        void LayoutStatus()
        {
            Skin.ScrollArea page = _statusPage;
            if (page == null || _layingOutStatus || _checkHost == null) return;
            int w = ContentWidth;
            if (w < Ui.Px(200)) return;

            _layingOutStatus = true;
            _statusBody.SuspendLayout();
            try
            {
                int y = Ui.Px(2);

                _orb.SetBounds(0, y, w, Ui.Px(OrbZone));
                y += _orb.Height;

                int capW = _lblVerifCaption.PreferredWidth, valW = _lblVerif.PreferredWidth;
                int verifW = capW + Ui.Px(6) + valW;
                int vx = Math.Max(0, (w - verifW) / 2);
                // Caption on the right, value to its left, read in that order in Persian.
                if (Lang.Fa)
                {
                    _lblVerif.Location = new Point(vx, y);
                    _lblVerifCaption.Location = new Point(vx + valW + Ui.Px(6), y);
                }
                else
                {
                    _lblVerifCaption.Location = new Point(vx, y);
                    _lblVerif.Location = new Point(vx + capW + Ui.Px(6), y);
                }
                y += Math.Max(_lblVerif.PreferredHeight, _lblVerifCaption.PreferredHeight) + Ui.Px(14);

                if (_btnToggle.Visible)
                {
                    _btnToggle.Location = new Point(Math.Max(0, (w - _btnToggle.Width) / 2), y);
                    y += _btnToggle.Height + Ui.Px(12);
                }

                _stateDetail.MaximumSize = new Size(Math.Max(Ui.Px(120), w - Ui.Px(40)), 0);
                Size detail = _stateDetail.PreferredSize;
                _stateDetail.Size = detail;
                _stateDetail.Location = new Point(Math.Max(0, (w - detail.Width) / 2), y);
                y += detail.Height + Ui.Px(20);

                foreach (Theme.CardPanel pill in _factPills)
                {
                    pill.SetBounds(0, y, w, Ui.Px(58));
                    y += pill.Height + Ui.Px(8);
                }
                y += Ui.Px(14);

                // Checks header: title on the right, the phase beside it, Re-run on the left.
                int headH = Math.Max(_btnRecheck.Height, _lblChecks.PreferredHeight);
                _lblChecks.Location = new Point(w - _lblChecks.PreferredWidth - Ui.Px(4),
                                                y + (headH - _lblChecks.PreferredHeight) / 2);
                _btnRecheck.Location = new Point(0, y + (headH - _btnRecheck.Height) / 2);
                int phaseRight = _lblChecks.Left - Ui.Px(8);
                int phaseLeft = _btnRecheck.Right + Ui.Px(8);
                int phaseH = _lblPhase.PreferredHeight;
                _lblPhase.AutoSize = false;
                _lblPhase.TextAlign = Theme.VisualRight;
                _lblPhase.SetBounds(phaseLeft, _lblChecks.Bottom - phaseH - Ui.Px(1),
                                    Math.Max(0, phaseRight - phaseLeft), phaseH);
                y += headH + Ui.Px(12);

                int checksH = LayoutChips(w);
                _checkHost.SetBounds(0, y, w, Math.Max(1, checksH));
                y += checksH + Ui.Px(20);

                _statusBody.Height = y;
                page.Clamp();
            }
            finally
            {
                _statusBody.ResumeLayout();
                _layingOutStatus = false;
            }
        }

        /// <summary>
        /// Flows the check chips from the right edge, wrapping onto new rows; a warning or a
        /// failure takes a full row of its own. Returns the height used.
        /// </summary>
        int LayoutChips(int width)
        {
            int gap = Ui.Px(8);
            int x = width, y = 0, rowH = 0;
            foreach (Control c in _checkHost.Controls)
            {
                Skin.Chip chip = c as Skin.Chip;
                if (chip == null) continue;
                if (chip.Wide)
                {
                    if (x < width) { y += rowH + gap; }
                    chip.SetBounds(0, y, width, chip.Height);
                    y += chip.Height + gap;
                    x = width;
                    rowH = 0;
                    continue;
                }
                int cw = Math.Min(chip.Width, width);
                if (x - cw < 0 && x < width) { y += rowH + gap; x = width; rowH = 0; }
                chip.SetBounds(x - cw, y, cw, chip.Height);
                x -= cw + gap;
                rowH = Math.Max(rowH, chip.Height);
            }
            if (x < width) y += rowH; else if (y > 0) y -= gap;
            return y;
        }

        string _lastPhase = "";
        List<CheckResult> _lastChecks = new List<CheckResult>();

        /// <summary>
        /// Shows the pre-connection checks as chips: passed ones compact, anything that needs
        /// attention on a full-width card. The full detail and the hint of every check are in
        /// its tooltip, since a chip only has room for its name.
        /// </summary>
        void SetChecks(string phase, List<CheckResult> checks)
        {
            _lastPhase = phase;
            _lastChecks = checks;
            _lblPhase.Text = phase;

            _checkHost.SuspendLayout();
            foreach (Control old in new List<Control>(_checkHost.Controls.Cast<Control>()))
            {
                _tips.SetToolTip(old, null);
                old.Dispose();
            }
            _checkHost.Controls.Clear();

            if (checks != null)
            {
                foreach (CheckResult c in checks)
                {
                    bool wide = c.Status == CheckStatus.Warn || c.Status == CheckStatus.Fail;
                    Skin.Chip chip = new Skin.Chip(c, wide);
                    string tip = c.Title;
                    if (!string.IsNullOrEmpty(c.Detail)) tip += Environment.NewLine + c.Detail;
                    if (!string.IsNullOrEmpty(c.Hint)) tip += Environment.NewLine + Environment.NewLine + c.Hint;
                    _tips.SetToolTip(chip, tip);
                    _checkHost.Controls.Add(chip);
                }
            }
            _checkHost.ResumeLayout();
            LayoutStatus();
        }

        /// <summary>The orb is Pro Connect from a standing start, and Disconnect while connected.</summary>
        void OnOrbPressed()
        {
            if (_tunnel.State == TunnelState.Connected) BeginDisconnect();
            else ProConnect();
        }

        /// <summary>
        /// Lays a list of checks into a panel, dropping detail until it fits. Shared by the
        /// status page and the privacy page so both lists read identically.
        /// </summary>
        void RenderChecks(Panel host, List<CheckResult> checks)
        {
            RenderChecks(host, checks, false);
        }

        void RenderChecks(Panel host, List<CheckResult> checks, bool alwaysShowHints)
        {
            if (host.ClientSize.Width < 100 || checks == null || checks.Count == 0)
            {
                host.Controls.Clear();
                return;
            }

            host.SuspendLayout();
            foreach (Control old in new List<Control>(host.Controls.Cast<Control>())) old.Dispose();
            host.Controls.Clear();

            int width = host.ClientSize.Width;
            int available = host.ClientSize.Height;

            // A scrolling host lays its rows out before the vertical bar has claimed its width,
            // so the first pass overflows sideways and WinForms answers with a horizontal bar
            // that never goes away. Reserving the width up front costs a few pixels and removes
            // the second scrollbar entirely.
            if (alwaysShowHints && host.AutoScroll)
                width -= SystemInformation.VerticalScrollBarWidth;
            int lineH = Theme.TextH(Theme.FSmall);
            // Reserve room for the dot, the tag and the row padding; measuring wider than the
            // real label would under-estimate the number of wrapped lines and clip the hint.
            int hintWidth = Math.Max(Ui.Px(80), width - Ui.Px(80));

            Font titleFont = Theme.FB(Theme.FSmall);
            Font hintFont = Theme.F(Theme.FTiny);

            // Pass 1: full rows with hints. Pass 2: hints dropped. Pass 3: minimum rows.
            // A scrolling host has no height to fit into, so the first pass always wins
            // there and no explanation is thrown away to save a few pixels.
            if (alwaysShowHints) available = int.MaxValue;

            for (int pass = 0; pass < 3; pass++)
            {
                bool showHints = (pass == 0);
                int topPad = pass == 2 ? Ui.Px(7) : Ui.Px(11);
                int rowBase = lineH + topPad * 2;
                int total = 0;
                List<int> heights = new List<int>();
                foreach (CheckResult c in checks)
                {
                    int h = rowBase;
                    if (showHints && !string.IsNullOrEmpty(c.Hint))
                    {
                        Size hs = TextRenderer.MeasureText(c.Hint, hintFont,
                            new Size(hintWidth, 0), TextFormatFlags.WordBreak);
                        h += hs.Height + Ui.Px(4);
                    }
                    heights.Add(h);
                    total += h;
                }
                if (total <= available || pass == 2)
                {
                    int y = 0;
                    for (int i = 0; i < checks.Count; i++)
                    {
                        host.Controls.Add(
                            BuildCheckRow(checks[i], width, heights[i], y, showHints,
                                          lineH, titleFont, hintFont));
                        y += heights[i];
                    }
                    break;
                }
            }

            host.ResumeLayout();
        }

        Control BuildCheckRow(CheckResult c, int width, int height, int y, bool showHint,
                              int lineH, Font titleFont, Font hintFont)
        {
            // No anchor: the whole list is rebuilt at the right width whenever the host resizes.
            Skin.Sheet row = new Skin.Sheet();
            row.Location = new Point(0, y);
            row.Size = new Size(width, height);

            Panel rule = new Panel();
            rule.Dock = DockStyle.Bottom;
            rule.Height = Math.Max(1, (int)Ui.Scale);
            rule.BackColor = Theme.Border;
            row.Controls.Add(rule);

            int topPad = showHint ? Ui.Px(11) : Ui.Px(7);
            if (height < lineH + topPad * 2) topPad = Math.Max(Ui.Px(5), (height - lineH) / 2);

            Skin.Dot dot = new Skin.Dot(7);
            dot.Fill = Theme.StatusColor(c.Status);
            dot.Location = new Point(width - dot.Width - Ui.Px(2),
                                     topPad + (lineH - dot.Height) / 2);
            row.Controls.Add(dot);

            bool marked = c.Status == CheckStatus.Warn || c.Status == CheckStatus.Fail;
            int tagWidth = marked ? Ui.Px(52) : 0;
            if (marked)
            {
                Label tag = new Label();
                tag.Text = c.Status == CheckStatus.Fail
                    ? Lang.T("خطا", "Error")
                    : Lang.T("هشدار", "Warning");
                tag.Font = hintFont;
                tag.ForeColor = Theme.StatusColor(c.Status);
                tag.BackColor = Color.Transparent;
                tag.AutoSize = false;
                tag.UseMnemonic = false;
                tag.RightToLeft = Theme.TextDirection;
                tag.Location = new Point(Ui.Px(4), topPad);
                tag.Size = new Size(tagWidth - Ui.Px(4), lineH);
                tag.TextAlign = Theme.VisualLeft;
                row.Controls.Add(tag);
            }

            int titleRight = width - dot.Width - Ui.Px(2) - Ui.Px(10);
            int titleWidth = Math.Min(Ui.Px(210), Math.Max(Ui.Px(90), width / 3));
            int titleLeft = Math.Max(Ui.Px(8) + tagWidth, titleRight - titleWidth);

            Label title = new Label();
            title.Text = c.Title;
            title.Font = titleFont;
            title.ForeColor = Theme.Text;
            title.BackColor = Color.Transparent;
            title.AutoSize = false;
            title.AutoEllipsis = true;
            title.UseMnemonic = false;
            title.RightToLeft = Theme.TextDirection;
            title.TextAlign = Theme.VisualRight;
            title.Location = new Point(titleLeft, topPad);
            title.Size = new Size(Math.Max(Ui.Px(60), titleRight - titleLeft), lineH);
            row.Controls.Add(title);

            // The detail sits on the same line as the title, to its left, so a check takes one
            // line instead of two.
            int detailRight = Math.Max(Ui.Px(40), titleLeft - Ui.Px(10));
            int detailLeft = Ui.Px(6) + tagWidth;
            Label detail = new Label();
            detail.Text = c.Detail;
            detail.Font = Theme.FLatin(Theme.FSmall);
            detail.ForeColor = Theme.Muted;
            detail.BackColor = Color.Transparent;
            detail.AutoSize = false;
            detail.AutoEllipsis = true;
            detail.UseMnemonic = false;
            detail.RightToLeft = Theme.TextDirection;
            detail.TextAlign = Theme.VisualRight;
            detail.Location = new Point(detailLeft, topPad);
            detail.Size = new Size(Math.Max(Ui.Px(40), detailRight - detailLeft), lineH);
            row.Controls.Add(detail);

            if (showHint && !string.IsNullOrEmpty(c.Hint))
            {
                int hintLeft = Ui.Px(6) + tagWidth;
                int hintRight = width - dot.Width - Ui.Px(2) - Ui.Px(10);
                Label hint = new Label();
                hint.Text = c.Hint;
                hint.Font = hintFont;
                hint.ForeColor = marked ? Theme.StatusColor(c.Status) : Theme.Muted;
                hint.BackColor = Color.Transparent;
                hint.AutoSize = false;
                hint.UseMnemonic = false;
                hint.RightToLeft = Theme.TextDirection;
                hint.TextAlign = Theme.VisualTopRight;
                hint.Location = new Point(hintLeft, topPad + lineH + Ui.Px(2));
                hint.Size = new Size(Math.Max(Ui.Px(80), hintRight - hintLeft),
                                     Math.Max(lineH, height - topPad - lineH - Ui.Px(6)));
                row.Controls.Add(hint);
            }
            return row;
        }

        void RunPreflightAsync()
        {
            _btnRecheck.Enabled = false;
            SetChecks(Lang.T("در حال بررسی…", "Checking…"), new List<CheckResult> {
                new CheckResult(CheckStatus.Running,
                    Lang.T("در حال آزمودن پروکسی", "Testing the proxy"),
                    Lang.T("TCP، اینترنت و UDP…", "TCP, internet reach, UDP relay…"))
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                bool fatal, udp;
                List<CheckResult> results = Preflight.Run(_settings, out fatal, out udp);
                UiInvoke(delegate
                {
                    _btnRecheck.Enabled = true;
                    SetChecks(Lang.T("بررسی پیش از اتصال", "Before connecting"), results);
                    RefreshSummary();
                });
            });
        }

        void RefreshSummary()
        {
            _sumProxy.Text = _settings.ProxyHost + ":" +
                _settings.ProxyPort.ToString(CultureInfo.InvariantCulture) + "  " + _settings.ProxyType;
            _sumDns.Text = ConfigBuilder.DescribeDns(_settings) + "  →  " + _settings.RemoteDns;
            _sumRouting.Text = _settings.Routing == RoutingMode.IranDirect
                ? Lang.T("ایران مستقیم", "Iran direct")
                : Lang.T("تونل کامل", "Full tunnel");

            string alias = TunAdapter.FindAlias();
            _sumAdapter.Text = alias == null ? Lang.T("بالا نیامده", "not up") : alias;
            _sumAdapter.ForeColor = alias == null ? Theme.Muted : Theme.Green;

            _sumExit.Text = string.IsNullOrEmpty(_tunnel.ExitIp) ? "—" : _tunnel.ExitIp;
            bool guard = FirewallGuard.IsActive();
            _sumGuard.Text = guard ? Lang.T("فعال", "armed") : Lang.T("غیرفعال", "off");
            _sumGuard.ForeColor = guard ? Theme.Amber : Theme.Muted;
        }

        // =================================================================== settings page

        Panel _column;
        Theme.CardPanel _section;
        Skin.Sheet _settingsColumn;
        int _sectionY, _columnY, _columnWidth;

        Panel BuildSettingsPage()
        {
            Skin.ScrollArea page = new Skin.ScrollArea();
            Skin.Sheet inner = ScrollBody(page, ContentWidth);
            _settingsColumn = inner;

            // One column of section cards; the page scrolls by moving this column.
            Skin.Sheet column = new Skin.Sheet();
            column.Width = ContentWidth;
            inner.Controls.Add(column);

            _columnWidth = ContentWidth;
            BeginColumn(column);

            BeginSection(Lang.T("زبان", "Language"));
            _segLang = new Theme.Segmented(
                new string[] { "fa", "en" }, new string[] { "فارسی", "English" }, 96);
            _segLang.ValueChanged += delegate { if (!_loading) RestartWith(_segLang.Value, _settings.Theme); };
            Row("", _segLang);
            EndSection();

            BeginSection(Lang.T("پروکسی بالادست", "Upstream proxy"));
            _txtHost = new TextBox();
            Row(Lang.T("آدرس", "Host"), Grow(Theme.Well(_txtHost, 200)));

            _numPort = new NumericUpDown();
            _numPort.Minimum = 1;
            _numPort.Maximum = 65535;
            Row(Lang.T("پورت", "Port"), Theme.Well(_numPort, 110));

            _segType = new Theme.Segmented(new string[] { "socks", "http" }, null, 84);
            Row(Lang.T("نوع پروکسی", "Proxy type"), _segType);

            Panel actions = new Panel();
            actions.Size = Ui.Sz(300, 34);
            actions.BackColor = Color.Transparent;
            Button detect = Theme.Button(Lang.T("شناسایی خودکار", "Auto-detect"), Theme.CardHi, 150, 34);
            detect.Click += delegate { DetectProxy(); };
            Button test = Theme.Button(Lang.T("تست پروکسی", "Test proxy"), Theme.CardHi, 140, 34);
            test.Location = new Point(Ui.Px(158), 0);
            test.Click += delegate { SaveSettingsFromUi(false); ShowPage(PageStatus); RunPreflightAsync(); };
            actions.Controls.Add(detect);
            actions.Controls.Add(test);
            Row("", actions);
            EndSection();

            BeginSection(Lang.T("مسیریابی", "Routing"));
            _rbFull = Radio(Lang.T("تونل کامل — همه ترافیک از پروکسی",
                                   "Full tunnel — everything through the proxy"));
            RowFull(_rbFull);
            _rbIran = Radio(Lang.T("تونل کامل + ایران مستقیم",
                                   "Full tunnel + Iranian sites direct"));
            RowFull(_rbIran);
            EndSection();

            BeginSection(Lang.T("DNS و پروتکل", "DNS and protocol"));
            _segDnsMode = new Theme.Segmented(
                new string[] { "doh", "dot", "tcp", "udp" },
                new string[] { "DoH", "DoT", "TCP", "UDP" }, 64);
            Row(Lang.T("روش DNS", "DNS transport"), _segDnsMode);

            _txtDns = new TextBox();
            Row(Lang.T("سرور DNS", "DNS server"), Grow(Theme.Well(_txtDns, 200)));

            // Pro Connect uses its own resolver when one is given, so that the hardened
            // connection does not have to share whatever the ordinary one is pointed at.
            _txtProDns = new TextBox();
            _txtProDns.TextChanged += delegate
            {
                if (_loading) return;
                _settings.ProDns = _txtProDns.Text.Trim();
            };
            Row(Lang.T("DNS اتصال پیشرفته", "Pro Connect DNS"), Grow(Theme.Well(_txtProDns, 200)));

            _chkQuic = Check(Lang.T("بستن QUIC (برای سرورهای بدون UDP)",
                                    "Block QUIC (for servers without UDP)"));
            RowFull(_chkQuic);
            _chkIpv6 = Check(Lang.T("عبور IPv6 از تونل", "Carry IPv6 through the tunnel"));
            RowFull(_chkIpv6);
            EndSection();

            BeginSection(Lang.T("امنیت و رفتار", "Safety and behaviour"));
            _chkKill = Check(Lang.T("کیل‌سوئیچ — با قطع تونل اینترنت هم قطع شود",
                                    "Kill switch — cut the internet if the tunnel drops"));
            RowFull(_chkKill);
            _chkAuto = Check(Lang.T("اتصال خودکار هنگام باز شدن برنامه",
                                    "Connect automatically on start"));
            RowFull(_chkAuto);
            _chkStartup = Check(Lang.T("اجرا هنگام روشن شدن ویندوز", "Run at Windows startup"));
            RowFull(_chkStartup);
            _chkTray = Check(Lang.T("با بستن پنجره کنار ساعت بماند",
                                    "Keep running in the tray when closed"));
            RowFull(_chkTray);
            _chkUpdate = Check(Lang.T("بررسی روزانه به‌روزرسانی", "Check for updates daily"));
            RowFull(_chkUpdate);

            _chkNotify = Check(Lang.T("اعلان کنار ساعت هنگام اتصال و قطع",
                                      "Tray notifications when connecting and disconnecting"));
            RowFull(_chkNotify);
            EndSection();

            BeginSection(Lang.T("پیشرفته", "Advanced"));
            _segStack = new Theme.Segmented(new string[] { "gvisor", "system", "mixed" }, null, 84);
            Row(Lang.T("پشته شبکه", "Network stack"), _segStack);

            _numMtu = new NumericUpDown();
            _numMtu.Minimum = 576;
            _numMtu.Maximum = 9000;
            _numMtu.Increment = 100;
            Row("MTU", Theme.Well(_numMtu, 110));

            _txtExtraDirect = new TextBox();
            Row(Lang.T("برنامه‌های مستثنی", "Bypass apps"), Grow(Theme.Well(_txtExtraDirect, 230)));
            EndSection();

            Theme.RoundButton save = Theme.Button(Lang.T("ذخیره تنظیمات", "Save settings"), Theme.Accent, 200, 42);
            save.Font = Theme.FB(Theme.FBody);
            save.Location = new Point(0, _columnY + Ui.Px(2));
            save.Click += delegate { SaveSettingsFromUi(true); };
            column.Controls.Add(save);

            int contentHeight = _columnY + Ui.Px(2) + save.Height + Ui.Px(20);
            column.Height = contentHeight;
            inner.Height = contentHeight;
            page.Clamp();
            return page;
        }

        /// <summary>Marks a field to stretch with its card, minus the card's inner margin.</summary>
        static Control Grow(Control c)
        {
            c.Tag = "grow";
            return c;
        }

        void BeginColumn(Panel column)
        {
            _column = column;
            _columnY = Ui.Px(4);
        }

        void BeginSection(string caption)
        {
            _section = new Theme.CardPanel();
            _section.Fill = Theme.Card;
            _section.Line = Theme.Border;
            _section.Radius = Skin.RCard;
            _section.Location = new Point(0, _columnY);
            _section.Width = _columnWidth;

            Label l = Theme.Label(caption, Theme.FBody, Theme.Text, true);
            l.Location = Ui.Pt(CardPad, 14);
            _section.Controls.Add(l);
            _sectionY = Ui.Px(14) + Theme.TextH(Theme.FBody) + Ui.Px(10);
        }

        void EndSection()
        {
            _section.Height = _sectionY + Ui.Px(6);
            _column.Controls.Add(_section);
            _columnY += _section.Height + Ui.Px(12);
        }

        /// <summary>
        /// A caption on its own line with the control under it: the narrow window has no room
        /// for a label column beside the fields.
        /// </summary>
        void Row(string caption, Control control)
        {
            if (caption.Length > 0)
            {
                Label l = Theme.Label(caption, Theme.FSmall, Theme.Muted, false);
                l.Location = new Point(Ui.Px(CardPad), _sectionY);
                _section.Controls.Add(l);
                _sectionY += Theme.TextH(Theme.FSmall) + Ui.Px(6);
            }
            control.Location = new Point(Ui.Px(CardPad), _sectionY);
            _section.Controls.Add(control);
            _sectionY += control.Height + Ui.Px(14);
        }

        /// <summary>A control that carries its own label.</summary>
        void RowFull(Control control)
        {
            control.Location = new Point(Ui.Px(CardPad), _sectionY);
            _section.Controls.Add(control);
            _sectionY += Math.Max(control.Height, Ui.Px(20)) + Ui.Px(12);
        }

        CheckBox Check(string text)
        {
            Theme.FlatCheckBox c = new Theme.FlatCheckBox();
            c.WrapWidth = OptionWrap;
            c.Text = text;
            c.AutoSize = true;
            return c;
        }

        RadioButton Radio(string text)
        {
            Theme.FlatRadioButton r = new Theme.FlatRadioButton();
            r.WrapWidth = OptionWrap;
            r.Text = text;
            r.AutoSize = true;
            return r;
        }

        // =================================================================== tools page

        Panel BuildToolsPage()
        {
            Skin.ScrollArea page = new Skin.ScrollArea();
            Skin.Sheet inner = ScrollBody(page, ContentWidth);

            int y = Ui.Px(4);
            y = ToolCard(inner, y,
                Lang.T("رفع محدودیت اپ‌های UWP / Store", "Fix Windows Store / UWP apps"),
                Lang.T("اپ‌های UWP به‌صورت پیش‌فرض اجازه ارتباط با آدرس محلی ندارند. این دکمه برای همه بسته‌های نصب‌شده استثنا ثبت می‌کند.",
                       "UWP apps are denied loopback access by default. This registers an exemption for every installed package."),
                Theme.CardHi, delegate { FixUwp(); });

            y = ToolCard(inner, y,
                Lang.T("بررسی IP خروجی فعلی", "Check current external IP"),
                Lang.T("یک درخواست بدون پروکسی می‌فرستد. با تونل سالم باید IP سرور خارجی برگردد.",
                       "Sends an unproxied request. With a healthy tunnel this returns the server address."),
                Theme.CardHi, delegate { CheckExternalIp(); });

            y = ToolCard(inner, y,
                Lang.T("باز کردن پوشه لاگ و کانفیگ", "Open log and config folder"),
                AppPaths.DataDir,
                Theme.CardHi, delegate { OpenDataFolder(); });

            y = ToolCard(inner, y,
                Lang.T("لغو محدودیت‌زدایی UWP", "Undo UWP exemptions"),
                Lang.T("استثناهای loopback که بالا ثبت شده‌اند را پاک می‌کند.",
                       "Clears the loopback exemptions registered above."),
                Theme.CardHi, delegate { ClearUwp(); });

            y = ToolCard(inner, y,
                Lang.T("بازیابی شبکه", "Repair network"),
                Lang.T("همه قوانین فایروال VMTun را حذف و سیاست خروجی ویندوز را به حالت اول برمی‌گرداند. اگر برنامه ناگهانی بسته شد و اینترنت قطع ماند، این را بزنید.",
                       "Removes every VMTun firewall rule and restores the Windows outbound policy. Use it if the app was killed and left you offline."),
                Theme.Danger, delegate { RepairNetwork(); });

            ToolCard(inner, y,
                Lang.T("بررسی به‌روزرسانی", "Check for updates"),
                Lang.T("نسخه فعلی " + Integration.Version + ". نسخه تازه از گیت‌هاب گرفته و نصب می‌شود. " +
                       "درخواست از داخل تونل می‌رود، پس بهتر است اول وصل باشید.",
                       "You have version " + Integration.Version + ". A newer build is fetched from GitHub and " +
                       "installed. The request travels through the tunnel, so connect first if you can."),
                Theme.CardHi, delegate { CheckForUpdate(true); });

            // Height taken from the cards that are actually on the body, not from the running
            // offset. The last card — the one that checks for updates — used to start below the
            // end of the body, so it was clipped away and the scroller could not reach it.
            int height = Ui.Px(4);
            foreach (Control card in inner.Controls) height = Math.Max(height, card.Bottom + Ui.Px(6));
            inner.Height = height;
            page.Clamp();
            return page;
        }

        /// <summary>
        /// One tool: a card with the action as a full-width button and what it does under it.
        /// The description is measured at the card's inner width so it is never cut off.
        /// </summary>
        int ToolCard(Panel parent, int y, string title, string description, Color buttonColor, EventHandler onClick)
        {
            int cardW = ContentWidth;
            int innerW = cardW - Ui.Px(CardPad * 2);

            Theme.CardPanel card = new Theme.CardPanel();
            card.Fill = Theme.Card;
            card.Line = Theme.Border;
            card.Radius = Skin.RCard;
            card.Location = new Point(0, y);
            card.Width = cardW;

            Theme.RoundButton b = Theme.Button(title, buttonColor, 300, 38);
            b.Font = Theme.FB(Theme.FSmall);
            if (buttonColor == Theme.Danger) { b.Ink = Theme.Red; b.LineColor = Theme.StatusLine(CheckStatus.Fail); }
            b.Location = Ui.Pt(CardPad, 14);
            b.Width = innerW;
            b.Tag = "grow";
            card.Controls.Add(b);
            b.Click += onClick;

            Label d = new Label();
            d.Text = description;
            d.Font = Theme.F(Theme.FTiny);
            // A filesystem path is not prose: keep it in the Latin face so the digits stay
            // as typed instead of being remapped to Persian ones by the interface font.
            if (description.IndexOf(':') == 1) d.Font = Theme.FLatin(Theme.FTiny);
            d.ForeColor = Theme.Muted;
            d.BackColor = Color.Transparent;
            d.AutoSize = false;
            d.UseMnemonic = false;
            d.RightToLeft = Theme.TextDirection;
            d.TextAlign = ContentAlignment.TopLeft;
            int textH = TextRenderer.MeasureText(description, d.Font, new Size(innerW, 0),
                                                 TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
            d.Location = new Point(Ui.Px(CardPad), b.Bottom + Ui.Px(10));
            d.Size = new Size(innerW, textH + Ui.Px(4));
            d.Tag = "grow";                 // width is maintained by StretchCards
            card.Controls.Add(d);

            card.Height = d.Bottom + Ui.Px(14);
            parent.Controls.Add(card);
            return y + card.Height + Ui.Px(10);
        }

        // =================================================================== log page

        Panel BuildLogPage()
        {
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet inner = CenteredColumn(page, WinW);

            Theme.CardPanel card = new Theme.CardPanel();
            card.Dock = DockStyle.Fill;
            card.Fill = Theme.LogBg;
            card.Line = Theme.Border;
            card.Radius = 16;
            card.Padding = Ui.Pad(14, 12, 10, 12);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Theme.LogBg;
            _log.ForeColor = Theme.Text;
            _log.BorderStyle = BorderStyle.None;
            // No scroll bar, here or anywhere else in this window. The box still keeps its
            // scroll range — the wheel drives it through EM_LINESCROLL below — and long lines
            // wrap to the column instead of running off the side under a horizontal bar.
            _log.ScrollBars = RichTextBoxScrollBars.None;
            _log.WordWrap = true;
            _log.Font = new Font("Consolas", Ui.Px(12), FontStyle.Regular, GraphicsUnit.Pixel);
            _log.DetectUrls = false;
            _log.RightToLeft = RightToLeft.No;
            card.Controls.Add(_log);

            // Buttons on the first line, the verbose switch under them: the narrow window has
            // no room for all three side by side.
            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Ui.Px(104);
            Button clear = Theme.Button(Lang.T("پاک کردن", "Clear"), Theme.CardHi, 110, 34);
            clear.Location = Ui.Pt(0, 12);
            clear.Click += delegate { _log.Clear(); };
            Button open = Theme.Button(Lang.T("باز کردن فایل لاگ", "Open log file"), Theme.CardHi, 160, 34);
            open.Location = Ui.Pt(118, 12);
            open.Click += delegate { OpenLogFile(); };

            // Lives here rather than in Settings: this is where you are when you need it,
            // and the Advanced card has no room left for another row.
            _chkVerbose = Check(Lang.T("گزارش کامل هسته — فقط برای عیب‌یابی، برنامه را کند می‌کند",
                                       "Verbose core log — for diagnosis only, it slows the app down"));
            ((Theme.FlatCheckBox)_chkVerbose).WrapWidth = WinW - PagePad * 2;
            _chkVerbose.Location = Ui.Pt(0, 58);
            _chkVerbose.CheckedChanged += delegate
            {
                if (_loading) return;
                _settings.VerboseCoreLog = _chkVerbose.Checked;
                _settings.Save();
                if (_tunnel.State == TunnelState.Connected)
                    AppendLog(LogLevel.Warn, Lang.T("برای اعمال، یک بار قطع و دوباره وصل کنید.",
                                                    "Reconnect for this to take effect."));
            };

            bar.Controls.Add(clear);
            bar.Controls.Add(open);
            bar.Controls.Add(_chkVerbose);

            inner.Controls.Add(card);
            inner.Controls.Add(bar);
            return page;
        }

        // =================================================================== settings <-> ui

        void LoadSettingsIntoUi()
        {
            _segLang.SetQuiet(_settings.Lang);
            _txtHost.Text = _settings.ProxyHost;
            _numPort.Value = Math.Min(Math.Max(_settings.ProxyPort, 1), 65535);
            _segType.SetQuiet(_settings.ProxyType);
            _rbFull.Checked = _settings.Routing == RoutingMode.Full;
            _rbIran.Checked = _settings.Routing == RoutingMode.IranDirect;
            _segDnsMode.SetQuiet(_settings.DnsMode);
            _txtDns.Text = _settings.RemoteDns;
            _chkQuic.Checked = _settings.BlockQuic;
            _chkIpv6.Checked = _settings.EnableIpv6;
            _chkKill.Checked = _settings.KillSwitch;
            _chkAuto.Checked = _settings.AutoConnect;
            _chkStartup.Checked = _settings.StartWithWindows;
            _chkTray.Checked = _settings.MinimizeToTray;
            _chkUpdate.Checked = _settings.AutoUpdate;
            _chkNotify.Checked = _settings.Notifications;
            _segStack.SetQuiet(_settings.Stack);
            _numMtu.Value = Math.Min(Math.Max(_settings.Mtu, 576), 9000);
            _txtExtraDirect.Text = _settings.ExtraDirectProcesses;
            _chkVerbose.Checked = _settings.VerboseCoreLog;
            _txtProDns.Text = _settings.ProDns;
        }

        void SaveSettingsFromUi(bool announce)
        {
            bool startupWas = _settings.StartWithWindows;

            _settings.ProxyHost = _txtHost.Text.Trim().Length > 0 ? _txtHost.Text.Trim() : "127.0.0.1";
            _settings.ProxyPort = (int)_numPort.Value;
            _settings.ProxyType = _segType.Value;
            _settings.Routing = _rbIran.Checked ? RoutingMode.IranDirect : RoutingMode.Full;
            _settings.DnsMode = _segDnsMode.Value;
            _settings.RemoteDns = _txtDns.Text.Trim().Length > 0 ? _txtDns.Text.Trim() : "1.1.1.1";
            _settings.ProDns = _txtProDns.Text.Trim();
            _settings.BlockQuic = _chkQuic.Checked;
            _settings.EnableIpv6 = _chkIpv6.Checked;
            _settings.KillSwitch = _chkKill.Checked;
            _settings.AutoConnect = _chkAuto.Checked;
            _settings.StartWithWindows = _chkStartup.Checked;
            _settings.MinimizeToTray = _chkTray.Checked;
            _settings.AutoUpdate = _chkUpdate.Checked;
            _settings.Notifications = _chkNotify.Checked;
            _settings.Stack = _segStack.Value;
            _settings.Mtu = (int)_numMtu.Value;
            _settings.ExtraDirectProcesses = _txtExtraDirect.Text.Trim();
            _settings.Save();

            if (startupWas != _settings.StartWithWindows) ApplyStartupTask(_settings.StartWithWindows);

            RefreshSummary();
            if (announce)
            {
                AppendLog(LogLevel.Info, Lang.T("تنظیمات ذخیره شد.", "Settings saved."));
                if (_tunnel.State == TunnelState.Connected)
                    AppendLog(LogLevel.Warn, Lang.T("برای اعمال، یک بار قطع و دوباره وصل کنید.",
                                                    "Reconnect for the changes to take effect."));
            }
        }

        /// <summary>Saves, then asks Program to rebuild the window with the new look.</summary>
        void RestartWith(string lang, string theme)
        {
            if (_tunnel.State == TunnelState.Connecting || _tunnel.State == TunnelState.Disconnecting)
            {
                Theme.Tell(this, Lang.T("تا پایان اتصال صبر کنید.", "Wait until the connection settles."));
                _loading = true;
                _segLang.SetQuiet(_settings.Lang);
                _loading = false;
                return;
            }

            SaveSettingsFromUi(false);
            _settings.Lang = lang;
            _settings.Theme = theme;
            _settings.Save();

            // The tunnel is owned by Program, not by this window, so changing the look rebuilds
            // the window and leaves the connection alone.
            RestartRequested = true;
            RestartPage = _currentPage;
            _reallyExit = true;
            Close();
        }

        /// <summary>
        /// A Run-key entry cannot launch an elevated app, so startup goes through a scheduled
        /// task that runs with the highest privileges at logon.
        /// </summary>
        void ApplyStartupTask(bool enable)
        {
            try
            {
                string exe = Process.GetCurrentProcess().MainModule.FileName;
                string o, e;
                if (enable)
                {
                    int code = ProcUtil.Run("schtasks.exe",
                        "/Create /TN \"VMTun\" /TR \"\\\"" + exe + "\\\" --tray\" /SC ONLOGON /RL HIGHEST /F",
                        20000, out o, out e);
                    AppendLog(code == 0 ? LogLevel.Info : LogLevel.Error,
                        code == 0
                            ? Lang.T("اجرای خودکار فعال شد.", "Startup task created.")
                            : Lang.T("ساخت وظیفه راه‌اندازی ناموفق بود: ", "Could not create the startup task: ") + (e + o).Trim());
                }
                else
                {
                    ProcUtil.Run("schtasks.exe", "/Delete /TN \"VMTun\" /F", 20000, out o, out e);
                    AppendLog(LogLevel.Info, Lang.T("اجرای خودکار غیرفعال شد.", "Startup task removed."));
                }
            }
            catch (Exception ex) { Log.Error("Startup task change failed", ex); }
        }

        // =================================================================== actions

        void ToggleTunnel()
        {
            if (_tunnel.State == TunnelState.Connected) BeginDisconnect();
            else BeginConnect();
        }

        /// <summary>
        /// Connects with every hardening measure on. Consent is taken once, in full, because
        /// this rearranges Windows rather than the app; after that it is one button.
        /// </summary>
        void ProConnect()
        {
            if (_tunnel.State == TunnelState.Connected || _tunnel.State == TunnelState.Connecting) return;

            SaveSettingsFromUi(false);

            if (!_settings.ProConsent)
            {
                string dns;
                if (!ProDialog.Show(this, _settings, out dns)) return;
                _settings.ProDns = dns;
                _settings.ProConsent = true;
                _settings.Save();
                if (_txtProDns != null) _txtProDns.Text = dns;   // kept in step with Settings
            }

            ShowPage(PageStatus);
            _btnToggle.Enabled = false;
            _orb.Enabled = false;

            Settings pro = ProMode.Derive(_settings);
            Log.Info("Pro Connect: kill switch on, IPv6 unbound, clock and region matched, DNS " +
                     pro.DnsMode + " via " + pro.RemoteDns);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                _tunnel.Start(pro, out error);
                UiInvoke(delegate { RefreshSummary(); });
            });
        }

        void BeginConnect()
        {
            SaveSettingsFromUi(false);
            ShowPage(PageStatus);
            _btnToggle.Enabled = false;
            _orb.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                _tunnel.Start(_settings, out error);
                UiInvoke(delegate { RefreshSummary(); });
            });
        }

        void BeginDisconnect()
        {
            _btnToggle.Enabled = false;
            _orb.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                _tunnel.Stop();
                UiInvoke(delegate { RefreshSummary(); });
            });
        }

        void DetectProxy()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<ProxyCandidate> found = ProxyDetector.Discover();
                int configured = found.Count == 0 ? ProxyDetector.ReadV2rayNConfiguredPort() : 0;
                UiInvoke(delegate
                {
                    if (found.Count > 0)
                    {
                        _txtHost.Text = found[0].Host;
                        _numPort.Value = found[0].Port;
                        _segType.SetQuiet(found[0].Kind == "http" ? "http" : "socks");
                        StringBuilder sb = new StringBuilder(Lang.T("پیدا شد: ", "Found: "));
                        foreach (ProxyCandidate c in found) sb.Append(c.ToString()).Append("   ");
                        AppendLog(LogLevel.Info, sb.ToString().Trim());
                        SaveSettingsFromUi(false);
                        ShowPage(PageStatus);
                        RunPreflightAsync();
                    }
                    else if (configured > 0)
                    {
                        _numPort.Value = configured;
                        SaveSettingsFromUi(false);
                        AppendLog(LogLevel.Warn, Lang.T(
                            "پروکسی فعالی پیدا نشد؛ پورت " + configured + " از تنظیمات v2rayN خوانده شد.",
                            "No live proxy found; port " + configured + " was read from the v2rayN settings."));
                    }
                    else
                    {
                        AppendLog(LogLevel.Error, Lang.T(
                            "هیچ پروکسی محلی پیدا نشد. v2rayN را اجرا و به یک سرور وصل کنید.",
                            "No local proxy found. Start v2rayN and connect to a server."));
                        ShowPage(PageLog);
                    }
                });
            });
        }

        void FixUwp()
        {
            if (!Theme.Ask(this,
                Lang.T("دسترسی loopback برای همه اپ‌های UWP باز می‌شود. ادامه می‌دهید؟",
                       "Loopback access will be granted to every UWP app. Continue?"),
                Lang.T("ادامه", "Continue"), Lang.T("انصراف", "Cancel"))) return;

            ShowPage(PageLog);
            AppendLog(LogLevel.Info, Lang.T("در حال اعمال محدودیت‌زدایی UWP…", "Applying UWP loopback exemptions…"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                int n = UwpLoopback.ExemptAll(
                    delegate(string progress) { UiInvoke(delegate { AppendLog(LogLevel.Core, progress); }); },
                    out error);
                UiInvoke(delegate
                {
                    if (n > 0) AppendLog(LogLevel.Info, Lang.T("انجام شد برای ", "Done for ") + n + Lang.T(" بسته.", " packages."));
                    else AppendLog(LogLevel.Error, Lang.T("ناموفق: ", "Failed: ") + error);
                });
            });
        }

        void ClearUwp()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                UwpLoopback.ClearAll();
                UiInvoke(delegate { AppendLog(LogLevel.Info, Lang.T("استثناهای UWP پاک شد.", "UWP exemptions cleared.")); });
            });
        }

        void RepairNetwork()
        {
            ShowPage(PageLog);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string notes = TunnelService.CleanupStale();
                string err;
                FirewallGuard.Remove(out err);
                UiInvoke(delegate
                {
                    AppendLog(LogLevel.Info, Lang.T(
                        "شبکه بازیابی شد. قوانین فایروال VMTun حذف و سیاست خروجی به حالت اول برگشت.",
                        "Network repaired. VMTun firewall rules removed and the outbound policy restored."));
                    if (!string.IsNullOrEmpty(notes)) AppendLog(LogLevel.Info, notes.Trim());
                    RefreshSummary();
                });
            });
        }

        void OpenDataFolder()
        {
            try
            {
                AppPaths.EnsureDirs();
                Process.Start("explorer.exe", "\"" + AppPaths.DataDir + "\"");
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        void OpenLogFile()
        {
            try
            {
                if (File.Exists(AppPaths.LogFile)) Process.Start("notepad.exe", "\"" + AppPaths.LogFile + "\"");
                else OpenDataFolder();
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }

        void CheckExternalIp()
        {
            ShowPage(PageLog);
            ThreadPool.QueueUserWorkItem(delegate
            {
                string result = null, error = null;
                try
                {
                    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://api.ipify.org");
                    req.Proxy = null;
                    req.Timeout = 15000;
                    req.UserAgent = "VMTun";
                    using (WebResponse resp = req.GetResponse())
                    using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                        result = sr.ReadToEnd().Trim();
                }
                catch (Exception ex) { error = ex.Message; }

                string r = result, e = error;
                UiInvoke(delegate
                {
                    if (!string.IsNullOrEmpty(r))
                        AppendLog(LogLevel.Info, Lang.T("IP خروجی فعلی: ", "Current external IP: ") + r);
                    else
                        AppendLog(LogLevel.Error, Lang.T("IP خروجی خوانده نشد: ", "Could not read the external IP: ") + e);
                });
            });
        }

        // =================================================================== updates

        bool _updateCheckInFlight;
        System.Windows.Forms.Timer _updateTimer;

        /// <summary>
        /// Starts the automatic check on a timer of its own.
        ///
        /// It used to hang off one event: the tunnel reaching Connected and verified. That is a
        /// good moment to check — the request rides a connection just proven to work — but it is
        /// the only moment, so anyone who does not switch the tunnel on never got a check at all,
        /// and an instance left connected for a week never got a second one.
        /// </summary>
        void StartUpdateTimer()
        {
            _updateTimer = new System.Windows.Forms.Timer();
            // Not at once: let the window finish painting and any auto-connect settle first.
            _updateTimer.Interval = 45 * 1000;
            _updateTimer.Tick += delegate
            {
                // Six hours after the first tick. A tray application runs for days, and the
                // daily stamp below decides whether the check actually goes ahead.
                _updateTimer.Interval = 6 * 60 * 60 * 1000;
                MaybeAutoCheckUpdate();
            };
            _updateTimer.Start();
        }

        /// <summary>
        /// One check per run, and at most one per day. It runs after the tunnel has proved
        /// itself, because that is when a request to GitHub is most likely to get through.
        /// </summary>
        void MaybeAutoCheckUpdate()
        {
            if (!_settings.AutoUpdate || _updateCheckInFlight || !Updater.Configured(_settings)) return;
            // Once a day, and only counted once it has actually succeeded: a check that fails
            // because the connection was down must not block the retry that would have worked.
            if (_settings.LastUpdateCheck == DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                return;
            CheckForUpdate(false);
        }

        void CheckForUpdate(bool manual)
        {
            if (!Updater.Configured(_settings))
            {
                if (manual) Theme.Tell(this, Lang.T("مخزن به‌روزرسانی تنظیم نشده است.",
                                                    "No update repository is configured."));
                return;
            }

            if (manual) AppendLog(LogLevel.Info, Lang.T("در حال بررسی به‌روزرسانی…", "Checking for updates…"));

            _updateCheckInFlight = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                ReleaseInfo release = Updater.CheckLatest(_settings, out error);

                // Stamped only on success. Recording the attempt would mean one failure —
                // no connection, GitHub unreachable — suppressed every further check until
                // tomorrow, which is precisely when it most needed to try again.
                if (release != null)
                {
                    _settings.LastUpdateCheck =
                        DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    _settings.Save();
                }

                UiInvoke(delegate
                {
                    _updateCheckInFlight = false;
                    if (release == null)
                    {
                        AppendLog(manual ? LogLevel.Error : LogLevel.Warn,
                            Lang.T("بررسی به‌روزرسانی ناموفق: ", "Update check failed: ") + error);
                        if (manual) Theme.Tell(this, error);
                        return;
                    }

                    if (!Updater.IsNewer(release.Version, Integration.Version))
                    {
                        AppendLog(LogLevel.Info, Lang.T(
                            "آخرین نسخه را دارید (" + Integration.Version + ").",
                            "You already have the latest version (" + Integration.Version + ")."));
                        if (manual)
                            Theme.Tell(this, Lang.T("آخرین نسخه را دارید.", "You are up to date."));
                        return;
                    }

                    AppendLog(LogLevel.Info, Lang.T("نسخه تازه موجود است: ", "A newer version is available: ") + release.Tag);
                    string question = Lang.T(
                        "نسخه " + release.Version + " منتشر شده (نسخه فعلی " + Integration.Version + "). " +
                        "دانلود و نصب شود؟ برنامه در پایان بسته و دوباره باز می‌شود.",
                        "Version " + release.Version + " is out (you have " + Integration.Version + "). " +
                        "Download and install it? VMTun will restart when it is done.");
                    if (!Theme.Ask(this, question, Lang.T("به‌روزرسانی", "Update"), Lang.T("بعداً", "Later")))
                        return;

                    StartUpdate(release);
                });
            });
        }

        void StartUpdate(ReleaseInfo release)
        {
            ShowPage(PageLog);
            AppendLog(LogLevel.Info, Lang.T("در حال دانلود ", "Downloading ") + release.Tag + "…");

            ThreadPool.QueueUserWorkItem(delegate
            {
                int lastShown = -10;
                string error;
                string file = Updater.Download(release,
                    delegate(int percent)
                    {
                        if (percent < lastShown + 10) return;
                        lastShown = percent;
                        UiInvoke(delegate { AppendLog(LogLevel.Core, percent + "%"); });
                    },
                    out error);

                UiInvoke(delegate
                {
                    if (file == null)
                    {
                        AppendLog(LogLevel.Error, Lang.T("دانلود ناموفق: ", "Download failed: ") + error);
                        Theme.Tell(this, error);
                        return;
                    }

                    // The tunnel has to come down first: the installer replaces the very files
                    // the running core was started from.
                    AppendLog(LogLevel.Info, Lang.T("در حال قطع تونل و اجرای نصب‌کننده…",
                                                    "Stopping the tunnel and launching the installer…"));
                    if (_tunnel.State == TunnelState.Connected) _tunnel.Stop();

                    string launchError;
                    if (!Updater.Launch(file, out launchError))
                    {
                        AppendLog(LogLevel.Error, launchError);
                        Theme.Tell(this, launchError);
                        return;
                    }

                    _reallyExit = true;
                    Close();
                });
            });
        }

        // =================================================================== plumbing

        void OnChecksUpdated(string phase, List<CheckResult> checks)
        {
            UiInvoke(delegate { SetChecks(phase, checks); RefreshSummary(); });
        }

        void OnTunnelState(TunnelState state, string message)
        {
            UiInvoke(delegate
            {
                _stateDetail.Text = message == null ? "" : message;
                RefreshHeader();
                RefreshSummary();

                if (state == TunnelState.Connected && _tunnel.Verified) MaybeAutoCheckUpdate();

                // Only a real transition earns a balloon.
                //
                // The tunnel re-asserts its state rather than only announcing changes: the
                // periodic re-verification calls SetState(Connected) again every minute, each
                // connect step reports progress the same way, and every one of those raised
                // this event. Notifying on each turned a quiet tray icon into a stream of
                // identical pop-ups a couple of minutes into every session.
                bool verified = _tunnel.Verified;
                if (state == _notifiedState && verified == _notifiedVerified) return;
                _notifiedState = state;
                _notifiedVerified = verified;

                if (!_settings.Notifications) return;

                if (state == TunnelState.Connected && verified)
                    Notify(Lang.T("تونل فعال و تأیید شد.", "Tunnel is up and verified."));
                else if (state == TunnelState.Connected)
                    Notify(Lang.T("تونل بالا آمد ولی ترافیک عبور نمی‌کند — تب وضعیت را ببینید.",
                                  "Tunnel is up but traffic is not flowing — see the Status page."));
                else if (state == TunnelState.Faulted)
                    Notify(message);
            });
        }

        // What the last balloon said, so the same thing is never said twice running.
        TunnelState _notifiedState = (TunnelState)(-1);
        bool _notifiedVerified;

        /// <summary>
        /// A tray balloon, but only when the window is hidden and always with ToolTipIcon.None:
        /// any other icon makes Windows play the notification sound, and the window itself
        /// already shows the same thing in the header.
        /// </summary>
        void Notify(string message)
        {
            if (_tray == null || string.IsNullOrEmpty(message)) return;
            if (Visible && WindowState != FormWindowState.Minimized) return;
            try { _tray.ShowBalloonTip(3000, "VMTun", message, ToolTipIcon.None); }
            catch { }
        }

        void RefreshHeader()
        {
            TunnelState s = _tunnel.State;
            bool connected = s == TunnelState.Connected;
            bool busy = s == TunnelState.Connecting || s == TunnelState.Disconnecting;

            Color c;
            string title, compact, line;
            if (connected && _tunnel.Verified)
            {
                c = Theme.Green;
                title = Lang.T("متصل و تأیید شد", "Connected and verified");
                compact = Lang.T("متصل", "Connected");
                line = Lang.T("تونل فعال و تأیید شد", "Tunnel up and verified");
            }
            else if (connected)
            {
                c = Theme.Amber;
                title = Lang.T("متصل، ولی ترافیک عبور نمی‌کند", "Connected, but no traffic");
                compact = Lang.T("متصل", "Connected");
                line = Lang.T("ترافیک عبور نمی‌کند", "Traffic is not flowing");
            }
            else if (busy)
            {
                c = Theme.Accent;
                bool connecting = s == TunnelState.Connecting;
                title = connecting ? Lang.T("در حال اتصال…", "Connecting…")
                                   : Lang.T("در حال قطع…", "Disconnecting…");
                compact = connecting ? Lang.T("در حال اتصال", "Connecting")
                                     : Lang.T("در حال قطع", "Disconnecting");
                line = Lang.T("کمی صبر کنید", "One moment");
            }
            else if (s == TunnelState.Faulted)
            {
                c = Theme.Red;
                title = Lang.T("خطا", "Error");
                compact = Lang.T("خطا", "Error");
                line = Lang.T("اتصال ناموفق بود", "The connection failed");
            }
            else
            {
                c = Theme.Muted;
                title = Lang.T("قطع", "Disconnected");
                compact = Lang.T("قطع", "Disconnected");
                line = Lang.T("تونل خاموش", "Tunnel off");
            }

            _capsule.Text = compact;
            _capsule.DotColor = c;
            _tips.SetToolTip(_capsule, line + " — v" + Integration.Version);

            bool verified = connected && _tunnel.Verified;
            _lblVerif.Text = verified ? Lang.T("تأیید شد", "Verified") : Lang.T("تأیید نشده", "Not verified");
            _lblVerif.ForeColor = verified ? Theme.Green : (connected ? Theme.Amber : Theme.Text);

            if (_stateDetail.Text.Length == 0)
                _stateDetail.Text = Lang.T(
                    "v2rayN باید به یک سرور وصل باشد. سپس اتصال پیشرفته یا اتصال را بزنید.",
                    "Connect v2rayN to a server first, then press Pro Connect or Connect.");

            // The orb is Pro Connect from a standing start. While connected it is the way out,
            // so the undo for a Pro session always runs before anything else is started.
            if (busy)
            {
                _orb.Text = s == TunnelState.Connecting ? Lang.T("در حال اتصال…", "Connecting…")
                                                        : Lang.T("در حال قطع…", "Disconnecting…");
            }
            else if (connected)
            {
                _orb.Text = Lang.T("قطع اتصال", "Disconnect");
            }
            else
            {
                _orb.Text = Lang.T("اتصال پیشرفته", "Pro Connect");
            }
            _orb.Enabled = !busy;

            _btnToggle.Text = Lang.T("اتصال", "Connect");
            _btnToggle.FitWidth(140);
            _btnToggle.Enabled = !busy;
            _btnToggle.Visible = !connected;
            if (_miToggle != null)
                _miToggle.Text = connected ? Lang.T("قطع اتصال", "Disconnect") : Lang.T("اتصال", "Connect");

            LayoutStatus();

            string tip = "VMTun — " + title;
            if (_tray != null) _tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
        }

        // Log lines arrive from the core's stdout on a background thread and can burst into the
        // hundreds per second. Marshalling each one to the UI with BeginInvoke floods the message
        // queue and freezes the window, so they are queued here and drained on a timer instead.
        readonly Queue<KeyValuePair<LogLevel, string>> _pendingLog =
            new Queue<KeyValuePair<LogLevel, string>>();
        int _droppedLog;
        System.Windows.Forms.Timer _logPump;

        void OnLogLine(LogLevel level, string message)
        {
            lock (_pendingLog)
            {
                if (_pendingLog.Count >= 500) { _droppedLog++; return; }
                _pendingLog.Enqueue(new KeyValuePair<LogLevel, string>(level, message));
            }
        }

        void StartLogPump()
        {
            _logPump = new System.Windows.Forms.Timer();
            _logPump.Interval = 250;
            _logPump.Tick += delegate { DrainLog(); };
            _logPump.Start();
        }

        void DrainLog()
        {
            if (_log == null || _log.IsDisposed) return;

            List<KeyValuePair<LogLevel, string>> batch = new List<KeyValuePair<LogLevel, string>>();
            int dropped;
            lock (_pendingLog)
            {
                // A hard cap per tick: even a runaway core cannot monopolise the UI thread.
                while (_pendingLog.Count > 0 && batch.Count < 60) batch.Add(_pendingLog.Dequeue());
                dropped = _droppedLog;
                _droppedLog = 0;
            }
            if (batch.Count == 0 && dropped == 0) return;

            _log.SuspendLayout();
            foreach (KeyValuePair<LogLevel, string> item in batch) AppendLog(item.Key, item.Value);
            if (dropped > 0)
                AppendLog(LogLevel.Warn, Lang.T(
                    dropped + " خط گزارش به دلیل حجم زیاد نمایش داده نشد.",
                    dropped + " log lines were dropped to keep the window responsive."));
            _log.ResumeLayout();
        }

        void AppendLog(LogLevel level, string message)
        {
            if (_log == null || _log.IsDisposed) return;
            Color c = Theme.Text;
            if (level == LogLevel.Warn) c = Theme.Amber;
            else if (level == LogLevel.Error) c = Theme.Red;
            else if (level == LogLevel.Core) c = Theme.Muted;

            // Whether the reader is at the newest line or has scrolled back to read something.
            // Appending text moves the view to the end on its own, so a reader looking at an
            // earlier line would be dragged down again by every line the core writes.
            bool follow = LogAtTail();
            int anchor = follow ? -1 : FirstVisibleLine();

            if (_log.Lines.Length > 700)
            {
                _log.SelectionStart = 0;
                _log.SelectionLength = _log.GetFirstCharIndexFromLine(350);
                _log.SelectedText = "";
                if (anchor > 0) anchor = Math.Max(0, anchor - 350);
            }

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = c;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
            _log.SelectionColor = _log.ForeColor;

            if (follow || anchor < 0)
            {
                _log.SelectionStart = _log.TextLength;
                _log.ScrollToCaret();
                return;
            }

            // Put the view back where the reader left it.
            int now = FirstVisibleLine();
            if (now != anchor)
                SendMessage(_log.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(anchor - now));
        }

        int FirstVisibleLine()
        {
            if (_log == null || _log.IsDisposed || !_log.IsHandleCreated) return 0;
            return (int)SendMessage(_log.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        }

        void UiInvoke(Action a)
        {
            if (IsDisposed) return;
            try
            {
                if (InvokeRequired) BeginInvoke(a);
                else a();
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        void ShowError(string message)
        {
            AppendLog(LogLevel.Error, message);
            Theme.Tell(this, message);
        }

        void RestoreWindow()
        {
            Show();
            ShowInTaskbar = true;
            WindowState = FormWindowState.Normal;
            Activate();
        }

        void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_reallyExit && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (_settings.Notifications)
                    _tray.ShowBalloonTip(2000, "VMTun",
                        Lang.T("برنامه کنار ساعت در حال اجراست.", "Still running in the tray."),
                        ToolTipIcon.None);
                return;
            }

            // The tunnel outlives this window, so stop listening before it goes away.
            if (_logPump != null) { _logPump.Stop(); _logPump.Dispose(); _logPump = null; }
            if (_navTween != null) { _navTween.Stop(); _navTween.Dispose(); _navTween = null; }
            if (_wheel != null) { Application.RemoveMessageFilter(_wheel); _wheel = null; }
            Log.Line -= OnLogLine;
            _tunnel.StateChanged -= OnTunnelState;
            _tunnel.ChecksUpdated -= OnChecksUpdated;

            // A theme or language change only rebuilds the window; the connection stays up.
            // Any other close is a real exit, and must never leave the machine firewalled off
            // with no tunnel behind it.
            if (!RestartRequested)
            {
                if (_tunnel.State == TunnelState.Connected || FirewallGuard.IsActive()) _tunnel.Stop();
            }
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        }
    }
}
