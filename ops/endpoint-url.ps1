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
