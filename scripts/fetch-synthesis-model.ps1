<#
.SYNOPSIS
    Stage a Qwen2.5-Instruct ONNX Runtime GenAI model for the in-process synthesis backend
    (OnnxGenAiTextGenerator). Produces a directory with genai_config.json + model.onnx[.data]
    + tokenizer files, which is what the backend loads.

.DESCRIPTION
    Two modes:
      * DEFAULT (no Python needed): downloads a pre-built GenAI-format model from Hugging Face.
      * -UseBuilder: converts from the HF source model with the onnxruntime-genai model builder
        (requires Python + onnxruntime-genai/transformers/torch).

    Point the backend at the result via SYNTHESIS_ONNX_MODEL_DIR, or place it at
    {app-output}/LocalSynthesisModel/qwen2.5-1.5b (the backend's default search path).

    Default model is Qwen2.5-1.5B-Instruct — best quality-vs-speed for batch synthesis
    (2026-06-03 benchmark). Pass -Size 0.5B for the lighter tier.

.PARAMETER Size
    1.5B (default) or 0.5B.

.PARAMETER OutDir
    Destination directory. Default: ./LocalSynthesisModel/qwen2.5-<size>

.PARAMETER UseBuilder
    Build from source with the Python onnxruntime-genai model builder instead of downloading pre-built.

.PARAMETER Precision
    Builder-only quantization: int4 (default), fp16, fp32.

.PARAMETER Execution
    Builder-only execution provider: cpu (default), cuda, dml.

.EXAMPLE
    ./scripts/fetch-synthesis-model.ps1                 # pre-built Qwen2.5-1.5B (no Python)
    ./scripts/fetch-synthesis-model.ps1 -Size 0.5B      # pre-built lighter tier
    ./scripts/fetch-synthesis-model.ps1 -UseBuilder     # convert from source (needs Python)
#>
[CmdletBinding()]
param(
    [ValidateSet('1.5B','0.5B')] [string]$Size = '1.5B',
    [string]$OutDir,
    [switch]$UseBuilder,
    [ValidateSet('int4','fp16','fp32')] [string]$Precision = 'int4',
    [ValidateSet('cpu','cuda','dml')]   [string]$Execution = 'cpu'
)

$ErrorActionPreference = 'Stop'

# Pre-built GenAI-format repos (CPU, contain genai_config.json). Verified 2026-06-03.
$prebuilt = @{
    '1.5B' = 'elbruno/Qwen2.5-1.5B-Instruct-onnx'
    '0.5B' = 'hazemmabbas/Qwen2.5-0.5B-int4-block-32-acc-3-Instruct-onnx-cpu'
}
$sourceModel = @{ '1.5B' = 'Qwen/Qwen2.5-1.5B-Instruct'; '0.5B' = 'Qwen/Qwen2.5-0.5B-Instruct' }[$Size]

if (-not $OutDir) {
    $folder = "qwen2.5-$($Size.ToLower())"
    $OutDir = Join-Path (Get-Location) "LocalSynthesisModel/$folder"
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

if ($UseBuilder) {
    Write-Host "Building $sourceModel -> $OutDir (precision=$Precision, ep=$Execution)" -ForegroundColor Cyan
    $py = (Get-Command python -ErrorAction SilentlyContinue)
    if (-not $py -or -not (& $py.Source --version 2>$null)) {
        throw "Python not available. Run without -UseBuilder to download a pre-built model instead."
    }
    & $py.Source -m pip install --quiet --upgrade onnxruntime-genai transformers torch huggingface_hub
    $cache = Join-Path $OutDir '.builder-cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    & $py.Source -m onnxruntime_genai.models.builder -m $sourceModel -o $OutDir -p $Precision -e $Execution -c $cache
}
else {
    $repo = $prebuilt[$Size]
    Write-Host "Downloading pre-built GenAI model '$repo' -> $OutDir" -ForegroundColor Cyan
    # Discover the repo's file list from the HF API, then fetch each file (skips .gitattributes).
    $api = Invoke-RestMethod -Uri "https://huggingface.co/api/models/$repo"
    $files = $api.siblings.rfilename | Where-Object { $_ -ne '.gitattributes' }
    foreach ($f in $files) {
        $dest = Join-Path $OutDir $f
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Write-Host "  $f"
        Invoke-WebRequest -Uri "https://huggingface.co/$repo/resolve/main/$f" -OutFile $dest
    }
}

# A pre-built repo may nest files (e.g. cpu_and_mobile/); locate the genai_config.json that was fetched.
$cfg = Get-ChildItem -Path $OutDir -Recurse -Filter genai_config.json | Select-Object -First 1
if (-not $cfg) { throw "Staging finished but no genai_config.json found under $OutDir." }
$modelDir = $cfg.Directory.FullName

Write-Host "Done. Model staged at: $modelDir" -ForegroundColor Green
Write-Host "Set the env var so the backend finds it:" -ForegroundColor Green
Write-Host "  `$env:SYNTHESIS_ONNX_MODEL_DIR = '$modelDir'" -ForegroundColor Green
