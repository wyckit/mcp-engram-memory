<#
.SYNOPSIS
Publish the already validated release archives to NuGet.org only.

.DESCRIPTION
Defaults to the exact 2.1.0 archives from merged-commit CI, already published on
GitHub. Checks every package's name, size and SHA256 against the recorded release
manifest before prompting for credentials or uploading anything. Never builds,
packs, deletes artifacts, changes an installation, or publishes to GitHub.

.EXAMPLE
./publish-nuget.ps1 -WhatIf
Verify the existing archives and display the upload plan without credentials or uploads.

.EXAMPLE
./publish-nuget.ps1
Verify all archives, prompt for the NuGet API key with hidden input, then upload.
NUGET_API_KEY or -ApiKey can supply the key instead; never put a literal key in history.

.EXAMPLE
./publish-nuget.ps1 -PackageDirectory C:\Downloads\engram-2.1.0 -WhatIf
Verify another folder containing the same release archives, without rebuilding them.
#>
[CmdletBinding()]
param(
    [string]$ApiKey = $env:NUGET_API_KEY,
    [string]$PackageDirectory = "",
    # Use only a manifest from a completed, trusted release validation run.
    [string]$ValidationManifest = "",
    [switch]$WhatIf
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$source = 'https://api.nuget.org/v3/index.json'
$packageNames = @('McpEngramMemory.Core', 'McpEngramMemory.Synthesis.Onnx', 'McpEngramMemory')
$openPackages = @()
$completed = @()
$promptedKey = $null

try {
    if (-not $ValidationManifest) {
        $ValidationManifest = Join-Path $PSScriptRoot 'scripts/release-packages.json'
    }
    $manifest = Get-Content -LiteralPath $ValidationManifest -Raw | ConvertFrom-Json
    if ($manifest.status -ne 'passed' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') {
        throw 'A passed validation manifest with a stable release version is required.'
    }
    $version = [string]$manifest.version
    $records = @($manifest.packages)
    if ($records.Count -ne $packageNames.Count) {
        throw 'The validation manifest must contain exactly the three release packages.'
    }
    if (-not $PackageDirectory) {
        $PackageDirectory = Join-Path $PSScriptRoot "artifacts/release-$version/publication/packages"
    }
    $PackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory).ProviderPath
    Write-Host "Verifying existing Engram Memory $version packages in $PackageDirectory" -ForegroundColor Cyan

    # Validate the complete set before the first upload. Keep read handles open
    # through publication so Windows cannot replace a verified archive mid-run.
    $paths = @()
    foreach ($name in $packageNames) {
        $packageRecords = @($records | Where-Object { $_.package -ceq $name })
        if ($packageRecords.Count -ne 1) { throw "Missing or duplicate manifest entry: $name" }
        $record = $packageRecords[0]
        if ($record.version -cne $version -or $record.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            $record.bytes -le 0 -or $record.bytes -gt 250MB) {
            throw "Invalid version, size or checksum in manifest entry: $name"
        }
        $path = Join-Path $PackageDirectory "$name.$version.nupkg"
        $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $openPackages += $stream
        if ($stream.Length -ne $record.bytes) { throw "Package size mismatch: $name" }
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $actualHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
        } finally {
            $hasher.Dispose()
        }
        if ($actualHash -cne $record.sha256.ToLowerInvariant()) { throw "Package checksum mismatch: $name" }
        $paths += $path
        Write-Host "  Verified $name $version ($actualHash)" -ForegroundColor Gray
    }

    Write-Host "Destination: NuGet.org only ($source)" -ForegroundColor Cyan
    Write-Host "Upload order: $($packageNames -join ' -> ')"
    if ($WhatIf) {
        Write-Host '-WhatIf: all archives verified; nothing built or uploaded.' -ForegroundColor Green
        exit 0
    }

    # Resolve the command before asking for a credential, but never invoke it in a dry run.
    $null = Get-Command dotnet -ErrorAction Stop
    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        $promptedKey = Read-Host 'NuGet.org API key (input hidden)' -AsSecureString
        $ApiKey = [Net.NetworkCredential]::new('', $promptedKey).Password
    }
    if ([string]::IsNullOrWhiteSpace($ApiKey)) { throw 'A NuGet.org API key is required to upload.' }

    for ($i = 0; $i -lt $paths.Count; $i++) {
        $name = $packageNames[$i]
        Write-Host "Uploading $name $version to NuGet.org..." -ForegroundColor Yellow
        & dotnet nuget push $paths[$i] --api-key $ApiKey --source $source --skip-duplicate
        if ($LASTEXITCODE -ne 0) {
            throw "NuGet.org upload failed for $name (exit $LASTEXITCODE). Later packages were not attempted."
        }
        $completed += $name
    }
    Write-Host "NuGet.org upload commands completed for all three $version packages; existing versions were skipped." -ForegroundColor Green
    Write-Host 'NuGet may still be processing/indexing the uploads. GitHub was not changed.'
} catch {
    Write-Host "Publish stopped: $($_.Exception.Message)" -ForegroundColor Red
    if ($completed.Count -gt 0) {
        Write-Host "Earlier uploads completed or already existed: $($completed -join ', ')." -ForegroundColor Yellow
        Write-Host 'Resolve the failure and rerun; existing NuGet versions will be skipped.'
    }
    exit 1
} finally {
    foreach ($stream in $openPackages) { $stream.Dispose() }
    if ($null -ne $promptedKey) { $promptedKey.Dispose() }
    $ApiKey = $null
}
