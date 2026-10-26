using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
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

        // top bar and status hero
        Skin.Dot _topDot, _heroDot;
        Label _stateTitle, _stateDetail, _stateLine;
        Theme.RoundButton _btnToggle, _btnPro;

        // navigation
        readonly List<Skin.TopTab> _navButtons = new List<Skin.TopTab>();
        readonly List<Panel> _pages = new List<Panel>();
        int _currentPage;

        // status page
        Panel _checkHost;
        Label _sumProxy, _sumDns, _sumRouting, _sumAdapter, _sumExit, _sumGuard, _lblPhase;
        Theme.RoundButton _btnRecheck;

        // settings page
        TextBox _txtHost, _txtDns, _txtProDns, _txtExtraDirect;
        NumericUpDown _numPort, _numMtu;
        Theme.Segmented _segType, _segStack, _segDnsMode, _segTheme, _segLang;
        RadioButton _rbFull, _rbIran;
        CheckBox _chkKill, _chkIpv6, _chkQuic, _chkAuto, _chkStartup, _chkTray, _chkVerbose, _chkUpdate, _chkNotify;

        // log page
        RichTextBox _log;

        bool _reallyExit;
        bool _loading = true;

        // ---- design geometry, in 96-dpi pixels; everything goes through Ui.Px -------------
        const int WinW = 1240, WinH = 790;
        const int ColGap = 20;
        const int LabelCol = 150;   // where a row's control starts inside a section card

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
            Icon = LoadAppIcon();

            // The window is never mirrored: RightToLeftLayout stays off, so the sidebar, the
            // connect button and every card keep the same place in both languages. Only the
            // reading order of the text inside the controls follows the language.
            RightToLeft = Theme.TextDirection;
            RightToLeftLayout = false;

            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(
                Math.Min(Ui.Px(WinW), (int)(work.Width * 0.94)),
                Math.Min(Ui.Px(WinH), (int)(work.Height * 0.94)));
            MinimumSize = new Size(
                Math.Min(Ui.Px(1120), work.Width), Math.Min(Ui.Px(720), work.Height));

            // The backdrop is the parent of everything, not a sibling behind it. A transparent
            // WinForms control paints its *parent's* background, so a wash sitting beside the
            // layout rather than under it composites against nothing and the panels above it
            // come out empty.
            Skin.Backdrop back = new Skin.Backdrop();
            back.Dock = DockStyle.Fill;
            Controls.Add(back);

            // The filler is added first and the top bar second: docking resolves from the
            // highest z-order down, so the later top bar claims its edge and the filler gets
            // what is left.
            Panel main = new Panel();
            main.Dock = DockStyle.Fill;
            main.BackColor = Color.Transparent;
            back.Controls.Add(main);

            BuildTopBar(back);

            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.Transparent;
            content.Padding = Ui.Pad(24, 18, 24, 18);
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

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray)
                {
                    Hide(); ShowInTaskbar = false;
                }
            };
            FormClosing += OnFormClosing;
        }

        void BuildTopBar(Control host)
        {
            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(56);
            host.Controls.Add(bar);

            Panel rule = new Panel();
            rule.Dock = DockStyle.Bottom;
            rule.Height = Math.Max(1, (int)Ui.Scale);
            rule.BackColor = Theme.Border;
            bar.Controls.Add(rule);

            Panel brand = new Panel();
            brand.BackColor = Color.Transparent;
            brand.Height = Ui.Px(40);
            bar.Controls.Add(brand);

            Theme.CardPanel tile = new Theme.CardPanel();
            tile.Fill = Theme.Accent;
            tile.DrawBorder = false;
            tile.Radius = 6;
            tile.Location = new Point(0, (brand.Height - Ui.Px(22)) / 2);
            tile.Size = Ui.Sz(22, 22);
            tile.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.CardPanel t = (Theme.CardPanel)sender;
                int inset = Ui.Px(6);
                Skin.DrawIcon(e.Graphics, Skin.Icon.Bolt,
                    new RectangleF(inset, inset, t.Width - inset * 2, t.Height - inset * 2),
                    Theme.OnAccent, Math.Max(1.2f, Ui.Scale));
            };
            brand.Controls.Add(tile);

            Label logo = Theme.Label("VMTun", Theme.FBody, Theme.Text, true);
            logo.Font = Theme.FLatinB(Theme.FBody);
            logo.Location = new Point(Ui.Px(30), (brand.Height - logo.Height) / 2);
            brand.Controls.Add(logo);

            _topDot = new Skin.Dot(7);
            _topDot.Location = new Point(logo.Right + Ui.Px(8), (brand.Height - _topDot.Height) / 2);
            brand.Controls.Add(_topDot);
            brand.Width = _topDot.Right;

            Label version = Theme.Label("v" + Integration.Version, Theme.FTiny, Theme.Muted, false);
            version.Font = Theme.FLatin(Theme.FTiny);
            bar.Controls.Add(version);

            Theme.RoundButton theme = Theme.Button("", Theme.Bg, 30, 30);
            theme.Outline = false;
            theme.Radius = 999;
            theme.Icon = Skin.Icon.Theme;
            theme.Click += delegate
            {
                RestartWith(_settings.Lang, Theme.IsDark ? "light" : "dark");
            };
            bar.Controls.Add(theme);

            string[] names =
            {
                Lang.T("وضعیت", "Status"),
                Lang.T("تنظیمات", "Settings"),
                Lang.T("حریم خصوصی", "Privacy"),
                Lang.T("ابزارها", "Tools"),
                Lang.T("گزارش", "Log")
            };
            for (int i = 0; i < names.Length; i++)
            {
                Skin.TopTab tab = new Skin.TopTab(names[i]);
                tab.Active = (i == 0);
                int index = i;
                tab.Click += delegate { ShowPage(index); };
                bar.Controls.Add(tab);
                _navButtons.Add(tab);
            }

            EventHandler layout = delegate { LayoutTopBar(bar, brand, version, theme); };
            bar.SizeChanged += layout;
            layout(bar, EventArgs.Empty);
        }

        void LayoutTopBar(Control bar, Control brand, Control version, Control theme)
        {
            int h = bar.ClientSize.Height;
            if (h <= 0) return;

            brand.Left = Math.Max(0, bar.ClientSize.Width - Ui.Px(22) - brand.Width);
            brand.Top = (h - brand.Height) / 2;
            theme.Left = Ui.Px(22);
            theme.Top = (h - theme.Height) / 2;
            version.Left = theme.Right + Ui.Px(8);
            version.Top = (h - version.Height) / 2;

            int gap = Ui.Px(2);
            int total = gap * Math.Max(0, _navButtons.Count - 1);
            foreach (Skin.TopTab tab in _navButtons) total += tab.Width;

            int groupRight = (bar.ClientSize.Width + total) / 2;
            int maxRight = Math.Max(Ui.Px(140), brand.Left - Ui.Px(20));
            if (groupRight > maxRight) groupRight = maxRight;

            int x = groupRight;
            int tabHeight = Math.Max(Ui.Px(30), h - Math.Max(1, (int)Ui.Scale));
            foreach (Skin.TopTab tab in _navButtons)
            {
                x -= tab.Width;
                tab.Left = x;
                tab.Top = 0;
                tab.Height = tabHeight;
                x -= gap;
            }
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
                int available = host.ClientSize.Width - Ui.Px(24);
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
            _tray.Icon = LoadAppIcon();
            _tray.Visible = true;
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { RestoreWindow(); };
        }

        static Icon LoadAppIcon()
        {
            try
            {
                string ico = Path.Combine(AppPaths.ExeDir, "VMTun.ico");
                if (File.Exists(ico)) return new Icon(ico);
            }
            catch { }
            try { return Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName); }
            catch { return SystemIcons.Shield; }
        }

        /// <summary>
        /// Keeps a stack of cards exactly as wide as the panel holding them. Anchoring cannot
        /// do this: a card created at a design width then anchored grows by the difference
        /// every time the parent resizes, ending up far wider than the window. That is
        /// invisible with left-aligned text and hides right-aligned text completely.
        /// </summary>
        static void StretchCards(Panel host)
        {
            EventHandler resize = delegate
            {
                int w = host.ClientSize.Width;
                if (w < Ui.Px(200)) return;
                host.SuspendLayout();
                foreach (Control card in host.Controls)
                {
                    // Only the cards stretch; a loose button such as Save keeps its size.
                    if (!(card is Theme.CardPanel)) continue;
                    card.Width = w;
                    foreach (Control inner in card.Controls)
                    {
                        if ((inner.Tag as string) != "grow") continue;
                        inner.Width = Math.Max(Ui.Px(60), w - inner.Left - Ui.Px(18));
                    }
                }
                host.ResumeLayout();
            };
            host.SizeChanged += resize;
            resize(host, EventArgs.Empty);
        }

        void ShowPage(int index)
        {
            if (index < 0 || index >= _pages.Count) index = 0;
            _currentPage = index;
            for (int i = 0; i < _pages.Count; i++) _pages[i].Visible = (i == index);
            for (int i = 0; i < _navButtons.Count; i++) _navButtons[i].Active = (i == index);
            // A form control receiving focus can scroll a long page before the user sees it.
            if (index == PageSettings && _settingsColumn != null)
                _settingsColumn.AutoScrollPosition = Point.Empty;
        }

        // =================================================================== status page

        Panel BuildStatusPage()
        {
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet col = CenteredColumn(page, 680);

            // The check list fills whatever is left; rows are sized to fit so it never scrolls.
            _checkHost = new Skin.Sheet();
            _checkHost.Dock = DockStyle.Fill;
            col.Controls.Add(_checkHost);

            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(46);
            Panel barRule = new Panel();
            barRule.Dock = DockStyle.Bottom;
            barRule.Height = Math.Max(1, (int)Ui.Scale);
            barRule.BackColor = Theme.Border;
            bar.Controls.Add(barRule);

            Label title = Theme.Label(Lang.T("بررسی‌ها", "Checks"), Theme.FBody, Theme.Text, true);
            title.Location = new Point(0, Ui.Px(12));
            bar.Controls.Add(title);

            _lblPhase = Theme.Label("", Theme.FTiny, Theme.Muted, false);
            _lblPhase.Location = new Point(0, Ui.Px(18));
            bar.Controls.Add(_lblPhase);

            _btnRecheck = Theme.Button(Lang.T("بررسی مجدد", "Re-run checks"), Theme.Bg, 120, 30);
            _btnRecheck.Outline = false;
            _btnRecheck.Font = Theme.F(Theme.FSmall);
            _btnRecheck.FitWidth(110);
            _btnRecheck.Click += delegate { RunPreflightAsync(); };
            bar.Controls.Add(_btnRecheck);

            EventHandler barLayout = delegate
            {
                title.Left = Math.Max(0, bar.ClientSize.Width - title.Width - Ui.Px(2));
                // The label's own Width is still zero when TextChanged fires, so measure the
                // text instead of waiting for AutoSize to catch up.
                int phaseWidth = string.IsNullOrEmpty(_lblPhase.Text) ? 0
                    : TextRenderer.MeasureText(_lblPhase.Text, _lblPhase.Font).Width;
                _lblPhase.Left = Math.Max(0, title.Left - phaseWidth - Ui.Px(12));
                _btnRecheck.Left = 0;
                _btnRecheck.Top = Math.Max(0, (bar.ClientSize.Height - _btnRecheck.Height) / 2);
            };
            bar.SizeChanged += barLayout;
            // The phase starts empty and is filled by the first check pass; its width is zero
            // until then, so re-run the bar layout when the text arrives.
            _lblPhase.TextChanged += delegate { barLayout(bar, EventArgs.Empty); };
            barLayout(bar, EventArgs.Empty);
            col.Controls.Add(bar);

            // ---- facts -----------------------------------------------------------------
            Skin.Sheet facts = new Skin.Sheet();
            facts.Dock = DockStyle.Top;
            facts.Height = Ui.Px(124);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.BackColor = Color.Transparent;
            grid.RightToLeft = RightToLeft.No;
            grid.ColumnCount = 3;
            grid.RowCount = 2;
            for (int c = 0; c < 3; c++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            for (int r = 0; r < 2; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50f));

            // Columns run right to left: the first fact is in the rightmost cell.
            _sumProxy = FactCell(grid, 2, 0, Lang.T("پروکسی بالادست", "Upstream proxy"));
            _sumDns = FactCell(grid, 1, 0, Lang.T("روش DNS", "DNS transport"));
            _sumRouting = FactCell(grid, 0, 0, Lang.T("مسیریابی", "Routing"));
            _sumAdapter = FactCell(grid, 2, 1, Lang.T("آداپتور", "Adapter"));
            _sumExit = FactCell(grid, 1, 1, Lang.T("IP خروجی", "Exit IP"));
            _sumGuard = FactCell(grid, 0, 1, Lang.T("کیل‌سوئیچ", "Kill switch"));

            Panel factsTop = new Panel();
            factsTop.Dock = DockStyle.Top;
            factsTop.Height = Math.Max(1, (int)Ui.Scale);
            factsTop.BackColor = Theme.Border;

            Panel factsBottom = new Panel();
            factsBottom.Dock = DockStyle.Bottom;
            factsBottom.Height = Math.Max(1, (int)Ui.Scale);
            factsBottom.BackColor = Theme.Border;

            // Fill first, then the edges: docking runs back to front, so the last-added edge
            // takes its strip before the fill claims what remains.
            facts.Controls.Add(grid);
            facts.Controls.Add(factsTop);
            facts.Controls.Add(factsBottom);
            col.Controls.Add(facts);

            // ---- hero ------------------------------------------------------------------
            Skin.Sheet hero = new Skin.Sheet();
            hero.Dock = DockStyle.Top;
            hero.Height = Ui.Px(218);

            _heroDot = new Skin.Dot(8);
            hero.Controls.Add(_heroDot);

            _stateLine = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            hero.Controls.Add(_stateLine);

            _stateTitle = Theme.Label("", 34, Theme.Text, false);
            hero.Controls.Add(_stateTitle);

            _stateDetail = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            _stateDetail.MaximumSize = new Size(Ui.Px(600), Ui.Px(58));
            _stateDetail.TextAlign = ContentAlignment.MiddleCenter;
            hero.Controls.Add(_stateDetail);

            _btnToggle = Theme.Button("", Theme.Accent, 172, 46);
            _btnToggle.Font = Theme.FB(Theme.FBody);
            _btnToggle.Click += delegate { ToggleTunnel(); };
            hero.Controls.Add(_btnToggle);

            // Sits beside Connect rather than replacing it: the plain connect stays the one
            // that changes nothing outside this app, which is what most runs should be.
            _btnPro = Theme.Button(Lang.T("اتصال پیشرفته", "Pro Connect"), Theme.Bg, 150, 46);
            _btnPro.Font = Theme.FB(Theme.FSmall);
            _btnPro.Icon = Skin.Icon.Bolt;
            _btnPro.Outline = false;
            _btnPro.FitWidth(140);
            _btnPro.Click += delegate { ProConnect(); };
            hero.Controls.Add(_btnPro);

            hero.SizeChanged += delegate { LayoutStatusHero(hero); };
            col.Controls.Add(hero);
            LayoutStatusHero(hero);

            _checkHost.SizeChanged += delegate
            {
                if (_lastChecks.Count > 0) SetChecks(_lastPhase, _lastChecks);
            };
            return page;
        }

        Label FactCell(TableLayoutPanel grid, int col, int row, string caption)
        {
            Skin.Sheet cell = new Skin.Sheet();
            cell.Dock = DockStyle.Fill;
            cell.Padding = Ui.Pad(10, 6, 10, 4);

            Label k = Theme.Label(caption, Theme.FTiny, Theme.Muted, false);
            k.AutoSize = false;
            k.Dock = DockStyle.Top;
            k.Height = Theme.TextH(Theme.FTiny);
            k.RightToLeft = Theme.TextDirection;
            k.TextAlign = Theme.VisualRight;

            Label v = Theme.Label("—", Theme.FSmall, Theme.Text, false);
            v.Font = Theme.FLatin(Theme.FSmall);
            v.AutoSize = false;
            v.Dock = DockStyle.Top;
            v.Height = Theme.TextH(Theme.FSmall);
            v.RightToLeft = Theme.TextDirection;
            v.TextAlign = Theme.VisualRight;

            cell.Controls.Add(v);
            cell.Controls.Add(k);
            grid.Controls.Add(cell, col, row);
            return v;
        }

        void LayoutStatusHero(Control hero)
        {
            int w = hero.ClientSize.Width;
            if (w <= 0) return;

            _heroDot.Left = Math.Max(0, (w - _heroDot.Width) / 2);
            _heroDot.Top = Ui.Px(20);

            _stateLine.Left = Math.Max(0, (w - _stateLine.Width) / 2);
            _stateLine.Top = Ui.Px(34);

            _stateTitle.Left = Math.Max(0, (w - _stateTitle.Width) / 2);
            _stateTitle.Top = _stateLine.Bottom + Ui.Px(2);

            int detailTop = _stateTitle.Bottom + Ui.Px(2);
            _stateDetail.Left = Math.Max(0, (w - _stateDetail.Width) / 2);
            _stateDetail.Top = detailTop;

            int buttonTop = _stateDetail.Bottom + Ui.Px(16);
            int gap = Ui.Px(8);
            int total = _btnToggle.Width + (_btnPro.Visible ? _btnPro.Width + gap : 0);
            int x = Math.Max(0, (w - total) / 2);
            if (_btnPro.Visible)
            {
                _btnPro.Location = new Point(x, buttonTop);
                x += _btnPro.Width + gap;
            }
            _btnToggle.Location = new Point(x, buttonTop);
        }

        string _lastPhase = "";
        List<CheckResult> _lastChecks = new List<CheckResult>();

        /// <summary>
        /// Lays the checks out to fill the available height exactly, so the list never needs a
        /// scrollbar: hints are dropped first, then the rows are compacted, before anything is
        /// allowed to overflow.
        /// </summary>
        void SetChecks(string phase, List<CheckResult> checks)
        {
            _lastPhase = phase;
            _lastChecks = checks;
            _lblPhase.Text = phase;

            RenderChecks(_checkHost, checks);
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
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet inner = CenteredColumn(page, 1000);
            _settingsColumn = inner;

            // Two columns side by side. The page scrolls when the window is too short to
            // show every section, rather than cutting the bottom of the right column off.
            TableLayoutPanel cols = new TableLayoutPanel();
            cols.Dock = DockStyle.Fill;

            // Keep the left column on the left in both languages.
            cols.RightToLeft = RightToLeft.No;
            cols.ColumnCount = 2;
            cols.RowCount = 1;
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            Panel left = new Panel();
            left.Dock = DockStyle.Fill;

            left.Margin = new Padding(0, 0, Ui.Px(ColGap), 0);

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;

            right.Margin = new Padding(0);

            cols.Controls.Add(left, 0, 0);
            cols.Controls.Add(right, 1, 0);
            inner.Controls.Add(cols);

            _columnWidth = (Ui.Px(1000) - Ui.Px(ColGap)) / 2;

            // ---------------- left column ----------------
            BeginColumn(left);

            BeginSection(Lang.T("ظاهر", "Appearance"));
            _segTheme = new Theme.Segmented(
                new string[] { "dark", "light", "auto" },
                new string[] { Lang.T("تاریک", "Dark"), Lang.T("روشن", "Light"), Lang.T("خودکار", "Auto") }, 84);
            _segTheme.ValueChanged += delegate { if (!_loading) RestartWith(_settings.Lang, _segTheme.Value); };
            Row(Lang.T("تم", "Theme"), _segTheme);

            _segLang = new Theme.Segmented(
                new string[] { "fa", "en" }, new string[] { "فارسی", "English" }, 84);
            _segLang.ValueChanged += delegate { if (!_loading) RestartWith(_segLang.Value, _settings.Theme); };
            Row(Lang.T("زبان", "Language"), _segLang);
            EndSection();

            BeginSection(Lang.T("پروکسی بالادست", "Upstream proxy"));
            _txtHost = new TextBox();
            Row(Lang.T("آدرس", "Host"), Theme.Well(_txtHost, 170));

            _numPort = new NumericUpDown();
            _numPort.Minimum = 1;
            _numPort.Maximum = 65535;
            Row(Lang.T("پورت", "Port"), Theme.Well(_numPort, 110));

            _segType = new Theme.Segmented(new string[] { "socks", "http" }, null, 84);
            Row(Lang.T("نوع پروکسی", "Proxy type"), _segType);

            Panel actions = new Panel();
            actions.Size = Ui.Sz(340, 32);
            actions.BackColor = Color.Transparent;
            Button detect = Theme.Button(Lang.T("شناسایی خودکار", "Auto-detect"), Theme.CardHi, 160, 32);
            detect.Click += delegate { DetectProxy(); };
            Button test = Theme.Button(Lang.T("تست پروکسی", "Test proxy"), Theme.CardHi, 140, 32);
            test.Location = new Point(Ui.Px(168), 0);
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

            Button save = Theme.Button(Lang.T("ذخیره تنظیمات", "Save settings"), Theme.Accent, 180, 38);
            save.Font = Theme.FB(Theme.FBody);
            save.Location = new Point(0, _columnY + Ui.Px(6));
            save.Click += delegate { SaveSettingsFromUi(true); };
            left.Controls.Add(save);
            int leftBottom = _columnY + Ui.Px(6) + save.Height + Ui.Px(8);

            // ---------------- right column ----------------
            BeginColumn(right);

            BeginSection(Lang.T("DNS و پروتکل", "DNS and protocol"));
            _segDnsMode = new Theme.Segmented(
                new string[] { "doh", "dot", "tcp", "udp" },
                new string[] { "DoH", "DoT", "TCP", "UDP" }, 66);
            Row(Lang.T("روش DNS", "DNS transport"), _segDnsMode);

            _txtDns = new TextBox();
            Row(Lang.T("سرور DNS", "DNS server"), Theme.Well(_txtDns, 170));

            // Pro Connect uses its own resolver when one is given, so that the hardened
            // connection does not have to share whatever the ordinary one is pointed at.
            _txtProDns = new TextBox();
            _txtProDns.TextChanged += delegate
            {
                if (_loading) return;
                _settings.ProDns = _txtProDns.Text.Trim();
            };
            Row(Lang.T("DNS اتصال پیشرفته", "Pro Connect DNS"), Theme.Well(_txtProDns, 170));

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
            Row(Lang.T("برنامه‌های مستثنی", "Bypass apps"), Theme.Well(_txtExtraDirect, 230));
            EndSection();

            int contentHeight = Math.Max(leftBottom, _columnY) + Ui.Px(4);
            cols.Dock = DockStyle.Top;
            cols.Height = contentHeight;
            cols.RowStyles.Clear();
            cols.RowStyles.Add(new RowStyle(SizeType.Absolute, contentHeight));
            inner.AutoScroll = true;
            inner.AutoScrollMinSize = new Size(0, contentHeight);

            StretchCards(left);
            StretchCards(right);
            return page;
        }

        void BeginColumn(Panel column)
        {
            _column = column;
            _columnY = Ui.Px(8);
        }

        void BeginSection(string caption)
        {
            _section = new Theme.CardPanel();
            _section.Fill = Theme.Bg;
            _section.DrawBorder = false;
            _section.Radius = 0;
            _section.Location = new Point(0, _columnY);
            _section.Width = _columnWidth;

            Label l = Theme.Label(caption, Theme.FBody, Theme.Text, true);
            l.Location = Ui.Pt(0, 6);
            _section.Controls.Add(l);
            _sectionY = Ui.Px(36);
        }

        void EndSection()
        {
            _section.Height = _sectionY + Ui.Px(8);
            Panel rule = new Panel();
            rule.Dock = DockStyle.Bottom;
            rule.Height = Math.Max(1, (int)Ui.Scale);
            rule.BackColor = Theme.Border;
            _section.Controls.Add(rule);
            _column.Controls.Add(_section);
            _columnY += _section.Height + Ui.Px(14);
        }

        void Row(string caption, Control control)
        {
            if (caption.Length > 0)
            {
                Label l = Theme.Label(caption, Theme.FSmall, Theme.Text, false);
                int labelRight = Ui.Px(LabelCol) - Ui.Px(10);
                // AutoSize has not measured the label yet at construction time, so measure
                // the text itself and place the right edge just before the control.
                int labelWidth = TextRenderer.MeasureText(caption, l.Font).Width;
                l.Location = new Point(Math.Max(0, labelRight - labelWidth),
                                       _sectionY + (control.Height - l.PreferredHeight) / 2);
                _section.Controls.Add(l);
            }
            control.Location = new Point(Ui.Px(LabelCol), _sectionY);
            _section.Controls.Add(control);
            _sectionY += Math.Max(control.Height, Ui.Px(26)) + Ui.Px(10);
        }

        /// <summary>A control that carries its own label, spanning from the left edge.</summary>
        void RowFull(Control control)
        {
            control.Location = new Point(0, _sectionY);
            _section.Controls.Add(control);
            _sectionY += Math.Max(control.Height, Ui.Px(20)) + Ui.Px(10);
        }

        CheckBox Check(string text)
        {
            CheckBox c = new Theme.FlatCheckBox();
            c.Text = text;
            c.AutoSize = true;
            return c;
        }

        RadioButton Radio(string text)
        {
            RadioButton r = new Theme.FlatRadioButton();
            r.Text = text;
            r.AutoSize = true;
            return r;
        }

        // =================================================================== tools page

        Panel BuildToolsPage()
        {
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet inner = CenteredColumn(page, 820);
            inner.AutoScroll = true;

            int y = 0;
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

            StretchCards(inner);
            return page;
        }

        int ToolCard(Panel parent, int y, string title, string description, Color buttonColor, EventHandler onClick)
        {
            Theme.CardPanel card = new Theme.CardPanel();
            card.Fill = Theme.Bg;
            card.DrawBorder = false;
            card.Radius = 0;
            card.Location = new Point(0, y);
            card.Size = new Size(Ui.Px(820),
                                 Math.Max(Ui.Px(82), Theme.TextH(Theme.FTiny) * 3 + Ui.Px(24)));

            Button b = Theme.Button(title, buttonColor, 320, 36);
            b.Font = Theme.FB(Theme.FSmall);
            b.Location = Ui.Pt(0, 14);
            card.Controls.Add(b);
            b.Click += onClick;

            Label d = new Label();
            d.Text = description;
            d.Font = Theme.F(Theme.FTiny);
            d.ForeColor = Theme.Muted;
            d.BackColor = Color.Transparent;
            d.AutoSize = false;
            d.Location = Ui.Pt(356, 12);
            d.Size = new Size(card.Width - Ui.Px(356 + 2), card.Height - Ui.Px(20));
            // A filesystem path is not prose: keep it in the Latin face so the digits stay
            // as typed instead of being remapped to Persian ones by the interface font.
            if (description.IndexOf(':') == 1) d.Font = Theme.FLatin(Theme.FTiny);
            d.Tag = "grow";                 // width is maintained by StretchCards
            card.Controls.Add(d);

            Panel rule = new Panel();
            rule.Dock = DockStyle.Bottom;
            rule.Height = Math.Max(1, (int)Ui.Scale);
            rule.BackColor = Theme.Border;
            card.Controls.Add(rule);

            parent.Controls.Add(card);
            return y + card.Height + Ui.Px(8);
        }

        // =================================================================== log page

        Panel BuildLogPage()
        {
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet inner = CenteredColumn(page, 1000);

            Theme.CardPanel card = new Theme.CardPanel();
            card.Dock = DockStyle.Fill;
            card.Fill = Theme.LogBg;
            card.Radius = 10;
            card.Padding = Ui.Pad(12, 10, 12, 10);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Theme.LogBg;
            _log.ForeColor = Theme.Text;
            _log.BorderStyle = BorderStyle.None;
            _log.Font = new Font("Consolas", Ui.Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _log.DetectUrls = false;
            _log.RightToLeft = RightToLeft.No;
            card.Controls.Add(_log);

            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Ui.Px(50);
            Button clear = Theme.Button(Lang.T("پاک کردن", "Clear"), Theme.CardHi, 120, 32);
            clear.Location = Ui.Pt(0, 12);
            clear.Click += delegate { _log.Clear(); };
            Button open = Theme.Button(Lang.T("باز کردن فایل لاگ", "Open log file"), Theme.CardHi, 160, 32);
            open.Location = Ui.Pt(130, 12);
            open.Click += delegate { OpenLogFile(); };

            // Lives here rather than in Settings: this is where you are when you need it,
            // and the Advanced card has no room left for another row.
            _chkVerbose = Check(Lang.T("گزارش کامل هسته — فقط برای عیب‌یابی، برنامه را کند می‌کند",
                                       "Verbose core log — for diagnosis only, it slows the app down"));
            _chkVerbose.Location = Ui.Pt(306, 18);
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
            _segTheme.SetQuiet(_settings.Theme);
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
                _segTheme.SetQuiet(_settings.Theme);
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
            _btnPro.Enabled = false;

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

            if (_topDot != null) _topDot.Fill = c;
            if (_heroDot != null) _heroDot.Fill = c;
            _stateTitle.Text = compact;
            _stateTitle.ForeColor = connected || busy ? Theme.Text : c;
            _stateLine.Text = line;
            _stateLine.ForeColor = c;
            if (_stateDetail.Text.Length == 0)
                _stateDetail.Text = Lang.T(
                    "v2rayN باید به یک سرور وصل باشد. سپس دکمه اتصال را بزنید.",
                    "Connect v2rayN to a server first, then press Connect.");

            _btnToggle.Text = connected ? Lang.T("قطع اتصال", "Disconnect") : Lang.T("اتصال", "Connect");
            _btnToggle.Fill = Theme.Accent;
            _btnToggle.FitWidth(172);
            _btnToggle.Enabled = !busy;
            if (_miToggle != null) _miToggle.Text = _btnToggle.Text;

            // Only offered from a standing start: while connected, the way to Pro is to
            // disconnect first, so the undo for the previous session always runs.
            if (_btnPro != null)
            {
                _btnPro.Enabled = !busy && !connected;
                _btnPro.Visible = !connected;
            }

            if (_stateTitle.Parent != null) LayoutStatusHero(_stateTitle.Parent);

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

            if (_log.Lines.Length > 700)
            {
                _log.SelectionStart = 0;
                _log.SelectionLength = _log.GetFirstCharIndexFromLine(350);
                _log.SelectedText = "";
            }

            _log.SelectionStart = _log.TextLength;
            _log.SelectionLength = 0;
            _log.SelectionColor = c;
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
            _log.SelectionColor = _log.ForeColor;
            _log.SelectionStart = _log.TextLength;
            _log.ScrollToCaret();
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
