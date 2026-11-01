"""Best-effort atomic progress publishing; a busy status file must not kill QA."""
import json
import os
from pathlib import Path
import sys
import time

_RETRY_DELAYS = (0, .02, .05, .1)
_last_warning = None


def _warn(path, error):
    global _last_warning
    now = time.monotonic()
    if _last_warning is not None and now - _last_warning < 30:
        return
    _last_warning = now
    try:
        print(f"Local QA progress not published to {path}: {error}; verification continues.",
              file=sys.stderr, flush=True)
    except (OSError, ValueError):
        pass


def write_status(path, values):
    """Publish complete JSON, retry transient filesystem failures, return False if busy.

    Status is observability, not a prerequisite for draining the worker's stdout.
    The previous complete document stays readable if publication cannot succeed.
    The single controller lock serializes writers; the PID avoids stale temp reuse.
    """
    path = Path(path)
    payload = json.dumps(values, ensure_ascii=False, indent=2) + "\n"
    temporary = path.with_name(f"{path.name}.{os.getpid()}.tmp")
    last_error = None
    try:
        for delay in _RETRY_DELAYS:
            if delay:
                time.sleep(delay)
            try:
                temporary.write_text(payload, encoding="utf-8")
                os.replace(temporary, path)
                return True
            except OSError as error:
                last_error = error
        _warn(path, last_error)
        return False
    finally:
        try:
            temporary.unlink(missing_ok=True)
        except OSError:
            pass
