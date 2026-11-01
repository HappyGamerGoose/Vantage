param(
    [Parameter(Mandatory)][string]$Version,
    [string]$ReleaseDir,
    [string]$OutputDir
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReleaseDir) { $ReleaseDir = Join-Path $repoRoot "releases\velopack" }
if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot "releases\github\$Version" }

# VeloPack's local index lists every package it keeps for building deltas. A
# GitHub release carries only this version, and a delta needs the previous
# version installed, so the published index names this full package alone.
$feed = Get-Content -LiteralPath (Join-Path $ReleaseDir "releases.win.json") -Raw | ConvertFrom-Json
$fullPackage = @($feed.Assets | Where-Object { $_.Version -eq $Version -and $_.Type -eq "Full" })
if ($fullPackage.Count -ne 1) { throw "Expected one full package for $Version, found $($fullPackage.Count)." }

$assets = @(
    "HappyGamerGoose.Vantage-win-Setup.exe",
    "HappyGamerGoose.Vantage-win-Portable.zip",
    $fullPackage[0].FileName
)
foreach ($asset in $assets) {
    if (-not (Test-Path -LiteralPath (Join-Path $ReleaseDir $asset))) { throw "Missing release asset: $asset" }
}

$hash = (Get-FileHash -LiteralPath (Join-Path $ReleaseDir $fullPackage[0].FileName) -Algorithm SHA256).Hash
if ($hash -ne $fullPackage[0].SHA256) { throw "$($fullPackage[0].FileName) does not match its checksum in the index." }

if (Test-Path -LiteralPath $OutputDir) { Remove-Item -LiteralPath $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($asset in $assets) {
    Copy-Item -LiteralPath (Join-Path $ReleaseDir $asset) -Destination $OutputDir
}

$index = '{"Assets":[' + ($fullPackage[0] | ConvertTo-Json -Compress) + ']}'
[System.IO.File]::WriteAllText(
    (Join-Path $OutputDir "releases.win.json"),
    $index,
    (New-Object System.Text.UTF8Encoding($false)))

Write-Host "GitHub release files for $Version are ready in $OutputDir"
