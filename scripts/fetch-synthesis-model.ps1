<#
.SYNOPSIS
    Stage a Qwen2.5-Instruct ONNX Runtime GenAI model for the in-process synthesis backend
    (OnnxGenAiTextGenerator). Produces a directory with genai_config.json + model.onnx[.data]
    + tokenizer files, which is what the backend loads.

.DESCRIPTION
    Two modes:
      * DEFAULT (no Python needed): downloads a pinned commit of a pre-built GenAI-format
        model from Hugging Face and verifies every file's exact byte size and cryptographic
        hash (SHA-256 for LFS files, git-blob SHA-1 for non-LFS files) before moving it
        into place.
      * -UseBuilder: converts from a pinned HF source model inside an isolated virtual
        environment (`.builder-venv`) with pinned Python packages (never mutates global pip).

    Point the backend at the result via SYNTHESIS_ONNX_MODEL_DIR, or place it at
    {app-output}/LocalSynthesisModel/qwen2.5-1.5b (the backend's default search path).

    Default model is Qwen2.5-1.5B-Instruct — best quality-vs-speed for batch synthesis
    (2026-06-03 benchmark). Pass -Size 0.5B for the lighter tier.

.PARAMETER Size
    1.5B (default) or 0.5B.

.PARAMETER OutDir
    Destination directory. Default: ./LocalSynthesisModel/qwen2.5-<size>

.PARAMETER Revision
    Optional 40-character Hugging Face commit SHA override for pre-built downloads.
    Floating branch names (such as 'main') are refused unless -AllowUnpinnedRevision is set.

.PARAMETER AllowUnpinnedRevision
    Explicit opt-in to download from a custom non-pinned revision (still verifies tree hashes
    returned by the Hugging Face API for that revision).

.PARAMETER UseBuilder
    Build from source inside an isolated Python venv with pinned package versions.

.PARAMETER Precision
    Builder-only quantization: int4 (default), fp16, fp32.

.PARAMETER Execution
    Builder-only execution provider: cpu (default), cuda, dml.

.EXAMPLE
    ./scripts/fetch-synthesis-model.ps1                 # pinned pre-built Qwen2.5-1.5B
    ./scripts/fetch-synthesis-model.ps1 -Size 0.5B      # pinned pre-built lighter tier
    ./scripts/fetch-synthesis-model.ps1 -UseBuilder     # convert from source in isolated venv
#>
[CmdletBinding()]
param(
    [ValidateSet('1.5B','0.5B')] [string]$Size = '1.5B',
    [string]$OutDir,
    [string]$Revision,
    [switch]$AllowUnpinnedRevision,
    [switch]$UseBuilder,
    [ValidateSet('int4','fp16','fp32')] [string]$Precision = 'int4',
    [ValidateSet('cpu','cuda','dml')]   [string]$Execution = 'cpu'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pinned pre-built GenAI-format repos, exact commit SHAs, byte sizes, and hashes
# (LFS files verified by SHA-256; non-LFS git blobs verified by git-blob SHA-1).
$prebuiltManifests = @{
    '1.5B' = @{
        Repo     = 'elbruno/Qwen2.5-1.5B-Instruct-onnx'
        Revision = '80a33348682e2b45ef7efd19b1db0bbcdbe7cc15'
        Files    = @(
            @{ Path = 'added_tokens.json';     Bytes = 629L;        Sha256 = '';                                                                 GitOid = '06135f3c7bfd850215c27c2a6c37b8043d564aff' }
            @{ Path = 'chat_template.jinja';   Bytes = 2561L;       Sha256 = '';                                                                 GitOid = '9840d40f49842423ad9d3c92bbedac488564c088' }
            @{ Path = 'genai_config.json';     Bytes = 1570L;       Sha256 = '';                                                                 GitOid = '33b4b56a308b22e71dae708fc625fef95d5f3573' }
            @{ Path = 'merges.txt';            Bytes = 1671853L;    Sha256 = '';                                                                 GitOid = '31349551d90c7606f325fe0f11bbb8bd5fa0d7c7' }
            @{ Path = 'model.onnx';            Bytes = 220738L;     Sha256 = '5f57ffda5e4c67ec314f5e4302e934c14ced56a9bb7d7f8cd87c48ad9bd8b127'; GitOid = '34fa9aa613d75e0c7e121c8ca1164a234e30fb4a' }
            @{ Path = 'model.onnx.data';       Bytes = 1919811584L; Sha256 = '0b6e545a36d7310d5d30f991d6a102a1093c14a37e2f0f407b7b609e6adb0037'; GitOid = 'eb67e726d300344725c9dd6ed32811ab3806439d' }
            @{ Path = 'special_tokens_map.json'; Bytes = 644L;      Sha256 = '';                                                                 GitOid = '3a7840311c8c90df33c823cdbb06626f920c509e' }
            @{ Path = 'tokenizer.json';        Bytes = 11421896L;   Sha256 = '9c5ae00e602b8860cbd784ba82a8aa14e8feecec692e7076590d014d7b7fdafa'; GitOid = '51ebb3ba93988c73bbd83a3c163a14343fbc5b06' }
            @{ Path = 'tokenizer_config.json'; Bytes = 4892L;       Sha256 = '';                                                                 GitOid = 'ed95d585356392217ebcef477fc0e2eb89ae6464' }
            @{ Path = 'vocab.json';            Bytes = 2776833L;    Sha256 = '';                                                                 GitOid = '4783fe10ac3adce15ac8f358ef5462739852c569' }
        )
    }
    '0.5B' = @{
        Repo     = 'hazemmabbas/Qwen2.5-0.5B-int4-block-32-acc-3-Instruct-onnx-cpu'
        Revision = '5c2e56e94edeb740724f082a0b2dc433ae095481'
        Files    = @(
            @{ Path = 'added_tokens.json';                   Bytes = 605L;       Sha256 = '';                                                                 GitOid = '482ced4679301bf287ebb310bdd1790eb4514232' }
            @{ Path = 'config.json';                         Bytes = 659L;       Sha256 = '';                                                                 GitOid = '0dbb161213629a23f0fc00ef286e6b1e366d180f' }
            @{ Path = 'genai_config.json';                   Bytes = 1537L;      Sha256 = '';                                                                 GitOid = '07d38da10b870ffe7c5f7470d4a416144c3fd4bd' }
            @{ Path = 'merges.txt';                          Bytes = 1671853L;   Sha256 = '';                                                                 GitOid = '31349551d90c7606f325fe0f11bbb8bd5fa0d7c7' }
            @{ Path = 'qwen-2.5-32k-500m-instruct.onnx';     Bytes = 175377L;    Sha256 = '8eacb605cbf786a1b71af958780619c022fa76c5310d2fa9418a7eba82272d72'; GitOid = 'fa9207bcff2556b4ea5682c256efbb55bea9107a' }
            @{ Path = 'qwen-2.5-32k-500m-instruct.onnx.data'; Bytes = 861939200L; Sha256 = '96a87e09a1ef31fd5901a28f655d8530d2c09799786dac785567550e30b11c64'; GitOid = '653090db2453ac33212eb78c5e3584c1d1468e53' }
            @{ Path = 'special_tokens_map.json';             Bytes = 613L;       Sha256 = '';                                                                 GitOid = 'ac23c0aaa2434523c494330aeb79c58395378103' }
            @{ Path = 'tokenizer.json';                      Bytes = 7031673L;   Sha256 = '';                                                                 GitOid = 'd24314ef7f0afd1b678c2e24c767e19f24f86b0e' }
            @{ Path = 'tokenizer_config.json';               Bytes = 7306L;      Sha256 = '';                                                                 GitOid = '8adf747ccaf85ff9587338cee6ed6be027b98210' }
            @{ Path = 'vocab.json';                          Bytes = 2776833L;   Sha256 = '';                                                                 GitOid = '4783fe10ac3adce15ac8f358ef5462739852c569' }
        )
    }
}

$sourceModel = @{ '1.5B' = 'Qwen/Qwen2.5-1.5B-Instruct'; '0.5B' = 'Qwen/Qwen2.5-0.5B-Instruct' }[$Size]

# Pinned Python builder dependencies matching Microsoft.ML.OnnxRuntimeGenAI 0.15.2.
$builderPackages = @(
    'onnxruntime-genai==0.15.2',
    'transformers==4.49.0',
    'torch==2.6.0',
    'huggingface_hub==0.29.3'
)

function Get-FileSha256Hex {
    param([Parameter(Mandatory)] [string]$FilePath)
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($FilePath)
    try {
        return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Get-GitBlobSha1Hex {
    param([Parameter(Mandatory)] [string]$FilePath)
    $info = Get-Item -LiteralPath $FilePath
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($info.Length)`0")
    $sha1 = [Security.Cryptography.SHA1]::Create()
    $stream = [IO.File]::OpenRead($FilePath)
    try {
        $null = $sha1.TransformBlock($header, 0, $header.Length, $header, 0)
        $buffer = New-Object byte[] 65536
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $null = $sha1.TransformBlock($buffer, 0, $read, $buffer, 0)
        }
        $null = $sha1.TransformFinalBlock([byte[]]::new(0), 0, 0)
        return [BitConverter]::ToString($sha1.Hash).Replace('-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha1.Dispose()
    }
}

function Assert-VerifiedFile {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string]$RelativePath,
        [Parameter(Mandatory)] [long]$ExpectedBytes,
        [string]$ExpectedSha256,
        [string]$ExpectedGitOid
    )
    $actualBytes = (Get-Item -LiteralPath $FilePath).Length
    if ($actualBytes -ne $ExpectedBytes) {
        throw "Integrity check failed for '$RelativePath': expected $ExpectedBytes bytes, got $actualBytes bytes."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedSha256)) {
        $actualSha256 = Get-FileSha256Hex -FilePath $FilePath
        if ($actualSha256 -cne $ExpectedSha256.ToLowerInvariant()) {
            throw "SHA-256 mismatch for '$RelativePath': expected $ExpectedSha256, got $actualSha256."
        }
    } elseif (-not [string]::IsNullOrWhiteSpace($ExpectedGitOid)) {
        $actualOid = Get-GitBlobSha1Hex -FilePath $FilePath
        if ($actualOid -cne $ExpectedGitOid.ToLowerInvariant()) {
            throw "Git-blob SHA-1 mismatch for '$RelativePath': expected $ExpectedGitOid, got $actualOid."
        }
    } else {
        throw "No integrity hash recorded for '$RelativePath'; refusing unverified download."
    }
}

if (-not $UseBuilder) {
    $precheckManifest = $prebuiltManifests[$Size]
    $precheckRevision = if ([string]::IsNullOrWhiteSpace($Revision)) { $precheckManifest.Revision } else { $Revision.Trim() }
    if (($precheckRevision -ine $precheckManifest.Revision) -and -not $AllowUnpinnedRevision) {
        if ($precheckRevision -notmatch '^[a-fA-F0-9]{40}$') {
            throw "Revision '$precheckRevision' must be a full 40-character commit SHA (or pass -AllowUnpinnedRevision)."
        }
    }
}

if (-not $OutDir) {
    $folder = "qwen2.5-$($Size.ToLower())"
    $OutDir = Join-Path (Get-Location) "LocalSynthesisModel/$folder"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$resolvedOutDir = (Resolve-Path -LiteralPath $OutDir).ProviderPath

if ($UseBuilder) {
    Write-Host "Building $sourceModel -> $resolvedOutDir (precision=$Precision, ep=$Execution)" -ForegroundColor Cyan
    $py = (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $py) {
        $py = (Get-Command python3 -ErrorAction SilentlyContinue)
    }
    if (-not $py -or -not (& $py.Source --version 2>$null)) {
        throw "Python not available. Run without -UseBuilder to download a pinned pre-built model instead."
    }
    $venvDir = Join-Path $resolvedOutDir '.builder-venv'
    if (-not (Test-Path -LiteralPath $venvDir)) {
        & $py.Source -m venv $venvDir
        if ($LASTEXITCODE -ne 0) { throw "Failed to create isolated Python venv at '$venvDir'." }
    }
    # Avoid bare $IsWindows which is undefined under Windows PowerShell 5.1 with Set-StrictMode -Version Latest.
    $isWinPlatform = ([IO.Path]::DirectorySeparatorChar -eq '\')
    $venvPython = if ($isWinPlatform) {
        Join-Path $venvDir 'Scripts/python.exe'
    } else {
        Join-Path $venvDir 'bin/python'
    }
    & $venvPython -m pip install --quiet --disable-pip-version-check @builderPackages
    if ($LASTEXITCODE -ne 0) { throw "Failed to install pinned builder packages into '$venvDir'." }

    $cache = Join-Path $resolvedOutDir '.builder-cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    & $venvPython -m onnxruntime_genai.models.builder -m $sourceModel -o $resolvedOutDir -p $Precision -e $Execution -c $cache
    if ($LASTEXITCODE -ne 0) { throw "Model builder failed with exit code $LASTEXITCODE." }
}
else {
    $manifest = $prebuiltManifests[$Size]
    $repo = $manifest.Repo
    $targetRevision = if ([string]::IsNullOrWhiteSpace($Revision)) { $manifest.Revision } else { $Revision.Trim() }
    $isPinnedDefault = ($targetRevision -ieq $manifest.Revision)
    if ($isPinnedDefault) {
        $targetRevision = $manifest.Revision
    }

    if (-not $isPinnedDefault -and -not $AllowUnpinnedRevision) {
        if ($targetRevision -notmatch '^[a-fA-F0-9]{40}$') {
            throw "Revision '$targetRevision' must be a full 40-character commit SHA (or pass -AllowUnpinnedRevision)."
        }
    }

    if ($isPinnedDefault) {
        $filesToFetch = $manifest.Files
    } else {
        Write-Host "Querying Hugging Face tree for '$repo' at revision '$targetRevision'..." -ForegroundColor Yellow
        $tree = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo/tree/${targetRevision}?recursive=true"
        $filesToFetch = @(
            $tree | Where-Object { $_.type -eq 'file' -and $_.path -ne '.gitattributes' } | ForEach-Object {
                $lfsSha = if ($_.PSObject.Properties['lfs'] -and $_.lfs -and $_.lfs.PSObject.Properties['oid']) { [string]$_.lfs.oid } else { '' }
                @{ Path = [string]$_.path; Bytes = [long]$_.size; Sha256 = $lfsSha; GitOid = [string]$_.oid }
            }
        )
    }

    Write-Host "Downloading pinned GenAI model '$repo' @ $targetRevision -> $resolvedOutDir" -ForegroundColor Cyan
    foreach ($entry in $filesToFetch) {
        $relPath = [string]$entry.Path
        if ($relPath.Contains('..') -or [IO.Path]::IsPathRooted($relPath)) {
            throw "Refusing unsafe relative model path '$relPath'."
        }
        $dest = Join-Path $resolvedOutDir $relPath
        $destFull = [IO.Path]::GetFullPath($dest)
        $rootPrefix = $resolvedOutDir.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        if (-not $destFull.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing path escaping output directory: '$relPath'."
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $destFull) | Out-Null

        if (Test-Path -LiteralPath $destFull) {
            try {
                Assert-VerifiedFile -FilePath $destFull -RelativePath $relPath `
                    -ExpectedBytes $entry.Bytes -ExpectedSha256 $entry.Sha256 -ExpectedGitOid $entry.GitOid
                Write-Host "  [cached/verified] $relPath" -ForegroundColor DarkGray
                continue
            } catch {
                Write-Host "  [re-downloading] $relPath ($($_.Exception.Message))" -ForegroundColor Yellow
            }
        }

        $tmp = "$destFull.$([Guid]::NewGuid().ToString('N')).tmp"
        Write-Host "  $relPath"
        $prevProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            $webParams = @{
                Uri     = "https://huggingface.co/$repo/resolve/$targetRevision/$relPath"
                OutFile = $tmp
            }
            if ($PSVersionTable.PSVersion.Major -lt 6) {
                $webParams['UseBasicParsing'] = $true
            }
            Invoke-WebRequest @webParams
            Assert-VerifiedFile -FilePath $tmp -RelativePath $relPath `
                -ExpectedBytes $entry.Bytes -ExpectedSha256 $entry.Sha256 -ExpectedGitOid $entry.GitOid
            Move-Item -LiteralPath $tmp -Destination $destFull -Force
        } catch {
            if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
            throw
        } finally {
            $ProgressPreference = $prevProgress
        }
    }
}

# A pre-built repo may nest files (e.g. cpu_and_mobile/); locate the genai_config.json that was fetched.
$cfg = Get-ChildItem -Path $resolvedOutDir -Recurse -Filter genai_config.json | Select-Object -First 1
if (-not $cfg) { throw "Staging finished but no genai_config.json found under $resolvedOutDir." }
$modelDir = $cfg.Directory.FullName

Write-Host "Done. Model staged and verified at: $modelDir" -ForegroundColor Green
Write-Host "Set the env var so the backend finds it:" -ForegroundColor Green
Write-Host "  `$env:SYNTHESIS_ONNX_MODEL_DIR = '$modelDir'" -ForegroundColor Green
