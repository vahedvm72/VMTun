using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The Privacy page: what the machine still says about itself once the packets are tunnelled.
    ///
    /// Everything here is a comparison rather than a setting. Each row puts something the outside
    /// world can read next to what the exit address claims, and the disagreement is the finding —
    /// a tunnel moves the traffic, but the clock, the region Windows was set up with and the
    /// resolver that answered the lookup all still describe the machine it left from.
    /// </summary>
    partial class MainForm
    {
        Panel _privacyHost;
        Button _btnPrivacyScan;
        Label _lblPrivacyPhase;
        CheckBox _chkMatchTz;
        List<CheckResult> _privacyChecks;

        Panel BuildPrivacyPage()
        {
            Panel page = new Panel();
            page.BackColor = Theme.Bg;

            // The explanations are the substance of this page, so it scrolls rather than trimming
            // them away. The status list can shrink to fit because its rows are one line of state;
            // here the hint is the whole point of the row.
            _privacyHost = new Panel();
            _privacyHost.Dock = DockStyle.Fill;
            _privacyHost.BackColor = Theme.Bg;
            _privacyHost.AutoScroll = true;
            page.Controls.Add(_privacyHost);

            Panel bar = new Panel();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(48);
            bar.BackColor = Theme.Bg;

            Label title = Theme.Label(Lang.T("نشت و اثرانگشت", "Leaks and fingerprint"),
                                      Theme.FH3, Theme.Text, true);
            title.Location = Ui.Pt(2, 2);
            bar.Controls.Add(title);

            _lblPrivacyPhase = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            _lblPrivacyPhase.AutoSize = false;
            _lblPrivacyPhase.Location = Ui.Pt(2, 24);
            _lblPrivacyPhase.Size = new Size(Ui.Px(700), Theme.TextH(Theme.FSmall));
            _lblPrivacyPhase.TextAlign = Theme.VisualLeft;
            bar.Controls.Add(_lblPrivacyPhase);

            _btnPrivacyScan = Theme.Button(Lang.T("بررسی", "Scan"), Theme.CardHi, 130, 32);
            _btnPrivacyScan.Location = Ui.Pt(WinW - SideW - Pad * 2 - 130, 4);
            _btnPrivacyScan.Click += delegate { RunPrivacyScanAsync(); };
            bar.Controls.Add(_btnPrivacyScan);

            // Kept at the right edge without mirroring anything: only this one button moves.
            bar.SizeChanged += delegate
            {
                _btnPrivacyScan.Left = Math.Max(Ui.Px(8), bar.ClientSize.Width - _btnPrivacyScan.Width);
            };

            page.Controls.Add(bar);

            // The one control on the page that changes anything, and the answer to the first
            // finding above it.
            Theme.CardPanel opt = new Theme.CardPanel();
            opt.Dock = DockStyle.Bottom;
            opt.Height = Math.Max(Ui.Px(78), Theme.TextH(Theme.FTiny) * 3 + Ui.Px(30));

            _chkMatchTz = Check(Lang.T("هنگام اتصال، منطقه زمانی ویندوز با کشور آدرس خروجی یکی شود",
                                       "Match the Windows time zone to the exit country while connected"));
            _chkMatchTz.Location = Ui.Pt(16, 12);
            _chkMatchTz.CheckedChanged += delegate
            {
                if (_loading) return;
                _settings.MatchTimeZone = _chkMatchTz.Checked;
                _settings.Save();
            };
            opt.Controls.Add(_chkMatchTz);

            Label note = new Label();
            note.Text = Lang.T(
                "ساعت هنگام قطع اتصال به حالت خودش برمی‌گردد، و اگر برنامه ناگهانی بسته شود اجرای بعدی برش می‌گرداند. " +
                "تا وقتی وصل هستید، ساعت تقویم و جلسات و زمان فایل‌ها هم با همین منطقه نوشته می‌شود.",
                "The clock is put back on disconnect, and if the app is killed the next run restores it. " +
                "While you are connected, calendar entries, meetings and file timestamps follow this zone too.");
            note.Font = Theme.F(Theme.FTiny);
            note.ForeColor = Theme.Muted;
            note.BackColor = Color.Transparent;
            note.AutoSize = false;
            note.UseMnemonic = false;
            note.Location = Ui.Pt(16, 38);
            note.Size = new Size(Ui.Px(WinW - SideW - Pad * 2 - 32), opt.Height - Ui.Px(46));
            opt.Controls.Add(note);
            opt.SizeChanged += delegate
            {
                note.Size = new Size(Math.Max(Ui.Px(100), opt.ClientSize.Width - Ui.Px(32)),
                                     Math.Max(Theme.TextH(Theme.FTiny), opt.ClientSize.Height - Ui.Px(46)));
            };
            page.Controls.Add(opt);

            _privacyHost.SizeChanged += delegate
            {
                if (_privacyChecks != null) RenderChecks(_privacyHost, _privacyChecks, true);
            };

            SetPrivacyChecks(Lang.T("هنوز بررسی نشده", "Not checked yet"), new List<CheckResult> {
                new CheckResult(CheckStatus.Info,
                    Lang.T("بررسی انجام نشده", "No scan yet"),
                    Lang.T("دکمهٔ بررسی را بزنید", "press Scan"),
                    Lang.T("بررسی چند درخواست به سرویس‌های عمومی تشخیص IP می‌فرستد و از همان مسیری می‌رود که بقیهٔ " +
                           "ترافیک می‌رود. برای اینکه نتیجه معنی بدهد اول تونل را وصل کنید، وگرنه گزارش اتصال " +
                           "معمولی خودتان را توصیف می‌کند.",
                           "A scan sends a few requests to public address-lookup services along the same path as the " +
                           "rest of your traffic. Connect the tunnel first — otherwise the report describes your " +
                           "bare connection."))
            });

            return page;
        }

        void SetPrivacyChecks(string phase, List<CheckResult> checks)
        {
            _privacyChecks = checks;
            _lblPrivacyPhase.Text = phase;
            RenderChecks(_privacyHost, checks, true);
        }

        void RunPrivacyScanAsync()
        {
            _btnPrivacyScan.Enabled = false;
            SetPrivacyChecks(Lang.T("در حال بررسی…", "Scanning…"), new List<CheckResult> {
                new CheckResult(CheckStatus.Running,
                    Lang.T("در حال خواندن آدرس خروجی", "Reading the exit address"),
                    Lang.T("موقعیت، منطقه زمانی، نام معکوس و سرویس‌دهندهٔ DNS…",
                           "location, time zone, reverse name and DNS resolver…"))
            });

            ThreadPool.QueueUserWorkItem(delegate
            {
                string exitError, resolverError;
                ExitInfo exit = Fingerprint.LookupExit(out exitError);
                // The reverse lookup is separate: it is the slowest step and the one most likely
                // to time out, and it must not cost the rest of the report when it does.
                if (exit != null) exit.ReverseName = Fingerprint.ReverseName(exit.Ip);
                ResolverInfo resolver = Fingerprint.LookupResolver(out resolverError);

                List<CheckResult> results = Fingerprint.Audit(exit, resolver, exitError);
                bool connected = _tunnel.State == TunnelState.Connected;
                string phase = connected
                    ? Lang.T("با تونل متصل", "With the tunnel connected")
                    : Lang.T("تونل وصل نیست — این گزارش اتصال معمولی شما را توصیف می‌کند",
                             "Tunnel not connected — this report describes your bare connection");

                UiInvoke(delegate
                {
                    _btnPrivacyScan.Enabled = true;
                    SetPrivacyChecks(phase, results);
                });
            });
        }
    }
}
