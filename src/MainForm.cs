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

        // header
        Theme.StatusIcon _dot;
        Label _stateTitle, _stateDetail;
        Theme.RoundButton _btnToggle;

        // navigation
        readonly List<Skin.NavItem> _navButtons = new List<Skin.NavItem>();
        readonly List<Panel> _pages = new List<Panel>();
        int _currentPage;

        // status page
        Panel _checkHost;
        Panel _chipHost;
        Theme.RoundButton _btnPro;
        Label _sumProxy, _sumDns, _sumRouting, _sumAdapter, _sumExit, _sumGuard, _lblPhase;
        Button _btnRecheck;

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
        const int SideW = 212;
        const int HeaderH = 142;
        const int Pad = 22;
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

            // The filler is added first and the sidebar second, which is the wrong way round
            // until you know that docking is resolved from the highest z-order index down: the
            // later child claims its edge first, and the Fill added earlier gets what is left.
            Panel main = new Panel();
            main.Dock = DockStyle.Fill;
            main.BackColor = Color.Transparent;
            back.Controls.Add(main);

            BuildSidebar(back);

            // Fill first, header second: docking is applied back to front, so the control added
            // last takes its edge first and the filler gets whatever is left.
            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.Transparent;
            content.Padding = Ui.Pad(Pad - 8, Pad - 10, Pad - 8, Pad - 8);
            main.Controls.Add(content);

            BuildHeaderCard(main);

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

        void BuildSidebar(Control host)
        {
            // The sidebar is a card in its own right, floating on the backdrop rather than
            // butting against the window edge. That is what lets the nav items be pills.
            Panel gutter = new Panel();
            gutter.Dock = DockStyle.Left;
            gutter.Width = Ui.Px(SideW);
            gutter.BackColor = Color.Transparent;
            gutter.Padding = Ui.Pad(Pad - 8, Pad - 8, 0, Pad - 8);
            host.Controls.Add(gutter);

            Theme.CardPanel side = new Theme.CardPanel();
            side.Dock = DockStyle.Fill;
            side.Fill = Theme.Sidebar;
            side.Line = Theme.Border;
            gutter.Controls.Add(side);

            Label version = Theme.Label("v" + Integration.Version, Theme.FTiny, Theme.Muted, false);
            version.Font = Theme.FLatin(Theme.FTiny);
            version.Dock = DockStyle.Bottom;
            version.AutoSize = false;
            version.Height = Ui.Px(34);
            version.TextAlign = ContentAlignment.MiddleCenter;
            side.Controls.Add(version);

            string[] names =
            {
                Lang.T("وضعیت", "Status"),
                Lang.T("تنظیمات", "Settings"),
                Lang.T("حریم خصوصی", "Privacy"),
                Lang.T("ابزارها", "Tools"),
                Lang.T("گزارش", "Log")
            };
            Skin.Icon[] icons =
            {
                Skin.Icon.Activity, Skin.Icon.Gear, Skin.Icon.Shield,
                Skin.Icon.Wrench, Skin.Icon.Document
            };

            // Added bottom-up: top-docked children lay out in reverse order of addition.
            for (int i = names.Length - 1; i >= 0; i--)
            {
                Skin.NavItem item = new Skin.NavItem(names[i], icons[i]);
                item.Dock = DockStyle.Top;
                item.Height = Ui.Px(46);
                item.Margin = Ui.Pad(0, 0, 0, 6);
                int index = i;
                item.Click += delegate { ShowPage(index); };
                side.Controls.Add(item);
                _navButtons.Insert(0, item);
            }

            // A spacer, so the first pill does not sit against the brand.
            Panel gap = new Panel();
            gap.Dock = DockStyle.Top;
            gap.Height = Ui.Px(10);
            gap.BackColor = Color.Transparent;
            side.Controls.Add(gap);

            Panel brand = new Panel();
            brand.Dock = DockStyle.Top;
            brand.Height = Ui.Px(86);
            brand.BackColor = Color.Transparent;

            Theme.CardPanel tile = new Theme.CardPanel();
            tile.Fill = Theme.Accent;
            tile.Frosted = false;
            tile.Line = Color.FromArgb(70, Color.White);
            tile.Radius = 11;
            tile.Location = Ui.Pt(14, 16);
            tile.Size = Ui.Sz(42, 42);
            tile.Paint += delegate(object sender, PaintEventArgs e)
            {
                Theme.CardPanel t = (Theme.CardPanel)sender;
                int inset = Ui.Px(10);
                Skin.DrawIcon(e.Graphics, Skin.Icon.Shield,
                    new RectangleF(inset, inset, t.Width - inset * 2, t.Height - inset * 2),
                    Theme.OnAccent, Math.Max(1.6f, Ui.Scale * 1.5f));
            };
            brand.Controls.Add(tile);

            Label logo = Theme.Label("VMTun", Theme.FH2, Theme.Text, true);
            logo.Font = Theme.FLatinB(Theme.FH2);
            logo.Location = Ui.Pt(68, 20);
            brand.Controls.Add(logo);

            Label tagline = Theme.Label(Lang.T("تونل سراسری ویندوز", "System-wide tunnel"),
                                        Theme.FTiny, Theme.Muted, false);
            tagline.Location = Ui.Pt(68, 44);
            tagline.MaximumSize = new Size(Ui.Px(SideW - 76), 0);
            brand.Controls.Add(tagline);

            side.Controls.Add(brand);
        }

        void BuildHeaderCard(Panel parent)
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Top;
            host.Height = Ui.Px(HeaderH);
            host.BackColor = Color.Transparent;
            host.Padding = Ui.Pad(Pad - 8, Pad - 8, Pad - 8, 0);
            parent.Controls.Add(host);

            Theme.CardPanel h = new Theme.CardPanel();
            h.Dock = DockStyle.Fill;
            host.Controls.Add(h);

            _dot = new Theme.StatusIcon(CheckStatus.Info, 26);
            _dot.Location = Ui.Pt(24, 30);
            h.Controls.Add(_dot);

            _stateTitle = Theme.Label("", Theme.FH1, Theme.Text, true);
            _stateTitle.Location = Ui.Pt(64, 22);
            h.Controls.Add(_stateTitle);

            _stateDetail = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            _stateDetail.Location = Ui.Pt(65, 58);
            _stateDetail.MaximumSize = new Size(Ui.Px(620), Theme.TextH(Theme.FSmall) + Ui.Px(2));
            h.Controls.Add(_stateDetail);

            // A row of facts, each in its own pill: what the header used to say in one long
            // sentence, broken up so the eye can find the one it wants.
            _chipHost = new Panel();
            _chipHost.Location = Ui.Pt(64, 86);
            _chipHost.Size = new Size(Ui.Px(700), Ui.Px(28));
            _chipHost.BackColor = Color.Transparent;
            h.Controls.Add(_chipHost);

            _btnToggle = Theme.Button("", Theme.Accent, 172, 50);
            _btnToggle.Font = Theme.FB(Theme.FH3);
            _btnToggle.Glow = true;
            _btnToggle.Frosted = false;
            _btnToggle.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnToggle.Click += delegate { ToggleTunnel(); };
            h.Controls.Add(_btnToggle);

            // Sits beside Connect rather than replacing it: the plain connect stays the one
            // that changes nothing outside this app, which is what most runs should be.
            _btnPro = Theme.Button(Lang.T("اتصال پیشرفته", "Pro Connect"), Theme.CardHi, 150, 50);
            _btnPro.Font = Theme.FB(Theme.FSmall);
            _btnPro.Icon = Skin.Icon.Bolt;
            // Sized to its own label: "Pro Connect" and "اتصال پیشرفته" are different widths,
            // and a fixed box cropped the longer one.
            _btnPro.FitWidth(150);
            _btnPro.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnPro.Click += delegate { ProConnect(); };
            h.Controls.Add(_btnPro);

            h.SizeChanged += delegate { LayoutHeaderButtons(h); };
            LayoutHeaderButtons(h);
        }

        void LayoutHeaderButtons(Control h)
        {
            int pad = Ui.Px(20);
            int y = (h.ClientSize.Height - _btnToggle.Height) / 2;
            _btnToggle.Location = new Point(h.ClientSize.Width - pad - _btnToggle.Width, y);
            _btnPro.Location = new Point(_btnToggle.Left - Ui.Px(12) - _btnPro.Width, y);
        }

        /// <summary>
        /// Rebuilds the row of chips under the status line. They are re-made rather than updated
        /// because each one is sized to its own text, and a stale width is worse than a rebuild
        /// that happens a few times a minute.
        /// </summary>
        void RefreshChips()
        {
            if (_chipHost == null) return;
            _chipHost.SuspendLayout();
            foreach (Control c in new List<Control>(_chipHost.Controls.Cast<Control>())) c.Dispose();
            _chipHost.Controls.Clear();

            List<Skin.Chip> chips = new List<Skin.Chip>();

            string alias = TunAdapter.FindAlias();
            Skin.Chip adapter = new Skin.Chip(alias == null
                ? Lang.T("تونل خاموش", "Tunnel off")
                : Lang.T("تونل فعال", "Tunnel active"));
            adapter.Dot = alias == null ? Theme.Muted : Theme.Green;
            chips.Add(adapter);

            chips.Add(new Skin.Chip(Lang.T("مسیر ", "Routing ") +
                (_settings.Routing == RoutingMode.IranDirect
                    ? Lang.T("ایران مستقیم", "Iran direct")
                    : Lang.T("کامل", "full"))));

            chips.Add(new Skin.Chip("DNS " + _settings.DnsMode.ToUpperInvariant()));

            if (FirewallGuard.IsActive())
            {
                Skin.Chip guard = new Skin.Chip(Lang.T("کیل‌سوئیچ", "Kill switch"));
                guard.Dot = Theme.Amber;
                chips.Add(guard);
            }

            if (_tunnel.LatencyMs > 0)
                chips.Add(new Skin.Chip(Lang.T("تأخیر ", "Latency ") +
                    _tunnel.LatencyMs.ToString(CultureInfo.InvariantCulture) + " ms"));

            int x = 0;
            int height = Ui.Px(26);
            foreach (Skin.Chip chip in chips)
            {
                chip.Height = height;
                chip.Width = chip.Measure();
                chip.Location = new Point(x, 0);
                _chipHost.Controls.Add(chip);
                x += chip.Width + Ui.Px(8);
            }
            _chipHost.ResumeLayout();
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
        }

        // =================================================================== status page

        Panel BuildStatusPage()
        {
            Panel page = new Panel();
            page.BackColor = Theme.Bg;

            // The check list fills whatever is left; rows are sized to fit so it never scrolls.
            _checkHost = new Panel();
            _checkHost.Dock = DockStyle.Fill;
            _checkHost.BackColor = Theme.Bg;
            page.Controls.Add(_checkHost);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(46);
            bar.BackColor = Theme.Bg;

            Label title = Theme.Label(Lang.T("بررسی‌ها", "Checks"), Theme.FH3, Theme.Text, true);
            title.Location = Ui.Pt(2, 14);
            bar.Controls.Add(title);

            _lblPhase = Theme.Label("", Theme.FTiny, Theme.Muted, false);
            _lblPhase.Location = Ui.Pt(92, 18);
            bar.Controls.Add(_lblPhase);

            _btnRecheck = Theme.Button(Lang.T("بررسی مجدد", "Re-run checks"), Theme.CardHi, 146, 32);
            _btnRecheck.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnRecheck.Location = new Point(bar.Width - Ui.Px(146), Ui.Px(6));
            _btnRecheck.Click += delegate { RunPreflightAsync(); };
            bar.Controls.Add(_btnRecheck);
            page.Controls.Add(bar);

            // ---- summary card ---------------------------------------------------------
            Theme.CardPanel card = new Theme.CardPanel();
            card.Dock = DockStyle.Top;
            card.Height = Theme.TextH(Theme.FSmall) * 3 + Ui.Px(30);
            card.Padding = Ui.Pad(16, 12, 16, 12);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.BackColor = Color.Transparent;
            // A right-to-left TableLayoutPanel reverses its columns; the cells must stay put.
            grid.RightToLeft = RightToLeft.No;
            grid.ColumnCount = 4;
            grid.RowCount = 3;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 16f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34f));
            for (int r = 0; r < 3; r++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34f));

            _sumProxy = SummaryCell(grid, 0, 0, Lang.T("پروکسی بالادست", "Upstream proxy"));
            _sumDns = SummaryCell(grid, 0, 1, Lang.T("روش DNS", "DNS transport"));
            _sumRouting = SummaryCell(grid, 0, 2, Lang.T("مسیریابی", "Routing"));
            _sumAdapter = SummaryCell(grid, 2, 0, Lang.T("آداپتور", "Adapter"));
            _sumExit = SummaryCell(grid, 2, 1, Lang.T("IP خروجی", "Exit IP"));
            _sumGuard = SummaryCell(grid, 2, 2, Lang.T("کیل‌سوئیچ", "Kill switch"));

            card.Controls.Add(grid);
            page.Controls.Add(card);

            _checkHost.SizeChanged += delegate
            {
                if (_lastChecks.Count > 0) SetChecks(_lastPhase, _lastChecks);
            };
            return page;
        }

        Label SummaryCell(TableLayoutPanel grid, int col, int row, string caption)
        {
            // Dock rather than Anchor: a Label keeps its unscaled default height of 23px
            // otherwise, which clips text that is 26px tall at this scale.
            Label k = Theme.Label(caption, Theme.FTiny, Theme.Muted, false);
            k.AutoSize = false;
            k.Dock = DockStyle.Fill;
            k.RightToLeft = Theme.TextDirection;
            k.TextAlign = Theme.VisualLeft;
            grid.Controls.Add(k, col, row);

            Label v = Theme.Label("—", Theme.FSmall, Theme.Text, true);
            v.Font = Theme.FLatinB(Theme.FSmall);
            v.AutoSize = false;
            v.Dock = DockStyle.Fill;
            v.RightToLeft = Theme.TextDirection;
            v.TextAlign = Theme.VisualLeft;
            grid.Controls.Add(v, col + 1, row);
            return v;
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
            host.Controls.Clear();

            int width = host.ClientSize.Width;
            int available = host.ClientSize.Height;

            // A scrolling host lays its rows out before the vertical bar has claimed its width,
            // so the first pass overflows sideways and WinForms answers with a horizontal bar
            // that never goes away. Reserving the width up front costs a few pixels and removes
            // the second scrollbar entirely.
            if (alwaysShowHints && host.AutoScroll)
                width -= SystemInformation.VerticalScrollBarWidth;
            int gap = Ui.Px(8);
            int iconSize = 18;
            int lineH = Theme.TextH(Theme.FSmall);
            int textLeft = Ui.Px(46);
            int textWidth = width - textLeft - Ui.Px(16);

            Font titleFont = Theme.FB(Theme.FSmall);
            Font hintFont = Theme.F(Theme.FTiny);

            // Pass 1: full rows with hints. Pass 2: hints dropped. Pass 3: minimum rows.
            // A scrolling host has no height to fit into, so the first pass always wins
            // there and no explanation is thrown away to save a few pixels.
            if (alwaysShowHints) available = int.MaxValue;

            for (int pass = 0; pass < 3; pass++)
            {
                bool showHints = (pass == 0);
                int rowBase = lineH + ((pass == 2) ? Ui.Px(12) : Ui.Px(22));
                int total = 0;
                List<int> heights = new List<int>();
                foreach (CheckResult c in checks)
                {
                    int h = rowBase;
                    if (showHints && !string.IsNullOrEmpty(c.Hint))
                    {
                        Size hs = TextRenderer.MeasureText(c.Hint, hintFont,
                            new Size(textWidth, 0), TextFormatFlags.WordBreak);
                        h += hs.Height + Ui.Px(10);
                    }
                    heights.Add(h);
                    total += h + gap;
                }
                if (total - gap <= available || pass == 2)
                {
                    int y = 0;
                    for (int i = 0; i < checks.Count; i++)
                    {
                        host.Controls.Add(
                            BuildCheckRow(checks[i], width, heights[i], y, showHints,
                                          iconSize, textLeft, textWidth, lineH, titleFont, hintFont));
                        y += heights[i] + gap;
                    }
                    break;
                }
            }

            host.ResumeLayout();
        }

        Control BuildCheckRow(CheckResult c, int width, int height, int y, bool showHint,
                              int iconSize, int textLeft, int textWidth, int lineH, Font titleFont, Font hintFont)
        {
            // No anchor: the whole list is rebuilt at the right width whenever the host resizes.
            Theme.CardPanel row = new Theme.CardPanel();
            row.Location = new Point(0, y);
            row.Size = new Size(width, height);
            // A warning that looks exactly like an everything-is-fine row is a warning nobody
            // reads, so the card itself carries the colour.
            row.Fill = Theme.StatusFill(c.Status);
            row.Line = Theme.StatusLine(c.Status);

            int topPad = (lineH + Ui.Px(22) - lineH) / 2;
            Theme.StatusIcon icon = new Theme.StatusIcon(c.Status, iconSize);
            icon.Location = new Point(Ui.Px(16), topPad + (lineH - Ui.Px(iconSize)) / 2);
            row.Controls.Add(icon);

            Label title = Theme.Label(c.Title, Theme.FSmall, Theme.Text, true);
            title.Font = titleFont;
            title.Location = new Point(textLeft, topPad);
            row.Controls.Add(title);

            // The detail sits on the same line as the title, pushed to the right, so a check
            // takes one line instead of two.
            Label detail = new Label();
            detail.Text = c.Detail;
            detail.Font = Theme.FLatin(Theme.FSmall);
            detail.ForeColor = Theme.Muted;
            detail.BackColor = Color.Transparent;
            detail.AutoSize = false;
            detail.AutoEllipsis = true;
            detail.UseMnemonic = false;
            detail.TextAlign = Theme.VisualRight;
            int detailLeft = textLeft + Ui.Px(220);
            detail.Location = new Point(detailLeft, topPad);
            detail.Size = new Size(Math.Max(Ui.Px(80), width - detailLeft - Ui.Px(16)), lineH);
            row.Controls.Add(detail);

            if (showHint && !string.IsNullOrEmpty(c.Hint))
            {
                Label hint = new Label();
                hint.Text = c.Hint;
                hint.Font = hintFont;
                hint.ForeColor = Theme.StatusColor(c.Status);
                hint.BackColor = Color.Transparent;
                hint.AutoSize = false;
                hint.UseMnemonic = false;
                hint.Location = new Point(textLeft, topPad + lineH + Ui.Px(4));
                hint.Size = new Size(textWidth, Math.Max(lineH, height - topPad - lineH - Ui.Px(8)));
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

            RefreshChips();
        }

        // =================================================================== settings page

        Panel _column;
        Theme.CardPanel _section;
        int _sectionY, _columnY, _columnWidth;

        Panel BuildSettingsPage()
        {
            Panel page = new Panel();
            page.BackColor = Theme.Bg;

            // Two columns side by side, sized so the whole page fits without scrolling.
            TableLayoutPanel cols = new TableLayoutPanel();
            cols.Dock = DockStyle.Fill;
            cols.BackColor = Theme.Bg;
            // Keep the left column on the left in both languages.
            cols.RightToLeft = RightToLeft.No;
            cols.ColumnCount = 2;
            cols.RowCount = 1;
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
            cols.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

            Panel left = new Panel();
            left.Dock = DockStyle.Fill;
            left.BackColor = Theme.Bg;
            left.Margin = new Padding(0, 0, Ui.Px(ColGap), 0);

            Panel right = new Panel();
            right.Dock = DockStyle.Fill;
            right.BackColor = Theme.Bg;
            right.Margin = new Padding(0);

            cols.Controls.Add(left, 0, 0);
            cols.Controls.Add(right, 1, 0);
            page.Controls.Add(cols);

            _columnWidth = (Ui.Px(WinW) - Ui.Px(SideW) - Ui.Px(Pad) * 2 - Ui.Px(ColGap)) / 2;

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
            _txtHost.Width = Ui.Px(170);
            Theme.StyleInput(_txtHost);
            Row(Lang.T("آدرس", "Host"), _txtHost);

            _numPort = new NumericUpDown();
            _numPort.Width = Ui.Px(110);
            _numPort.Minimum = 1;
            _numPort.Maximum = 65535;
            Theme.StyleInput(_numPort);
            Row(Lang.T("پورت", "Port"), _numPort);

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

            // ---------------- right column ----------------
            BeginColumn(right);

            BeginSection(Lang.T("DNS و پروتکل", "DNS and protocol"));
            _segDnsMode = new Theme.Segmented(
                new string[] { "doh", "dot", "tcp", "udp" },
                new string[] { "DoH", "DoT", "TCP", "UDP" }, 66);
            Row(Lang.T("روش DNS", "DNS transport"), _segDnsMode);

            _txtDns = new TextBox();
            _txtDns.Width = Ui.Px(170);
            Theme.StyleInput(_txtDns);
            Row(Lang.T("سرور DNS", "DNS server"), _txtDns);

            // Pro Connect uses its own resolver when one is given, so that the hardened
            // connection does not have to share whatever the ordinary one is pointed at.
            _txtProDns = new TextBox();
            _txtProDns.Width = Ui.Px(170);
            Theme.StyleInput(_txtProDns);
            _txtProDns.TextChanged += delegate
            {
                if (_loading) return;
                _settings.ProDns = _txtProDns.Text.Trim();
            };
            Row(Lang.T("DNS اتصال پیشرفته", "Pro Connect DNS"), _txtProDns);

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
            _numMtu.Width = Ui.Px(110);
            _numMtu.Minimum = 576;
            _numMtu.Maximum = 9000;
            _numMtu.Increment = 100;
            Theme.StyleInput(_numMtu);
            Row("MTU", _numMtu);

            _txtExtraDirect = new TextBox();
            _txtExtraDirect.Width = Ui.Px(230);
            Theme.StyleInput(_txtExtraDirect);
            Row(Lang.T("برنامه‌های مستثنی", "Bypass apps"), _txtExtraDirect);
            EndSection();

            StretchCards(left);
            StretchCards(right);
            return page;
        }

        void BeginColumn(Panel column)
        {
            _column = column;
            _columnY = 0;
        }

        void BeginSection(string caption)
        {
            _section = new Theme.CardPanel();
            _section.Location = new Point(0, _columnY);
            _section.Width = _columnWidth;

            Label l = Theme.Label(caption, Theme.FBody, Theme.Accent, true);
            l.Location = Ui.Pt(18, 13);
            _section.Controls.Add(l);
            _sectionY = Ui.Px(44);
        }

        void EndSection()
        {
            _section.Height = _sectionY + Ui.Px(8);
            _column.Controls.Add(_section);
            _columnY += _section.Height + Ui.Px(14);
        }

        void Row(string caption, Control control)
        {
            if (caption.Length > 0)
            {
                Label l = Theme.Label(caption, Theme.FSmall, Theme.Text, false);
                l.Location = new Point(Ui.Px(20), _sectionY + (control.Height - l.PreferredHeight) / 2);
                _section.Controls.Add(l);
            }
            control.Location = new Point(Ui.Px(LabelCol), _sectionY);
            _section.Controls.Add(control);
            _sectionY += Math.Max(control.Height, Ui.Px(26)) + Ui.Px(10);
        }

        /// <summary>A control that carries its own label, spanning from the left edge.</summary>
        void RowFull(Control control)
        {
            control.Location = new Point(Ui.Px(20), _sectionY);
            _section.Controls.Add(control);
            _sectionY += Math.Max(control.Height, Ui.Px(20)) + Ui.Px(10);
        }

        CheckBox Check(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.ForeColor = Theme.Text;
            c.BackColor = Color.Transparent;
            c.Font = Theme.F(Theme.FSmall);
            c.Cursor = Cursors.Hand;
            return c;
        }

        RadioButton Radio(string text)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.ForeColor = Theme.Text;
            r.BackColor = Color.Transparent;
            r.Font = Theme.F(Theme.FSmall);
            r.Cursor = Cursors.Hand;
            return r;
        }

        // =================================================================== tools page

        Panel BuildToolsPage()
        {
            Panel page = new Panel();
            page.BackColor = Theme.Bg;

            int y = 0;
            y = ToolCard(page, y,
                Lang.T("رفع محدودیت اپ‌های UWP / Store", "Fix Windows Store / UWP apps"),
                Lang.T("اپ‌های UWP به‌صورت پیش‌فرض اجازه ارتباط با آدرس محلی ندارند. این دکمه برای همه بسته‌های نصب‌شده استثنا ثبت می‌کند.",
                       "UWP apps are denied loopback access by default. This registers an exemption for every installed package."),
                Theme.CardHi, delegate { FixUwp(); });

            y = ToolCard(page, y,
                Lang.T("بررسی IP خروجی فعلی", "Check current external IP"),
                Lang.T("یک درخواست بدون پروکسی می‌فرستد. با تونل سالم باید IP سرور خارجی برگردد.",
                       "Sends an unproxied request. With a healthy tunnel this returns the server address."),
                Theme.CardHi, delegate { CheckExternalIp(); });

            y = ToolCard(page, y,
                Lang.T("باز کردن پوشه لاگ و کانفیگ", "Open log and config folder"),
                AppPaths.DataDir,
                Theme.CardHi, delegate { OpenDataFolder(); });

            y = ToolCard(page, y,
                Lang.T("لغو محدودیت‌زدایی UWP", "Undo UWP exemptions"),
                Lang.T("استثناهای loopback که بالا ثبت شده‌اند را پاک می‌کند.",
                       "Clears the loopback exemptions registered above."),
                Theme.CardHi, delegate { ClearUwp(); });

            y = ToolCard(page, y,
                Lang.T("بازیابی شبکه", "Repair network"),
                Lang.T("همه قوانین فایروال VMTun را حذف و سیاست خروجی ویندوز را به حالت اول برمی‌گرداند. اگر برنامه ناگهانی بسته شد و اینترنت قطع ماند، این را بزنید.",
                       "Removes every VMTun firewall rule and restores the Windows outbound policy. Use it if the app was killed and left you offline."),
                Theme.Danger, delegate { RepairNetwork(); });

            ToolCard(page, y,
                Lang.T("بررسی به‌روزرسانی", "Check for updates"),
                Lang.T("نسخه فعلی " + Integration.Version + ". نسخه تازه از گیت‌هاب گرفته و نصب می‌شود. " +
                       "درخواست از داخل تونل می‌رود، پس بهتر است اول وصل باشید.",
                       "You have version " + Integration.Version + ". A newer build is fetched from GitHub and " +
                       "installed. The request travels through the tunnel, so connect first if you can."),
                Theme.CardHi, delegate { CheckForUpdate(true); });

            StretchCards(page);
            return page;
        }

        int ToolCard(Panel parent, int y, string title, string description, Color buttonColor, EventHandler onClick)
        {
            Theme.CardPanel card = new Theme.CardPanel();
            card.Location = new Point(0, y);
            card.Size = new Size(Ui.Px(WinW - SideW - Pad * 2),
                                 Math.Max(Ui.Px(82), Theme.TextH(Theme.FTiny) * 3 + Ui.Px(24)));

            Button b = Theme.Button(title, buttonColor, 320, 36);
            b.Font = Theme.FB(Theme.FSmall);
            b.Location = Ui.Pt(18, 14);
            card.Controls.Add(b);
            b.Click += onClick;

            Label d = new Label();
            d.Text = description;
            d.Font = Theme.F(Theme.FTiny);
            d.ForeColor = Theme.Muted;
            d.BackColor = Color.Transparent;
            d.AutoSize = false;
            d.Location = Ui.Pt(356, 12);
            d.Size = new Size(card.Width - Ui.Px(356 + 18), card.Height - Ui.Px(20));
            // A filesystem path is not prose: keep it in the Latin face so the digits stay
            // as typed instead of being remapped to Persian ones by the interface font.
            if (description.IndexOf(':') == 1) d.Font = Theme.FLatin(Theme.FTiny);
            d.Tag = "grow";                 // width is maintained by StretchCards
            card.Controls.Add(d);

            parent.Controls.Add(card);
            return y + card.Height + Ui.Px(12);
        }

        // =================================================================== log page

        Panel BuildLogPage()
        {
            Panel page = new Panel();
            page.BackColor = Theme.Bg;

            Theme.CardPanel card = new Theme.CardPanel();
            card.Dock = DockStyle.Fill;
            card.Padding = Ui.Pad(12, 10, 12, 10);

            _log = new RichTextBox();
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = Theme.Card;
            _log.ForeColor = Theme.Text;
            _log.BorderStyle = BorderStyle.None;
            _log.Font = new Font("Consolas", Ui.Px(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _log.DetectUrls = false;
            _log.RightToLeft = RightToLeft.No;
            card.Controls.Add(_log);
            page.Controls.Add(card);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = Ui.Px(50);
            bar.BackColor = Theme.Bg;
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
            page.Controls.Add(bar);

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

            CheckStatus icon;
            Color c;
            string title;
            if (connected && _tunnel.Verified)
            {
                icon = CheckStatus.Ok; c = Theme.Green;
                title = Lang.T("متصل و تأیید شد", "Connected and verified");
            }
            else if (connected)
            {
                icon = CheckStatus.Warn; c = Theme.Amber;
                title = Lang.T("متصل، ولی ترافیک عبور نمی‌کند", "Connected, but no traffic");
            }
            else if (busy)
            {
                icon = CheckStatus.Running; c = Theme.Accent;
                title = s == TunnelState.Connecting
                    ? Lang.T("در حال اتصال…", "Connecting…")
                    : Lang.T("در حال قطع…", "Disconnecting…");
            }
            else if (s == TunnelState.Faulted)
            {
                icon = CheckStatus.Fail; c = Theme.Red;
                title = Lang.T("خطا", "Error");
            }
            else
            {
                icon = CheckStatus.Info; c = Theme.Muted;
                title = Lang.T("قطع", "Disconnected");
            }

            _dot.Status = icon;
            _stateTitle.Text = title;
            _stateTitle.ForeColor = connected || busy ? Theme.Text : c;
            if (_stateDetail.Text.Length == 0)
                _stateDetail.Text = Lang.T(
                    "v2rayN باید به یک سرور وصل باشد. سپس دکمه اتصال را بزنید.",
                    "Connect v2rayN to a server first, then press Connect.");

            _btnToggle.Text = connected ? Lang.T("قطع اتصال", "Disconnect") : Lang.T("اتصال", "Connect");
            _btnToggle.Fill = connected ? Theme.Red : Theme.Accent;
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
