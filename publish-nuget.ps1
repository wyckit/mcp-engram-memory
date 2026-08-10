<#
.SYNOPSIS
Pack and publish the three MCP Engram Memory packages.

.DESCRIPTION
Packs McpEngramMemory.Core, McpEngramMemory.Synthesis.Onnx, and McpEngramMemory,
validates them, and pushes to nuget.org and/or GitHub Packages.

Everything is packed and size-checked BEFORE the first push, because nuget.org has
no delete - a published version can only be unlisted. A failure part-way through
therefore leaves earlier packages permanently live.

.EXAMPLE
./publish-nuget.ps1 -ApiKey <key> -WhatIf
Packs and validates without pushing anything.

.EXAMPLE
./publish-nuget.ps1 -ApiKey <nuget-key> -GitHubToken <gh-pat>
Publishes to both nuget.org and GitHub Packages.

.EXAMPLE
./publish-nuget.ps1 -GitHubToken <gh-pat> -SkipNuGetOrg
Publishes to GitHub Packages only.
#>
param(
    # nuget.org API key. Required unless -SkipNuGetOrg is set.
    [string]$ApiKey,

    # GitHub PAT with write:packages. When set, packages also go to GitHub Packages.
    [string]$GitHubToken,

    [string]$Configuration = "Release",
    [string]$Source = "https://api.nuget.org/v3/index.json",
    [string]$GitHubSource = "https://nuget.pkg.github.com/wyckit/index.json",
    [switch]$SkipNuGetOrg,
    [switch]$SkipTests,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

# Validate the credential combination before doing any work, so a missing key is
# reported in a second rather than after a full test + pack run.
if (-not $SkipNuGetOrg -and -not $ApiKey -and -not $WhatIf) {
    Write-Host "-ApiKey is required unless -SkipNuGetOrg or -WhatIf is set." -ForegroundColor Red
    exit 1
}
if (-not $SkipNuGetOrg -and -not $GitHubToken) {
    Write-Host "Note: publishing to nuget.org only. Pass -GitHubToken to also push to GitHub Packages." -ForegroundColor Yellow
}
if ($SkipNuGetOrg -and -not $GitHubToken -and -not $WhatIf) {
    Write-Host "-SkipNuGetOrg with no -GitHubToken leaves nothing to publish." -ForegroundColor Red
    exit 1
}

# Pre-flight: a running MCP server started from bin\$Configuration holds its
# assemblies open, and packing the tool republishes into that same directory.
# MSBuild reports this as MSB3027 ("Exceeded retry count of 10") after a long
# retry loop, which reads like a build error rather than "stop your server".
# Detect it up front and say so.
$lockTarget = "$PSScriptRoot\src\McpEngramMemory\bin\$Configuration\net8.0\McpEngramMemory.Core.dll"
if (Test-Path $lockTarget) {
    try {
        $fs = [System.IO.File]::Open($lockTarget, 'Open', 'Write', 'None')
        $fs.Close()
    } catch {
        Write-Host "Build output is locked by a running process:" -ForegroundColor Red
        Write-Host "  $lockTarget" -ForegroundColor Red
        $holders = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
            Where-Object { $_.CommandLine -like '*McpEngramMemory*' }
        if ($holders) {
            Write-Host "Likely an MCP Engram Memory server (PID $($holders.ProcessId -join ', '))." -ForegroundColor Yellow
            Write-Host "Disconnect the engram MCP server (or stop those PIDs) and re-run." -ForegroundColor Yellow
        } else {
            Write-Host "Close whatever is using it and re-run." -ForegroundColor Yellow
        }
        exit 1
    }
}

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
function Push-Feed {
    param([string]$FeedName, [string]$FeedUrl, [string]$Key, [string[]]$Nupkgs, $PackageList)

    Write-Host "`nPushing to $FeedName ($FeedUrl)..." -ForegroundColor Yellow
    for ($i = 0; $i -lt $Nupkgs.Count; $i++) {
        $name = $PackageList[$i].Name
        Write-Host "  pushing $name..." -ForegroundColor Gray

        # --skip-duplicate: GitHub Packages rejects a re-push of an existing
        # version, and a partial earlier run is a normal state to recover from.
        dotnet nuget push $Nupkgs[$i] --api-key $Key --source $FeedUrl --skip-duplicate
        if ($LASTEXITCODE -ne 0) {
            Write-Host "Push to $FeedName failed for $name." -ForegroundColor Red
            if ($i -gt 0) {
                $done = ($PackageList[0..($i-1)] | ForEach-Object { $_.Name }) -join ", "
                Write-Host "Already pushed to ${FeedName} this run (nuget.org cannot be undone, only unlisted): $done" -ForegroundColor Yellow
            }
            return $false
        }
    }
    return $true
}

# nuget.org first: it is the feed that cannot be undone, so if it is going to
# fail, fail before anything reaches GitHub Packages.
if (-not $SkipNuGetOrg) {
    if (-not (Push-Feed -FeedName "nuget.org" -FeedUrl $Source -Key $ApiKey -Nupkgs $nupkgs -PackageList $Packages)) { exit 1 }
} else {
    Write-Host "`nSkipping nuget.org (-SkipNuGetOrg)." -ForegroundColor Yellow
}

if ($GitHubToken) {
    if (-not (Push-Feed -FeedName "GitHub Packages" -FeedUrl $GitHubSource -Key $GitHubToken -Nupkgs $nupkgs -PackageList $Packages)) { exit 1 }
}

$published = ($Packages | ForEach-Object { $_.Name }) -join ", "
$feeds = @()
if (-not $SkipNuGetOrg) { $feeds += "nuget.org" }
if ($GitHubToken)       { $feeds += "GitHub Packages" }
$feedList = $feeds -join " + "
Write-Host "`nPublished v$version to $feedList - $published" -ForegroundColor Green
Write-Host "Next: create the GitHub Release for v$version (gh release create v$version)." -ForegroundColor Cyan
