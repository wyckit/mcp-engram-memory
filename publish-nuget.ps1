param(
    [Parameter(Mandatory=$true)]
    [string]$ApiKey,

    [string]$Configuration = "Release",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [switch]$SkipTests,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

# Publish order is a dependency order, not a preference: Core must be live before
# Synthesis.Onnx and the server can resolve against it on a clean feed.
$Packages = @(
    @{ Name = "McpEngramMemory.Core";           Path = "$PSScriptRoot\src\McpEngramMemory.Core\McpEngramMemory.Core.csproj" },
    @{ Name = "McpEngramMemory.Synthesis.Onnx"; Path = "$PSScriptRoot\src\McpEngramMemory.Synthesis.Onnx\McpEngramMemory.Synthesis.Onnx.csproj" },
    @{ Name = "McpEngramMemory";                Path = "$PSScriptRoot\src\McpEngramMemory\McpEngramMemory.csproj" }
)

# nuget.org rejects anything larger with 413 RequestEntityTooLarge. Catching it here
# beats discovering it after the first package of a set has already gone live -
# published versions can be unlisted but never deleted.
$MaxPackageBytes = 250MB

$versions = @{}
foreach ($pkg in $Packages) {
    $csproj = [xml](Get-Content $pkg.Path)
    $v = ($csproj.Project.PropertyGroup.Version | Where-Object { $_ }) | Select-Object -First 1
    if (-not $v) { Write-Host "No <Version> found in $($pkg.Path)" -ForegroundColor Red; exit 1 }
    $versions[$pkg.Name] = $v
}

# @() is load-bearing: with all versions equal, Sort-Object -Unique returns a bare
# string, and indexing [0] into a string yields its first character ("1"), not "1.4.0".
$distinct = @($versions.Values | Sort-Object -Unique)
if ($distinct.Count -ne 1) {
    Write-Host "Version mismatch across packages:" -ForegroundColor Red
    $versions.GetEnumerator() | ForEach-Object { Write-Host "  $($_.Key) = $($_.Value)" -ForegroundColor Red }
    Write-Host "All three packages ship as a set; align them before publishing." -ForegroundColor Red
    exit 1
}
$version = $distinct[0]
Write-Host "Publishing MCP Engram Memory v$version ($($Packages.Count) packages)" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "`nRunning tests..." -ForegroundColor Yellow
    dotnet test "$PSScriptRoot\tests\McpEngramMemory.Tests" --configuration $Configuration --filter "Category!=MSA&Category!=LiveBenchmark&Category!=T2Benchmark"
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Tests failed. Aborting publish." -ForegroundColor Red
        exit 1
    }
}

# Pack everything and validate sizes BEFORE pushing anything.
$artifacts = "$PSScriptRoot\artifacts"
Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "`nPacking..." -ForegroundColor Yellow
foreach ($pkg in $Packages) {
    dotnet pack $pkg.Path --configuration $Configuration --output $artifacts
    if ($LASTEXITCODE -ne 0) { Write-Host "Pack failed for $($pkg.Name)." -ForegroundColor Red; exit 1 }
}

$nupkgs = @()
foreach ($pkg in $Packages) {
    $nupkg = Join-Path $artifacts "$($pkg.Name).$version.nupkg"
    if (-not (Test-Path $nupkg)) { Write-Host "Package not found at $nupkg" -ForegroundColor Red; exit 1 }
    $size = (Get-Item $nupkg).Length
    $mb = [math]::Round($size / 1MB, 1)
    if ($size -gt $MaxPackageBytes) {
        Write-Host "$($pkg.Name) is $mb MB, over nuget.org's 250 MB limit. Aborting before any push." -ForegroundColor Red
        exit 1
    }
    Write-Host ("  {0,-34} {1,7} MB" -f $pkg.Name, $mb) -ForegroundColor Gray
    $nupkgs += $nupkg
}

if ($WhatIf) {
    Write-Host "`n-WhatIf: packed and validated, nothing pushed." -ForegroundColor Yellow
    exit 0
}

# Push in dependency order. A failure part-way leaves earlier packages live -
# nuget.org has no delete - so report exactly where it stopped.
Write-Host "`nPushing to $Source..." -ForegroundColor Yellow
for ($i = 0; $i -lt $nupkgs.Count; $i++) {
    $name = $Packages[$i].Name
    Write-Host "  pushing $name..." -ForegroundColor Gray
    dotnet nuget push $nupkgs[$i] --api-key $ApiKey --source $Source
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Push failed for $name." -ForegroundColor Red
        if ($i -gt 0) {
            $done = ($Packages[0..($i-1)] | ForEach-Object { $_.Name }) -join ", "
            Write-Host "Already published this run (cannot be undone, only unlisted): $done" -ForegroundColor Yellow
        }
        exit 1
    }
}

$published = ($Packages | ForEach-Object { $_.Name }) -join ", "
Write-Host "`nPublished v$version - $published" -ForegroundColor Green
