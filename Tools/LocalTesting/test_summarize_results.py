import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

import summarize_results as digest
from prompt_builder import build_prompt


def gate():
    return {"method": "Example.Run", "passed": True, "exitCode": 0, "timedOut": False,
            "summaries": ["[Example] PASS checks=12 failures=0"], "problems": [], "log": "run.log"}


class DigestTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "summary.json"

    def classify(self, obj):
        self.path.write_text(json.dumps(obj), encoding="utf-8")
        return digest.classify(self.path)

    def test_passed_gate_and_editmode(self):
        result = self.classify({"passed": True, "results": [gate()]})
        self.assertEqual((result["status"], result["rows"][0]["checks"]), ("pass", 12))
        result = self.classify({"passed": True, "exitCode": 0, "timedOut": False,
                                "tests": 402, "failed": 0, "skipped": 0, "inconclusive": 0,
                                "status": "Passed", "problems": [], "log": "editor.log", "results": "results.xml"})
        self.assertEqual(result["status"], "pass")

    def test_failure_never_becomes_pass_on_zero_exit(self):
        for change in ({"problems": ["FAIL"]}, {"summaries": []}, {"timedOut": True},
                       {"exitCode": 1}, {"summaries": ["PASS checks=0 failures=0"]}):
            with self.subTest(change=change):
                item = gate()
                item.update(change)
                self.assertEqual(self.classify({"passed": True, "results": [item]})["status"], "attention")

    def test_mixed_batch_keeps_individual_results(self):
        bad = gate()
        bad.update(method="Bad.Run", passed=False, problems=["mismatch"], summaries=[])
        result = self.classify({"passed": False, "results": [gate(), bad]})
        self.assertEqual(result["status"], "attention")
        self.assertEqual([r["status"] for r in result["rows"]], ["pass", "attention"])

    def test_incomplete_or_bad_summary_is_not_a_pass(self):
        for data in ({"passed": True, "results": []}, {"passed": True, "tests": 4},
                     {"results": [gate()]}, []):
            with self.subTest(data=data):
                self.assertEqual(self.classify(data)["status"], "invalid")
        self.path.write_text("not json", encoding="utf-8")
        self.assertEqual(digest.classify(self.path)["status"], "invalid")
        self.assertEqual(digest.classify(self.path.parent / "missing.json")["status"], "invalid")

    def test_skips_and_inconclusive_require_attention(self):
        base = {"passed": True, "exitCode": 0, "timedOut": False, "tests": 1,
                "failed": 0, "skipped": 0, "inconclusive": 0, "status": "Passed",
                "problems": [], "log": "editor.log", "results": "results.xml"}
        for field in ("skipped", "inconclusive", "failed"):
            data = dict(base, **{field: 1})
            self.assertEqual(self.classify(data)["status"], "attention")

    def test_cli_returns_nonzero_for_bad_input(self):
        self.path.write_text("{}", encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(digest.main([str(self.path)]), 1)

    def test_profile_is_injected_between_contract_and_brief(self):
        paths = [Path(self.temp.name) / x for x in ("contract", "profile", "brief")]
        for path, value in zip(paths, ("contract", "profile", "task")):
            path.write_text(value, encoding="utf-8")
        self.assertEqual(build_prompt(*paths), "contract\n\nprofile\n\ntask")
        paths[1].write_text("", encoding="utf-8")
        with self.assertRaises(ValueError):
            build_prompt(*paths)


if __name__ == "__main__":
    unittest.main()
