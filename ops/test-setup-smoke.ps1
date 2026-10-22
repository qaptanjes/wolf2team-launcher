<#
  Smoke test of the built installer: silent install into a temp folder whose path has spaces and Turkish letters,
  checks files / shortcuts / uninstall entry, a silent repair, then a silent uninstall and the cleanup.
  The desktop, Start menu, registry key and %APPDATA% folder are redirected (test switches /desktopdir, /startmenudir,
  /regkey, /appdata), so nothing of the real user profile is touched. The launcher is not started.

  usage: powershell -File ops\test-setup-smoke.ps1 [-Setup C:\tools\re\out\setup\WolfTeamDirilis-Setup.exe]
         [-IniFile C:\tools\re\dist\WolfTeam\WolfTeamDirilis.ini]   (only its hash is compared, never printed)
#>
param(
    [string]$Setup = 'C:\tools\re\out\setup\WolfTeamDirilis-Setup.exe',
    [string]$IniFile = 'C:\tools\re\dist\WolfTeam\WolfTeamDirilis.ini',
    # the launcher version the setup must install (default: Program.Version of this source tree)
    [string]$LauncherVersion = '',
    [switch]$PreparationOnly
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'endpoint-url.ps1')
$script:fail = 0
function Check([string]$name, [bool]$ok) {
    if ($ok) { Write-Output "PASS $name" } else { Write-Output "FAIL $name"; $script:fail++ }
}
function Run([string]$exe, [string]$argLine) {
    # Turkish fixture paths exercise install/repair, independent of the host locale.
    # CP1252 rejection is covered separately by SetupTests and BootstrapTests.
    $previousCodePage = $env:WOLFTEAM_TEST_ANSI_CODEPAGE
    try {
        $env:WOLFTEAM_TEST_ANSI_CODEPAGE = '1254'
        $p = Start-Process -FilePath $exe -ArgumentList $argLine -Wait -PassThru -WindowStyle Hidden
        return $p.ExitCode
    } finally { $env:WOLFTEAM_TEST_ANSI_CODEPAGE = $previousCodePage }
}
function LinkTarget([string]$lnk) {
    # WScript.Shell returns an empty target for these Unicode .lnk paths on English Windows.
    # Use the freshly built setup's explicit IShellLinkW reader (no UI/installer entry point).
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Setup))
    $reader = $assembly.GetType('WolfTeamDirilis.Setup.Shortcuts').GetMethod('ReadTarget')
    return $reader.Invoke($null, @($lnk))
}

# Windows PowerShell reads a BOM-less script as ANSI, so the Turkish letters are built from code points.
$sh = [char]0x015F
$product = "WolfTeam Dirili$sh"
$id = [guid]::NewGuid().ToString('N').Substring(0, 8)
$root = Join-Path $env:TEMP ("wtd-smoke " + [char]0x011E + [char]0x00FC + $sh + [char]0x0130 + " $id")
# Match the installer's .NET canonicalization, including 8.3 TEMP ancestors on hosted runners.
$dir = [IO.Path]::GetFullPath((Join-Path $root 'Oyunlar\WolfTeam Dirilis'))
$desk = Join-Path $root 'Desktop'
$prog = Join-Path $root 'Programs'
$appd = Join-Path $root 'AppData'
$regParent = 'Software\WolfTeamDirilisSmoke'
$reg = "$regParent\$id"
New-Item -ItemType Directory -Force -Path $desk, $prog | Out-Null
$redirect = "`"/desktopdir=$desk`" `"/startmenudir=$prog`" `"/regkey=$reg`" `"/appdata=$appd`""
$tempBefore = @(Get-ChildItem -LiteralPath $env:TEMP -Filter 'WolfTeamDirilis-Kaldir-*.exe' -ErrorAction SilentlyContinue | ForEach-Object { $_.Name })

try {
    # ---- install -----------------------------------------------------------------------------------------------
    $code = Run $Setup "/S $redirect /D=$dir"
    Check "silent install exit code 0 (got $code)" ($code -eq 0)
    $expectedFiles = @('WolfTeamDirilis.exe', 'launcher\WolfTeamLauncher.exe', 'launcher\endpoints.ini', 'WolfTeamDirilis.ini', 'KURULUM.txt', 'WolfTeamDirilis-Kaldir.exe', 'WolfTeamDirilis.kurulum')
    if (-not $PreparationOnly) { $expectedFiles += 'bg.png' }
    else { Check 'preparation install contains no game-derived artwork' (-not (Test-Path -LiteralPath (Join-Path $dir 'bg.png'))) }
    foreach ($f in $expectedFiles) {
        Check "installed $f" (Test-Path -LiteralPath (Join-Path $dir $f))
    }
    if (-not $LauncherVersion) {
        $LauncherVersion = [regex]::Match([System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'launcher\csharp\Program.cs')),
            '\bVersion = "([0-9.]+)"').Groups[1].Value
    }
    $got = (Get-Item -LiteralPath (Join-Path $dir 'launcher\WolfTeamLauncher.exe')).VersionInfo.FileVersion
    Check "installed launcher is version $LauncherVersion (got $got)" ($got -eq (ConvertTo-FourPartVersion $LauncherVersion))
    $bootWant = [regex]::Match([System.IO.File]::ReadAllText((Join-Path (Split-Path -Parent $PSScriptRoot) 'launcher\bootstrap\Bootstrap.cs')),
        '\bVersion = "([0-9.]+)"').Groups[1].Value
    $bootGot = (Get-Item -LiteralPath (Join-Path $dir 'WolfTeamDirilis.exe')).VersionInfo.FileVersion
    Check "installed bootstrap is version $bootWant (got $bootGot)" ($bootGot -eq (ConvertTo-FourPartVersion $bootWant))
    if (Test-Path -LiteralPath $IniFile) {
        $same = (Get-FileHash -LiteralPath $IniFile).Hash -eq (Get-FileHash -LiteralPath (Join-Path $dir 'WolfTeamDirilis.ini')).Hash
        Check 'launcher settings are the release ones (hash)' $same
    }
    $deskLink = Join-Path $desk "$product.lnk"
    $menuLink = Join-Path $prog "$product\$product.lnk"
    Check 'desktop shortcut' (Test-Path -LiteralPath $deskLink)
    Check 'start menu shortcut' (Test-Path -LiteralPath $menuLink)
    $linkTarget = LinkTarget $deskLink
    $expectedTarget = Join-Path $dir 'WolfTeamDirilis.exe'
    # WScript.Shell may return an 8.3 path; compare both existing paths canonically.
    $canonicalTarget = if ($linkTarget) { [IO.Path]::GetFullPath($linkTarget) } else { '' }
    if ($canonicalTarget -ne $expectedTarget) {
        Write-Output ("Shortcut diagnostic: raw=[$linkTarget] canonical=[$canonicalTarget] expected=[$expectedTarget]")
    }
    Check 'shortcut points at the launcher' ($canonicalTarget -eq $expectedTarget)
    $k = Get-ItemProperty -LiteralPath "HKCU:\$reg" -ErrorAction SilentlyContinue
    Check 'uninstall entry InstallLocation' ($k -and $k.InstallLocation -eq $dir)
    Check 'uninstall entry UninstallString' ($k -and $k.UninstallString -eq "`"$dir\WolfTeamDirilis-Kaldir.exe`" /uninstall")
    Check 'uninstall entry DisplayName' ($k -and $k.DisplayName -eq $product)

    # ---- refused folder: Program Files ---------------------------------------------------------------------------
    $code = Run $Setup "/S $redirect /D=$env:ProgramFiles\WolfTeam Smoke $id"
    Check "Program Files refused (exit $code)" ($code -eq 1 -and -not (Test-Path -LiteralPath "$env:ProgramFiles\WolfTeam Smoke $id"))

    # ---- repair without the desktop shortcut ---------------------------------------------------------------------
    Set-Content -LiteralPath (Join-Path $dir 'wolf.xfs') -Value 'stand-in for a downloaded game file'
    $code = Run $Setup "/S /nodesktop $redirect /D=$dir"
    Check "silent repair exit code 0 (got $code)" ($code -eq 0)
    Check 'repair removed the unwanted desktop shortcut' (-not (Test-Path -LiteralPath $deskLink))
    Check 'repair kept game files' (Test-Path -LiteralPath (Join-Path $dir 'wolf.xfs'))

    # ---- uninstall (keeps nothing) -------------------------------------------------------------------------------
    New-Item -ItemType Directory -Force -Path $appd | Out-Null
    Set-Content -LiteralPath (Join-Path $appd 'user.txt') -Value 'smoke'
    $code = Run (Join-Path $dir 'WolfTeamDirilis-Kaldir.exe') "/uninstall /S $redirect"
    Check "uninstall first stage exit code 0 (got $code)" ($code -eq 0)
    $deadline = (Get-Date).AddSeconds(40)
    while ((Test-Path -LiteralPath $dir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    Check 'install folder removed' (-not (Test-Path -LiteralPath $dir))
    Check 'start menu folder removed' (-not (Test-Path -LiteralPath (Join-Path $prog $product)))
    Check 'uninstall entry removed' (-not (Test-Path -LiteralPath "HKCU:\$reg"))
    Check 'launcher settings folder removed' (-not (Test-Path -LiteralPath $appd))
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $left = @(Get-ChildItem -LiteralPath $env:TEMP -Filter 'WolfTeamDirilis-Kaldir-*.exe' -ErrorAction SilentlyContinue |
            Where-Object { $tempBefore -notcontains $_.Name })
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)
    Check 'temporary uninstaller copy deleted itself' ($left.Count -eq 0)
}
finally {
    # Verify the resolved cleanup target stays inside this test's generated temporary workspace.
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $tempPrefix = [IO.Path]::GetFullPath($env:TEMP).TrimEnd('\') + '\'
    if (-not $resolvedRoot.StartsWith($tempPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedRoot)).StartsWith('wtd-smoke ', [StringComparison]::Ordinal)) {
        throw 'Refusing smoke cleanup outside its temporary workspace'
    }
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "HKCU:\$reg" -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output ("smoke: " + $(if ($script:fail -eq 0) { 'ALL PASSED' } else { "$script:fail FAILED" }))
exit $script:fail
