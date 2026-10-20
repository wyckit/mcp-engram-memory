#!/usr/bin/env python3
"""Validate new recall/load evidence. --release additionally rejects all frozen quality misses."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys

FROZEN_HASH = "11D86B86DF1629FC9931DE7DC846E4A7B317A9C31F15FB6363D73323CC4F6553"
MODES = ("direct", "automatic", "broadcast")


def require(ok, message):
    if not ok:
        raise ValueError(message)


def finite(value):
    return isinstance(value, (int, float)) and math.isfinite(value) and value >= 0


def metadata(report, backend):
    require(report["SchemaVersion"] == 1 and report["Backend"] == backend, "Wrong schema/backend")
    require(report["Metadata"]["FixtureSha256"] == FROZEN_HASH, "Changed fixture identity")
    require(report["Metadata"]["FreezeRevision"] == "991873a", "Changed freeze revision")
    require(report["Metadata"]["Framework"] == ".NETCoreApp,Version=v8.0", "Wrong framework")
    require(len(report["Metadata"]["ModelSha256"]) == 64, "Missing model hash")
    revision = os.environ.get("ENGRAM_EVAL_REVISION")
    if revision:
        require(report["Metadata"]["SourceRevision"] == revision, "Stale source revision")


def validate_quality(report, fixture, backend):
    metadata(report, backend)
    entries = {e["id"]: e for e in fixture["entries"]}
    queries = {q["id"]: q for q in fixture["queries"]}
    require(report["Questions"] == len(queries) == 24, "Wrong question count")
    rows = report["Observations"]
    keys = [(r["Id"], r["Mode"], r["Phase"]) for r in rows]
    expected_keys = {(q, m, p) for q in queries for m in MODES for p in ("warm", "restarted")}
    require(len(keys) == len(set(keys)) and set(keys) == expected_keys, "Incomplete quality matrix")
    failed = []
    for r in rows:
        q = queries[r["Id"]]
        ids = r["ReturnedIds"]
        require(len(ids) <= 5 and len(ids) == len(set(ids)) and all(i in entries for i in ids), "Invalid source IDs")
        direct = r["Mode"] == "direct"
        eligible = lambda i: not direct or entries[i]["ns"] == q["ns"]
        require(not direct or all(eligible(i) for i in ids), "Direct namespace escape")
        expected = [i for i in q["expected"] if eligible(i)]
        require(r["Expected"] == expected, "Altered gold labels")
        hit = bool(set(expected) & set(ids))
        top = bool(ids and ids[0] in expected)
        abstain = not expected and not ids
        facets = [[i for i in f if eligible(i)] for f in q["facets"].values()]
        facets = [f for f in facets if f]
        coverage = sum(bool(set(f) & set(ids)) for f in facets) / len(facets) if facets else None
        wrong = [i for i in ids if entries[i]["project"] != q["project"] or
                 (q["environment"] and entries[i]["environment"] != q["environment"])]
        require(r["AnswerHit"] == hit and r["TopOneCorrect"] == top and r["CorrectAbstention"] == abstain,
                "Incorrect answer/abstention metric")
        require(r["FacetCoverage"] == coverage and r["WrongScopeIds"] == wrong, "Incorrect facet/scope metric")
        require(isinstance(r["UnsupportedResolution"], bool), "Missing conflict metric")
        reasons = []
        if expected and not hit:
            reasons.append("answer missed")
        if q["topOne"] and not top:
            reasons.append("incorrect top one")
        if not expected and not abstain:
            reasons.append("non-answer evidence")
        if coverage is not None and coverage < 1:
            reasons.append("incomplete facets")
        if wrong:
            reasons.append("project/environment mismatch")
        if r["UnsupportedResolution"]:
            reasons.append("unsupported conflict resolution")
        require(r["Failures"] == reasons, "Hidden or invented quality failures")
        require(finite(r["Milliseconds"]), "Invalid latency")
        require(r["Strategy"] in ("direct", "deep_recall") if direct else
                r["Strategy"] == "broadcast" if r["Mode"] == "broadcast" else
                r["Strategy"] in ("broadcast", "expert_routed"), "Wrong route strategy")
        if reasons:
            failed.append(r)
    require(report["ReleaseReady"] == (not failed) and report["FailedObservations"] == len(failed),
            "False release claim")
    return failed


def percentile(values, fraction):
    return sorted(values)[math.ceil(len(values) * fraction) - 1]


def validate_load(report, backend, size):
    metadata(report, backend)
    require(report["DataMemories"] == size and report["Namespaces"] == 8, "Wrong load cell")
    required = {"ConcurrentReads": 144, "ConcurrentWriterBatches": 96, "Mutations": 384,
                "MaintenancePasses": 48, "DurableAppends": 108, "FinalVersions": 3, "AbsentDeletes": 108}
    require(all(report[k] == v for k, v in required.items()), "Incomplete operations or durability checks")
    require(report["ScopeChecksPassed"] is True and report["DurabilityPassed"] is True, "Safety/durability failed")
    samples = report["Samples"]
    keys = [(s["Lane"], s["Phase"], s["Round"]) for s in samples]
    expected = {(lane, "serial", i) for lane in (*MODES, "writer-serial") for i in range(12)}
    expected |= {(lane, "concurrent", i) for lane in (*MODES, "writer-0", "writer-1", "maintenance") for i in range(48)}
    require(len(keys) == len(set(keys)) and set(keys) == expected, "Incomplete or duplicate load samples")
    frequency = report["StopwatchFrequency"]
    require(finite(frequency) and frequency > 0, "Invalid clock frequency")
    for s in samples:
        require(s["Start"] >= 0 and s["End"] >= s["Start"] and finite(s["Milliseconds"]), "Invalid request interval")
        require(math.isclose(s["Milliseconds"], (s["End"] - s["Start"]) * 1000 / frequency), "Invalid duration")
        if s["Lane"] in MODES:
            require(s["Strategy"] in ("direct", "deep_recall") if s["Lane"] == "direct" else
                    s["Strategy"] == "broadcast" if s["Lane"] == "broadcast" else
                    s["Strategy"] in ("broadcast", "expert_routed"), "Invalid actual load route")
    reads = [s for s in samples if s["Phase"] == "concurrent" and s["Lane"] in MODES]
    writes = [s for s in samples if s["Phase"] == "concurrent" and s["Lane"].startswith("writer-")]
    overlap = sum(any(r["Start"] < w["End"] and w["Start"] < r["End"] for w in writes) for r in reads)
    require(overlap > 0 and report["OverlappingReads"] == overlap, "No verified read/write overlap")
    groups = {(s["Lane"], s["Phase"]) for s in samples}
    distributions = report["Distributions"]
    require(len(distributions) == len(groups) and {(d["Lane"], d["Phase"]) for d in distributions} == groups,
            "Incomplete distributions")
    for d in distributions:
        values = [s["Milliseconds"] for s in samples if (s["Lane"], s["Phase"]) == (d["Lane"], d["Phase"])]
        expected_d = {"Count": len(values), "P50": percentile(values, .5), "P95": percentile(values, .95),
                      "P99": percentile(values, .99), "Max": max(values)}
        require(d["Milliseconds"] == expected_d, "Incorrect latency distribution")
    for field, lanes, key in (("ReadP95Ratios", MODES, "Mode"), ("WriterP95Ratios", ("writer-0", "writer-1"), "Lane")):
        ratios = report[field]
        require(len(ratios) == len(lanes) and {r[key] for r in ratios} == set(lanes), "Incomplete contention ratios")
        for r in ratios:
            serial_lane = r[key] if field == "ReadP95Ratios" else "writer-serial"
            concurrent = [s["Milliseconds"] for s in samples if s["Lane"] == r[key] and s["Phase"] == "concurrent"]
            serial = [s["Milliseconds"] for s in samples if s["Lane"] == serial_lane and s["Phase"] == "serial"]
            require(math.isclose(r["P95Ratio"], percentile(concurrent, .95) / percentile(serial, .95)), "Incorrect contention ratio")
    for key in ("SeedMilliseconds", "RestartMilliseconds", "FirstRequestMilliseconds", "FinalRestartMilliseconds", "ConcurrentMilliseconds"):
        require(finite(report[key]), "Missing or invalid phase timing")
    require(report["ConcurrentMilliseconds"] <= 180_000, "Load exceeded frozen liveness bound")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--release", action="store_true")
    args = parser.parse_args(argv)
    raw = (Path(__file__).resolve().parents[1] / "tests/McpEngramMemory.Tests/Fixtures/recall-independent-v1.json").read_bytes()
    require(hashlib.sha256(raw).hexdigest().upper() == FROZEN_HASH, "Frozen fixture changed")
    fixture = json.loads(raw)
    failures = []
    lines = []
    identities = set()
    for backend in ("json", "sqlite"):
        report = json.loads((args.directory / f"recall-independent-{backend}-net8.json").read_bytes())
        rows = validate_quality(report, fixture, backend)
        identities.add((report["Metadata"]["SourceRevision"], report["Metadata"]["ModelSha256"]))
        failures.extend(rows)
        lines.append(f"{backend}: 144 quality observations, {len(rows)} failed frozen release criteria.")
        for size in (1000, 5000):
            load = json.loads((args.directory / f"recall-load-{backend}-{size}-net8.json").read_bytes())
            validate_load(load, backend, size)
            require(load["Metadata"]["SourceRevision"] == report["Metadata"]["SourceRevision"] and
                    load["Metadata"]["ModelSha256"] == report["Metadata"]["ModelSha256"], "Mixed source/model reports")
            lines.append(f"{backend}/{size}: 144 concurrent reads, 384 mutations, 48 maintenance passes; "
                         f"{load['OverlappingReads']} reads overlap writes; scope and durability passed.")
    require(len(identities) == 1, "Mixed backend source/model reports")
    lines.append(f"NOT RELEASE READY: {len(failures)} failed observations across "
                 f"{len({r['Id'] for r in failures})} questions." if failures else
                 "No declared blockers in this suite; other release checks still required.")
    print("\n".join(lines))
    if path := os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(path, "a", encoding="utf-8") as f:
            f.write("## Independent recall and load validation\n\n" + "\n\n".join(lines) + "\n")
    return 1 if args.release and failures else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError, ZeroDivisionError) as e:
        print(f"Invalid independent validation evidence: {e}", file=sys.stderr)
        sys.exit(2)
