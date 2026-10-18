<#
  Compiles the WolfTeam Dirilis bootstrap (WolfTeamDirilis.exe: launcher\bootstrap\*.cs + launcher\csharp\BootLayout.cs)
  and the launcher (WolfTeamLauncher.exe: launcher\csharp\*.cs) with the .NET Framework csc.exe that ships with Windows
  (C# 5, no NuGet, no SDK) and optionally installs both into a client package folder (layout: launcher\csharp\BootLayout.cs).
  ops\build-setup.ps1 runs the tests, calls this script and builds the installer WolfTeamDirilis-Setup.exe.

  usage:
    powershell -File ops\build-launcher.ps1 -AssetDir C:\tools\re\dist\launcher-assets
        [-OutDir C:\tools\re\dist\launcher-build] [-PackageDir C:\tools\re\dist\WolfTeam]
        [-SecretFile C:\tools\secrets\wolfteam-dl.txt] [-ServerHost 188.40.176.195] [-EndpointsFile <file>]

  -AssetDir      folder with bg.png and wt.ico (made by ops\make-launcher-assets.ps1; client-derived, never in git)
  -PackageDir    copy launcher\WolfTeamLauncher.exe and bg.png there (the release package the manifest is made from); the
                 package's WolfTeamDirilis.exe stays the 1.2 launcher (1.2 players migrate with setup 1.4 only)
  -SecretFile    with -PackageDir: write WolfTeamDirilis.ini (UpdateBaseUrl/NewsUrl contain the download secret; the secret is
                 read from DL_TOKEN=... and never printed). An existing WolfTeamDirilis.ini is kept unless -OverwriteConfig.
  -ServerHost    the game server written into that WolfTeamDirilis.ini (188.40.176.195 since 2026-10-02; it was
                 45.76.145.60 until then, which is refused now).
  -EndpointsFile with -PackageDir: lines LoginUrl=, UpdateBaseUrl=, NewsUrl= (https, each optional; security
                 audit R5; the values hold secret paths and are never printed) and ServerHost=<host or IPv4> (optional,
                 launcher 1.6.1+: wins over the one in WolfTeamDirilis.ini) -> <package>\launcher\endpoints.ini
#>
param(
    [Parameter(Mandatory = $true)][string]$AssetDir,
    [string]$OutDir = 'C:\tools\re\dist\launcher-build',
    [string]$PackageDir = '',
    [string]$SecretFile = '',
    [string]$ServerHost = '188.40.176.195',
    [int]$StaticPort = 8080,
    [string]$EndpointsFile = '',
    [switch]$AllowHttpLogin,
    [switch]$OverwriteConfig
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'endpoint-url.ps1')
if (-not (Test-ServerHost $ServerHost)) { throw '-ServerHost is not a valid, current host name or IPv4 address (value not shown)' }
$src =Join-Path $repo 'launcher\csharp'
$bootSrcDir = Join-Path $repo 'launcher\bootstrap'
# Only this compiler is allowed; there is deliberately no fallback to the 32-bit Framework folder.
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw "required compiler not found: $csc" }

$icon = Join-Path $AssetDir 'wt.ico'
$bg = Join-Path $AssetDir 'bg.png'
foreach ($f in @($icon, $bg)) { if (-not (Test-Path -LiteralPath $f)) { throw "missing asset: $f (run ops\make-launcher-assets.ps1)" } }
# App manifest (asInvoker + supportedOS): declares the real per-user privilege level so the shipped exes are not
# flagged by the installer-detection heuristic, and an unsigned build carries a proper manifest (AV reputation).
$manifest = Join-Path $repo 'launcher\package\app.manifest'
if (-not (Test-Path -LiteralPath $manifest -PathType Leaf)) { throw "missing manifest: $manifest" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$common = @('/nologo', '/target:winexe', '/platform:anycpu32bitpreferred', '/optimize+', '/codepage:65001', '/warnaserror+',
    '/langversion:5', "/win32icon:$icon", "/win32manifest:$manifest", '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll',
    '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')

$launcher = Join-Path $OutDir 'WolfTeamLauncher.exe'
$sources = Get-ChildItem -LiteralPath $src -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
& $csc @($common + @("/out:$launcher") + $sources)
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }

$boot = Join-Path $OutDir 'WolfTeamDirilis.exe'
$bootSources = @((Join-Path $src 'BootLayout.cs')) + (Get-ChildItem -LiteralPath $bootSrcDir -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
& $csc @($common + @("/out:$boot") + $bootSources)
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Copy-Item -LiteralPath $bg -Destination (Join-Path $OutDir 'bg.png') -Force
Write-Output "built $boot"
Write-Output "built $launcher"

if ($PackageDir) {
    if (-not (Test-Path -LiteralPath (Join-Path $PackageDir 'Wolfteam.bin'))) { throw "not a client package (no Wolfteam.bin): $PackageDir" }
    foreach ($n in @('WolfTeamDirilis', 'WolfTeamLauncher')) {
        if (Get-Process -Name $n -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($PackageDir, [StringComparison]::OrdinalIgnoreCase) }) {
            throw "the launcher is running from $PackageDir; close it first"
        }
    }
    $launcherDir = Join-Path $PackageDir 'launcher'
    New-Item -ItemType Directory -Force -Path $launcherDir | Out-Null
    # The package's WolfTeamDirilis.exe stays the 1.2 launcher: 1.2 players keep updating with it and never replace
    # themselves with the bootstrap (that move is not crash-safe; they migrate with setup 1.4 instead). 1.4 installs
    # never update that file (the bootstrap is the setup's). The bootstrap is shipped only inside the setup.
    $pkgExe = Join-Path $PackageDir 'WolfTeamDirilis.exe'
    if ((Test-Path -LiteralPath $pkgExe) -and ((Get-Item -LiteralPath $pkgExe).VersionInfo.FileDescription -cmatch '^WolfTeam Dirili.$')) {
        throw 'the package WolfTeamDirilis.exe is a 1.4 bootstrap: 1.2 players would replace themselves with it; put the 1.2 launcher back'
    }
    Copy-Item -LiteralPath $launcher -Destination (Join-Path $launcherDir 'WolfTeamLauncher.exe') -Force
    Copy-Item -LiteralPath $bg -Destination (Join-Path $PackageDir 'bg.png') -Force
    # Players start the launcher from the shortcut the installer made (WolfTeamDirilis-Setup.exe); OYNA.bat and the
    # PowerShell launcher are not part of the release any more (tools/release-set.txt).
    $ini = Join-Path $PackageDir 'WolfTeamDirilis.ini'
    if ($SecretFile -and ($OverwriteConfig -or -not (Test-Path -LiteralPath $ini))) {
        $line = Get-Content -LiteralPath $SecretFile | Where-Object { $_ -match '^\s*DL_TOKEN\s*=' } | Select-Object -First 1
        if (-not $line) { throw "DL_TOKEN=... not found in the secret file" }
        $token = ($line -split '=', 2)[1].Trim()
        if ($token -notmatch '^[A-Za-z0-9_-]+$') { throw 'DL_TOKEN has unexpected characters' }
        $base = "http://${ServerHost}:$StaticPort/$token/"
        $text = "; WolfTeam Dirilis launcher settings`r`n" +
                "ServerHost=$ServerHost`r`n" +
                "LoginUrl=http://$ServerHost/api/login.php`r`n" +
                "UpdateBaseUrl=${base}client/`r`n" +
                "NewsUrl=${base}news.json`r`n" +
                "HomeUrl=`r`nSupportUrl=`r`n"
        [System.IO.File]::WriteAllText($ini, $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Output 'WolfTeamDirilis.ini written (download secret not shown)'
    }
    if ($EndpointsFile) {
        $lines = @()
        # Each key is optional: e.g. LoginUrl only once the HTTPS login route is live (security audit R5 steps 1-2);
        # a key that is left out keeps the WolfTeamDirilis.ini address.
        foreach ($key in @('LoginUrl', 'UpdateBaseUrl', 'NewsUrl')) {
            $l = Get-Content -LiteralPath $EndpointsFile | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1
            if (-not $l) { continue }
            $v = ($l -split '=', 2)[1].Trim()
            if (-not (Test-HttpsEndpointUrl $v)) { throw "$key is not a plain, valid https URL (value not shown)" }
            $lines += "$key=$v"
        }
        # ServerHost (launcher 1.6.1+) is a host name or IPv4 address, not a URL.
        $l = Get-Content -LiteralPath $EndpointsFile | Where-Object { $_ -match '^\s*ServerHost\s*=' } | Select-Object -First 1
        if ($l) {
            $v = ($l -split '=', 2)[1].Trim()
            if (-not (Test-ServerHost $v)) { throw 'ServerHost in the endpoints file is not a valid, current host name or IPv4 address (value not shown)' }
            $lines += "ServerHost=$v"
        }
        if (-not ($lines -match '^UpdateBaseUrl=')) { throw 'UpdateBaseUrl (https) missing in the endpoints file' }
        if (-not ($lines -match '^LoginUrl=') -and -not $AllowHttpLogin) {
            throw 'LoginUrl (https) missing in the endpoints file; -AllowHttpLogin ships the consent-only http login'
        }
        $text = "; HTTPS addresses of the WolfTeam Dirilis launcher (security audit R5). Shipped with the release.`r`n" + ($lines -join "`r`n") + "`r`n"
        [System.IO.File]::WriteAllText((Join-Path $launcherDir 'endpoints.ini'), $text, (New-Object System.Text.UTF8Encoding($false)))
        Write-Output 'launcher\endpoints.ini written (addresses not shown)'
    }
    # Validate what is actually shipped (a fresh or a retained endpoints.ini): https updates, and an https login unless
    # the consent-only http login is explicitly allowed.
    $shipped = Join-Path $launcherDir 'endpoints.ini'
    if (-not (Test-Path -LiteralPath $shipped)) { throw 'the package has no launcher\endpoints.ini; pass -EndpointsFile' }
    $kv = @(Get-Content -LiteralPath $shipped | Where-Object { $_ -match '^\s*(LoginUrl|UpdateBaseUrl|NewsUrl)\s*=' })
    foreach ($l in $kv) {
        if (-not (Test-HttpsEndpointUrl (($l -split '=', 2)[1].Trim()))) { throw 'the shipped endpoints.ini has an address that is not a plain, valid https URL (value not shown)' }
    }
    foreach ($l in @(Get-Content -LiteralPath $shipped | Where-Object { $_ -match '^\s*ServerHost\s*=' })) {
        if (-not (Test-ServerHost (($l -split '=', 2)[1].Trim()))) { throw 'the shipped endpoints.ini has a ServerHost that is not a valid, current host (value not shown)' }
    }
    if (-not ($kv -match '^\s*UpdateBaseUrl\s*=')) { throw 'the shipped endpoints.ini has no UpdateBaseUrl' }
    if (-not ($kv -match '^\s*LoginUrl\s*=') -and -not $AllowHttpLogin) {
        throw 'the shipped endpoints.ini has no https LoginUrl; -AllowHttpLogin ships the consent-only http login'
    }
    Write-Output "installed into $PackageDir"
}
