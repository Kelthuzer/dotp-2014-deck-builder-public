$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK was not found. Install .NET 8 SDK and run this script again.'
}

$version = (& dotnet --version).Trim()
if (-not $version.StartsWith('8.')) {
    Write-Warning "This project is tested with .NET 8 SDK. Detected: $version"
}

$shortSha = 'local'
if (Get-Command git -ErrorAction SilentlyContinue) {
    try {
        $candidate = (& git rev-parse --short HEAD 2>$null).Trim()
        if ($candidate) { $shortSha = $candidate }
    }
    catch { }
}

$buildId = "local-$shortSha"
$outDir = Join-Path $root 'artifacts\modern-x64-local'
$zipPath = Join-Path $root 'artifacts\dotp-2014-deck-builder-modern-x64-local.zip'

Write-Host "[1/4] Core checks"
& dotnet run --project 'tests\DeckBuilder.Core.Checks\DeckBuilder.Core.Checks.csproj' --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Core checks failed: $LASTEXITCODE" }

Write-Host "[2/4] Modern checks"
& dotnet run --project 'tests\DeckBuilder.Modern.Checks\DeckBuilder.Modern.Checks.csproj' --configuration Release
if ($LASTEXITCODE -ne 0) { throw "Modern checks failed: $LASTEXITCODE" }

Write-Host "[3/4] Publish Modern x64 ($buildId)"
Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
& dotnet publish `
    'src\DeckBuilder.Modern\DeckBuilder.Modern.csproj' `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $outDir `
    -p:PublishSingleFile=true `
    -p:BuildIdentifier=$buildId
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }

$exe = Join-Path $outDir 'DotP 2014 Deck Builder Modern.exe'
$squish = Join-Path $outDir 'squish_64.dll'
if (-not (Test-Path $exe)) { throw "Missing output: $exe" }
if (-not (Test-Path $squish)) { throw "Missing output: $squish" }

Write-Host '[4/4] Package ZIP'
New-Item (Split-Path $zipPath -Parent) -ItemType Directory -Force | Out-Null
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -Force

Write-Host ''
Write-Host 'BUILD OK'
Write-Host "EXE: $exe"
Write-Host "ZIP: $zipPath"
