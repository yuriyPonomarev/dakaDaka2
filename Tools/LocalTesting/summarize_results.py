"""Read existing Unity runner summaries; never execute tests or modify evidence."""
import argparse
import json
from pathlib import Path
import re
import sys


PASS_LINE = re.compile(r"\bPASS\b.*\bchecks=(\d+)\b.*\bfailures=0\b")


def classify(path):
    """Return a conservative digest. Runner summaries and logs remain authoritative."""
    path = Path(path)
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if not isinstance(data, dict):
            raise ValueError("summary must be an object")
        if type(data.get("passed")) is not bool:
            raise ValueError("summary missing boolean passed")
        rows = []
        if "results" in data and isinstance(data["results"], list):
            if not data["results"]:
                raise ValueError("gate results are empty")
            for item in data["results"]:
                if not isinstance(item, dict) or not isinstance(item.get("method"), str):
                    raise ValueError("gate result missing method")
                summaries = item.get("summaries")
                problems = item.get("problems")
                if not isinstance(summaries, list) or not isinstance(problems, list):
                    raise ValueError("gate result missing summaries or problems")
                matches = [PASS_LINE.search(x) for x in summaries if isinstance(x, str)]
                matches = [m for m in matches if m]
                ok = (item.get("passed") is True
                      and item.get("exitCode") == 0 and item.get("timedOut") is False
                      and not problems and bool(matches) and int(matches[0].group(1)) > 0
                      and isinstance(item.get("log"), str) and bool(item["log"]))
                rows.append({"name": item["method"], "status": "pass" if ok else "attention",
                             "checks": int(matches[0].group(1)) if matches else None,
                             "problems": problems, "log": item.get("log")})
        elif "tests" in data:
            fields = ("tests", "failed", "skipped", "inconclusive")
            if any(type(data.get(k)) is not int or data[k] < 0 for k in fields):
                raise ValueError("invalid or missing EditMode counts")
            ok = (data.get("passed") is True and data.get("exitCode") == 0
                  and data.get("timedOut") is False and data.get("status") == "Passed"
                  and data["tests"] > 0 and all(data[k] == 0 for k in fields[1:])
                  and data.get("problems") == [] and isinstance(data.get("log"), str)
                  and isinstance(data.get("results"), str))
            rows.append({"name": "EditMode", "status": "pass" if ok else "attention",
                         **{k: data[k] for k in fields}, "log": data.get("log"),
                         "results": data.get("results")})
        else:
            raise ValueError("unrecognized runner summary")
        return {"summary": str(path), "status": "pass" if data["passed"] and all(r["status"] == "pass" for r in rows)
                else "attention", "rows": rows}
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        return {"summary": str(path), "status": "invalid", "error": str(exc), "rows": []}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("summaries", nargs="+", help="existing runner summary.json files")
    args = parser.parse_args(argv)
    reports = [classify(path) for path in args.summaries]
    print(json.dumps({"note": "Digest only; inspect original summaries and logs.", "reports": reports},
                     ensure_ascii=False, indent=2))
    return 0 if all(r["status"] == "pass" for r in reports) else 1


if __name__ == "__main__":
    sys.exit(main())
