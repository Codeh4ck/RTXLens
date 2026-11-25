#Requires -Version 7.0
param([Parameter(Mandatory)][string] $Directory)

$ErrorActionPreference = 'Stop'
$release = Invoke-RestMethod -Uri 'https://api.github.com/repos/namazso/PawnIO.Setup/releases/latest' -Headers @{ Accept = 'application/vnd.github+json' }
$assets = @($release.assets | Where-Object name -ceq 'PawnIO_setup.exe')
if ($release.draft -or $release.prerelease -or $release.tag_name -notmatch '^v?\d+\.\d+\.\d+(\.\d+)?$' -or $assets.Count -ne 1)
{
    throw 'The latest official PawnIO release must identify a stable version and exactly one PawnIO_setup.exe.'
}

$asset = $assets[0]
if ($asset.digest -notmatch '^sha256:([a-f0-9]{64})$')
{
    throw "Missing SHA-256 for PawnIO $($release.tag_name)."
}
$expected = $Matches[1]
[void][IO.Directory]::CreateDirectory($Directory)
$path = Join-Path $Directory "PawnIO_setup-$($release.tag_name).exe"
if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash -ne $expected)
{
    $pending = "$path.$([Guid]::NewGuid()).tmp.exe"
    try
    {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $pending
        if ((Get-FileHash -LiteralPath $pending).Hash -ne $expected) { throw 'PawnIO download does not match its release digest.' }
        [IO.File]::Move($pending, $path, $true)
    }
    finally { [IO.File]::Delete($pending) }
}

$signature = Get-AuthenticodeSignature -LiteralPath $path
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=namazso(?:,|$)')
{
    throw "Invalid PawnIO publisher signature: $path"
}
$version = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$')
{
    throw "Invalid PawnIO installer product version: $version"
}
[pscustomobject]@{ Path = $path; Version = $version; ReleaseTag = $release.tag_name; Sha256 = $expected }
