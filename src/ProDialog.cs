using System;
using System.Drawing;
using System.Windows.Forms;

namespace VMTun
{
    /// <summary>
    /// The one-time consent window for Pro Connect.
    ///
    /// Pro Connect changes settings that belong to Windows rather than to this app — the
    /// firewall's outbound policy, the clock's zone, the home country, the IPv6 binding on the
    /// user's own adapters. Every one is undone on disconnect, but a program that rearranges
    /// someone's machine has to say so in full before it does it, once, in a window they have
    /// to read rather than a line they can miss.
    ///
    /// Shown the first time only. The Privacy page reopens it for anyone who wants to change
    /// the resolver or read the list again.
    /// </summary>
    static class ProDialog
    {
        /// <summary>
        /// Returns true when the user accepted. `dns` carries the resolver they typed, which is
        /// empty when they left the field alone.
        /// </summary>
        public static bool Show(IWin32Window owner, Settings settings, out string dns)
        {
            TextBox box;
            using (Form f = Build(settings, owner != null, out box))
            {
                if (f.ShowDialog(owner) != DialogResult.OK)
                {
                    dns = settings.ProDns == null ? "" : settings.ProDns;
                    return false;
                }
                dns = box.Text.Trim();
                return true;
            }
        }

        /// <summary>
        /// The window itself, handed back unshown. Separate from Show so the layout can be
        /// rendered and checked without a person having to click through it.
        /// </summary>
        public static Form Build(Settings settings, bool hasOwner, out TextBox dnsField)
        {
            string dns = settings.ProDns == null ? "" : settings.ProDns;
            {
                Form f = new Form();
                f.Text = "VMTun — Pro Connect";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false;
                f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = hasOwner ? FormStartPosition.CenterParent : FormStartPosition.CenterScreen;
                f.BackColor = Theme.Bg;
                f.ForeColor = Theme.Text;
                f.AutoScaleMode = AutoScaleMode.None;
                f.RightToLeft = Theme.TextDirection;
                f.RightToLeftLayout = false;
                f.KeyPreview = true;

                int pad = Ui.Px(24);
                int w = Ui.Px(560);
                int y = pad;

                Label head = Theme.Label(Lang.T("اتصال پیشرفته", "Pro Connect"), Theme.FH2, Theme.Text, true);
                head.Location = new Point(pad, y);
                f.Controls.Add(head);
                y += Theme.TextH(Theme.FH2) + Ui.Px(6);

                Label sub = new Label();
                sub.Text = Lang.T(
                    "این حالت تنظیماتی از خود ویندوز را عوض می‌کند، نه فقط تنظیمات این برنامه. " +
                    "همه‌شان هنگام قطع اتصال به حالت اول برمی‌گردند و اگر برنامه ناگهانی بسته شود، " +
                    "اجرای بعدی برشان می‌گرداند.",
                    "This mode changes settings that belong to Windows, not just to this app. " +
                    "Every one of them is put back when you disconnect, and if the app is killed " +
                    "the next run restores them.");
                sub.Font = Theme.F(Theme.FSmall);
                sub.ForeColor = Theme.Muted;
                sub.BackColor = Color.Transparent;
                sub.AutoSize = false;
                sub.UseMnemonic = false;
                sub.Location = new Point(pad, y);
                sub.Size = new Size(w, MeasureH(sub.Text, sub.Font, w));
                f.Controls.Add(sub);
                y = sub.Bottom + Ui.Px(16);

                // ---- the list of changes ------------------------------------------------
                Panel list = new Panel();
                list.Location = new Point(pad, y);
                list.Width = w;
                list.BackColor = Theme.Bg;

                string[] items = ProMode.ChangeSummary(settings);
                int iy = 0;
                foreach (string item in items)
                {
                    int nl = item.IndexOf('\n');
                    string itemTitle = nl < 0 ? item : item.Substring(0, nl);
                    string itemBody = nl < 0 ? "" : item.Substring(nl + 1);

                    Theme.CardPanel row = new Theme.CardPanel();
                    row.Location = new Point(0, iy);
                    row.Width = w;

                    Theme.StatusIcon dot = new Theme.StatusIcon(CheckStatus.Warn, 14);
                    dot.Location = new Point(Ui.Px(14), Ui.Px(13));
                    row.Controls.Add(dot);

                    Label t = Theme.Label(itemTitle, Theme.FSmall, Theme.Text, true);
                    t.Location = new Point(Ui.Px(40), Ui.Px(10));
                    row.Controls.Add(t);

                    int bodyW = w - Ui.Px(40 + 14);
                    Label b = new Label();
                    b.Text = itemBody;
                    b.Font = Theme.F(Theme.FTiny);
                    b.ForeColor = Theme.Muted;
                    b.BackColor = Color.Transparent;
                    b.AutoSize = false;
                    b.UseMnemonic = false;
                    b.Location = new Point(Ui.Px(40), Ui.Px(10) + Theme.TextH(Theme.FSmall) + Ui.Px(2));
                    b.Size = new Size(bodyW, MeasureH(itemBody, b.Font, bodyW));
                    row.Controls.Add(b);

                    row.Height = b.Bottom + Ui.Px(11);
                    list.Controls.Add(row);
                    iy += row.Height + Ui.Px(7);
                }
                list.Height = iy - Ui.Px(7);
                f.Controls.Add(list);
                y = list.Bottom + Ui.Px(18);

                // ---- the resolver -------------------------------------------------------
                Label dnsLabel = Theme.Label(
                    Lang.T("DNS اختصاصی (اختیاری)", "Your own DNS resolver (optional)"),
                    Theme.FSmall, Theme.Text, true);
                dnsLabel.Location = new Point(pad, y);
                f.Controls.Add(dnsLabel);
                y += Theme.TextH(Theme.FSmall) + Ui.Px(6);

                TextBox dnsBox = new TextBox();
                dnsBox.Text = dns;
                dnsBox.Location = new Point(pad, y);
                dnsBox.Size = new Size(Ui.Px(240), Theme.TextH(Theme.FSmall) + Ui.Px(12));
                Theme.StyleInput(dnsBox);
                f.Controls.Add(dnsBox);

                Label dnsHint = new Label();
                dnsHint.Text = Lang.T(
                    "خالی بگذارید تا از " + settings.RemoteDns + " استفاده شود.",
                    "Leave it empty to use " + settings.RemoteDns + ".");
                dnsHint.Font = Theme.F(Theme.FTiny);
                dnsHint.ForeColor = Theme.Muted;
                dnsHint.BackColor = Color.Transparent;
                dnsHint.AutoSize = false;
                dnsHint.UseMnemonic = false;
                dnsHint.Location = new Point(pad + Ui.Px(252), y + Ui.Px(6));
                dnsHint.Size = new Size(w - Ui.Px(252), Theme.TextH(Theme.FTiny) * 2);
                dnsHint.TextAlign = Theme.VisualLeft;
                f.Controls.Add(dnsHint);

                y = dnsBox.Bottom + Ui.Px(22);

                // ---- buttons ------------------------------------------------------------
                int bw = Ui.Px(150), bh = Ui.Px(38), gap = Ui.Px(10);

                Button go = Theme.Button(Lang.T("متوجه‌ام، وصل شو", "I understand, connect"), Theme.Accent, 150, 38);
                go.Font = Theme.FB(Theme.FSmall);
                go.DialogResult = DialogResult.OK;
                go.Location = new Point(pad + w - bw, y);
                f.Controls.Add(go);

                Button cancel = Theme.Button(Lang.T("انصراف", "Cancel"), Theme.CardHi, 116, 38);
                cancel.DialogResult = DialogResult.Cancel;
                cancel.Location = new Point(pad + w - bw - Ui.Px(116) - gap, y);
                f.Controls.Add(cancel);

                f.AcceptButton = go;
                f.CancelButton = cancel;
                f.ClientSize = new Size(w + pad * 2, y + bh + pad);
                f.HandleCreated += delegate { Theme.ApplyTitleBar(f); };

                dnsField = dnsBox;
                return f;
            }
        }

        static int MeasureH(string text, Font font, int width)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return TextRenderer.MeasureText(text, font, new Size(width, 0),
                                            TextFormatFlags.WordBreak).Height + Ui.Px(2);
        }
    }
}
