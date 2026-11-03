# Shared check for the https addresses shipped in launcher\endpoints.ini (dot-sourced by build-setup.ps1 and
# build-launcher.ps1). A value must be a plain https URL AND parse with the same .NET Uri rules the launcher uses
# (LauncherConfig: Uri.TryCreate absolute, scheme https), so the launcher never silently drops a shipped address.
# Cases: ops\test-endpoint-url.ps1 (run by build-setup.ps1).
function Test-HttpsEndpointUrl([string]$Value) {
    if ($Value -cnotmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?/[A-Za-z0-9/_.-]*$') { return $false }
    $u = $null
    if (-not [System.Uri]::TryCreate($Value, [System.UriKind]::Absolute, [ref]$u)) { return $false }
    if ($u.Scheme -cne 'https' -or $u.UserInfo -or $u.Query -or $u.Fragment) { return $false }
    if ([System.Uri]::CheckHostName($u.Host) -eq [System.UriHostNameType]::Unknown) { return $false }
    return $true
}

# The game server's address until 2026-10-02 (LauncherConfig.RetiredServerHost; launcher 1.6.1+ ignores it).
$RetiredServerHost = '45.76.145.60'

# ServerHost (launcher 1.6.1, the game server the status badge probes): a bare host name or IPv4 address that
# LauncherConfig.IsValidHost accepts (Uri.CheckHostName), and not the retired address.
function Test-ServerHost([string]$Value) {
    if ($Value -cnotmatch '^[A-Za-z0-9.-]{1,253}$') { return $false }
    if ([System.Uri]::CheckHostName($Value) -eq [System.UriHostNameType]::Unknown) { return $false }
    if ($Value -eq $RetiredServerHost) { return $false }
    return $true
}

# "1.6.1" -> "1.6.1.0", "1.5" -> "1.5.0.0": the version resource the C# FileVersion constants produce.
function ConvertTo-FourPartVersion([string]$Value) {
    $parts = @($Value.Split('.'))
    while ($parts.Count -lt 4) { $parts += '0' }
    return ($parts -join '.')
}

# Validate a complete player release before either builder writes package files.
# Login and updates require HTTPS; news and the game server override remain optional.
function Read-LauncherReleaseEndpoints([string]$Path) {
    if (-not $Path -or -not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'endpoints file missing (-EndpointsFile; HTTPS LoginUrl and UpdateBaseUrl required)'
    }
    try { $source = @(Get-Content -LiteralPath $Path -Encoding UTF8 -ErrorAction Stop) }
    catch { throw 'Cannot read endpoints file (path and values not shown)' }
    $lines = @()
    foreach ($key in @('LoginUrl', 'UpdateBaseUrl', 'NewsUrl')) {
        $line = $source | Where-Object { $_ -match "^\s*$key\s*=" } | Select-Object -First 1
        if (-not $line) {
            if ($key -ne 'NewsUrl') { throw "$key (https) missing in the endpoints file" }
            continue
        }
        $value = ($line -split '=', 2)[1].Trim()
        if (-not (Test-HttpsEndpointUrl $value)) { throw "$key is not a plain, valid https URL (value not shown)" }
        $lines += "$key=$value"
    }
    $hostLine = $source | Where-Object { $_ -match '^\s*ServerHost\s*=' } | Select-Object -First 1
    if ($hostLine) {
        $value = ($hostLine -split '=', 2)[1].Trim()
        if (-not (Test-ServerHost $value)) { throw 'ServerHost is not a valid, current host name or IPv4 address (value not shown)' }
        $lines += "ServerHost=$value"
    }
    return $lines
}
