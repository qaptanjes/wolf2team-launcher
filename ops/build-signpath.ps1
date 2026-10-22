<#
  Rebuilds ONLY the three maintained C# applications from this checkout. No signing, deployment,
  client download, production assets, endpoints, existing EXEs, NuGet packages or SDK bootstrap.
  Run from a clean Git checkout. BuildRoot must be a new/empty folder beneath this checkout's build/.
  Example: powershell -NoProfile -File ops/build-signpath.ps1
#>
param([string]$BuildRoot = '')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path -Parent $PSScriptRoot
$buildBase = [IO.Path]::GetFullPath((Join-Path $repo 'build')) + [IO.Path]::DirectorySeparatorChar
if (-not $BuildRoot) { $BuildRoot = Join-Path $buildBase 'signpath' }
$BuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if (-not $BuildRoot.StartsWith($buildBase, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'BuildRoot must be beneath this checkout build/ directory'
}
if ((Test-Path -LiteralPath $BuildRoot) -and @(Get-ChildItem -LiteralPath $BuildRoot -Force).Count) {
    throw 'BuildRoot is not empty. Choose a new directory; existing artifacts will not be overwritten.'
}
$ancestor = $BuildRoot
while ($ancestor -and $ancestor.Length -ge $repo.Length) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'BuildRoot must not traverse a reparse point'
    }
    $ancestor = Split-Path -Parent $ancestor
}
$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc -PathType Leaf)) {
    throw '.NET Framework 4.x x64 compiler is required. Use the GitHub-hosted windows-2025 image.'
}
$dirty = @(& git -C $repo status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Use a clean Git checkout so the report identifies the exact reviewed source.' }
$commit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Cannot establish source commit' }

$unsigned = Join-Path $BuildRoot 'unsigned'
$logs = Join-Path $BuildRoot 'logs'
$tests = Join-Path $BuildRoot 'tests'
New-Item -ItemType Directory -Path $unsigned, $logs, $tests -Force | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)

function Invoke-Compiler([string[]]$Arguments) {
    & $csc @Arguments
    if ($LASTEXITCODE -ne 0) { throw "C# compiler failed with exit code $LASTEXITCODE" }
}

function Invoke-LoggedPowerShell([string]$Script, [string[]]$ScriptArgs, [string]$LogName) {
    $args = @('-NoProfile', '-File', ('"' + $Script + '"')) + $ScriptArgs
    $process = Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') `
        -ArgumentList $args -WindowStyle Hidden -Wait -PassThru `
        -RedirectStandardOutput (Join-Path $logs ($LogName + '.log')) `
        -RedirectStandardError (Join-Path $logs ($LogName + '.stderr.log'))
    if ($process.ExitCode -ne 0) { throw "$LogName failed ($($process.ExitCode)); see logs" }
}

# Existing regression suites run before building anything eligible for the artifact bundle.
Invoke-LoggedPowerShell (Join-Path $PSScriptRoot 'build-setup.ps1') @('-TestsOnly', '-BuildDir', ('"' + $tests + '"')) 'regression-tests'

$src = Join-Path $repo 'launcher\csharp'
$bootSrc = Join-Path $repo 'launcher\bootstrap'
$setupSrc = Join-Path $repo 'launcher\setup'
$prep = Join-Path $repo 'launcher\signpath'
$metadata = Join-Path $prep 'PreparationAssemblyInfo.cs'
$bootLayout = Join-Path $src 'BootLayout.cs'
$common = @('/nologo', '/target:winexe', '/platform:anycpu32bitpreferred', '/optimize+', '/codepage:65001',
    '/warnaserror+', '/langversion:5', '/define:SIGNPATH_PREPARATION',
    "/win32manifest:$(Join-Path $prep 'preparation.manifest')",
    '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll', '/r:System.Web.Extensions.dll')
$launcher = Join-Path $unsigned 'WolfTeamLauncher.exe'
$bootstrap = Join-Path $unsigned 'WolfTeamDirilis.exe'
$setup = Join-Path $unsigned 'WolfTeamDirilis-Setup.exe'
$launcherSources = @(Get-ChildItem -LiteralPath $src -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$bootSources = @(Get-ChildItem -LiteralPath $bootSrc -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$setupSources = @(Get-ChildItem -LiteralPath $setupSrc -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
Invoke-Compiler ($common + @("/out:$launcher", $metadata) + $launcherSources)
$preparationTest = Join-Path $tests 'PreparationTests.exe'
$testCommon = @($common | Where-Object { $_ -ne '/target:winexe' })
Invoke-Compiler ($testCommon + @('/target:exe', '/main:WolfTeamDirilis.PreparationTests', "/out:$preparationTest",
    (Join-Path $prep 'PreparationTests.cs')) + $launcherSources)
& $preparationTest $prep > (Join-Path $logs 'preparation-tests.log')
if ($LASTEXITCODE -ne 0) { throw 'Preparation endpoint isolation tests failed' }
Invoke-Compiler ($common + @("/out:$bootstrap", $metadata, $bootLayout) + $bootSources)
$resources = @("/resource:$bootstrap,WolfTeamDirilis.exe", "/resource:$launcher,launcher\WolfTeamLauncher.exe",
    "/resource:$(Join-Path $prep 'endpoints.ini'),launcher\endpoints.ini",
    "/resource:$(Join-Path $prep 'WolfTeamDirilis.ini'),WolfTeamDirilis.ini",
    "/resource:$(Join-Path $prep 'KURULUM.txt'),KURULUM.txt")
Invoke-Compiler ($common + @("/out:$setup", $metadata, $bootLayout, (Join-Path $src 'Theme.cs')) + $resources + $setupSources)

& (Join-Path $PSScriptRoot 'test-signpath-artifacts.ps1') -ArtifactDir $unsigned -SourceRoot $repo
Invoke-LoggedPowerShell (Join-Path $PSScriptRoot 'test-setup-smoke.ps1') `
    @('-Setup', ('"' + $setup + '"'), '-IniFile', ('"' + (Join-Path $prep 'WolfTeamDirilis.ini') + '"'), '-PreparationOnly') 'installer-smoke'

$names = @('WolfTeamDirilis.exe', 'WolfTeamLauncher.exe', 'WolfTeamDirilis-Setup.exe')
$artifacts = @($names | ForEach-Object {
    $p = Join-Path $unsigned $_
    [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.ToLowerInvariant();
        bytes = (Get-Item -LiteralPath $p).Length; signature = 'NotSigned';
        fileVersion = (Get-Item -LiteralPath $p).VersionInfo.FileVersion }
})
$sourceFiles = @(& git -C $repo ls-files -- LICENSE launcher ops/build-signpath.ps1 ops/test-signpath-artifacts.ps1 `
    ops/build-setup.ps1 ops/build-launcher.ps1 ops/test-setup-smoke.ps1 ops/endpoint-url.ps1 ops/test-endpoint-url.ps1 .github/workflows/windows-unsigned.yml)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate source inputs' }
$sources = @($sourceFiles | Sort-Object | ForEach-Object {
    [ordered]@{ path = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $repo $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$endCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $endCommit -ne $commit) { throw 'Source commit changed during build' }
$dirty = @(& git -C $repo status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0 -or $dirty.Count) { throw 'Source tree changed during build; outputs must not be published' }
$report = [ordered]@{
    purpose = 'Unsigned SignPath preparation; not a player release'
    eligibleForSigning = $false
    blockers = @('Source ownership/license and complete security review require approval',
        'SignPath project, artifact configuration, certificate and protected approval policy are not activated',
        'Preparation endpoints are intentionally non-operational')
    commit = $commit; repository = $env:GITHUB_REPOSITORY; runId = $env:GITHUB_RUN_ID
    builtAtUtc = [DateTime]::UtcNow.ToString('o'); runnerImage = $env:ImageVersion
    compiler = [ordered]@{ version = (Get-Item -LiteralPath $csc).VersionInfo.FileVersion;
        sha256 = (Get-FileHash -LiteralPath $csc -Algorithm SHA256).Hash.ToLowerInvariant() }
    tests = @('Launcher/bootstrap/setup regression suites', 'Endpoint validation', 'Preparation endpoint isolation', 'Artifact/resource boundary', 'Isolated install/repair/uninstall')
    artifacts = $artifacts; sources = $sources
}
[IO.File]::WriteAllText((Join-Path $unsigned 'build-report.json'), ($report | ConvertTo-Json -Depth 8), $utf8)
[IO.File]::WriteAllText((Join-Path $unsigned 'SHA256SUMS'), (($artifacts | ForEach-Object { $_.sha256 + '  ' + $_.name }) -join "`n") + "`n", $utf8)
[IO.File]::WriteAllText((Join-Path $unsigned 'DO-NOT-PUBLISH.txt'), "UNSIGNED PREPARATION BUILD. No game assets. Non-operational endpoints. No signing authorization. See CODE_SIGNING_POLICY.md.`n", $utf8)
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $unsigned 'LICENSE')
# Only this fixed directory is uploaded by CI. Test executables and local packages are excluded.
Write-Output "PASS: unsigned preparation artifacts and provenance in $unsigned"
