# Release transport policy; synthetic files only, no compiler or package writes.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'endpoint-url.ps1')
$fail = 0
$checks = 0

function Test-DeprecatedSwitch([string]$Script) {
    # Stop the old build before it could create output or recurse through these tests.
    function New-Item { throw 'unexpected-build-write' }
    $message = ''
    try {
        if ($Script -eq 'build-launcher.ps1') {
            & (Join-Path $PSScriptRoot $Script) -AssetDir 'fixture-assets-not-present' -AllowHttpLogin | Out-Null
        } else {
            & (Join-Path $PSScriptRoot $Script) -AllowHttpLogin -TestsOnly | Out-Null
        }
    } catch { $message = $_.Exception.Message }
    return $message -like '*HTTP login is no longer supported*'
}

function Test-BuilderPreflight([string]$Script, [string]$EndpointFile, [string]$ExpectedError) {
    function New-Item { throw 'unexpected-build-write' }
    $message = ''
    try {
        if ($Script -eq 'build-launcher.ps1') {
            & (Join-Path $PSScriptRoot $Script) -AssetDir 'fixture-assets-not-present' -PackageDir 'fixture-package-not-present' -EndpointsFile $EndpointFile | Out-Null
        } else {
            & (Join-Path $PSScriptRoot $Script) -EndpointsFile $EndpointFile | Out-Null
        }
    } catch { $message = $_.Exception.Message }
    return $message -like ('*' + $ExpectedError + '*') -and -not $message.Contains('SyntheticSecretDoNotPrint')
}

foreach ($script in @('build-launcher.ps1', 'build-setup.ps1')) {
    $checks++
    if (-not (Test-DeprecatedSwitch $script)) { Write-Output "FAIL ${script}: insecure option not rejected before build"; $fail++ }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('wt-endpoint-policy-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($scratch) | Out-Null
$file = Join-Path $scratch 'endpoints.ini'
$secret = 'SyntheticSecretDoNotPrint'
try {
    foreach ($script in @('build-launcher.ps1', 'build-setup.ps1')) {
        foreach ($case in @(
            @{ Text = ''; Error = 'endpoints file missing'; Missing = $true },
            @{ Text = "UpdateBaseUrl=https://example.org/client/"; Error = 'LoginUrl'; Missing = $false },
            @{ Text = "LoginUrl=http://example.org/$secret/`nUpdateBaseUrl=https://example.org/client/"; Error = 'LoginUrl'; Missing = $false }
        )) {
            $path = ''
            if (-not $case.Missing) {
                [IO.File]::WriteAllText($file, $case.Text, (New-Object Text.UTF8Encoding($false)))
                $path = $file
            }
            $checks++
            if (-not (Test-BuilderPreflight $script $path $case.Error)) {
                Write-Output "FAIL ${script}: endpoint validation must precede assets, compiler and writes (case $checks)"
                $fail++
            }
        }
    }
    $cases = @(
        @{ Text = "UpdateBaseUrl=https://example.org/$secret/"; Error = 'LoginUrl'; Count = 0 },
        @{ Text = "LoginUrl=https://example.org/$secret/"; Error = 'UpdateBaseUrl'; Count = 0 },
        @{ Text = "LoginUrl=http://example.org/$secret/`nUpdateBaseUrl=https://example.org/client/"; Error = 'LoginUrl'; Count = 0 },
        @{ Text = "LoginUrl=https://user@example.org/$secret/`nUpdateBaseUrl=https://example.org/client/"; Error = 'LoginUrl'; Count = 0 },
        @{ Text = "LoginUrl=https://example.org/login/`nUpdateBaseUrl=https://example.org/client/"; Error = ''; Count = 2 },
        @{ Text = "LoginUrl=https://example.org/login/`nUpdateBaseUrl=https://example.org/client/`nNewsUrl=https://example.org/news/`nServerHost=game.example.org"; Error = ''; Count = 4 },
        @{ Text = "LoginUrl=https://example.org/login/`nUpdateBaseUrl=https://example.org/client/`nServerHost=https://example.org/$secret/"; Error = 'ServerHost'; Count = 0 }
    )
    foreach ($case in $cases) {
        [IO.File]::WriteAllText($file, $case.Text, (New-Object Text.UTF8Encoding($false)))
        $message = ''; $lines = @()
        try { $lines = @(Read-LauncherReleaseEndpoints $file) } catch { $message = $_.Exception.Message }
        $checks++
        $ok = if ($case.Error) { $message -like ('*' + $case.Error + '*') -and -not $message.Contains($secret) }
              else { -not $message -and $lines.Count -eq $case.Count -and ($lines -contains 'LoginUrl=https://example.org/login/') }
        if (-not $ok) { Write-Output "FAIL release endpoints case $checks (values withheld)"; $fail++ }
    }
} finally {
    # Delete only the two known test paths; no recursive filesystem operation.
    if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
    [IO.Directory]::Delete($scratch)
}
if ($fail) { throw "release endpoint policy: $fail failed of $checks" }
Write-Output "release endpoint policy: $checks passed"
