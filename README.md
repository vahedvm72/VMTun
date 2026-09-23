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


### Privacy checks

A tunnel moves the packets; it does not move the computer. The **Privacy** page puts what the
outside world can read next to what the exit address claims, and a disagreement is the finding:

| Check | What it compares |
|---|---|
| Clock and time zone | the zone your browser reports vs. the exit country's |
| Windows home region | the country Windows was set up with vs. the exit country |
| Reverse DNS | the PTR name attached to the exit address — a personal name there identifies you |
| DNS leak | which resolver actually reached the authoritative server, not what the adapter is set to |
| Global IPv6 | an address that would bypass an IPv4-only tunnel |

One optional switch acts on the findings: **match the Windows time zone to the exit country
while connected**. It is off by default, it is put back on disconnect, and a killed app is
repaired on the next run or by `Repair-Network.cmd`. The browser layer — WebRTC, canvas, the
font list — is outside any tunnel's reach and is reported as such rather than papered over.


### Requirements

| | |
|---|---|
| Windows | 10 / 11 (x64) |
| .NET | Framework 4.8 — part of Windows, nothing to install |
| v2rayN | running and connected to a server |
| sing-box | 1.12 or newer (`sing-box.exe` + `wintun.dll` in `tools\`) |
| Rights | administrator (creating an adapter and editing routes requires it) |




<div dir="rtl">

### نکات مهم


- اگر برنامه به‌زور بسته شد و اینترنت قطع ماند: **ابزارها ← بازیابی شبکه**، یا اجرای
  `bin\Repair-Network.cmd` با دسترسی مدیر. برنامه در اجرای بعدی هم خودش این وضعیت را
  تشخیص می‌دهد و اصلاح می‌کند.

- اگر وصل شد ولی چیزی باز نشد: صفحه **وضعیت** دقیقا می‌گوید کدام مرحله شکست خورده.
  متداول‌ترین علت‌ها به ترتیب: یک آداپتور VPN دیگر (WireGuard / AmneziaVPN) بالاست و
  کیل‌سوئیچ خودش ترافیک VMTun را می‌بندد؛ خود سرور v2rayN کار نمی‌کند؛ یا DNS روی
  روشی است که سرور رد نمی‌کند که با تنظیم **روش DNS** حل می‌شود.

- اگر Microsoft Store هنوز مشکل داشت، یک بار **ابزارها ← رفع محدودیت اپ‌های ویندوز** را
  بزنید.

</div>

---

## Licence / مجوز

VMTun is released under the MIT licence — see [LICENSE](LICENSE).

It builds against two third-party binaries that carry their own terms and are **not**
stored here; the build fetches them from their publishers:
[sing-box](https://github.com/SagerNet/sing-box) (SagerNet) and
[Wintun](https://www.wintun.net) (WireGuard LLC).
The Persian interface font in `fonts/` is not covered by the MIT licence.
