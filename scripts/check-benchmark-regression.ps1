<#
.SYNOPSIS
    CI regression gate for engram benchmark artifacts (task T7).

.DESCRIPTION
    Reads benchmark result JSON files, extracts the IR-quality and/or
    agent-outcome metrics, and gates them two ways:

      1. ABSOLUTE FLOORS  - every dataset must clear the documented minimums
         (Recall >= 0.20, MRR >= 0.20, nDCG >= 0.15; see docs/benchmarks.md).
      2. BASELINE DRIFT   - if a matching pinned baseline exists under
         benchmarks/baselines/, the candidate metric must not drop more than
         a small tolerance (default 0.02) below the baseline value.

    Two artifact shapes are recognised and handled automatically:

      * IR-quality (flat):
          { "datasetId", "mode", "meanRecallAtK", "meanPrecisionAtK",
            "meanMrr", "meanNdcgAtK", "meanLatencyMs", ... }

      * Agent-outcome (nested live/proxy benchmark):
          { "datasetId", "baselineCondition",
            "comparisons": [ { "condition": "full_engram",
                               "result": { "meanSuccessScore", "passRate",
                                           "meanRequiredCoverage", ... } } ] }
        For this shape the gated quality metric is the full_engram condition's
        meanSuccessScore / passRate / meanRequiredCoverage (floor 0.20).

    Prints a pass/fail table and exits non-zero if ANY check fails, so a CI
    step can `run:` it and let the non-zero exit fail the job.

.PARAMETER Path
    A benchmark result .json file OR a directory. When a directory is given,
    every *.json directly inside it is checked (non-recursive unless -Recurse).
    Defaults to the newest dated folder under benchmarks/ if it exists.

.PARAMETER BaselineDir
    Directory of pinned baseline artifacts. Default: benchmarks/baselines.

.PARAMETER Tolerance
    Allowed downward drift from a pinned baseline before it counts as a
    regression. Default 0.02.

.PARAMETER RecallFloor / MrrFloor / NdcgFloor / OutcomeFloor
    Absolute minimum metric values. Defaults match docs/benchmarks.md.

.PARAMETER Recurse
    Recurse into subdirectories when Path is a directory.

.EXAMPLE
    ./scripts/check-benchmark-regression.ps1 -Path benchmarks/2026-04-16

.EXAMPLE
    ./scripts/check-benchmark-regression.ps1 -Path benchmarks/2026-04-16/default-v1-vector_rerank.json
#>
[CmdletBinding()]
param(
    [string]$Path,
    [string]$BaselineDir,
    [double]$Tolerance = 0.02,
    [double]$RecallFloor = 0.20,
    [double]$MrrFloor = 0.20,
    [double]$NdcgFloor = 0.15,
    [double]$OutcomeFloor = 0.20,
    [switch]$Recurse
)

$ErrorActionPreference = 'Stop'

# --- Resolve repo root (this script lives in <root>/scripts) ---------------
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot  = Split-Path -Parent $ScriptDir

if (-not $BaselineDir) {
    $BaselineDir = Join-Path $RepoRoot 'benchmarks/baselines'
}

# --- Resolve the target path ----------------------------------------------
if (-not $Path) {
    $benchRoot = Join-Path $RepoRoot 'benchmarks'
    $dated = Get-ChildItem -Path $benchRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d{4}-\d{2}-\d{2}' } |
        Sort-Object Name -Descending |
        Select-Object -First 1
    if ($null -eq $dated) {
        Write-Error "No -Path supplied and no dated benchmark folder found under $benchRoot."
        exit 2
    }
    $Path = $dated.FullName
    Write-Host "No -Path supplied; defaulting to newest dated folder: $Path"
}

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "Path not found: $Path"
    exit 2
}

# --- Collect candidate artifact files -------------------------------------
$item = Get-Item -LiteralPath $Path
if ($item.PSIsContainer) {
    $files = Get-ChildItem -LiteralPath $Path -Filter '*.json' -File -Recurse:$Recurse
} else {
    $files = @($item)
}

if (-not $files -or $files.Count -eq 0) {
    Write-Error "No .json benchmark files found at: $Path"
    exit 2
}

# --- Index pinned baselines by datasetId (+ mode when present) ------------
$baselines = @{}
if (Test-Path -LiteralPath $BaselineDir) {
    foreach ($bf in Get-ChildItem -LiteralPath $BaselineDir -Filter '*.json' -File) {
        try {
            $bj = Get-Content -LiteralPath $bf.FullName -Raw | ConvertFrom-Json
        } catch { continue }
        if ($null -eq $bj) { continue }
        $dsId = $bj.datasetId
        if (-not $dsId) { continue }
        $mode = if ($bj.PSObject.Properties.Name -contains 'mode' -and $bj.mode) { $bj.mode } else { '' }
        $baselines["$dsId|$mode"] = $bj
        # Also index by dataset alone so a candidate without a mode can still match.
        if (-not $baselines.ContainsKey("$dsId|")) { $baselines["$dsId|"] = $bj }
    }
}

# --- Helpers ---------------------------------------------------------------
function Get-Num($obj, $name) {
    if ($null -eq $obj) { return $null }
    if ($obj.PSObject.Properties.Name -contains $name) {
        $v = $obj.$name
        if ($null -ne $v -and $v -is [ValueType]) { return [double]$v }
        if ($null -ne $v) { return [double]$v }
    }
    return $null
}

function Get-FullEngramResult($json) {
    if ($null -eq $json.comparisons) { return $null }
    foreach ($c in $json.comparisons) {
        if ($c.condition -eq 'full_engram') { return $c.result }
    }
    return $null
}

# Returns an ordered hashtable of metricName -> value for the candidate,
# plus a "shape" tag, or $null if no recognised metrics.
function Get-Metrics($json) {
    $m = [ordered]@{}
    $shape = $null

    # IR-quality flat shape
    if ($json.PSObject.Properties.Name -contains 'meanRecallAtK' -or
        $json.PSObject.Properties.Name -contains 'meanMrr' -or
        $json.PSObject.Properties.Name -contains 'meanNdcgAtK') {
        $shape = 'ir'
        $r = Get-Num $json 'meanRecallAtK'
        $mrr = Get-Num $json 'meanMrr'
        $ndcg = Get-Num $json 'meanNdcgAtK'
        if ($null -ne $r)    { $m['Recall@K'] = $r }
        if ($null -ne $mrr)  { $m['MRR']      = $mrr }
        if ($null -ne $ndcg) { $m['nDCG@K']   = $ndcg }
    }

    # Agent-outcome nested shape (full_engram condition is what we gate on)
    $fe = Get-FullEngramResult $json
    if ($null -ne $fe) {
        $shape = if ($shape) { "$shape+outcome" } else { 'outcome' }
        $ss = Get-Num $fe 'meanSuccessScore'
        $pr = Get-Num $fe 'passRate'
        $rc = Get-Num $fe 'meanRequiredCoverage'
        if ($null -ne $ss) { $m['SuccessScore']     = $ss }
        if ($null -ne $pr) { $m['PassRate']         = $pr }
        if ($null -ne $rc) { $m['RequiredCoverage'] = $rc }
    }

    if ($m.Count -eq 0) { return $null }
    return @{ Metrics = $m; Shape = $shape }
}

# Floor lookup per metric name.
function Get-Floor($name) {
    switch ($name) {
        'Recall@K'         { return $RecallFloor }
        'MRR'              { return $MrrFloor }
        'nDCG@K'           { return $NdcgFloor }
        'SuccessScore'     { return $OutcomeFloor }
        'PassRate'         { return $OutcomeFloor }
        'RequiredCoverage' { return $OutcomeFloor }
        default            { return 0.0 }
    }
}

# --- Evaluate --------------------------------------------------------------
$rows = New-Object System.Collections.Generic.List[object]
$failures = 0
$skipped = 0

foreach ($f in ($files | Sort-Object FullName)) {
    $relName = $f.Name
    try {
        $json = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
    } catch {
        Write-Warning "Skipping unparseable JSON: $relName ($($_.Exception.Message))"
        $skipped++
        continue
    }

    $parsed = Get-Metrics $json
    if ($null -eq $parsed) {
        # Not a metrics-bearing artifact (e.g. a manifest); skip silently-ish.
        $skipped++
        continue
    }

    $dsId = if ($json.datasetId) { $json.datasetId } else { '(unknown)' }
    $mode = if ($json.PSObject.Properties.Name -contains 'mode' -and $json.mode) { $json.mode } else { '' }

    $baseline = $null
    if ($baselines.ContainsKey("$dsId|$mode")) { $baseline = $baselines["$dsId|$mode"] }
    elseif ($baselines.ContainsKey("$dsId|"))  { $baseline = $baselines["$dsId|"] }

    $baseParsed = if ($baseline) { Get-Metrics $baseline } else { $null }

    foreach ($metricName in $parsed.Metrics.Keys) {
        $value = [double]$parsed.Metrics[$metricName]
        $floor = Get-Floor $metricName

        $status = 'PASS'
        $reasons = @()

        # Floor check
        if ($value -lt $floor) {
            $status = 'FAIL'
            $reasons += ("below floor {0:F3}" -f $floor)
        }

        # Baseline-drift check
        $baseVal = $null
        if ($baseParsed -and $baseParsed.Metrics.Contains($metricName)) {
            $baseVal = [double]$baseParsed.Metrics[$metricName]
            $minAllowed = $baseVal - $Tolerance
            if ($value -lt $minAllowed) {
                $status = 'FAIL'
                $reasons += ("drift {0:+0.000;-0.000} vs baseline {1:F3} (tol {2:F3})" -f ($value - $baseVal), $baseVal, $Tolerance)
            }
        }

        if ($status -eq 'FAIL') { $failures++ }

        $rows.Add([pscustomobject]@{
            Dataset  = if ($mode) { "$dsId/$mode" } else { $dsId }
            Metric   = $metricName
            Value    = ("{0:F3}" -f $value)
            Floor    = ("{0:F3}" -f $floor)
            Baseline = if ($null -ne $baseVal) { "{0:F3}" -f $baseVal } else { '-' }
            Status   = $status
            Notes    = ($reasons -join '; ')
        })
    }
}

# --- Report ----------------------------------------------------------------
Write-Host ""
Write-Host "Benchmark regression gate  (tolerance=$Tolerance, baselines=$BaselineDir)"
Write-Host "============================================================================="
if ($rows.Count -eq 0) {
    Write-Warning "No metric-bearing benchmark artifacts were evaluated."
    exit 2
}

$rows | Format-Table -AutoSize Dataset, Metric, Value, Floor, Baseline, Status, Notes | Out-String | Write-Host

$total = $rows.Count
$passed = $total - $failures
Write-Host "-----------------------------------------------------------------------------"
Write-Host ("Checks: {0}   Passed: {1}   Failed: {2}   Files skipped: {3}" -f $total, $passed, $failures, $skipped)

if ($failures -gt 0) {
    Write-Host ""
    Write-Host "REGRESSION DETECTED - failing the build." -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All benchmark metrics within floors and baseline tolerance." -ForegroundColor Green
exit 0
