"""Check the specific ReviewD4c1 Prepare/Measure envelopes; raw evidence remains authoritative."""
import argparse
import json
import math
from pathlib import Path
import sys


def number(value):
    return type(value) in (int, float) and math.isfinite(value)


def load_envelope(path, entry):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    details = data["data"]
    call = details["result"]
    params = details["parameters"]
    if (data.get("success") is not True or call.get("success") is not True
            or details.get("command") != "script run"
            or params.get("file") != "AgentScripts/ReviewD4c1.cs"
            or params.get("entry") != entry or not isinstance(call.get("result"), dict)):
        raise ValueError("wrong or unsuccessful script-run envelope: " + str(path))
    target = details["target"]
    if not isinstance(target.get("projectPath"), str) or not target["projectPath"] or type(target.get("port")) is not int:
        raise ValueError("missing editor identity: " + str(path))
    return call["result"], (target["projectPath"], target["port"])


def check(prepare_path, measure_path):
    prepare, first_target = load_envelope(prepare_path, "ReviewD4c1.Prepare")
    measure, second_target = load_envelope(measure_path, "ReviewD4c1.Measure")
    issues = []
    if first_target != second_target:
        issues.append("Editor identity differs between calls")
    if prepare.get("prepared") is not True:
        issues.append("Prepare did not succeed")
    if not all(number(r.get(k)) for r in (prepare, measure) for k in ("frame", "gameTime")):
        issues.append("frame/time data missing or invalid")
    elif measure["frame"] <= prepare["frame"] or measure["gameTime"] <= prepare["gameTime"]:
        issues.append("frame or game time did not advance")
    if (measure.get("pass") is not True or measure.get("failures") != []
            or (measure.get("statLabels"), measure.get("statValues"), measure.get("equipmentLabels")) != (14, 14, 13)):
        issues.append("probe reports failure or wrong row counts")
    if not all(number(measure.get(k)) and measure[k] > 0 for k in ("width", "height")):
        issues.append("invalid viewport")
    if not isinstance(measure.get("graphics"), str) or measure["graphics"] in ("", "Null"):
        issues.append("no graphical device")
    if not isinstance(measure.get("opening"), str) or not measure["opening"] or measure.get("opening") != measure.get("expected"):
        issues.append("opening value missing or inconsistent")
    rows = measure.get("measurements")
    if not isinstance(rows, list) or len(rows) != 41 or any(not isinstance(r, dict) for r in rows):
        issues.append("missing or invalid measurement rows")
    else:
        names = [r.get("name") for r in rows]
        expected_names = {f"StatLabel{i}" for i in range(14)} | {f"StatValue{i}" for i in range(14)}
        if (len(set(names)) != 41 or not expected_names.issubset(names)
                or sum(isinstance(n, str) and n.startswith("EquipLabel_") for n in names) != 13):
            issues.append("missing or duplicate row names")
        flags = ("realMetrics", "fits", "onScreen", "inPanel", "inPane")
        if any(any(r.get(flag) is not True for flag in flags) for r in rows):
            issues.append("row metrics or containment failed")
        dimensions = ("width", "height", "preferredWidth", "preferredHeight", "screenWidth", "screenHeight")
        if any(any(not number(r.get(k)) or r[k] <= 0 for k in dimensions) for r in rows):
            issues.append("row dimensions missing or invalid")
    return {"status": "attention" if issues else "pass", "issues": issues,
            "prepare": str(prepare_path), "measure": str(measure_path),
            "project": first_target[0], "viewport": [measure.get("width"), measure.get("height")],
            "prepareFrame": prepare.get("frame"), "measureFrame": measure.get("frame"),
            "rows": len(rows) if isinstance(rows, list) else None,
            "note": "Numeric digest only; inspect raw evidence and capture. No visual review is implied."}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("prepare")
    parser.add_argument("measure")
    args = parser.parse_args(argv)
    try:
        result = check(args.prepare, args.measure)
    except (OSError, UnicodeError, json.JSONDecodeError, KeyError, TypeError, ValueError) as exc:
        print(json.dumps({"status": "invalid", "error": str(exc)}, ensure_ascii=False))
        return 2
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0 if result["status"] == "pass" else 1


if __name__ == "__main__":
    sys.exit(main())
