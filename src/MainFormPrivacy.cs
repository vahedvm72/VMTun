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
        Theme.RoundButton _btnPrivacyScan;
        Label _lblPrivacyPhase;
        List<CheckResult> _privacyChecks;

        Panel BuildPrivacyPage()
        {
            Skin.Sheet page = new Skin.Sheet();
            Skin.Sheet inner = CenteredColumn(page, 760);

            // The explanations are the substance of this page, so it scrolls rather than trimming
            // them away. The status list can shrink to fit because its rows are one line of state;
            // here the hint is the whole point of the row.
            _privacyHost = new Skin.Sheet();
            _privacyHost.Dock = DockStyle.Fill;
            _privacyHost.AutoScroll = true;
            inner.Controls.Add(_privacyHost);

            Skin.Sheet bar = new Skin.Sheet();
            bar.Dock = DockStyle.Top;
            bar.Height = Ui.Px(52);

            Panel barRule = new Panel();
            barRule.Dock = DockStyle.Bottom;
            barRule.Height = Math.Max(1, (int)Ui.Scale);
            barRule.BackColor = Theme.Border;
            bar.Controls.Add(barRule);

            Label title = Theme.Label(Lang.T("نشت و اثرانگشت", "Leaks and fingerprint"),
                                      Theme.FH3, Theme.Text, true);
            title.Location = Ui.Pt(0, 2);
            bar.Controls.Add(title);

            _lblPrivacyPhase = Theme.Label("", Theme.FSmall, Theme.Muted, false);
            _lblPrivacyPhase.AutoSize = false;
            _lblPrivacyPhase.Height = Theme.TextH(Theme.FSmall);
            _lblPrivacyPhase.TextAlign = Theme.VisualRight;
            bar.Controls.Add(_lblPrivacyPhase);

            _btnPrivacyScan = Theme.Button(Lang.T("بررسی", "Scan"), Theme.Bg, 130, 34);
            _btnPrivacyScan.Outline = false;
            _btnPrivacyScan.Icon = Skin.Icon.Refresh;
            _btnPrivacyScan.FitWidth(120);
            _btnPrivacyScan.Click += delegate { RunPrivacyScanAsync(); };
            bar.Controls.Add(_btnPrivacyScan);

            EventHandler barLayout = delegate
            {
                title.Left = Math.Max(0, bar.ClientSize.Width - title.Width - Ui.Px(2));
                _btnPrivacyScan.Left = 0;
                _btnPrivacyScan.Top = Math.Max(0, (bar.ClientSize.Height - _btnPrivacyScan.Height) / 2);
                int phaseLeft = _btnPrivacyScan.Right + Ui.Px(12);
                _lblPrivacyPhase.Location = new Point(phaseLeft, Ui.Px(26));
                _lblPrivacyPhase.Width = Math.Max(Ui.Px(80), bar.ClientSize.Width - phaseLeft);
            };
            bar.SizeChanged += barLayout;
            barLayout(bar, EventArgs.Empty);
            inner.Controls.Add(bar);

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
                bool viaProxy;
                ExitInfo exit = Fingerprint.LookupExit(_settings, out exitError, out viaProxy);
                // The reverse lookup is separate: it is the slowest step and the one most likely
                // to time out, and it must not cost the rest of the report when it does.
                if (exit != null)
                {
                    exit.ReverseName = Fingerprint.ReverseName(exit.Ip);
                    // Plain UDP, deliberately not through the proxy: the question is what escapes
                    // this machine on its own, which is exactly what a browser's WebRTC would find.
                    // Every server, because they can disagree with each other.
                    exit.UdpReplies = Stun.QueryAll(4000);
                }
                ResolverInfo resolver = Fingerprint.LookupResolver(_settings, out resolverError);

                List<CheckResult> results = Fingerprint.Audit(exit, resolver, exitError);

                // Which path answered decides what the report describes, not whether the tunnel
                // happens to be up. Asking through the proxy reaches the exit server either way,
                // while a direct answer describes whatever adapter owns the default route — which
                // may be an entirely different VPN, and was reporting its country as if it were
                // the server's.
                string phase = viaProxy
                    ? Lang.T("از مسیر پروکسی — سرور مقصد",
                             "Through the proxy — your destination server")
                    : Lang.T("بدون پروکسی — این گزارش اتصال فعلی ویندوز را توصیف می‌کند، نه سرور شما را",
                             "Not through the proxy — this describes the connection Windows is "
                             + "using, not your server");

                UiInvoke(delegate
                {
                    _btnPrivacyScan.Enabled = true;
                    SetPrivacyChecks(phase, results);
                });
            });
        }
    }
}
