"""The release gate must not accept incomplete evidence or a false ready flag."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("gate", Path(__file__).parents[1] / "check-noisy-recall.py")
gate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(gate)


class ReportGateTests(unittest.TestCase):
    def setUp(self):
        self.fixture = json.dumps({"queries": [{"id": "q", "expected": ["answer"], "critical": True,
                                                 "split": "evaluation", "kind": "fact", "project": "Juniper",
                                                 "environment": "production", "history": False, "facets": {}}]}).encode()
        self.manifest = json.dumps([{"Id": "answer", "Ns": "public", "Text": "answer text", "Project": "Juniper",
                                    "Environment": "production", "Obsolete": False}]).encode()
        rows = [dict(Id="q", Mode=mode, Phase=phase, Expected=["answer"], Critical=True,
                     Split="evaluation", Kind="fact", ReturnedIds=["answer"], WrongScopeIds=[],
                     UnsupportedResolution=False, AnswerHit=True, CorrectAbstention=False,
                     FalseAbstention=False, StaleTopOne=False, FacetCoverage=None,
                     Milliseconds=1, EmbeddingMilliseconds=.5, EmbeddingCalls=1)
                for mode in ("direct", "automatic", "broadcast") for phase in ("warm", "restarted")]
        self.report = dict(SchemaVersion=1, DataMemories=1, Namespaces=1, DistinctTexts=1,
                           FixtureSha256=hashlib.sha256(self.fixture).hexdigest().upper(),
                           ManifestSha256=hashlib.sha256(self.manifest).hexdigest().upper(),
                           Questions=1, ExpectedObservations=6, Observations=rows,
                           Protections=dict(Passed=True, Checks=list(range(11)), ConcurrentWrites=32,
                                            ConcurrentReads=16, MaintenancePasses=8),
                           ReleaseReady=True, ReleaseBlockers=[])

    def validate(self):
        return gate.validate(self.report, self.manifest, self.fixture, 1, 1)

    def test_complete_evidence(self):
        self.assertEqual(set(), self.validate())

    def test_missing_route(self):
        self.report["Observations"].pop()
        with self.assertRaisesRegex(ValueError, "matrix"):
            self.validate()

    def test_duplicate_observation_does_not_replace_missing_route(self):
        self.report["Observations"][-1] = copy.deepcopy(self.report["Observations"][0])
        with self.assertRaisesRegex(ValueError, "matrix"):
            self.validate()

    def test_false_ready_flag(self):
        self.report["ReleaseReady"] = False
        with self.assertRaisesRegex(ValueError, "ready"):
            self.validate()

    def test_miss_cannot_be_hidden_by_omitting_blocker(self):
        self.report["Observations"][0]["AnswerHit"] = False
        self.report["Observations"][0]["ReturnedIds"] = []
        self.report["Observations"][0]["FalseAbstention"] = True
        with self.assertRaisesRegex(ValueError, "Blocker"):
            self.validate()

    def test_recorded_miss_is_not_ready(self):
        self.report["Observations"][0]["AnswerHit"] = False
        self.report["Observations"][0]["ReturnedIds"] = []
        self.report["Observations"][0]["FalseAbstention"] = True
        self.report["ReleaseBlockers"] = [dict(Query="q", Mode="direct", Phase="warm", Reason="critical answer missed")]
        self.report["ReleaseReady"] = False
        self.assertEqual(1, len(self.validate()))

    def test_critical_label_cannot_be_relaxed(self):
        self.report["Observations"][0]["Critical"] = False
        with self.assertRaisesRegex(ValueError, "labels"):
            self.validate()

    def test_miss_cannot_be_claimed_as_a_hit(self):
        self.report["Observations"][0]["ReturnedIds"] = []
        with self.assertRaisesRegex(ValueError, "answer-hit"):
            self.validate()

    def test_stale_fixture(self):
        self.report["FixtureSha256"] = "old"
        with self.assertRaisesRegex(ValueError, "Fixture"):
            self.validate()

    def test_truncated_manifest(self):
        self.manifest = b"[]"
        with self.assertRaisesRegex(ValueError, "Manifest"):
            self.validate()

    def test_missing_files_never_fall_back(self):
        with self.assertRaises(OSError):
            gate.main(["__missing_noisy_recall_evidence__"])

    def test_scope_failures_are_not_quality_waivers(self):
        self.report["Protections"]["Passed"] = False
        with self.assertRaisesRegex(ValueError, "protections"):
            self.validate()


if __name__ == "__main__":
    unittest.main()
