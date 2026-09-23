# VMTun

[![Build](https://github.com/vahedvm72/VMTun/actions/workflows/build.yml/badge.svg)](https://github.com/vahedvm72/VMTun/actions/workflows/build.yml)
[![Latest release](https://img.shields.io/github/v/release/vahedvm72/VMTun)](https://github.com/vahedvm72/VMTun/releases/latest)

**[Download the installer](https://github.com/vahedvm72/VMTun/releases/latest/download/VMTun-Setup.exe)** — one file, everything inside.

A small Windows tray application that turns the local SOCKS/HTTP proxy exposed by
**v2rayN / Xray** into a real system-wide tunnel — the same way WireGuard or OpenVPN work —
so that *every* process on the machine goes through it, including UWP / Microsoft Store apps
that ignore a plain proxy setting.

برنامه‌ای کوچک برای ویندوز که پروکسی محلی **v2rayN / Xray** را به یک تونل واقعی در سطح کل
سیستم تبدیل می‌کند — درست مثل WireGuard و OpenVPN — تا **تمام** ترافیک ویندوز، از جمله
اپ‌های UWP و Microsoft Store، از آن عبور کند و هیچ برنامه‌ای نتواند دورش بزند.

---

## English

### How it works

```
         ┌──────────────────────────────────────────────┐
         │  Every process on Windows                    │
         │  (browsers, games, Microsoft Store, UWP …)   │
         └───────────────────────┬──────────────────────┘
                                 │  default route + WFP filters
                                 ▼
                    ┌────────────────────────┐
                    │  VMTun adapter (Wintun)│  172.19.0.1/30
                    └───────────┬────────────┘
                                │  TCP/UDP/DNS
                                ▼
                    ┌────────────────────────┐
                    │  sing-box (tun → socks)│  ← VMTun runs and supervises this
                    └───────────┬────────────┘
                                │  127.0.0.1:10808
                                ▼
                    ┌────────────────────────┐
                    │  v2rayN / Xray         │  ── direct route ──▶  your server
                    └────────────────────────┘
```

1. A virtual **Wintun** adapter is created and becomes the machine's default route, so the
   traffic of every process is handed to it — a proxy setting can be ignored by an
   application, a default route cannot.
2. Because it is the *route* and not a proxy setting, UWP / Microsoft Store apps are covered
   automatically — they use ordinary sockets and follow the route table like everything else.
   The **kill switch** then enforces this at the firewall layer: outbound traffic is denied by
   default and only the tunnel adapter, the proxy core and the LAN are allowed, so an
   application cannot reach the physical adapter even if it tries. `strict_route` adds the
   WFP filters that stop Windows' multi-homed DNS resolution from leaking queries sideways.
3. Traffic leaving the adapter is forwarded to the SOCKS5 port that v2rayN already listens on,
   so your existing servers, subscriptions and routing inside v2rayN keep working untouched.
4. The proxy core itself (`xray.exe`, `v2rayN.exe`, …) is routed **direct**, otherwise its own
   connection to your server would be fed back into the tunnel and deadlock.
5. DNS is hijacked off the adapter and resolved through the proxy. The transport matters:
   plain UDP DNS only works if the server relays UDP, and many VLESS/VMess-over-WebSocket
   servers do not — every lookup then times out and the tunnel looks dead while TCP is fine.
   VMTun therefore sends DNS over **DoH (TCP 443)** by default, and blocks QUIC so browsers
   fall back to TCP at once. With IPv6 switched off the adapter carries no IPv6 address at
   all, so IPv6 is never routed into the tunnel and applications fall straight back to IPv4 —
   claiming the address and then refusing the traffic inside the core makes them retry in a
   tight loop instead. The Status page warns when the machine has a real IPv6 uplink that
   would therefore bypass the tunnel.
6. Once the adapter is up, VMTun makes a real unproxied request and only then reports
   *Connected and verified*, showing the exit IP. If nothing gets through, it says so.

### Requirements

| | |
|---|---|
| Windows | 10 / 11 (x64) |
| .NET | Framework 4.8 — part of Windows, nothing to install |
| v2rayN | running and connected to a server |
| sing-box | 1.12 or newer (`sing-box.exe` + `wintun.dll` in `tools\`) |
| Rights | administrator (creating an adapter and editing routes requires it) |

`build.ps1` copies `sing-box.exe` and `wintun.dll` out of an existing v2rayN installation
automatically. If you do not have v2rayN installed, drop the two files into `bin\tools\`
by hand.

### Install

Download **[VMTun-Setup.exe](https://github.com/vahedvm72/VMTun/releases/latest/download/VMTun-Setup.exe)** from the latest release and run it. It is one self-contained file: the application, the
sing-box core, `wintun.dll`, the icon and the Persian font all travel inside it
gzip-compressed, so nothing else has to be downloaded and no font has to be installed.

It installs to `C:\Program Files\VMTun` by default, makes a Start Menu entry and an
optional desktop shortcut, and registers itself under Apps and Features. Silent install:

```powershell
VMTun-Setup.exe /S /D=C:\Program Files\VMTun
```

**Uninstalling** goes through Apps and Features, or `VMTun.exe --uninstall`. It puts the
firewall back, removes the startup task and the shortcuts, and deletes the folder.

### Updates

VMTun checks this repository for a newer release once a day and offers to install it;
**Tools → Check for updates** does it on demand. The check is an ordinary unproxied
request, so it travels through the tunnel whenever the tunnel is up — which is the point,
because a direct request to GitHub is often exactly what does not work. That is also why
the automatic check runs a moment after the tunnel verifies rather than at startup.

Turn it off with **Settings → Check for updates daily**. To follow a fork instead, put
`UpdateRepo=owner/name` in `data\settings.ini`.

### Releasing

Bump `Version` in `src/Integration.cs`, then push a matching tag:

```bash
git tag v1.4 && git push origin v1.4
```

`.github/workflows/release.yml` fetches sing-box and Wintun from their publishers, builds
the installer and attaches it to the release. It refuses to build when the tag and the
version constant disagree, because a release that believes it is older than itself would
offer the same update forever.

### Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

There is no SDK, NuGet or internet dependency: it compiles with `csc.exe` from the
.NET Framework that already ships with Windows. The application lands in `bin\` and the
packaged installer in `dist\VMTun-Setup.exe`; pass `-NoInstaller` to skip the packaging
step while iterating.

**Display scaling.** The layout is written in 96-dpi design pixels and scaled at runtime from
the real screen DPI (`Ui.Px`), because WinForms performs no automatic scaling for a form built
in code. Fonts are declared in pixels for the same reason, and box heights are derived from the
measured line height so a different font cannot clip the text.

**Fonts.** Any `.ttf` in `bin\fonts\` is loaded for the process alone, so nothing has to be
installed. The Persian interface uses **B Nazanin** when it is present and falls back to Tahoma
otherwise. Addresses, ports, versions and anything else you type or compare stay in a Latin
face: B Nazanin maps the ASCII digits to Persian ones, which would turn `127.0.0.1:10808` into
`۱۲۷.۰.۰.۱:۱۰۸۰۸`.

### Use

1. Start **v2rayN** and connect to a server as usual. Leave v2rayN's **own TUN mode off** —
   two tunnel adapters would fight over the default route.
2. Start **VMTun** from the Start Menu or the desktop shortcut (it asks for elevation).
3. Press **Connect**. The **Status** page runs its checks, and the dot turns green only once a
   real request has been seen leaving through the tunnel. Amber means the adapter is up but
   traffic is not flowing — the failing check says why.

The window closes to the tray; use the tray menu to connect, disconnect or quit.

### Settings

| Setting | Meaning |
|---|---|
| **Theme** | `Dark`, `Light`, or `Auto` to follow the Windows app-theme setting. The window is rebuilt on a switch, so the title bar changes with it. |
| **Language** | Persian or English. Both switch immediately and are remembered. The layout is never mirrored — controls stay where they are and only the text changes. |
| **Host / Port / Type** | The local proxy to feed. *Detect proxy automatically* finds it by scanning the listeners owned by a known core. |
| **Full tunnel** | Everything goes through the proxy. Only the LAN and the proxy core are exempt. |
| **Full tunnel + Iran direct** | As above, but `.ir` domains plus the `geoip-ir` / `geosite-ir` rule sets go direct. The rule sets are downloaded through the proxy on first connect and cached. |
| **DNS transport** | `DoH` (default, TCP 443), `DoT` (TCP 853), `TCP` (port 53) or `UDP`. Use a TCP form unless the server relays UDP — the Status page tells you which. |
| **Block QUIC** | On by default. Rejects UDP/443 so browsers use TCP, which works even on a server with no UDP relay. |
| **Kill switch** | **Off by default.** When on, it sets the Windows Firewall default outbound action to *Block* and allows only the tunnel adapter, the proxy core, loopback, the LAN and DHCP. It is armed only after traffic has been verified, and rolled back automatically if it breaks connectivity. |
| **IPv6** | Off by default. The adapter still claims IPv6 so the core can reject it — that is what keeps IPv6 from escaping around the tunnel. Turn it on only if your server supports it. |
| **Network stack** | `gvisor` (safest, default), `system` (fastest, needs a healthy driver), `mixed`. |
| **MTU** | 9000 suits gvisor. Lower it to 1500 if you see stalls. |
| **Remote DNS** | The resolver queried through the proxy. |
| **Bypass these executables** | Extra `.exe` names routed direct, on top of the proxy cores. |

### If something goes wrong

**No internet after VMTun was force-killed.** The kill switch outlived the tunnel. Either
press **Tools → Repair network**, or run `bin\Repair-Network.cmd` as administrator, or:

```powershell
Get-NetFirewallRule -Group 'VMTun' | Remove-NetFirewallRule
Set-NetFirewallProfile -Name Domain,Private,Public -DefaultOutboundAction NotConfigured
```

VMTun also repairs this automatically the next time it starts.

**The adapter never comes up.** Check `bin\data\vmtun.log`. Almost always either `wintun.dll`
is missing from `tools\`, or the app is not elevated.

**Connected but nothing loads.** Open the **Status** page — it names the failing step. The
usual causes, in order: another VPN adapter (WireGuard / AmneziaVPN) is up and its own kill
switch is blocking VMTun; the v2rayN server itself is down, which the *Internet through the
proxy* check catches before the tunnel is even built; or DNS is on a transport the server
cannot carry, which the **DNS transport** setting fixes.

**Microsoft Store still fails.** Run **Tools → Fix Windows Store / UWP apps** once. It grants
loopback exemption to every installed package; Windows denies it by default inside the
AppContainer sandbox.

### Files

```
src\                 C# sources; Setup.cs is the installer and builds separately
fonts\               Persian UI font, copied to bin\fonts\ by the build
build.ps1            build script, uses the Windows-supplied compiler
dist\VMTun-Setup.exe the single installable file, everything packed inside
bin\VMTun.exe        the application
bin\tools\           sing-box.exe + wintun.dll
bin\fonts\           B-NAZANIN.TTF, loaded per-process (no installation needed)
bin\Repair-Network.cmd   standalone firewall recovery
bin\data\               generated config, settings.ini, vmtun.log, rule-set cache
                     (falls back to %LOCALAPPDATA%\VMTun when bin\ is not writable)
```

---

<div dir="rtl">

## فارسی

### چطور کار می‌کند

۱. یک آداپتور مجازی **Wintun** ساخته می‌شود و به مسیر پیش‌فرض (default route) سیستم تبدیل
می‌گردد. برنامه‌ها می‌توانند تنظیم پروکسی را نادیده بگیرند، اما نمی‌توانند مسیر پیش‌فرض
ویندوز را دور بزنند.

۲. چون این یک **مسیر** است و نه یک تنظیم پروکسی، اپ‌های UWP و Microsoft Store هم خودبه‌خود
پوشش داده می‌شوند؛ آن‌ها از سوکت معمولی استفاده می‌کنند و مثل بقیه از جدول مسیریابی پیروی
می‌کنند. سپس **کیل‌سوئیچ** همین را در لایه فایروال تثبیت می‌کند: خروجی به‌صورت پیش‌فرض
مسدود است و فقط آداپتور تونل، هسته پروکسی و شبکه محلی مجازند، پس هیچ برنامه‌ای حتی اگر
بخواهد هم نمی‌تواند به کارت شبکه فیزیکی برسد. `strict_route` هم فیلترهای WFP را اضافه
می‌کند تا رفتار چندمسیره DNS ویندوز باعث نشت پرس‌وجوها نشود.

۳. ترافیکی که از آداپتور خارج می‌شود به همان پورت SOCKS5 که v2rayN از قبل باز کرده تحویل
داده می‌شود؛ بنابراین سرورها، ساب‌اسکریپشن‌ها و قوانین مسیریابی داخل v2rayN دست‌نخورده
باقی می‌مانند.

۴. خود هسته پروکسی (`xray.exe`، `v2rayN.exe` و …) **مستقیم** مسیریابی می‌شود، وگرنه
ارتباط آن با سرور شما دوباره وارد تونل می‌شد و یک حلقه بسته ایجاد می‌کرد.

۵. درخواست‌های DNS از روی آداپتور ربوده و از داخل پروکسی حل می‌شوند. روش ارسال مهم است: DNS
روی UDP فقط وقتی کار می‌کند که سرور UDP را رد کند، و خیلی از سرورهای VLESS/VMess روی
WebSocket این کار را نمی‌کنند — آن وقت همه پرس‌وجوها تایم‌اوت می‌شوند و تونل مرده به نظر
می‌رسد در حالی که TCP سالم است. به همین دلیل VMTun پیش‌فرض DNS را روی **DoH (TCP 443)**
می‌فرستد و QUIC را می‌بندد. IPv6 هم توسط آداپتور گرفته و داخل هسته رد می‌شود تا از کارت
شبکه فیزیکی فرار نکند.

۶. بعد از بالا آمدن آداپتور، VMTun یک درخواست واقعی بدون پروکسی می‌فرستد و تنها آن وقت
می‌گوید «متصل و تأیید شد» و IP خروجی را نشان می‌دهد. اگر چیزی عبور نکند، همان را
صریح می‌گوید.

### پیش‌نیازها

| | |
|---|---|
| ویندوز | ۱۰ یا ۱۱، ۶۴ بیتی |
| دات‌نت | Framework 4.8 — جزئی از خود ویندوز، نیازی به نصب نیست |
| v2rayN | در حال اجرا و متصل به یک سرور |
| sing-box | نسخه ۱.۱۲ یا بالاتر (`sing-box.exe` و `wintun.dll` در پوشه `tools`) |
| دسترسی | مدیر (Administrator) |

اسکریپت `build.ps1` این دو فایل را به‌طور خودکار از روی نصب موجود v2rayN کپی می‌کند. اگر
v2rayN نصب نیست، خودتان آن‌ها را در `bin\tools\` بگذارید.

### ساخت

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

هیچ SDK، NuGet یا اینترنتی لازم نیست؛ با کامپایلر `csc.exe` که همراه خود ویندوز می‌آید
کامپایل می‌شود. خروجی در پوشه `bin` قرار می‌گیرد.

### نصب

فایل **`dist\VMTun-Setup.exe`** را اجرا کنید. همه‌چیز داخل همین یک فایل است: خود برنامه،
هسته sing-box، `wintun.dll`، آیکن و فونت فارسی — همه فشرده‌شده. نه چیزی باید دانلود شود
و نه فونتی باید نصب شود.

پیش‌فرض در `C:\Program Files\VMTun` نصب می‌شود، در منوی استارت و (اختیاری) روی دسکتاپ
میان‌بر می‌سازد و در «برنامه‌ها و قابلیت‌ها» ثبت می‌شود.

**حذف** از همان «برنامه‌ها و قابلیت‌ها» انجام می‌شود. قبل از پاک کردن، فایروال را به حالت
اول برمی‌گرداند و وظیفه راه‌اندازی خودکار را حذف می‌کند.

### به‌روزرسانی

برنامه روزی یک‌بار نسخه تازه را از همین مخزن می‌گیرد و اگر بود، می‌پرسد و نصب می‌کند.
**ابزارها ← بررسی به‌روزرسانی** هم دستی این کار را می‌کند.

درخواست بدون پروکسی فرستاده می‌شود، یعنی **از داخل خود تونل** رد می‌شود — چون جایی
که این برنامه به درد می‌خورد، معمولاً دسترسی مستقیم به گیت‌هاب کار نمی‌کند. به همین
دلیل بررسی خودکار کمی پس از تأیید شدن تونل اجرا می‌شود، نه موقع باز شدن برنامه.

برای خاموش کردن: **تنظیمات ← بررسی روزانه به‌روزرسانی**.

### استفاده

۱. **v2rayN** را اجرا کنید و طبق معمول به یک سرور وصل شوید. حالت **TUN خود v2rayN را خاموش
بگذارید** — دو آداپتور تونل سر مسیر پیش‌فرض با هم درگیر می‌شوند.

۲. فایل `bin\VMTun.exe` را اجرا کنید (خودش دسترسی مدیر می‌گیرد).

۳. دکمه **اتصال** را بزنید. صفحه **وضعیت** بررسی‌ها را اجرا می‌کند و چراغ فقط وقتی سبز
می‌شود که یک درخواست واقعی از داخل تونل رفته و برگشته باشد. نارنجی یعنی آداپتور بالا آمده
ولی ترافیک عبور نمی‌کند — همان بررسی شکست‌خورده علتش را می‌گوید.

با بستن پنجره، برنامه کنار ساعت باقی می‌ماند.

### نکات مهم

- **تم روشن و تاریک** در تنظیمات ← ظاهر انتخاب می‌شود؛ حالت **خودکار** از تم خود
  ویندوز پیروی می‌کند. زبان هم همانجاست و هر دو فوری اعمال می‌شوند. با تعویض زبان
  چینمان آینه نمی‌شود — همه دکمه‌ها سر جای خودشان می‌مانند و فقط متن ترجمه می‌شود.

- **فونت فارسی** از پوشه `bin\fonts\` خوانده می‌شود و نیازی به نصب ندارد؛ پیش‌فرض
  **B Nazanin** است و اگر نباشد Tahoma. آدرس، پورت و نسخه با فونت لاتین نوشته می‌شوند،
  چون B Nazanin ارقام انگلیسی را به فارسی تبدیل می‌کند و خواندن IP سخت می‌شود.

- **کیل‌سوئیچ** سیاست خروجی فایروال ویندوز را روی *Block* می‌گذارد و فقط آداپتور تونل،
  هسته پروکسی، loopback، شبکه محلی و DHCP را مجاز می‌کند. اگر تونل قطع شود، ترافیک
  به اتصال بی‌واسطه برنمی‌گردد.

- اگر برنامه به‌زور بسته شد و اینترنت قطع ماند: **ابزارها ← بازیابی شبکه**، یا اجرای
  `bin\Repair-Network.cmd` با دسترسی مدیر. برنامه در اجرای بعدی هم خودش این وضعیت را
  تشخیص می‌دهد و اصلاح می‌کند.

- اگر آداپتور بالا نیامد، فایل `bin\data\vmtun.log` را ببینید. تقریبا همیشه
  یا `wintun.dll` در پوشه `tools` نیست، یا برنامه با دسترسی مدیر اجرا نشده.

- اگر وصل شد ولی چیزی باز نشد: صفحه **وضعیت** دقیقا می‌گوید کدام مرحله شکست خورده.
  متداول‌ترین علت‌ها به ترتیب: یک آداپتور VPN دیگر (WireGuard / AmneziaVPN) بالاست و
  کیل‌سوئیچ خودش ترافیک VMTun را می‌بندد؛ خود سرور v2rayN کار نمی‌کند؛ یا DNS روی
  روشی است که سرور رد نمی‌کند که با تنظیم **روش DNS** حل می‌شود.

- اگر Microsoft Store هنوز مشکل داشت، یک بار **ابزارها ← رفع محدودیت اپ‌های ویندوز** را
  بزنید.

</div>

---

## Licence / مجوز

The application code here is yours to use and modify freely. It bundles two third-party
binaries at build time, each under its own licence: **sing-box** (SagerNet) and
**Wintun** (WireGuard LLC). Neither is redistributed in this repository.
