param(
    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [string]$Configuration = "Release",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$SkipTests,
    [switch]$CoreOnly,
    [int]$IndexWaitSeconds = 180
)

$ErrorActionPreference = "Stop"

$CoreProject = "$PSScriptRoot\src\McpEngramMemory.Core\McpEngramMemory.Core.csproj"
$ToolProject = "$PSScriptRoot\src\McpEngramMemory\McpEngramMemory.csproj"

# Extract versions
$coreXml = [xml](Get-Content $CoreProject)
$coreVersion = $coreXml.Project.PropertyGroup.Version
$toolXml = [xml](Get-Content $ToolProject)
$toolVersion = $toolXml.Project.PropertyGroup.Version

if ($CoreOnly) {
    Write-Host "Publishing McpEngramMemory.Core v$coreVersion (core only)" -ForegroundColor Cyan
} else {
    Write-Host "Publishing McpEngramMemory.Core v$coreVersion + McpEngramMemory v$toolVersion" -ForegroundColor Cyan
    if ($coreVersion -ne $toolVersion) {
        Write-Host "WARNING: Core ($coreVersion) and tool ($toolVersion) versions differ — the tool package depends on Core, so intentional skew only." -ForegroundColor Yellow
    }
}

# Run tests unless skipped
if (-not $SkipTests) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    dotnet test "$PSScriptRoot\tests\McpEngramMemory.Tests" --configuration $Configuration --filter "Category!=MSA&Category!=LiveBenchmark&Category!=T2Benchmark"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed. Aborting publish." -ForegroundColor Red
        exit 1
    }
}

function Publish-Package {
    param(
        [string]$ProjectPath,
        [string]$PackageId,
        [string]$Version,
        [string]$OutputDir
    )

    Write-Host "`nPacking $PackageId v$Version..." -ForegroundColor Yellow
    dotnet pack $ProjectPath --configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Pack failed for $PackageId." -ForegroundColor Red
        exit 1
    }

    $nupkg = Join-Path $OutputDir "$PackageId.$Version.nupkg"
    if (-not (Test-Path $nupkg)) {
        Write-Host "Package not found at $nupkg" -ForegroundColor Red
        exit 1
    }

    Write-Host "Pushing $PackageId v$Version to $Source..." -ForegroundColor Yellow
    dotnet nuget push $nupkg --api-key $ApiKey --source $Source
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Push failed for $PackageId." -ForegroundColor Red
        exit 1
    }

    Write-Host "Published $PackageId v$Version" -ForegroundColor Green
}

# Core must land first — the tool package has a PackageReference on it, so a
# tool install against nuget.org will fail if Core hasn't propagated yet.
Publish-Package `
    -ProjectPath $CoreProject `
    -PackageId "McpEngramMemory.Core" `
    -Version $coreVersion `
    -OutputDir "$PSScriptRoot\src\McpEngramMemory.Core\bin\$Configuration"

if ($CoreOnly) {
    Write-Host "`nDone (core only)." -ForegroundColor Green
    exit 0
}

# Wait for nuget.org to index Core before publishing the tool package, otherwise
# consumers of the tool would hit NU1102 "unable to find McpEngramMemory.Core"
# until the index catches up (usually under a minute on nuget.org).
Write-Host "`nWaiting ${IndexWaitSeconds}s for nuget.org to index Core v$coreVersion before publishing tool..." -ForegroundColor Yellow
Start-Sleep -Seconds $IndexWaitSeconds

Publish-Package `
    -ProjectPath $ToolProject `
    -PackageId "McpEngramMemory" `
    -Version $toolVersion `
    -OutputDir "$PSScriptRoot\src\McpEngramMemory\bin\$Configuration"

Write-Host "`nAll packages published." -ForegroundColor Green
