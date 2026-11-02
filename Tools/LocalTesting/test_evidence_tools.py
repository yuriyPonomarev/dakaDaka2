import contextlib
import copy
import io
import json
from pathlib import Path
import tempfile
import unittest

import compare_sources
import summarize_render


def envelope(entry, result):
    return {"success": True, "data": {"command": "script run",
            "parameters": {"file": "AgentScripts/ReviewD4c1.cs", "entry": entry},
            "target": {"projectPath": "project", "port": 7800},
            "result": {"success": True, "result": result}}}


def measurement():
    names = ([f"StatLabel{i}" for i in range(14)] + [f"StatValue{i}" for i in range(14)]
             + [f"EquipLabel_{i}" for i in range(13)])
    rows = [{"name": name, "realMetrics": True, "fits": True, "onScreen": True,
             "inPanel": True, "inPane": True, "width": 100, "height": 10,
             "preferredWidth": 50, "preferredHeight": 9, "screenWidth": 100,
             "screenHeight": 10} for name in names]
    return {"pass": True, "failures": [], "frame": 10, "gameTime": 2,
            "width": 1280, "height": 720, "graphics": "Direct3D12", "statLabels": 14,
            "statValues": 14, "equipmentLabels": 13, "opening": "1x", "expected": "1x",
            "measurements": rows}


class EvidenceToolTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.a = Path(self.temp.name) / "a.json"
        self.b = Path(self.temp.name) / "b.json"

    def write(self, path, value):
        path.write_text(json.dumps(value), encoding="utf-8")

    def test_source_comparison_finds_added_removed_and_changed(self):
        self.write(self.a, {"a": "a" * 64, "b": "b" * 64})
        self.write(self.b, {"a": "c" * 64, "c": "c" * 64})
        result = compare_sources.compare(self.a, self.b)
        self.assertEqual((result["added"], result["removed"], result["changed"]), (["c"], ["b"], ["a"]))
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(compare_sources.main([str(self.a), str(self.b)]), 1)
            self.assertEqual(compare_sources.main([str(self.a), str(self.a)]), 0)

    def test_bad_snapshot_is_not_identical(self):
        self.write(self.a, {"a": "not-a-hash"})
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(compare_sources.main([str(self.a), str(self.a)]), 2)

    def test_render_requires_frames_and_complete_real_metrics(self):
        prep = envelope("ReviewD4c1.Prepare", {"prepared": True, "frame": 1, "gameTime": 0})
        result = measurement()
        self.write(self.a, prep)
        self.write(self.b, envelope("ReviewD4c1.Measure", result))
        self.assertEqual(summarize_render.check(self.a, self.b)["status"], "pass")
        for field, bad in (("frame", 1), ("gameTime", 0), ("width", 0), ("failures", ["bad"])):
            with self.subTest(field=field):
                value = copy.deepcopy(result)
                value[field] = bad
                self.write(self.b, envelope("ReviewD4c1.Measure", value))
                self.assertEqual(summarize_render.check(self.a, self.b)["status"], "attention")
        for mutation in ("duplicate", "flag", "zero"):
            value = copy.deepcopy(result)
            if mutation == "duplicate":
                value["measurements"][1]["name"] = value["measurements"][0]["name"]
            elif mutation == "flag":
                value["measurements"][0]["fits"] = False
            else:
                value["measurements"][0]["preferredWidth"] = 0
            self.write(self.b, envelope("ReviewD4c1.Measure", value))
            self.assertEqual(summarize_render.check(self.a, self.b)["status"], "attention")

    def test_wrong_editor_or_failed_transport_cannot_pass(self):
        self.write(self.a, envelope("ReviewD4c1.Prepare", {"prepared": True, "frame": 1, "gameTime": 0}))
        value = envelope("ReviewD4c1.Measure", measurement())
        value["data"]["target"]["port"] = 7801
        self.write(self.b, value)
        self.assertEqual(summarize_render.check(self.a, self.b)["status"], "attention")
        value["success"] = False
        self.write(self.b, value)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(summarize_render.main([str(self.a), str(self.b)]), 2)


if __name__ == "__main__":
    unittest.main()
