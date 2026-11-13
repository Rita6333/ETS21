[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [Parameter(Mandatory = $true)]
    [string] $DownloadUrl,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath,

    [string] $Version,
    [string] $Notes = ''
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $ExecutablePath).ProviderPath
$file = Get-Item -LiteralPath $executable
if ($file.PSIsContainer -or $file.Extension -ine '.exe') {
    throw 'ExecutablePath must name the published Windows EXE, not a ZIP or directory.'
}
if ($file.Length -lt 64 -or $file.Length -gt 200MB) {
    throw 'The executable must be between 64 bytes and 200 MiB.'
}

$stream = [System.IO.File]::OpenRead($executable)
try {
    if ($stream.ReadByte() -ne 0x4D -or $stream.ReadByte() -ne 0x5A) {
        throw 'ExecutablePath does not contain a Windows executable (missing MZ header).'
    }
}
finally {
    $stream.Dispose()
}

$uri = $null
if (-not [System.Uri]::TryCreate($DownloadUrl, [System.UriKind]::Absolute, [ref] $uri) -or
    $uri.Scheme -ne 'https' -or [string]::IsNullOrWhiteSpace($uri.Host) -or
    -not [string]::IsNullOrEmpty($uri.UserInfo) -or
    -not [string]::IsNullOrEmpty($uri.Fragment)) {
    throw 'DownloadUrl must be an absolute HTTPS URL without credentials or a fragment.'
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $file.VersionInfo.ProductVersion
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        $Version = $Version.Split('+')[0]
    }
}
$parsedVersion = $null
if ([string]::IsNullOrWhiteSpace($Version) -or
    $Version -notmatch '^\d+\.\d+(\.\d+){0,2}$' -or
    -not [System.Version]::TryParse($Version, [ref] $parsedVersion)) {
    throw 'Version must contain 2 to 4 numeric components, for example 1.2.0; pass -Version if the EXE has no version.'
}

$output = [System.IO.Path]::GetFullPath($OutputPath)
if ([string]::Equals($output, $executable, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputPath cannot overwrite the executable.'
}

$manifest = [ordered] @{
    version = $Version
    downloadUrl = $uri.AbsoluteUri
    sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    size = $file.Length
    notes = $Notes
}
$json = ($manifest | ConvertTo-Json -Depth 3) + [Environment]::NewLine
$encoding = New-Object System.Text.UTF8Encoding($false)
if ($encoding.GetByteCount($json) -gt 64KB) {
    throw 'The update manifest exceeds the 64 KiB client limit. Shorten Notes.'
}
$parent = [System.IO.Path]::GetDirectoryName($output)
[System.IO.Directory]::CreateDirectory($parent) | Out-Null
[System.IO.File]::WriteAllText($output, $json, $encoding)
Write-Output "Created $output (version $Version, $($file.Length) bytes)."
Write-Output 'Upload the EXE first and then this manifest to the same GitHub release.'
