#Requires -Version 7.0
<# Builds local release artifacts; never executes the installer or changes an installed service. #>
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [string] $VcRedistPath,
    [switch] $SkipChecks,

    [ValidatePattern('^[A-Fa-f0-9]{40}$')][string] $SigningCertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'

Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot 'Release.psm1') -Force

$solution = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$manifestPath = Join-Path $solution 'version.json'
$versions = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

if ($versions.schemaVersion -ne 1 -or $versions.releaseTag -notmatch '^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')
{
    throw 'version.json contains an unsupported release tag or schema.'
}

foreach ($component in @('desktop', 'service', 'widget'))
{
    if ($versions.$component -isnot [string] -or $versions.$component -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')
    {
        throw "Invalid $component version in version.json."
    }
}

$manifestVersion = $versions.releaseTag.Substring(1)

if ($Version -and $Version -ne $manifestVersion)
{
    throw 'The release version must match version.json. Update the manifest before packaging.'
}

$Version = $manifestVersion
$work = Join-Path $solution ('.dist\build\' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
$release = Join-Path $solution ".dist\releases\$Version"

New-Item -ItemType Directory -Path $payload -Force | Out-Null

if (-not $VcRedistPath)
{
    $VcRedistPath = Join-Path $solution '.dist\prerequisites\vc_redist.x64.exe'
}

$VcRedistPath = [IO.Path]::GetFullPath($VcRedistPath)
$vcVersion = Get-VerifiedVcRedistributable $VcRedistPath

Push-Location $solution

try
{
    if (-not $SkipChecks)
    {
        Invoke-ReleaseCommand dotnet @('run', '--project', 'tests/Backend', '-c', 'Release')

        Invoke-ReleaseCommand dotnet @('run', '--project', 'tests/Desktop', '-c', 'Release', '-p:EnableTelemetryDebug=true')

        Invoke-ReleaseCommand dotnet @('run', '--project', 'tests/Installer', '-c', 'Release')
    }

    Invoke-ReleaseCommand dotnet @('publish', 'src/Service', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-o', (Join-Path $payload 'Service'))

    Invoke-ReleaseCommand dotnet @('publish', 'src/Desktop/App', '-c', 'Release', '-p:PublishProfile=Portable',
        '-p:EnableTelemetryDebug=true', '-o', (Join-Path $payload 'Desktop'), '-m:1')

    if (-not $SkipChecks)
    {
        Invoke-ReleaseCommand dotnet @('run', '--project', 'tests/Desktop', '-c', 'Release', '-p:EnableTelemetryDebug=true',
            '--', '--verify-release', (Join-Path $payload 'Desktop'))
    }

    Push-Location (Join-Path $solution 'src/Widget')

    try
    {
        if (-not (Test-Path -LiteralPath 'node_modules'))
        {
            Invoke-ReleaseCommand npm.cmd @('ci')
        }

        $widgetBuild = @('run', 'build')

        if ($SkipChecks)
        {
            $widgetBuild += @('--', '--skip-checks')
        }

        Invoke-ReleaseCommand npm.cmd $widgetBuild

        if (-not $SkipChecks)
        {
            Invoke-ReleaseCommand npm.cmd @('run', 'verify')
        }
    }
    finally
    {
        Pop-Location
    }

    $signTool = ''

    if ($SigningCertificateThumbprint)
    {
        $signTool = Get-ReleaseSignTool

        foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Name -like 'RTXLens.*' -and $_.Extension -in @('.exe', '.dll') })
        {
            Invoke-ReleaseCommand $signTool @('sign', '/sha1', $SigningCertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $file.FullName)
        }
    }

    $buildProperties = @('-t:Rebuild', '-c', 'Release', "-p:InstallerVersion=$Version", "-p:PayloadDirectory=$payload")

    Invoke-ReleaseCommand dotnet (@('build', 'installer/Package/RTXLens.Installer.wixproj') + $buildProperties)

    $msi = Join-Path $solution "installer\Package\bin\x64\Release\RTXLens-$Version-win-x64.msi"

    if ($SigningCertificateThumbprint)
    {
        Invoke-ReleaseCommand $signTool @('sign', '/sha1', $SigningCertificateThumbprint, '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', $msi)
    }

    if (-not $SkipChecks)
    {
        Invoke-ReleaseCommand dotnet @('run', '--project', 'tests/Installer', '-c', 'Release', '--', '--msi', $msi, $payload)
    }

    Invoke-ReleaseCommand dotnet (@('build', 'installer/Bootstrapper/RTXLens.Bootstrapper.wixproj') + $buildProperties +
        @('-p:BuildProjectReferences=false', "-p:VcRedistPath=$VcRedistPath", "-p:VcRedistVersion=$vcVersion",
            "-p:SigningCertificateThumbprint=$SigningCertificateThumbprint", "-p:TimestampUrl=$TimestampUrl", "-p:SignToolPath=$signTool"))

    $setup = Join-Path $solution "installer\Bootstrapper\bin\x64\Release\RTXLens-Setup-$Version-win-x64.exe"

    if (-not $SkipChecks)
    {
        & (Join-Path $PSScriptRoot 'Verify-Setup.ps1') -SetupPath $setup -MsiPath $msi -VcRedistPath $VcRedistPath -OutputDirectory (Join-Path $work 'extracted-setup')
    }

    New-Item -ItemType Directory -Path $release -Force | Out-Null

    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $release 'version.json') -Force
    Copy-Item -LiteralPath $setup -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $solution '.dist\rtxlens.icuewidget') -Destination $release -Force
    Copy-Item -LiteralPath (Join-Path $solution 'installer\Resources\ReleaseReadme.txt') -Destination (Join-Path $release 'README.txt') -Force
    Copy-Item -LiteralPath (Join-Path $solution 'installer\Resources\CHANGELOG.txt') -Destination $release -Force

    & (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -SolutionDirectory $solution -OutputPath (Join-Path $release 'THIRD-PARTY-NOTICES.txt')

    Copy-Item -LiteralPath (Join-Path $solution 'scripts/uninstall.ps1') -Destination (Join-Path $release 'Uninstall-RTXLens.ps1') -Force

    $widget = Get-Content -LiteralPath (Join-Path $solution '.dist\latest.json') -Raw | ConvertFrom-Json
    $commit = 'unavailable'
    $sourceHasChanges = $null

    if (Test-Path -LiteralPath (Join-Path $solution '.git'))
    {
        $commit = & git rev-parse HEAD

        if ($LASTEXITCODE -ne 0)
        {
            $commit = 'unavailable'
        }
        else
        {
            $sourceHasChanges = [bool](& git status --porcelain)
        }
    }

    $metadata = [ordered]@{
        version = $Version
        runtime = 'win-x64'
        widgetVersion = $widget.version
        sourceCommit = $commit
        sourceHasChanges = $sourceHasChanges
        signed = [bool]$SigningCertificateThumbprint
        vcRedistVersion = $vcVersion
        vcRedistSha256 = (Get-FileHash -LiteralPath $VcRedistPath -Algorithm SHA256).Hash
        builtAt = [DateTime]::UtcNow.ToString('o')
        cleanMachineAcceptance = 'pending'
        checksSkipped = [bool]$SkipChecks
        serviceVersion = $versions.service
        desktopVersion = $versions.desktop
    }

    $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'release.json') -Encoding UTF8

    Write-ReleaseFileManifest $release

    $zip = Join-Path $release 'RTXLens-win-x64.zip'
    $entries = Get-ChildItem -LiteralPath $release -File | Where-Object Extension -ne '.zip'

    Compress-Archive -LiteralPath $entries.FullName -DestinationPath $zip -Force

    [ordered]@{
        version = $Version
        installer = (Join-Path $release ([IO.Path]::GetFileName($setup)))
        zip = $zip
        sha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
        signed = [bool]$SigningCertificateThumbprint
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $solution '.dist\latest-release.json') -Encoding UTF8

    Write-Host "Release ZIP: $zip" -ForegroundColor Green

    if (-not $SigningCertificateThumbprint)
    {
        Write-Warning 'Unsigned release candidate. Code signing and clean-machine acceptance are required before a public release.'
    }
}
finally
{
    Pop-Location
}
