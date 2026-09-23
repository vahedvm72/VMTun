# Collects everything needed to diagnose a VMTun leak report, into one text file.
#
#   Right-click -> Run with PowerShell,  or:
#   powershell -ExecutionPolicy Bypass -File .\Collect-Diagnostics.ps1
#
# Read-only. It changes nothing on the machine; it reads state and asks three public
# services what they see. Run it WHILE CONNECTED, or the answers describe nothing useful.
#
# The file lands on the Desktop as VMTun-diagnostics-<date>.txt.

[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = 'Tls12'

if (-not $OutFile) {
    $stamp = Get-Date -Format 'yyyy-MM-dd-HHmm'
    $OutFile = Join-Path ([Environment]::GetFolderPath('Desktop')) "VMTun-diagnostics-$stamp.txt"
}

$out = New-Object Collections.Generic.List[string]
function Say($line) { $out.Add($line); Write-Host $line }
function Head($t) { Say ''; Say ('=' * 70); Say $t; Say ('=' * 70) }

Head "VMTun diagnostics  -  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"

# ---------------------------------------------------------------- the app
Head 'VMTun'
$install = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VMTun').InstallLocation
Say "installed version : $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VMTun').DisplayVersion)"
Say "install location  : $install"
Say "running           : $([bool](Get-Process VMTun))"
Say "core running      : $([bool](Get-Process sing-box))"

$data = Join-Path $install 'data'
Say ''
Say 'settings:'
Get-Content (Join-Path $data 'settings.ini') | Where-Object { $_ -and $_ -notmatch '^#' } | ForEach-Object { Say "  $_" }

Say ''
Say 'state files still applied (each means something is NOT restored):'
foreach ($f in 'timezone.state','region.state','ipv6.state','guard.state') {
    $p = Join-Path $data $f
    if (Test-Path $p) { Say "  $f  ->  $((Get-Content $p) -join ', ')" }
}
if (-not (Get-ChildItem $data -Filter *.state)) { Say '  (none - clean)' }

# ---------------------------------------------------------------- proxy chain
Head 'Proxy / VPN processes'
Get-Process | Where-Object { $_.ProcessName -match 'xray|v2ray|sing-box|mihomo|clash|Amnezia|wireguard|openvpn' } |
    Select-Object -Expand ProcessName -Unique | Sort-Object | ForEach-Object { Say "  $_" }

Head 'Adapters and routing'
Get-NetAdapter | Where-Object Status -eq 'Up' |
    ForEach-Object { Say ("  UP   {0,-28} {1}" -f $_.Name, $_.InterfaceDescription) }
Say ''
Say 'default routes (lowest metric wins - this is what un-tunnelled traffic follows):'
Get-NetRoute -DestinationPrefix '0.0.0.0/0' | Sort-Object RouteMetric |
    ForEach-Object { Say ("  metric {0,-6} {1,-24} next hop {2}" -f $_.RouteMetric, $_.InterfaceAlias, $_.NextHop) }
Say ''
Say 'IPv6 binding per adapter (Enabled=True is a way around an IPv4 tunnel):'
Get-NetAdapterBinding -ComponentID ms_tcpip6 |
    ForEach-Object { Say ("  {0,-28} {1}" -f $_.Name, $_.Enabled) }
Say ''
Say 'routable IPv6 addresses (link-local fe80:: and unique-local fd:: do not count):'
$v6 = Get-NetIPAddress -AddressFamily IPv6 | Where-Object { $_.IPAddress -notmatch '^(fe80|fd|::1)' }
if ($v6) { $v6 | ForEach-Object { Say "  $($_.InterfaceAlias)  $($_.IPAddress)" } } else { Say '  (none)' }

# ---------------------------------------------------------------- identity
Head 'Clock and region'
Say "windows time zone : $(tzutil /g)"
Say "current offset    : $((Get-Date).ToString('zzz'))"
Say "home region (Geo) : $((Get-ItemProperty 'HKCU:\Control Panel\International\Geo').Name)"
Say "UI culture        : $((Get-UICulture).Name)   culture: $((Get-Culture).Name)"

# ---------------------------------------------------------------- what the world sees
Head 'What the outside world sees'

function Get-Json($url) {
    try {
        $c = New-Object Net.WebClient
        $c.Headers.Add('User-Agent', 'VMTun-diagnostics')
        return $c.DownloadString($url) | ConvertFrom-Json
    } catch { return $null }
}

$ip = Get-Json 'https://ipwho.is/'
if ($ip -and $ip.success) {
    Say "HTTPS exit        : $($ip.ip)"
    Say "  country         : $($ip.country) ($($ip.country_code))   city: $($ip.city)"
    Say "  isp             : $($ip.connection.isp)"
    Say "  time zone       : $($ip.timezone.id)  ($($ip.timezone.utc))"
} else { Say 'HTTPS exit        : lookup FAILED (no internet, or the service is blocked)' }

$edns = Get-Json 'https://edns.ip-api.com/json'
if ($edns) { Say "DNS resolver seen : $($edns.dns.geo)   ($($edns.dns.ip))" }
else       { Say 'DNS resolver seen : lookup FAILED' }

# The same question a browser asks before it publishes a WebRTC candidate.
function Get-StunAddress([string]$server, [int]$port) {
    $udp = New-Object Net.Sockets.UdpClient
    $udp.Client.ReceiveTimeout = 4000
    try {
        $req = New-Object byte[] 20
        $req[0] = 0x00; $req[1] = 0x01
        [Array]::Copy([byte[]](0x21,0x12,0xA4,0x42), 0, $req, 4, 4)
        $rng = New-Object byte[] 12
        (New-Object Random).NextBytes($rng)
        [Array]::Copy($rng, 0, $req, 8, 12)
        $udp.Connect($server, $port)
        [void]$udp.Send($req, $req.Length)
        $ep = New-Object Net.IPEndPoint([Net.IPAddress]::Any, 0)
        $resp = $udp.Receive([ref]$ep)
        $i = 20
        while ($i + 4 -le $resp.Length) {
            $type = ($resp[$i] -shl 8) -bor $resp[$i+1]
            $len  = ($resp[$i+2] -shl 8) -bor $resp[$i+3]
            $v = $i + 4
            if ($type -eq 0x0020 -and $resp[$v+1] -eq 0x01) {
                $b = @(); for ($k=0; $k -lt 4; $k++) { $b += ($resp[$v+4+$k] -bxor $req[4+$k]) }
                return ($b -join '.')
            }
            $i = $v + $len + ((4 - ($len % 4)) % 4)
        }
        return $null
    } catch { return $null } finally { $udp.Close() }
}

# Several servers, not one. They do not have to agree: a real machine was seen answering
# with the tunnel's address to Google and with the subscriber's own to three others, so
# stopping at the first reply reports an all-clear over a live leak.
$stunServers = @(
    @('stun.l.google.com', 19302),
    @('stun.cloudflare.com', 3478),
    @('stun.nextcloud.com', 3478),
    @('stun.chat.bilibili.com', 3478),
    @('stun.miwifi.com', 3478),
    @('stun.qq.com', 3478)
)

Say ''
Say 'UDP / WebRTC - what each STUN server sees:'
$seen = @{}
foreach ($srv in $stunServers) {
    $addr = Get-StunAddress $srv[0] $srv[1]
    if ($addr) {
        Say ("  {0,-26} {1}" -f $srv[0], $addr)
        if (-not $seen.ContainsKey($addr)) { $seen[$addr] = @() }
        $seen[$addr] += $srv[0]
    } else {
        Say ("  {0,-26} (no answer)" -f $srv[0])
    }
}

Say ''
if ($seen.Count -eq 0) {
    Say '  VERDICT : no STUN server answered - UDP does not leave this machine (safe)'
} elseif ($ip -and $ip.success) {
    $stray = $seen.Keys | Where-Object { $_ -ne $ip.ip }
    if (-not $stray) {
        Say "  VERDICT : every answer matches the HTTPS exit ($($ip.ip)) - no WebRTC leak"
    } else {
        Say '  VERDICT : *** LEAK - these addresses are NOT the HTTPS exit ***'
        foreach ($a in $stray) {
            $who = Get-Json "https://ipwho.is/$a"
            $where = if ($who -and $who.success) { "$($who.country) / $($who.connection.isp)" } else { '?' }
            Say "            $a  via $($seen[$a] -join ', ')   [$where]"
        }
        Say "            HTTPS exit is $($ip.ip)"
        Say '            Servers disagreeing with each other means some destinations leave the'
        Say '            tunnel and others do not - include this whole file when reporting it.'
    }
} else {
    Say ('  addresses seen: ' + ($seen.Keys -join ', ') + '  (no HTTPS exit to compare against)')
}

# ---------------------------------------------------------------- logs
Head 'VMTun log (last 80 lines, core chatter removed)'
# Filter first, then take the last 80. Tailing first and filtering after left this section
# empty whenever verbose core logging was on, which is exactly when it is being read.
Get-Content (Join-Path $data 'vmtun.log') |
    Where-Object { $_ -notmatch '\[CORE\]' } | Select-Object -Last 80 | ForEach-Object { Say $_ }

Head 'Core routing decisions (which outbound each flow took)'
$core = Get-Content (Join-Path $data 'vmtun.log') |
    Where-Object { $_ -match 'outbound/|found process path|inbound packet connection to' } |
    Select-Object -Last 120
if ($core) { $core | ForEach-Object { Say $_ } }
else { Say '  (none - turn on "Verbose core log" in Settings and reproduce to capture these)' }

Head 'Installer log'
$setupLog = Join-Path $env:TEMP 'VMTun-Setup.log'
if (Test-Path $setupLog) { Get-Content $setupLog -Tail 20 | ForEach-Object { Say $_ } } else { Say '  (none)' }

# ---------------------------------------------------------------- done
$out | Set-Content -Path $OutFile -Encoding UTF8
Write-Host ''
Write-Host "Saved to: $OutFile" -ForegroundColor Green
Write-Host 'Send that one file back. It contains no passwords and no browsing history.' -ForegroundColor DarkGray
