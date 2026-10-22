# Cases for Test-HttpsEndpointUrl (ops\endpoint-url.ps1). Exit code 0 = all pass.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'endpoint-url.ps1')
$fail = 0
$cases = @(
    @('https://wolf2team.com/a/b/', $true),
    @('https://example.org:8443/api/login.php', $true),
    @('https://10.0.0.1/x', $true),
    @('https://', $false),
    @('https:///x', $false),
    @('https://example.org:99999/login', $false),
    @('https://example.org:/x', $false),
    @('http://example.org/x', $false),
    @('HTTPS://example.org/x', $false),
    @('https://exa mple.org/x', $false),
    @('https://example.org', $false),
    @('https://example.org/x?y=1', $false),
    @('https://user@example.org/x', $false),
    @('https://-.-/x', $false),
    @('https://a..b/x', $false),
    @('', $false)
)
foreach ($c in $cases) {
    $got = Test-HttpsEndpointUrl $c[0]
    if ($got -ne $c[1]) { Write-Output "FAIL '$($c[0])' expected $($c[1]) got $got"; $fail++ }
}
$hosts = @(
    @('188.40.176.195', $true),
    @('wolf2team.com', $true),
    @('game-1.example.org', $true),
    @('45.76.145.60', $false),
    @('', $false),
    @('188.40.176.195:40706', $false),
    @('https://wolf2team.com/', $false),
    @('exa mple.org', $false),
    @('a..b', $false),
    @('-.-', $false)
)
foreach ($c in $hosts) {
    $got = Test-ServerHost $c[0]
    if ($got -ne $c[1]) { Write-Output "FAIL host '$($c[0])' expected $($c[1]) got $got"; $fail++ }
}
$versions = @(@('1.6.1', '1.6.1.0'), @('1.5', '1.5.0.0'), @('1.6.1.2', '1.6.1.2'))
foreach ($c in $versions) {
    $got = ConvertTo-FourPartVersion $c[0]
    if ($got -ne $c[1]) { Write-Output "FAIL version '$($c[0])' expected $($c[1]) got $got"; $fail++ }
}
# A rejected -ServerHost (e.g. a download URL pasted by mistake) never shows up in the error (it may hold a secret path).
$leak = 'S3cretPathForTheTest42'
$msg = $null
try { & (Join-Path $PSScriptRoot 'build-launcher.ps1') -AssetDir $env:TEMP -OutDir (Join-Path $env:TEMP 'wt-no-build') -ServerHost "https://example.org/$leak/client/" | Out-Null }
catch { $msg = $_.Exception.Message }
if (-not $msg -or $msg.Contains($leak) -or -not $msg.Contains('ServerHost')) { Write-Output "FAIL rejected -ServerHost: refused without its value (got: $([bool]$msg))"; $fail++ }
if ($fail) { throw "endpoint url cases: $fail failed" }
Write-Output "endpoint url cases: $($cases.Count + $hosts.Count + $versions.Count + 1) passed"
