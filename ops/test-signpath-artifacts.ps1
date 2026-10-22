param([Parameter(Mandatory=$true)][string]$ArtifactDir,
      [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$expected = @('WolfTeamDirilis.exe', 'WolfTeamLauncher.exe', 'WolfTeamDirilis-Setup.exe')
$actual = @(Get-ChildItem -LiteralPath $ArtifactDir -Force | ForEach-Object Name)
if (@(Compare-Object ($expected | Sort-Object) ($actual | Sort-Object)).Count) {
    throw 'Artifact allowlist mismatch: only the three freshly built EXEs are permitted'
}
function Hash-Bytes([byte[]]$Bytes) {
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($Bytes)).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}
foreach ($name in $expected) {
    $path = Join-Path $ArtifactDir $name
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse point in artifact directory' }
    if ((Get-AuthenticodeSignature -LiteralPath $path).Status -ne 'NotSigned') { throw 'Expected a newly built unsigned executable' }
    # Reflection-only loading inspects metadata/resources without executing application code.
    $assembly = [Reflection.Assembly]::ReflectionOnlyLoad([IO.File]::ReadAllBytes($path))
    $metadata = @($assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute' })
    if (-not @($metadata | Where-Object { $_.ConstructorArguments[0].Value -eq 'BuildPurpose' -and $_.ConstructorArguments[1].Value -eq 'SignPathPreparation' }).Count) {
        throw 'Preparation build marker missing'
    }
    $resources = @($assembly.GetManifestResourceNames())
    if ($name -ne 'WolfTeamDirilis-Setup.exe') {
        if ($resources.Count) { throw 'Unexpected embedded launcher/bootstrap payload' }
        continue
    }
    $files = @{
        'WolfTeamDirilis.exe' = (Join-Path $ArtifactDir 'WolfTeamDirilis.exe')
        'launcher\WolfTeamLauncher.exe' = (Join-Path $ArtifactDir 'WolfTeamLauncher.exe')
        'WolfTeamDirilis.ini' = (Join-Path $SourceRoot 'launcher\signpath\WolfTeamDirilis.ini')
        'launcher\endpoints.ini' = (Join-Path $SourceRoot 'launcher\signpath\endpoints.ini')
        'KURULUM.txt' = (Join-Path $SourceRoot 'launcher\signpath\KURULUM.txt')
    }
    if (@(Compare-Object (@($files.Keys) | Sort-Object) ($resources | Sort-Object)).Count) {
        throw 'Installer resource allowlist mismatch: game payloads and derived artwork are prohibited'
    }
    foreach ($resource in $resources) {
        $stream = $assembly.GetManifestResourceStream($resource)
        $buffer = New-Object IO.MemoryStream
        try {
            $stream.CopyTo($buffer)
            if ((Hash-Bytes $buffer.ToArray()) -ne (Get-FileHash -LiteralPath $files[$resource] -Algorithm SHA256).Hash.ToLowerInvariant()) {
                throw 'Embedded payload does not match the new build or checked-in preparation fixture'
            }
        } finally { $stream.Dispose(); $buffer.Dispose() }
    }
}
Write-Output 'PASS: three unsigned managed EXEs; preparation markers; exact resource allowlist; embedded build hashes'
