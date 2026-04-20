param(
    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [string]$Configuration = "Release",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$ProjectPath = "$PSScriptRoot\src\McpEngramMemory.Core\McpEngramMemory.Core.csproj"

# Extract version from csproj
$csproj = [xml](Get-Content $ProjectPath)
$version = $csproj.Project.PropertyGroup.Version
Write-Host "Publishing McpEngramMemory.Core v$version" -ForegroundColor Cyan

# Run tests unless skipped
if (-not $SkipTests) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    dotnet test "$PSScriptRoot\tests\McpEngramMemory.Tests" --configuration $Configuration --filter "Category!=MSA&Category!=LiveBenchmark&Category!=T2Benchmark"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed. Aborting publish." -ForegroundColor Red
        exit 1
    }
}

# Pack
Write-Host "`nPacking..." -ForegroundColor Yellow
dotnet pack $ProjectPath --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Pack failed." -ForegroundColor Red
    exit 1
}

$nupkg = "$PSScriptRoot\src\McpEngramMemory.Core\bin\$Configuration\McpEngramMemory.Core.$version.nupkg"
if (-not (Test-Path $nupkg)) {
    Write-Host "Package not found at $nupkg" -ForegroundColor Red
    exit 1
}

# Push
Write-Host "`nPushing to $Source..." -ForegroundColor Yellow
dotnet nuget push $nupkg --api-key $ApiKey --source $Source
if ($LASTEXITCODE -ne 0) {
    Write-Host "Push failed." -ForegroundColor Red
    exit 1
}

Write-Host "`nPublished McpEngramMemory.Core v$version" -ForegroundColor Green
