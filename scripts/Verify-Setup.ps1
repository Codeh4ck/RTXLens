#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string] $SetupPath,
    [Parameter(Mandatory)][string] $MsiPath,
    [Parameter(Mandatory)][string] $VcRedistPath,
    [Parameter(Mandatory)][string] $PawnIoSetupPath,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'Release.psm1') -Force

$tool = Join-Path $env:USERPROFILE '.nuget/packages/wixtoolset.sdk/7.0.0/tools/net472/x64/wix.exe'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$baDirectory = Join-Path $OutputDirectory 'bootstrapper'
Invoke-ReleaseCommand $tool @('burn', 'extract', '-acceptEula', 'wix7', $SetupPath, '-o', $OutputDirectory, '-oba', $baDirectory)

[xml]$baData = Get-Content -LiteralPath (Join-Path $baDirectory 'BootstrapperApplicationData.xml') -Raw
$focus = $baData.SelectSingleNode("//*[local-name()='WixBalBAFunctions' and @FilePath='SetupFocus.dll']")
if (!$focus -or !(Test-Path -LiteralPath (Join-Path $baDirectory 'SetupFocus.dll') -PathType Leaf))
{
    throw 'Setup is missing its registered window-focus extension.'
}

foreach ($source in @($MsiPath, $VcRedistPath, $PawnIoSetupPath))
{
    $expected = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $matches = @(Get-ChildItem -LiteralPath $OutputDirectory -Recurse -File | Where-Object {
        $_.Length -eq (Get-Item -LiteralPath $source).Length -and (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -eq $expected
    })

    if ($matches.Count -ne 1)
    {
        throw "Offline setup does not contain exactly one matching payload for $source."
    }
}

Write-Host 'PASS: Setup contains the exact validated MSI and both verified prerequisites; extraction executed no installer actions.'
