param(
    [string]$Version = "1.5.88",
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "src\Vantage.csproj"
$publishDir = Join-Path $repoRoot "artifacts\publish\$Runtime\$Version"
$releaseDir = Join-Path $repoRoot "releases\velopack"
$iconPath = Join-Path $repoRoot "src\Assets\AppIcon.ico"
$splashPath = Join-Path $repoRoot "src\Assets\SplashScreen.scale-200.png"
$signingPropsPath = Join-Path $repoRoot "src\.vantage.signing.props"

$resolvedRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$resolvedPublish = [System.IO.Path]::GetFullPath($publishDir)
$resolvedRelease = [System.IO.Path]::GetFullPath($releaseDir)
if (-not $resolvedPublish.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $resolvedRelease.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "A build directory resolved outside the repository."
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir -Force | Out-Null
New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null

& (Join-Path $PSScriptRoot "build-shell-icon.ps1")
if (-not $?) { throw "Application icon generation failed." }

# VeloPack protects an existing channel from same-version overwrites. Remove
# only this version and the generated channel indexes so a corrected rebuild
# remains possible without deleting older full packages used for deltas.
$sameVersionPatterns = @(
    "*-$Version-full.nupkg",
    "*-$Version-delta.nupkg",
    "*-win-Setup.exe",
    "*-win-Portable.zip",
    "assets.win.json",
    "releases.win.json",
    "RELEASES"
)
foreach ($pattern in $sameVersionPatterns) {
    Get-ChildItem -LiteralPath $releaseDir -Filter $pattern -File -ErrorAction SilentlyContinue |
        Remove-Item -Force
}

dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "VeloPack tool restore failed." }

dotnet publish $projectPath `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $publishDir `
    -p:Platform=x64 `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=false `
    -p:PublishTrimmed=false
if ($LASTEXITCODE -ne 0) { throw "Vantage publish failed." }

$velopackArguments = @(
    "tool", "run", "vpk", "--", "pack",
    "--packId", "HappyGamerGoose.Vantage",
    "--packVersion", $Version,
    "--packDir", $publishDir,
    "--mainExe", "Vantage.exe",
    "--packTitle", "Vantage",
    "--packAuthors", "Vantage",
    "--runtime", $Runtime,
    "--icon", $iconPath,
    "--splashImage", $splashPath,
    "--splashProgressColor", "#5442FF",
    "--shortcuts", "StartMenuRoot",
    "--outputDir", $releaseDir
)

if (Test-Path -LiteralPath $signingPropsPath) {
    [xml]$signingProps = Get-Content -LiteralPath $signingPropsPath
    $certificatePath = [string]$signingProps.Project.PropertyGroup.VANTAGE_CERT_PFX
    $certificatePassword = [string]$signingProps.Project.PropertyGroup.VANTAGE_CERT_PASSWORD
    if (-not [System.IO.Path]::IsPathRooted($certificatePath)) {
        $certificatePath = Join-Path (Split-Path -Parent $signingPropsPath) $certificatePath
    }
    if ((Test-Path -LiteralPath $certificatePath) -and $certificatePassword) {
        $signParameters = "/fd SHA256 /f `"$certificatePath`" /p `"$certificatePassword`""
        $velopackArguments += @("--signParams", $signParameters)
    }
}

& dotnet $velopackArguments
if ($LASTEXITCODE -ne 0) { throw "VeloPack packaging failed." }

Write-Host "Vantage $Version is ready in $releaseDir"
