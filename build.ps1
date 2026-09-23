# Builds VMTun.exe with the C# compiler that ships with Windows.
# No SDK, no NuGet, no internet access required.
#
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
#
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path $PSScriptRoot 'bin'),
    [switch]$SkipTools,
    [switch]$NoInstaller
)

$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'src'

function Fail($message) {
    Write-Host "BUILD FAILED: $message" -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------- compiler
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) { Fail 'csc.exe not found. Install the .NET Framework 4.x runtime.' }
Write-Host "Compiler: $csc" -ForegroundColor DarkGray

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

# ---------------------------------------------------------------- icon
$icon = Join-Path $OutDir 'VMTun.ico'
if (-not (Test-Path $icon)) {
    Write-Host 'Generating icon...' -ForegroundColor DarkGray
    & (Join-Path $src 'make-icon.ps1') -OutPath $icon | Out-Null
}

# ---------------------------------------------------------------- compile
# Setup.cs has its own entry point and is built separately, into the installer.
$sources = Get-ChildItem -Path $src -Filter *.cs |
           Where-Object { $_.Name -ne 'Setup.cs' } |
           ForEach-Object { $_.FullName }
if ($sources.Count -eq 0) { Fail "No .cs files under $src" }

$exe = Join-Path $OutDir 'VMTun.exe'
$manifest = Join-Path $src 'app.manifest'

# Fonts are embedded as resources as well as copied beside the exe, so a lone VMTun.exe
# carries its own Persian face.
$embedded = @()
$fontSrc = Join-Path $PSScriptRoot 'fonts'
if (Test-Path $fontSrc) {
    foreach ($f in Get-ChildItem $fontSrc -Filter *.ttf) {
        $embedded += "/resource:$($f.FullName),$($f.Name)"
    }
}

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:3'
    "/out:$exe"
    "/win32icon:$icon"
    "/win32manifest:$manifest"
    '/reference:System.dll'
    '/reference:System.Core.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Web.Extensions.dll'
)
$cscArgs += $embedded
$cscArgs += $sources

Write-Host "Compiling $($sources.Count) source files..." -ForegroundColor DarkGray
$output = & $csc @cscArgs 2>&1
$exitCode = $LASTEXITCODE
$output | ForEach-Object {
    $line = "$_"
    if ($line -match 'error CS') { Write-Host $line -ForegroundColor Red }
    elseif ($line -match 'warning CS') { Write-Host $line -ForegroundColor Yellow }
    elseif ($line.Trim()) { Write-Host $line }
}
if ($exitCode -ne 0) { Fail "csc.exe returned $exitCode" }

# ---------------------------------------------------------------- tools
if (-not $SkipTools) {
    $toolsDir = Join-Path $OutDir 'tools'
    if (-not (Test-Path $toolsDir)) { New-Item -ItemType Directory -Path $toolsDir -Force | Out-Null }

    $roots = @($env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:LOCALAPPDATA, $env:APPDATA) |
             Where-Object { $_ -and (Test-Path $_) }

    function Copy-Tool([string[]]$relatives, [string]$destName) {
        $dest = Join-Path $toolsDir $destName
        if (Test-Path $dest) { Write-Host "  $destName already present" -ForegroundColor DarkGray; return $true }
        foreach ($root in $roots) {
            foreach ($rel in $relatives) {
                $candidate = Join-Path $root $rel
                if (Test-Path $candidate) {
                    Copy-Item $candidate $dest -Force
                    Write-Host "  $destName  <-  $candidate" -ForegroundColor DarkGray
                    return $true
                }
            }
        }
        Write-Host "  $destName NOT FOUND - copy it into $toolsDir by hand" -ForegroundColor Yellow
        return $false
    }

    Write-Host 'Collecting runtime tools...' -ForegroundColor DarkGray
    Copy-Tool @('v2rayN\bin\sing_box\sing-box.exe', 'v2rayN\bin\sing-box\sing-box.exe') 'sing-box.exe' | Out-Null
    Copy-Tool @('v2rayN\bin\xray\wintun.dll', 'v2rayN\bin\sing_box\wintun.dll', 'v2rayN\bin\mihomo\wintun.dll') 'wintun.dll' | Out-Null
}

# ---------------------------------------------------------------- fonts
# Shipped next to the exe and loaded for this process only, so nothing has to be installed.
$fontSrc = Join-Path $PSScriptRoot 'fonts'
if (Test-Path $fontSrc) {
    $fontDest = Join-Path $OutDir 'fonts'
    if (-not (Test-Path $fontDest)) { New-Item -ItemType Directory -Path $fontDest -Force | Out-Null }
    foreach ($f in Get-ChildItem $fontSrc -Filter *.ttf) {
        Copy-Item $f.FullName (Join-Path $fontDest $f.Name) -Force
        Write-Host "  font: $($f.Name)" -ForegroundColor DarkGray
    }
}

# ---------------------------------------------------------------- repair helper
$repair = Join-Path $OutDir 'Repair-Network.cmd'
$repairBody = @'
@echo off
REM Undoes everything VMTun applies to Windows, if it was killed before it could:
REM the firewall kill switch, the time zone, the home region and the IPv6 bindings.
REM Run this as administrator.
net session >nul 2>&1
if errorlevel 1 (
    echo Right-click this file and choose "Run as administrator".
    pause
    exit /b 1
)
echo Removing VMTun firewall rules and restoring the outbound policy...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-NetFirewallRule -Group 'VMTun' -ErrorAction SilentlyContinue | Remove-NetFirewallRule -ErrorAction SilentlyContinue; Set-NetFirewallProfile -Name Domain,Private,Public -Enabled True -DefaultOutboundAction NotConfigured -ErrorAction SilentlyContinue"
taskkill /F /IM sing-box.exe >nul 2>&1

REM Everything below is undone from the state files VMTun writes BEFORE it changes
REM anything, so a killed app can never leave one of these applied.

set "VMDATA=%~dp0data"

if exist "%VMDATA%\timezone.state" (
    for /f "usebackq delims=" %%Z in ("%VMDATA%\timezone.state") do (
        echo Restoring the time zone to %%Z ...
        tzutil /s "%%Z"
    )
    del /q "%VMDATA%\timezone.state"
)

if exist "%VMDATA%\region.state" (
    for /f "usebackq delims=" %%G in ("%VMDATA%\region.state") do (
        echo Restoring the Windows home region to geo id %%G ...
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Set-WinHomeLocation -GeoId %%G -ErrorAction SilentlyContinue"
    )
    del /q "%VMDATA%\region.state"
)

if exist "%VMDATA%\ipv6.state" (
    echo Re-enabling IPv6 on the adapters VMTun switched it off for ...
    for /f "usebackq delims=" %%A in ("%VMDATA%\ipv6.state") do (
        powershell -NoProfile -ExecutionPolicy Bypass -Command "Enable-NetAdapterBinding -Name '%%A' -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue"
    )
    del /q "%VMDATA%\ipv6.state"
)

echo.
echo Done. Your internet connection should work normally again.
pause
'@
Set-Content -Path $repair -Value $repairBody -Encoding ASCII

$size = [math]::Round((Get-Item $exe).Length / 1KB, 1)
Write-Host ''
Write-Host "Built: $exe  ($size KB)" -ForegroundColor Green
Write-Host "Run it as administrator, or right-click -> Run as administrator." -ForegroundColor DarkGray

# ---------------------------------------------------------------- installer
# One self-contained file: the app, the core, wintun, the icon and the font, each stored
# gzip-compressed as a resource and unpacked by Setup.cs at install time.
if (-not $NoInstaller) {
    Write-Host ''
    Write-Host 'Packaging the installer...' -ForegroundColor DarkGray

    $dist = Join-Path $PSScriptRoot 'dist'
    $stage = Join-Path $dist 'payload'
    foreach ($d in @($dist, $stage)) {
        if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    }

    # published path -> file on disk
    $payload = [ordered]@{}
    $payload['VMTun.exe'] = $exe
    $payload['VMTun.ico'] = $icon
    foreach ($f in Get-ChildItem (Join-Path $OutDir 'tools') -File -ErrorAction SilentlyContinue) {
        $payload["tools/$($f.Name)"] = $f.FullName
    }
    foreach ($f in Get-ChildItem (Join-Path $OutDir 'fonts') -File -ErrorAction SilentlyContinue) {
        $payload["fonts/$($f.Name)"] = $f.FullName
    }
    $repairCmd = Join-Path $OutDir 'Repair-Network.cmd'
    if (Test-Path $repairCmd) { $payload['Repair-Network.cmd'] = $repairCmd }

    $setupRes = @()
    $rawTotal = 0
    foreach ($name in $payload.Keys) {
        $source = $payload[$name]
        if (-not (Test-Path $source)) { Fail "Payload file missing: $source" }
        $gzPath = Join-Path $stage (($name -replace '[\\/]', '_') + '.gz')
        $bytes = [System.IO.File]::ReadAllBytes($source)
        $rawTotal += $bytes.Length
        $out = [System.IO.File]::Create($gzPath)
        $gz = New-Object System.IO.Compression.GZipStream($out, [System.IO.Compression.CompressionMode]::Compress)
        $gz.Write($bytes, 0, $bytes.Length)
        $gz.Close(); $out.Close()
        $setupRes += "/resource:$gzPath,payload/$name"
        Write-Host ("  {0,-24} {1,9:N0} -> {2,8:N0}" -f $name, $bytes.Length, (Get-Item $gzPath).Length) -ForegroundColor DarkGray
    }

    # The font is embedded uncompressed as well, so the installer's own UI can use it.
    foreach ($f in Get-ChildItem $fontSrc -Filter *.ttf -ErrorAction SilentlyContinue) {
        $setupRes += "/resource:$($f.FullName),$($f.Name)"
    }

    $setupExe = Join-Path $dist 'VMTun-Setup.exe'
    $setupSources = @(
        (Join-Path $src 'Core.cs')
        (Join-Path $src 'Theme.cs')
        (Join-Path $src 'Integration.cs')
        (Join-Path $src 'Setup.cs')
    )
    $setupArgs = @(
        '/nologo'
        '/target:winexe'
        '/platform:anycpu'
        '/optimize+'
        '/warn:3'
        "/out:$setupExe"
        "/win32icon:$icon"
        "/win32manifest:$manifest"
        '/reference:System.dll'
        '/reference:System.Core.dll'
        '/reference:System.Drawing.dll'
        '/reference:System.Windows.Forms.dll'
    ) + $setupRes + $setupSources

    $setupOut = & $csc @setupArgs 2>&1
    $setupCode = $LASTEXITCODE
    $setupOut | ForEach-Object {
        $line = "$_"
        if ($line -match 'error CS') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match 'warning CS') { Write-Host $line -ForegroundColor Yellow }
        elseif ($line.Trim()) { Write-Host $line }
    }
    if ($setupCode -ne 0) { Fail "installer compile returned $setupCode" }

    Remove-Item $stage -Recurse -Force
    $setupSize = [math]::Round((Get-Item $setupExe).Length / 1MB, 1)
    Write-Host ''
    Write-Host "Installer: $setupExe  ($setupSize MB, packs $([math]::Round($rawTotal/1MB,1)) MB)" -ForegroundColor Green
}
