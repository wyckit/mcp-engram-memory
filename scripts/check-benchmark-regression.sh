#!/usr/bin/env bash
# CI regression gate for engram benchmark artifacts (task T7).
#
# Reads benchmark result JSON files, extracts IR-quality and/or agent-outcome
# metrics, and gates them two ways:
#   1. ABSOLUTE FLOORS  - Recall >= 0.20, MRR >= 0.20, nDCG >= 0.15
#                         (see docs/benchmarks.md). Agent-outcome quality
#                         metrics (SuccessScore/PassRate/RequiredCoverage) use
#                         the same 0.20 floor.
#   2. BASELINE DRIFT   - if a matching pinned baseline exists under
#                         benchmarks/baselines/, the candidate must not drop
#                         more than --tolerance (default 0.02) below it.
#
# Recognised artifact shapes (auto-detected):
#   * IR-quality (flat):    meanRecallAtK / meanMrr / meanNdcgAtK
#   * Agent-outcome nested: comparisons[].condition=="full_engram".result
#                           .meanSuccessScore / .passRate / .meanRequiredCoverage
#
# Prints a pass/fail table and exits non-zero on ANY regression, so a CI step
# can run it and let the non-zero exit fail the job.
#
# Usage:
#   scripts/check-benchmark-regression.sh [PATH] [--baseline-dir DIR]
#       [--tolerance N] [--recall-floor N] [--mrr-floor N]
#       [--ndcg-floor N] [--outcome-floor N] [--recurse]
#
#   PATH is a result .json file or a directory of them. Defaults to the newest
#   dated folder under benchmarks/.
set -euo pipefail

if ! command -v jq >/dev/null 2>&1; then
  echo "ERROR: jq is required but not installed." >&2
  exit 2
fi

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

PATH_ARG=""
BASELINE_DIR="$REPO_ROOT/benchmarks/baselines"
TOLERANCE="0.02"
RECALL_FLOOR="0.20"
MRR_FLOOR="0.20"
NDCG_FLOOR="0.15"
OUTCOME_FLOOR="0.20"
RECURSE="0"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --baseline-dir) BASELINE_DIR="$2"; shift 2 ;;
    --tolerance) TOLERANCE="$2"; shift 2 ;;
    --recall-floor) RECALL_FLOOR="$2"; shift 2 ;;
    --mrr-floor) MRR_FLOOR="$2"; shift 2 ;;
    --ndcg-floor) NDCG_FLOOR="$2"; shift 2 ;;
    --outcome-floor) OUTCOME_FLOOR="$2"; shift 2 ;;
    --recurse) RECURSE="1"; shift ;;
    -*) echo "Unknown option: $1" >&2; exit 2 ;;
    *) PATH_ARG="$1"; shift ;;
  esac
done

# Default to newest dated benchmark folder if no path supplied.
if [[ -z "$PATH_ARG" ]]; then
  PATH_ARG="$(find "$REPO_ROOT/benchmarks" -maxdepth 1 -type d -name '[0-9][0-9][0-9][0-9]-*' 2>/dev/null \
              | sort | tail -n 1)"
  if [[ -z "$PATH_ARG" ]]; then
    echo "ERROR: no PATH supplied and no dated benchmark folder found." >&2
    exit 2
  fi
  echo "No PATH supplied; defaulting to newest dated folder: $PATH_ARG"
fi

if [[ ! -e "$PATH_ARG" ]]; then
  echo "ERROR: path not found: $PATH_ARG" >&2
  exit 2
fi

# Collect candidate files.
declare -a FILES=()
if [[ -d "$PATH_ARG" ]]; then
  if [[ "$RECURSE" == "1" ]]; then
    while IFS= read -r f; do FILES+=("$f"); done < <(find "$PATH_ARG" -type f -name '*.json' | sort)
  else
    while IFS= read -r f; do FILES+=("$f"); done < <(find "$PATH_ARG" -maxdepth 1 -type f -name '*.json' | sort)
  fi
else
  FILES+=("$PATH_ARG")
fi

if [[ ${#FILES[@]} -eq 0 ]]; then
  echo "ERROR: no .json benchmark files found at: $PATH_ARG" >&2
  exit 2
fi

# jq filter: emit "metric<TAB>value" lines for a recognised artifact.
read -r -d '' JQ_METRICS <<'JQ' || true
def num($v): if ($v == null) then empty else $v end;
[
  (if has("meanRecallAtK") then "Recall@K\t\(.meanRecallAtK)" else empty end),
  (if has("meanMrr")       then "MRR\t\(.meanMrr)"            else empty end),
  (if has("meanNdcgAtK")   then "nDCG@K\t\(.meanNdcgAtK)"     else empty end),
  ( (.comparisons // []) | map(select(.condition == "full_engram")) | .[0].result
    | if . == null then empty else
        ( if .meanSuccessScore     != null then "SuccessScore\t\(.meanSuccessScore)" else empty end ),
        ( if .passRate             != null then "PassRate\t\(.passRate)"             else empty end ),
        ( if .meanRequiredCoverage != null then "RequiredCoverage\t\(.meanRequiredCoverage)" else empty end )
      end
  )
] | .[]
JQ

floor_for() {
  case "$1" in
    "Recall@K") echo "$RECALL_FLOOR" ;;
    "MRR") echo "$MRR_FLOOR" ;;
    "nDCG@K") echo "$NDCG_FLOOR" ;;
    "SuccessScore"|"PassRate"|"RequiredCoverage") echo "$OUTCOME_FLOOR" ;;
    *) echo "0.0" ;;
  esac
}

# Build baseline index: "datasetId|mode" -> file, and "datasetId|" -> file.
declare -A BASELINE_FILE=()
if [[ -d "$BASELINE_DIR" ]]; then
  while IFS= read -r bf; do
    [[ -z "$bf" ]] && continue
    ds="$(jq -r '.datasetId // empty' "$bf" 2>/dev/null || true)"
    [[ -z "$ds" ]] && continue
    mode="$(jq -r '.mode // ""' "$bf" 2>/dev/null || true)"
    BASELINE_FILE["$ds|$mode"]="$bf"
    [[ -z "${BASELINE_FILE["$ds|"]:-}" ]] && BASELINE_FILE["$ds|"]="$bf"
  done < <(find "$BASELINE_DIR" -maxdepth 1 -type f -name '*.json' | sort)
fi

# baseline value for dataset/mode/metric (empty if none).
baseline_value() {
  local ds="$1" mode="$2" metric="$3"
  local bf="${BASELINE_FILE["$ds|$mode"]:-}"
  [[ -z "$bf" ]] && bf="${BASELINE_FILE["$ds|"]:-}"
  [[ -z "$bf" ]] && { echo ""; return; }
  jq -r --arg m "$metric" "$JQ_METRICS" "$bf" 2>/dev/null \
    | awk -F'\t' -v m="$metric" '$1==m {print $2; exit}'
}

printf "\nBenchmark regression gate  (tolerance=%s, baselines=%s)\n" "$TOLERANCE" "$BASELINE_DIR"
echo "============================================================================="
printf "%-28s %-16s %-8s %-8s %-9s %-6s %s\n" "Dataset" "Metric" "Value" "Floor" "Baseline" "Status" "Notes"
echo "-----------------------------------------------------------------------------"

TOTAL=0
FAILS=0
SKIPPED=0

for f in "${FILES[@]}"; do
  if ! jq empty "$f" >/dev/null 2>&1; then
    echo "WARN: skipping unparseable JSON: $(basename "$f")" >&2
    SKIPPED=$((SKIPPED+1))
    continue
  fi
  metrics="$(jq -r "$JQ_METRICS" "$f" 2>/dev/null || true)"
  if [[ -z "$metrics" ]]; then
    SKIPPED=$((SKIPPED+1))
    continue
  fi
  ds="$(jq -r '.datasetId // "(unknown)"' "$f")"
  mode="$(jq -r '.mode // ""' "$f")"
  label="$ds"; [[ -n "$mode" ]] && label="$ds/$mode"

  while IFS=$'\t' read -r metric value; do
    [[ -z "$metric" ]] && continue
    TOTAL=$((TOTAL+1))
    floor="$(floor_for "$metric")"
    bval="$(baseline_value "$ds" "$mode" "$metric")"

    status="PASS"
    notes=""

    # Floor check.
    if awk -v v="$value" -v fl="$floor" 'BEGIN{exit !(v < fl)}'; then
      status="FAIL"
      notes="below floor $floor"
    fi

    # Baseline-drift check.
    bdisp="-"
    if [[ -n "$bval" ]]; then
      bdisp="$(printf '%.3f' "$bval")"
      if awk -v v="$value" -v b="$bval" -v t="$TOLERANCE" 'BEGIN{exit !(v < (b - t))}'; then
        status="FAIL"
        drift="$(awk -v v="$value" -v b="$bval" 'BEGIN{printf "%+.3f", v-b}')"
        msg="drift $drift vs baseline $bdisp (tol $TOLERANCE)"
        notes="${notes:+$notes; }$msg"
      fi
    fi

    [[ "$status" == "FAIL" ]] && FAILS=$((FAILS+1))

    printf "%-28s %-16s %-8.3f %-8.3f %-9s %-6s %s\n" \
      "$label" "$metric" "$value" "$floor" "$bdisp" "$status" "$notes"
  done <<< "$metrics"
done

echo "-----------------------------------------------------------------------------"
PASSED=$((TOTAL-FAILS))
printf "Checks: %d   Passed: %d   Failed: %d   Files skipped: %d\n" "$TOTAL" "$PASSED" "$FAILS" "$SKIPPED"

if [[ "$TOTAL" -eq 0 ]]; then
  echo "WARN: no metric-bearing benchmark artifacts were evaluated." >&2
  exit 2
fi

if [[ "$FAILS" -gt 0 ]]; then
  echo ""
  echo "REGRESSION DETECTED - failing the build."
  exit 1
fi

echo ""
echo "All benchmark metrics within floors and baseline tolerance."
exit 0
