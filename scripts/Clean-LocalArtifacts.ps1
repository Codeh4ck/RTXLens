#Requires -Version 7.0
<#
.SYNOPSIS
Removes generated local build and test artifacts and superseded RTX Lens release packages.

.DESCRIPTION
Keeps the latest verified local release, the current widget, build prerequisites and local hardware research.
Use -WhatIf to preview the cleanup.
#>
[CmdletBinding(SupportsShouldProcess)]
param()

$ErrorActionPreference = 'Stop'

$solution = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$checkout = git -C $solution rev-parse --show-toplevel

if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($checkout) -ne $solution)
{
    throw 'Run this script from an RTX Lens Git checkout.'
}

for ($folder = Get-Item -LiteralPath $solution -Force; $folder; $folder = $folder.Parent)
{
    if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)
    {
        throw "The checkout path contains a symbolic link or junction: $($folder.FullName)"
    }
}

function Get-LocalItem([string] $Path)
{
    $absolutePath = [IO.Path]::GetFullPath($Path, $solution)
    $relativePath = [IO.Path]::GetRelativePath($solution, $absolutePath)

    if ($relativePath -eq '.' -or $relativePath -eq '..' -or $relativePath.StartsWith('..\') -or
        $relativePath.StartsWith('../') -or [IO.Path]::IsPathRooted($relativePath))
    {
        throw "Cleanup path is outside the checkout: $Path"
    }

    $current = $solution
    $item = $null

    foreach ($part in ($relativePath -split '[\\/]'))
    {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue

        if (!$item)
        {
            return
        }

        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            Write-Warning "Skipping a path through a symbolic link or junction: $current"
            return
        }
    }

    $item
}

function Get-GeneratedFiles([string] $Folder, [string] $Filter)
{
    $directory = Get-LocalItem $Folder

    if ($directory -and $directory.PSIsContainer)
    {
        Get-ChildItem -LiteralPath $directory.FullName -File -Force -Filter $Filter | ForEach-Object {
            "$Folder/$($_.Name)"
        }
    }
}

function Get-SupersededReleases
{
    $manifest = Get-LocalItem '.dist/latest-release.json'
    $releases = Get-LocalItem '.dist/releases'

    if (!$manifest -or !$releases)
    {
        return
    }

    $latest = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json

    if ($latest.version -notmatch '^\d+\.\d+\.\d+$')
    {
        Write-Warning 'Skipping old release packages because the latest release version is invalid.'
        return
    }

    $current = ".dist/releases/$($latest.version)"
    $installer = Get-LocalItem "$current/RTXLens-Setup-$($latest.version)-win-x64.exe"
    $zip = Get-LocalItem "$current/RTXLens-win-x64.zip"

    if (!$installer -or !$zip -or (Get-FileHash -LiteralPath $zip.FullName -Algorithm SHA256).Hash -ne $latest.sha256)
    {
        Write-Warning 'Skipping old release packages because the latest local release could not be verified.'
        return
    }

    $currentVersion = [version] $latest.version

    Get-ChildItem -LiteralPath $releases.FullName -Directory -Force | ForEach-Object {
        $version = $null

        if ($_.Name -match '^\d+\.\d+\.\d+$' -and [version]::TryParse($_.Name, [ref] $version) -and
            $version -lt $currentVersion)
        {
            ".dist/releases/$($_.Name)"
        }
    }
}

$paths = @(
    # Release staging, old widget packages and temporary diagnostic runs.
    '.dist/build'
    '.dist/components'
    '.dist/connector-profile-desktop-smoke'
    '.dist/diagnostic-tool'
    '.dist/diagnostics-smoke'
    '.dist/rtx5090monitor'
    '.dist/rtx5090monitor.icuewidget'
    '.dist/RTXMonitor-Diagnostic-Tool.zip'
    '.dist/verification'
    '.dist/verification-desktop-gpu'
    '.dist/PublishGitHubRelease.ps1'
    '.dist/PublishGitHubRelease-1.0.1.ps1'
    '.dist/VerifyRelease-1.0.1.ps1'
    '.dist/release-notes-1.0.0.md'
    '.dist/release-notes-1.0.1.md'

    # Generated UI, service, packaging and crash investigation output.
    'artifacts/diagnostics-tools'
    'artifacts/exit-confirmation-ui'
    'artifacts/layout-native-ui'
    'artifacts/layout-validation'
    'artifacts/msi-curve-api-recovery-ui'
    'artifacts/msi-fan-curve-ui'
    'artifacts/msi-service-verification'
    'artifacts/release-0.4.0-upgrade-check.json'

    Get-GeneratedFiles '.dist' '*.log'
    Get-GeneratedFiles 'artifacts' '*.log'
    Get-GeneratedFiles 'artifacts' 'fan-curve-crash-*.txt'
    Get-SupersededReleases
)

foreach ($project in 'Backend', 'Desktop', 'Installer')
{
    $paths += "tests/$project/bin", "tests/$project/obj"
}

foreach ($project in 'Bootstrapper', 'Package')
{
    $paths += Get-GeneratedFiles "installer/$project/bin/x64/Release" 'RtxMonitor-*'
    $paths += Get-GeneratedFiles "installer/$project/obj/Release" 'RtxMonitor-*'
}

$targets = @{}

foreach ($path in ($paths | Sort-Object -Unique))
{
    $item = Get-LocalItem $path

    if ($item)
    {
        $targets[$path] = $item
    }
}

if ($targets.Count -eq 0)
{
    Write-Host 'No stale local artifacts found.'
    return
}

# Check the Git index once for the entire cleanup plan.
$targetPaths = @($targets.Keys)
$tracked = git --literal-pathspecs -C $solution ls-files -- $targetPaths

if ($LASTEXITCODE -ne 0 -or $tracked)
{
    throw 'Cleanup stopped because a target includes tracked files or the Git index could not be checked.'
}

$ignored = git -C $solution -c core.quotePath=false check-ignore -- $targetPaths

if ($LASTEXITCODE -ne 0 -or (Compare-Object $targetPaths @($ignored)))
{
    throw 'Cleanup stopped because a target is not ignored by Git.'
}

$removed = 0

foreach ($path in ($targetPaths | Sort-Object))
{
    $item = $targets[$path]

    if ($item.PSIsContainer -and (Get-ChildItem -LiteralPath $item.FullName -Recurse -Force |
            Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1))
    {
        Write-Warning "Skipping a directory that contains a symbolic link or junction: $path"
        continue
    }

    if ($PSCmdlet.ShouldProcess($item.FullName, 'Remove generated artifact'))
    {
        Remove-Item -LiteralPath $item.FullName -Recurse:$item.PSIsContainer -Force
        $removed++
    }
}

if (!$WhatIfPreference)
{
    Write-Host "Removed $removed local artifact files and folders."
}
