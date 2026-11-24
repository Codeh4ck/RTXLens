#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $SolutionDirectory,
    [Parameter(Mandatory)][string] $OutputPath
)

$ErrorActionPreference = 'Stop'

$packages = @{}

foreach ($project in @('src/Service', 'src/Desktop/App', 'src/Desktop/Notifier', 'installer/Actions'))
{
    $assets = Get-Content -LiteralPath (Join-Path $SolutionDirectory "$project/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable

    foreach ($entry in $assets.libraries.GetEnumerator())
    {
        if ($entry.Value.type -ne 'package')
        {
            continue
        }

        foreach ($folder in $assets.packageFolders.Keys)
        {
            $directory = Join-Path $folder $entry.Value.path

            if (Test-Path -LiteralPath $directory -PathType Container)
            {
                $packages[$entry.Key] = $directory
                break
            }
        }
    }
}

$sections = [Collections.Generic.List[string]]::new()
$sections.Add((Get-Content -LiteralPath (Join-Path $SolutionDirectory 'installer/Resources/THIRD-PARTY-NOTICES.txt') -Raw))

foreach ($entry in $packages.GetEnumerator() | Sort-Object Key)
{
    $nuspec = Get-ChildItem -LiteralPath $entry.Value -Filter '*.nuspec' | Select-Object -First 1

    if (-not $nuspec)
    {
        throw "Missing license metadata for $($entry.Key)."
    }

    [xml] $document = Get-Content -LiteralPath $nuspec.FullName -Raw
    $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $text = "`n====================`n$($entry.Key)`n"

    if ($license -and $license.GetAttribute('type') -eq 'file')
    {
        $licensePath = [IO.Path]::GetFullPath((Join-Path $entry.Value $license.InnerText))

        if (-not $licensePath.StartsWith([IO.Path]::GetFullPath($entry.Value) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
        {
            throw 'Invalid package license path.'
        }

        $text += Get-Content -LiteralPath $licensePath -Raw
    }
    elseif ($license -and $license.GetAttribute('type') -eq 'expression')
    {
        $text += "SPDX license expression: $($license.InnerText)`n"

        foreach ($name in [regex]::Matches($license.InnerText, '[A-Za-z0-9][A-Za-z0-9.+-]*').Value | Where-Object { $_ -notin @('AND', 'OR', 'WITH') } | Select-Object -Unique)
        {
            $cache = Join-Path $SolutionDirectory ".dist/licenses/$name.txt"

            if (-not (Test-Path -LiteralPath $cache))
            {
                New-Item -ItemType Directory -Path (Split-Path -Parent $cache) -Force | Out-Null
                Invoke-WebRequest -Uri "https://raw.githubusercontent.com/spdx/license-list-data/main/text/$name.txt" -OutFile $cache
            }

            $text += "`n" + (Get-Content -LiteralPath $cache -Raw)
        }
    }
    else
    {
        $url = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
        $text += "License reference: $($url.InnerText)`n"

        foreach ($file in Get-ChildItem -LiteralPath $entry.Value -File | Where-Object { $_.Name -match '^(LICENSE|NOTICE|THIRD-PARTY)' })
        {
            $text += "`n$($file.Name)`n" + (Get-Content -LiteralPath $file.FullName -Raw)
        }
    }

    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')

    if ($repository)
    {
        $text += "`nSource: $($repository.GetAttribute('url'))`nSource commit: $($repository.GetAttribute('commit'))`n"
    }

    $sections.Add($text)
}

$sections.Add((Get-Content -LiteralPath (Join-Path $SolutionDirectory 'src/Widget/THIRD-PARTY-NOTICES.md') -Raw))

$sections | Set-Content -LiteralPath $OutputPath -Encoding UTF8
