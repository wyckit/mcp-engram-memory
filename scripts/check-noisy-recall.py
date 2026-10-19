#!/usr/bin/env python3
"""Validate complete noisy-recall evidence; --release also rejects product blockers.

No fallback to committed or previous reports. Point this at a fresh run directory.
Uses only the Python standard library, also available on the CI runner.
"""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def blocker_keys(rows):
    result = set()
    for row in rows:
        reasons = []
        if row["WrongScopeIds"]:
            reasons.append("project/environment mismatch")
        if row["UnsupportedResolution"]:
            reasons.append("unsupported conflict resolution")
        if row["Critical"] and row["Expected"] and not row["AnswerHit"]:
            reasons.append("critical answer missed")
        if row["Critical"] and not row["Expected"] and not row["CorrectAbstention"]:
            reasons.append("critical missing-answer query returned evidence")
        result.update((row["Id"], row["Mode"], row["Phase"], reason) for reason in reasons)
    return result


def validate(report, manifest_bytes, fixture_bytes, size, namespaces):
    fixture = json.loads(fixture_bytes)
    questions = {q["id"]: q for q in fixture["queries"]}
    require(report["SchemaVersion"] == 1, "Unsupported report schema")
    require(report["DataMemories"] == size and report["Namespaces"] == namespaces, "Incorrect matrix cell")
    require(report["FixtureSha256"] == hashlib.sha256(fixture_bytes).hexdigest().upper(), "Fixture hash mismatch")
    require(report["ManifestSha256"] == hashlib.sha256(manifest_bytes).hexdigest().upper(), "Manifest hash mismatch")
    manifest = json.loads(manifest_bytes)
    require(len(manifest) == size == report["DistinctTexts"], "Wrong corpus size")
    require(len({m["Id"] for m in manifest}) == size, "Duplicate memory IDs")
    require(len({m["Text"] for m in manifest}) == size, "Duplicate corpus texts")
    require(len({m["Ns"] for m in manifest}) == namespaces, "Wrong namespace count")
    memories = {m["Id"]: m for m in manifest}
    require(report["Questions"] == len(questions), "Wrong question count")
    require(report["ExpectedObservations"] == len(questions) * 6, "Wrong observation count")
    rows = report["Observations"]
    expected = {(q, mode, phase) for q in questions for mode in ("direct", "automatic", "broadcast")
                for phase in ("warm", "restarted")}
    keys = [(r["Id"], r["Mode"], r["Phase"]) for r in rows]
    require(len(keys) == len(set(keys)) and set(keys) == expected, "Incomplete/duplicate query-route-phase matrix")
    for row in rows:
        q = questions[row["Id"]]
        require(row["Expected"] == q["expected"] and row["Critical"] == q["critical"], "Changed answer/critical labels")
        require(row["Split"] == q["split"] and row["Kind"] == q["kind"], "Changed split/kind labels")
        require(len(row["ReturnedIds"]) <= 5, "Public top-five limit exceeded")
        ids = row["ReturnedIds"]
        require(len(ids) == len(set(ids)) and all(i in memories for i in ids), "Invalid returned IDs")
        wrong = [i for i in ids if (memories[i]["Project"] and memories[i]["Project"] != q["project"]) or
                 (q["environment"] and memories[i]["Environment"] and memories[i]["Environment"] != q["environment"])]
        require(row["WrongScopeIds"] == wrong, "Scope metric differs from manifest labels")
        require(row["AnswerHit"] == bool(set(q["expected"]) & set(ids)), "Incorrect answer-hit metric")
        require(row["FalseAbstention"] == bool(q["expected"] and not ids), "Incorrect false-abstention metric")
        require(row["CorrectAbstention"] == (not q["expected"] and not ids), "Incorrect correct-abstention metric")
        require(row["StaleTopOne"] == bool(not q["history"] and ids and memories[ids[0]]["Obsolete"]), "Incorrect stale metric")
        facets = q["facets"]
        coverage = sum(bool(set(f) & set(ids)) for f in facets.values()) / len(facets) if facets else None
        require(row["FacetCoverage"] == coverage, "Incorrect facet metric")
        require(row["EmbeddingCalls"] == 1, "Expected one real query embedding")
        for field in ("Milliseconds", "EmbeddingMilliseconds"):
            require(math.isfinite(row[field]) and row[field] >= 0, "Invalid timing")
    require(report["Protections"]["Passed"] is True, "Scope/durability protections failed")
    require(len(report["Protections"]["Checks"]) == 11, "Incomplete protection checks")
    require((report["Protections"]["ConcurrentWrites"], report["Protections"]["ConcurrentReads"],
             report["Protections"]["MaintenancePasses"]) == (32, 16, 8), "Incomplete mixed workload")
    computed = blocker_keys(rows)
    declared = {(b["Query"], b["Mode"], b["Phase"], b["Reason"]) for b in report["ReleaseBlockers"]}
    require(computed == declared and len(declared) == len(report["ReleaseBlockers"]), "Blocker list does not match observations")
    require(report["ReleaseReady"] == (not computed), "Incorrect release-ready flag")
    return computed


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    parser.add_argument("--release", action="store_true", help="Fail on any predeclared release blocker")
    parser.add_argument("--small", action="store_true", help="Validate the 128-memory development run only")
    parser.add_argument("--framework", choices=("8", "9", "10"), default="8")
    args = parser.parse_args(argv)
    fixture = (Path(__file__).resolve().parents[1] / "tests/McpEngramMemory.Tests/Fixtures/recall-noisy-v1.json").read_bytes()
    cells = [(128, 8)] if args.small else [(size, ns) for size in (1000, 5000) for ns in (8, 32)]
    total = 0
    summaries = []
    for size, namespaces in cells:
        stem = args.directory / f"recall-noisy-{size}-{namespaces}-net{args.framework}"
        report = json.loads(stem.with_suffix(".json").read_bytes())
        manifest = stem.with_name(stem.name + "-manifest.json").read_bytes()
        blockers = validate(report, manifest, fixture, size, namespaces)
        total += len(blockers)
        summary = (f"{size} memories / {namespaces} namespaces: {len(report['Observations'])} observations, "
                   f"safety checks passed, {len(blockers)} release-blocker observations")
        print(summary)
        summaries.append(summary)
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
            summary.write("## Noisy recall evaluation\n\n" + "\n\n".join(summaries) + "\n\n")
            summary.write("**NOT RELEASE READY.** The evaluation completed, but product blockers remain. "
                          "See the uploaded reports; `--release` rejects these results.\n" if total else
                          "No declared blockers in this evaluation. Other release checks remain required.\n")
    if total:
        print(f"NOT RELEASE READY: {total} blocker observations (repeated queries/routes, not independent defects).")
        return 1 if args.release else 0
    print("No declared blockers in this evaluation; other release checks remain required.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"Invalid or incomplete recall evaluation: {error}", file=sys.stderr)
        sys.exit(2)
