<#
  Builds the player installer WolfTeamDirilis-Setup.exe and the launcher WolfTeamDirilis.exe with the .NET Framework
  csc.exe that ships with Windows (C# 5, no NuGet, no SDK, no installer toolkit), after running both test exes.

  usage:
    powershell -File ops\build-setup.ps1 [-AssetDir C:\tools\re\dist\launcher-assets]
        [-IniFile C:\tools\re\dist\WolfTeam\WolfTeamDirilis.ini] [-OutDir C:\tools\re\out\setup] [-TestsOnly]

  -IniFile   the players' launcher settings (download address with the secret). Read only, embedded into the setup;
             never printed and never in git.
  -AssetDir  bg.png + wt.ico (client-derived, never in git; ops\make-launcher-assets.ps1).
  Output in -OutDir: WolfTeamDirilis-Setup.exe, WolfTeamDirilis.exe, SHA256SUMS.
  Screenshots: <build>\LauncherTests.exe --screenshots <dir>, <build>\SetupTests.exe --screenshots <dir> (off-screen).
  Smoke test (silent install/uninstall into a temp folder): powershell -File ops\test-setup-smoke.ps1
#>
param(
    [string]$AssetDir = 'C:\tools\re\dist\launcher-assets',
    [string]$IniFile = 'C:\tools\re\dist\WolfTeam\WolfTeamDirilis.ini',
    [string]$OutDir = 'C:\tools\re\out\setup',
    [string]$BuildDir = (Join-Path $env:TEMP 'wolfteam-setup-build'),
    # launcher\endpoints.ini for the setup (https UpdateBaseUrl required, LoginUrl/NewsUrl optional; never printed). The
    # launcher updates only over HTTPS, so a setup without it could not update. An optional ServerHost=<host or IPv4>
    # line (launcher 1.6.1+) sets the game server the status badge probes.
    [string]$EndpointsFile = '',
    # Ship without an https LoginUrl (the login then asks every player before sending the password over http). Only
    # until the HTTPS login route is live (security audit R5).
    [switch]$AllowHttpLogin,
    [switch]$TestsOnly
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'endpoint-url.ps1')
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) { throw "required compiler not found: $csc" }
New-Item -ItemType Directory -Force -Path $BuildDir | Out-Null

$launcherSrc = Get-ChildItem -LiteralPath (Join-Path $repo 'launcher\csharp') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$setupSrc = Get-ChildItem -LiteralPath (Join-Path $repo 'launcher\setup') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName }
$testDir = Join-Path $repo 'launcher\tests'
$kit = @((Join-Path $testDir 'TestKit.cs'))
$refs = @('/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
$bootLayout = Join-Path $repo 'launcher\csharp\BootLayout.cs'
$bootMain = Join-Path $repo 'launcher\bootstrap\Bootstrap.cs'
$bootAsm = Join-Path $repo 'launcher\bootstrap\AssemblyInfo.cs'
$common = @('/nologo', '/platform:anycpu32bitpreferred', '/codepage:65001', '/warnaserror+', '/langversion:5')

function Invoke-Csc([string[]]$cscArgs) {
    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
}

# ---- tests first -------------------------------------------------------------------------------------------------
# Real-process tests of the bootstrap: the bootstrap exe itself and a stand-in launcher (tests\FakeLauncher.cs).
$testBoot = Join-Path $BuildDir 'boot\WolfTeamDirilis.exe'
New-Item -ItemType Directory -Force -Path (Split-Path $testBoot) | Out-Null
Invoke-Csc ($common + @('/target:winexe', "/out:$testBoot") + $refs + @($bootLayout, $bootMain, $bootAsm))
$testFake = Join-Path $BuildDir 'FakeLauncher.exe'
Invoke-Csc ($common + @('/target:winexe', "/out:$testFake") + $refs + @($bootLayout, (Join-Path $testDir 'FakeLauncher.cs')))
$env:WT_TEST_BOOTSTRAP_EXE = $testBoot
$env:WT_TEST_FAKE_LAUNCHER_EXE = $testFake
$launcherTests = Join-Path $BuildDir 'LauncherTests.exe'
Invoke-Csc ($common + @('/target:exe', '/main:WolfTeamDirilis.Tests.LauncherTestMain', "/out:$launcherTests") + $refs + $launcherSrc + @($bootMain) + $kit +
    @((Join-Path $testDir 'TestHttpServer.cs'), (Join-Path $testDir 'LauncherTests.cs'), (Join-Path $testDir 'BootstrapTests.cs'),
      (Join-Path $testDir 'ConfigRepairTests.cs'),
      (Join-Path $testDir 'LauncherScreens.cs')))
& $launcherTests
if ($LASTEXITCODE -ne 0) { throw 'launcher tests failed' }

if ($setupSrc) {
    $setupTests = Join-Path $BuildDir 'SetupTests.exe'
    Invoke-Csc ($common + @('/target:exe', '/main:WolfTeamDirilis.Tests.SetupTestMain', "/out:$setupTests") + $refs +
        $setupSrc + @((Join-Path $repo 'launcher\csharp\Theme.cs'), $bootLayout) + $kit + @((Join-Path $testDir 'SetupTests.cs'), (Join-Path $testDir 'SetupScreens.cs')))
    & $setupTests
    if ($LASTEXITCODE -ne 0) { throw 'setup tests failed' }
}
& (Join-Path $PSScriptRoot 'test-endpoint-url.ps1')
if ($TestsOnly) { Write-Output 'tests passed'; return }

# ---- launcher ----------------------------------------------------------------------------------------------------
$launcherOut = Join-Path $BuildDir 'launcher'
& (Join-Path $PSScriptRoot 'build-launcher.ps1') -AssetDir $AssetDir -OutDir $launcherOut
$launcherExe = Join-Path $launcherOut 'WolfTeamLauncher.exe'
$bootExe = Join-Path $launcherOut 'WolfTeamDirilis.exe'
# The setup must carry the launcher of this source tree (the 1.1 installer kept shipping an old launcher once).
$launcherVersion = (Get-Item -LiteralPath $launcherExe).VersionInfo.FileVersion
$bootVersion = (Get-Item -LiteralPath $bootExe).VersionInfo.FileVersion
$bootSrcVersion = [regex]::Match([System.IO.File]::ReadAllText($bootMain), '\bVersion = "([0-9.]+)"').Groups[1].Value
if (-not $bootSrcVersion -or $bootVersion -ne (ConvertTo-FourPartVersion $bootSrcVersion)) { throw "bootstrap version '$bootVersion' does not match BootProgram.Version '$bootSrcVersion'" }
$srcVersion = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $repo 'launcher\csharp\Program.cs')), '\bVersion = "([0-9.]+)"').Groups[1].Value
if (-not $srcVersion -or $launcherVersion -ne (ConvertTo-FourPartVersion $srcVersion)) { throw "launcher version '$launcherVersion' does not match Program.Version '$srcVersion'" }
$setupVersion = [regex]::Match([System.IO.File]::ReadAllText((Join-Path $repo 'launcher\setup\SetupCore.cs')), '\bVersion = "([0-9.]+)"').Groups[1].Value
if ($setupVersion -ne $srcVersion) { throw "SetupInfo.Version '$setupVersion' differs from the launcher version '$srcVersion'" }
Write-Output "launcher version $srcVersion, bootstrap version $bootSrcVersion"

# ---- players' settings: checked, never printed ------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $IniFile -PathType Leaf)) { throw "launcher settings not found: $IniFile" }
$iniText = [System.IO.File]::ReadAllText($IniFile, [System.Text.Encoding]::UTF8)
foreach ($key in @('LoginUrl', 'UpdateBaseUrl', 'NewsUrl')) {
    if ($iniText -notmatch "(?m)^\s*$key\s*=\s*https?://\S+") { throw "$key missing in the launcher settings (values not shown)" }
}
# The game server moved on 2026-10-02: a new setup must not install the dead address again (regenerate the file with
# ops\build-launcher.ps1 -PackageDir ... -SecretFile ... -OverwriteConfig).
if ($iniText.Contains($RetiredServerHost)) {
    throw "the launcher settings still name the retired server $RetiredServerHost; regenerate them (build-launcher.ps1 -OverwriteConfig)"
}
$iniCopy = Join-Path $BuildDir 'WolfTeamDirilis.ini'
Copy-Item -LiteralPath $IniFile -Destination $iniCopy -Force
if (-not $EndpointsFile -or -not (Test-Path -LiteralPath $EndpointsFile -PathType Leaf)) { throw 'endpoints file missing (-EndpointsFile; the launcher updates only over HTTPS)' }
$epLines = @()
foreach ($key in @('LoginUrl', 'UpdateBaseUrl', 'NewsUrl')) {
    $l = Get-Content -LiteralPath $EndpointsFile | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1
    if (-not $l) {
        if ($key -eq 'UpdateBaseUrl') { throw 'UpdateBaseUrl missing in the endpoints file' }
        if ($key -eq 'LoginUrl' -and -not $AllowHttpLogin) { throw 'LoginUrl (https) missing in the endpoints file; -AllowHttpLogin ships the consent-only http login' }
        continue
    }
    $v = ($l -split '=', 2)[1].Trim()
    if (-not (Test-HttpsEndpointUrl $v)) { throw "$key is not a plain, valid https URL (value not shown)" }
    $epLines += "$key=$v"
}
$hostLine = Get-Content -LiteralPath $EndpointsFile | Where-Object { $_ -match '^\s*ServerHost\s*=' } | Select-Object -First 1
if ($hostLine) {
    $v = ($hostLine -split '=', 2)[1].Trim()
    if (-not (Test-ServerHost $v)) { throw "ServerHost in the endpoints file is not a valid, current host name or IPv4 address (value not shown)" }
    $epLines += "ServerHost=$v"
}
$epCopy = Join-Path $BuildDir 'endpoints.ini'
[System.IO.File]::WriteAllText($epCopy, "; HTTPS addresses of the WolfTeam Dirilis launcher (security audit R5).`r`n" + ($epLines -join "`r`n") + "`r`n", (New-Object System.Text.UTF8Encoding($false)))

# ---- setup -------------------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$setupExe = Join-Path $OutDir 'WolfTeamDirilis-Setup.exe'
$res = @(
    "/resource:$bootExe,WolfTeamDirilis.exe",
    "/resource:$launcherExe,launcher\WolfTeamLauncher.exe",
    "/resource:$epCopy,launcher\endpoints.ini",
    "/resource:$(Join-Path $AssetDir 'bg.png'),bg.png",
    "/resource:$iniCopy,WolfTeamDirilis.ini",
    "/resource:$(Join-Path $repo 'launcher\package\KURULUM.txt'),KURULUM.txt"
)
# App manifest (asInvoker): the installer is per-user, so declare it and avoid the "...-Setup.exe" auto-elevation heuristic.
$setupManifest = Join-Path $repo 'launcher\package\app.manifest'
if (-not (Test-Path -LiteralPath $setupManifest -PathType Leaf)) { throw "missing manifest: $setupManifest" }
Invoke-Csc ($common + @('/target:winexe', '/optimize+', "/win32icon:$(Join-Path $AssetDir 'wt.ico')", "/win32manifest:$setupManifest", "/out:$setupExe") + $refs + $res +
    $setupSrc + @((Join-Path $repo 'launcher\csharp\Theme.cs'), $bootLayout))
Remove-Item -LiteralPath $iniCopy, $epCopy -Force
Copy-Item -LiteralPath $bootExe -Destination (Join-Path $OutDir 'WolfTeamDirilis.exe') -Force
Copy-Item -LiteralPath $launcherExe -Destination (Join-Path $OutDir 'WolfTeamLauncher.exe') -Force

$sums = foreach ($f in @('WolfTeamDirilis-Setup.exe', 'WolfTeamDirilis.exe', 'WolfTeamLauncher.exe')) {
    (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $OutDir $f)).Hash.ToLowerInvariant() + '  ' + $f
}
[System.IO.File]::WriteAllText((Join-Path $OutDir 'SHA256SUMS'), (($sums -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
Write-Output "built $setupExe"
Write-Output ($sums -join "`n")
