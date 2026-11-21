#Requires -Version 7.0
<#
.SYNOPSIS
Removes generated local build and test artifacts and superseded RTX Lens release packages.

.DESCRIPTION
Keeps the latest verified local release, the current widget, build prerequisites and local hardware research.
The full cleanup includes ignored compiler outputs and project-specific Windows temp artifacts. Run after builds and tests finish.
Use -ListOnly to return candidate paths.
Pass these absolute paths with -LimitToPath to clean just a reviewed selection.
Use -PreviewsOnly to remove preview builds, -KeepPreview to retain specific preview folder names and -WhatIf to preview the cleanup.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [switch] $PreviewsOnly,
    [string[]] $KeepPreview = @(),
    [string[]] $LimitToPath,
    [switch] $ListOnly
)

$ErrorActionPreference = 'Stop'

$solution = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

function Assert-SafeRoot([string] $Root)
{
    for ($folder = Get-Item -LiteralPath $Root -Force; $folder; $folder = $folder.Parent)
    {
        if ($folder.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            throw "Cleanup root contains a symbolic link or junction: $($folder.FullName)"
        }
    }
}

function Assert-Checkout([string] $Root)
{
    Assert-SafeRoot $Root
    $checkout = git -C $Root rev-parse --show-toplevel
    if ($LASTEXITCODE -ne 0 -or [IO.Path]::GetFullPath($checkout) -ne $Root)
    {
        throw "Expected a Git checkout at $Root"
    }
}

Assert-Checkout $solution

function Get-LocalItem([string] $Path, [string] $Root = $solution)
{
    $absolutePath = [IO.Path]::GetFullPath($Path, $Root)
    $relativePath = [IO.Path]::GetRelativePath($Root, $absolutePath)

    if ($relativePath -eq '.' -or $relativePath -eq '..' -or $relativePath.StartsWith('..\') -or
        $relativePath.StartsWith('../') -or [IO.Path]::IsPathRooted($relativePath))
    {
        throw "Cleanup path is outside the checkout: $Path"
    }

    $current = $Root
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

    '.dist/superseded'
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

function Get-PreviewBuilds
{
    $olderPreviewNames = 'feature-parity', 'msi-pin-tabs'

    foreach ($tree in 'src', 'tests')
    {
        $directory = Get-LocalItem $tree
        if (!$directory)
        {
            continue
        }

        Get-ChildItem -LiteralPath $directory.FullName -Directory -Recurse -Filter 'bin' |
            ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory } |
            Where-Object { ($_.Name -like '*-preview' -or $_.Name -in $olderPreviewNames) -and $_.Name -notin $KeepPreview } |
            ForEach-Object { [IO.Path]::GetRelativePath($solution, $_.FullName).Replace('\', '/') }
    }

    foreach ($folder in '.dist', '.dist/build', 'artifacts')
    {
        $directory = Get-LocalItem $folder
        if ($directory)
        {
            Get-ChildItem -LiteralPath $directory.FullName -Directory -Filter '*-preview' |
                Where-Object Name -NotIn $KeepPreview |
                ForEach-Object { "$folder/$($_.Name)" }
        }
    }
}

function Get-BuildStaging
{
    $build = Get-LocalItem '.dist/build'
    if (!$build) { return }

    foreach ($dir in Get-ChildItem -LiteralPath $build.FullName -Directory)
    {
        $relative = ".dist/build/$($dir.Name)"
        if ($dir.Name -match '^[0-9a-f]{32}$') { $relative; continue }
        if ($dir.Name -notmatch '^mobile-run-\d+$' -or !(Get-LocalItem $relative)) { continue }

        # A downloaded workflow run may still be the only local copy of its mobile packages.
        $manifest = Get-LocalItem '.dist/latest-release.json'
        if (!$manifest) { continue }
        $version = (Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json).version
        if ($version -notmatch '^\d+\.\d+\.\d+$') { continue }
        $packages = 'RTX-Lens-preview.apk', 'RTX-Lens-unsigned.ipa'
        if (Get-ChildItem -LiteralPath $dir.FullName -Force | Where-Object Name -NotIn ($packages + 'apk.zip' + 'ipa.zip')) { continue }
        $verified = $true
        foreach ($name in $packages)
        {
            $staged = Get-LocalItem "$relative/$name"
            $shipped = Get-LocalItem ".dist/releases/$version/$name"
            if (!$staged -or !$shipped -or (Get-FileHash -LiteralPath $staged.FullName).Hash -ne (Get-FileHash -LiteralPath $shipped.FullName).Hash)
            {
                $verified = $false
                break
            }
        }
        if ($verified) { $relative }
    }
}

function Get-ProjectOutputs([string] $Root)
{
    $projects = git -C $Root ls-files -- '*.csproj' '*.wixproj'
    if ($LASTEXITCODE -ne 0) { throw "Could not enumerate projects in $Root" }
    foreach ($project in $projects)
    {
        $dir = [IO.Path]::GetDirectoryName($project)
        foreach ($output in 'bin', 'obj')
        {
            [IO.Path]::Combine($dir, $output).Replace('\', '/')
        }
    }
}

function Get-TempArtifacts([string] $Root)
{
    $id = '[a-f0-9]{32}'
    $fixture = "^(rtxlens(-fault-headline|-apk-validate|-pcie-test|-wireview-smoke|-v\d+\.\d+\.\d+-mobile)?|rtx-safeguard-fixture)-$id$"
    $log = '^rtxlens-(owner-[\w-]+|plugin-isolation-(final|repeat|tests)|\d+\.\d+\.\d+-(merged-release|release-build))\.log$'
    foreach ($item in Get-ChildItem -LiteralPath $Root -Force)
    {
        if (($item.PSIsContainer -and ($item.Name -match $fixture -or $item.Name -eq 'RTX-Lens-unsigned.ipa_extracted')) -or
            (!$item.PSIsContainer -and $item.Name -match $log))
        {
            $item.Name
        }
    }
}

$paths = @(Get-PreviewBuilds)
if (!$PreviewsOnly)
{
    $paths += @(
        # Release staging, old widget packages and temporary diagnostic runs.
        Get-BuildStaging
        '.dist/rtxlens'
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

    $paths += Get-ProjectOutputs $solution
}

$targets = @{}
function Add-Targets([string] $Root, [string[]] $Paths, [switch] $CheckGit)
{
    $relative = foreach ($path in ($Paths | Where-Object { $_ } | Sort-Object -Unique))
    {
        $item = Get-LocalItem $path $Root
        if (!$item) { continue }
        if ($item.Name -in $KeepPreview) { continue }

        # A kept preview inside bin must survive a full cleanup of its siblings.
        $children = if ($item.PSIsContainer -and $KeepPreview.Count) { @(Get-ChildItem -LiteralPath $item.FullName -Force) }
        if ($children -and ($children.Name | Where-Object { $_ -in $KeepPreview }))
        {
            foreach ($child in $children | Where-Object Name -NotIn $KeepPreview)
            {
                [IO.Path]::GetRelativePath($Root, $child.FullName).Replace('\', '/')
            }
        }
        else { $path }
    }

    $relative = @($relative | Sort-Object -Unique)
    if (!$relative.Count) { return }

    if ($CheckGit)
    {
        $tracked = git --literal-pathspecs -C $Root ls-files -- $relative
        if ($LASTEXITCODE -ne 0 -or $tracked) { throw "Tracked files or unreadable Git index under a cleanup target in $Root" }
        $ignored = git -C $Root -c core.quotePath=false check-ignore -- $relative
        if ($LASTEXITCODE -ne 0 -or (Compare-Object $relative @($ignored))) { throw "Cleanup target is not ignored by Git in $Root" }
    }

    foreach ($path in $relative)
    {
        $item = Get-LocalItem $path $Root
        if ($item) { $targets[$item.FullName] = @{ Root = $Root; Relative = $path } }
    }
}

Add-Targets $solution $paths -CheckGit
if (!$PreviewsOnly -and $IsWindows)
{
    $tempRoot = (Get-Item -LiteralPath ([IO.Path]::GetTempPath())).FullName.TrimEnd('\')
    Assert-SafeRoot $tempRoot
    Add-Targets $tempRoot @(Get-TempArtifacts $tempRoot)
}

$removed = 0
$failed = 0
foreach ($absolute in ($targets.Keys | Sort-Object))
{
    if ($PSBoundParameters.ContainsKey('LimitToPath') -and $absolute -notin $LimitToPath) { continue }
    $target = $targets[$absolute]
    $item = Get-LocalItem $target.Relative $target.Root
    if (!$item) { continue }

    # A full bin cleanup already includes its preview subdirectories.
    if ($item.PSIsContainer -and $targets.ContainsKey($item.Parent.FullName)) { continue }

    if ($item.PSIsContainer -and (Get-ChildItem -LiteralPath $item.FullName -Recurse -Force |
            Where-Object { $_.Name -eq '.git' -or $_.Attributes -band [IO.FileAttributes]::ReparsePoint } |
            Select-Object -First 1))
    {
        Write-Warning "Skipping a directory containing a Git checkout, symbolic link or junction: $absolute"
        continue
    }

    if ($ListOnly) { $absolute; continue }
    if ($PSCmdlet.ShouldProcess($absolute, 'Remove generated artifact'))
    {
        try
        {
            Remove-Item -LiteralPath $absolute -Recurse:$item.PSIsContainer -Force
            $removed++
        }
        catch
        {
            Write-Warning "Could not finish removing $absolute. $($_.Exception.Message)"
            $failed++
        }
    }
}
if (!$ListOnly -and !$WhatIfPreference) { Write-Host "Removed $removed local artifact files and folders." }
if ($failed) { throw "Cleanup could not remove $failed local artifact files or folders." }
