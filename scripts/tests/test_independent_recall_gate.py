import contextlib
import copy
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("independent_gate", ROOT / "scripts/check-independent-recall.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)
FIXTURE = json.loads((ROOT / "tests/McpEngramMemory.Tests/Fixtures/recall-independent-v1.json").read_bytes())


def metadata(backend):
    return {"SchemaVersion": 1, "Backend": backend, "Metadata": {
        "FixtureSha256": gate.FROZEN_HASH, "FreezeRevision": "991873a", "Framework": ".NETCoreApp,Version=v8.0",
        "ModelSha256": "0" * 64, "SourceRevision": os.environ.get("ENGRAM_EVAL_REVISION", "test")}}


def quality(backend="json", miss=False):
    entries = {e["id"]: e for e in FIXTURE["entries"]}
    rows = []
    for q in FIXTURE["queries"]:
        for mode in gate.MODES:
            expected = [i for i in q["expected"] if mode != "direct" or entries[i]["ns"] == q["ns"]]
            for phase in ("warm", "restarted"):
                rows.append({"Id": q["id"], "Mode": mode, "Phase": phase,
                             "Strategy": "direct" if mode == "direct" else "broadcast",
                             "ReturnedIds": expected[:], "Expected": expected, "AnswerHit": bool(expected),
                             "TopOneCorrect": bool(expected), "CorrectAbstention": not expected,
                             "FacetCoverage": 1.0 if q["facets"] else None, "WrongScopeIds": [],
                             "UnsupportedResolution": False, "Milliseconds": 2.0, "Failures": []})
    report = {**metadata(backend), "Questions": 24, "Observations": rows, "ReleaseReady": not miss, "FailedObservations": int(miss)}
    if miss:
        row = next(r for r in rows if r["Id"] == "absent-secret")
        row.update(ReturnedIds=["c-secret-note"], CorrectAbstention=False, Failures=["non-answer evidence"])
    return report


def load(backend="json", size=1000):
    samples = []
    for phase, rounds, lanes in (("serial", 12, (*gate.MODES, "writer-serial")),
                                 ("concurrent", 48, (*gate.MODES, "writer-0", "writer-1", "maintenance"))):
        for lane in lanes:
            for i in range(rounds):
                start = i * 10_000
                end = start + 1000
                samples.append({"Lane": lane, "Phase": phase, "Round": i, "Start": start, "End": end,
                                "Milliseconds": 1.0, "Strategy": "direct" if lane == "direct" else
                                "broadcast" if lane in gate.MODES else None})
    groups = sorted({(s["Lane"], s["Phase"]) for s in samples})
    return {**metadata(backend), "DataMemories": size, "Namespaces": 8, "StopwatchFrequency": 1_000_000,
            "ConcurrentReads": 144, "ConcurrentWriterBatches": 96, "Mutations": 384, "MaintenancePasses": 48,
            "DurableAppends": 108, "FinalVersions": 3, "AbsentDeletes": 108, "ScopeChecksPassed": True,
            "DurabilityPassed": True, "OverlappingReads": 144, "Samples": samples,
            "SeedMilliseconds": 1, "RestartMilliseconds": 1, "FirstRequestMilliseconds": 1,
            "FinalRestartMilliseconds": 1, "ConcurrentMilliseconds": 500,
            "Distributions": [{"Lane": lane, "Phase": phase, "Milliseconds": {
                "Count": 12 if phase == "serial" else 48, "P50": 1.0, "P95": 1.0, "P99": 1.0, "Max": 1.0}}
                for lane, phase in groups],
            "ReadP95Ratios": [{"Mode": mode, "P95Ratio": 1.0} for mode in gate.MODES],
            "WriterP95Ratios": [{"Lane": lane, "P95Ratio": 1.0} for lane in ("writer-0", "writer-1")]}


class IndependentGateTests(unittest.TestCase):
    def test_complete_reports_and_reachable_direct_facets(self):
        self.assertEqual([], gate.validate_quality(quality(), FIXTURE, "json"))
        gate.validate_load(load(), "json", 1000)

    def test_failure_is_evidence_not_execution_failure(self):
        self.assertEqual(1, len(gate.validate_quality(quality(miss=True), FIXTURE, "json")))

    def test_missing_or_duplicate_question(self):
        for duplicate in (False, True):
            r = quality()
            r["Observations"].pop()
            if duplicate:
                r["Observations"].append(copy.deepcopy(r["Observations"][0]))
            with self.assertRaises(ValueError):
                gate.validate_quality(r, FIXTURE, "json")

    def test_false_release_claim_and_hidden_miss(self):
        for field, value in (("ReleaseReady", True), ("FailedObservations", 0)):
            r = quality(miss=True)
            r[field] = value
            with self.assertRaises(ValueError):
                gate.validate_quality(r, FIXTURE, "json")
        r = quality(miss=True)
        next(row for row in r["Observations"] if row["Failures"])["Failures"] = []
        with self.assertRaises(ValueError):
            gate.validate_quality(r, FIXTURE, "json")

    def test_changed_gold_and_fixture(self):
        r = quality()
        r["Observations"][0]["Expected"] = []
        with self.assertRaises(ValueError):
            gate.validate_quality(r, FIXTURE, "json")
        r = quality()
        r["Metadata"]["FixtureSha256"] = "bad"
        with self.assertRaises(ValueError):
            gate.validate_quality(r, FIXTURE, "json")

    def test_direct_scope_escape(self):
        r = quality()
        r["Observations"][0]["ReturnedIds"] = ["m-owner"]
        with self.assertRaisesRegex(ValueError, "namespace escape"):
            gate.validate_quality(r, FIXTURE, "json")

    def test_missing_load_lane_and_acknowledgements(self):
        r = load()
        r["Samples"].pop()
        with self.assertRaises(ValueError):
            gate.validate_load(r, "json", 1000)
        for field in ("Mutations", "DurableAppends", "AbsentDeletes"):
            r = load()
            r[field] = 0
            with self.assertRaises(ValueError):
                gate.validate_load(r, "json", 1000)

    def test_safety_and_durability_are_required(self):
        for field in ("ScopeChecksPassed", "DurabilityPassed"):
            r = load()
            r[field] = False
            with self.assertRaises(ValueError):
                gate.validate_load(r, "json", 1000)

    def test_overlap_must_be_observed(self):
        r = load()
        for s in r["Samples"]:
            if s["Lane"].startswith("writer-"):
                s["Start"] += 1_000_000
                s["End"] += 1_000_000
        with self.assertRaisesRegex(ValueError, "overlap"):
            gate.validate_load(r, "json", 1000)

    def test_fabricated_percentiles_and_invalid_time(self):
        r = load()
        r["Distributions"][0]["Milliseconds"]["P95"] = 0
        with self.assertRaises(ValueError):
            gate.validate_load(r, "json", 1000)
        r = load()
        r["Samples"][0]["Milliseconds"] = float("nan")
        with self.assertRaises(ValueError):
            gate.validate_load(r, "json", 1000)

    def test_liveness_bound(self):
        r = load()
        r["ConcurrentMilliseconds"] = 180_001
        with self.assertRaises(ValueError):
            gate.validate_load(r, "json", 1000)

    def test_release_mode_and_missing_file(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            root = Path(directory)
            for backend in ("json", "sqlite"):
                (root / f"recall-independent-{backend}-net8.json").write_text(json.dumps(quality(backend, miss=True)))
                for size in (1000, 5000):
                    (root / f"recall-load-{backend}-{size}-net8.json").write_text(json.dumps(load(backend, size)))
            self.assertEqual(0, gate.main([directory]))
            self.assertEqual(1, gate.main([directory, "--release"]))
            (root / "recall-load-json-1000-net8.json").unlink()
            with self.assertRaises(OSError):
                gate.main([directory])


if __name__ == "__main__":
    unittest.main()
