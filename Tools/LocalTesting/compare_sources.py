"""Compare two raw controller source-before.json hash maps without altering them."""
import argparse
import json
from pathlib import Path
import re
import sys


def read_snapshot(path):
    data = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(data, dict) or not data:
        raise ValueError("snapshot must be a nonempty object")
    for name, value in data.items():
        if not isinstance(name, str) or not name or not isinstance(value, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", value):
            raise ValueError("snapshot must map source paths to SHA-256 hashes")
    return {k: v.lower() for k, v in data.items()}


def compare(left, right):
    before, after = read_snapshot(left), read_snapshot(right)
    old, new = set(before), set(after)
    return {"before": str(left), "after": str(right), "beforeCount": len(old), "afterCount": len(new),
            "added": sorted(new - old), "removed": sorted(old - new),
            "changed": sorted(k for k in old & new if before[k] != after[k])}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("before")
    parser.add_argument("after")
    args = parser.parse_args(argv)
    try:
        result = compare(args.before, args.after)
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as exc:
        print(json.dumps({"status": "invalid", "error": str(exc)}, ensure_ascii=False))
        return 2
    result["status"] = "changed" if any(result[k] for k in ("added", "removed", "changed")) else "identical"
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 1 if result["status"] == "changed" else 0


if __name__ == "__main__":
    sys.exit(main())
