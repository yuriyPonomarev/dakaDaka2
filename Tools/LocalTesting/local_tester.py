"""Single local Pi/Strata test worker. Codex develops; this worker only verifies."""
import argparse
import datetime
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import subprocess
import time
import urllib.request

PROJECT = Path(__file__).resolve().parents[2]
ROOT = PROJECT / "Logs" / "local-testing"
REQUESTS = ROOT / "requests"
RESULTS = ROOT / "results"
for folder in (ROOT, REQUESTS, RESULTS, ROOT / "sessions"):
    folder.mkdir(parents=True, exist_ok=True)
STATE = ROOT / "status.json"
CONTRACT = PROJECT / "Tools" / "LocalTesting" / "TESTER_CONTRACT.md"
# Share the original controller's OS lock without replacing its historical state.
lock_path = Path.home() / ".pi/agent/projects/dungeon-extraction/controller.lock"
lock_path.parent.mkdir(parents=True, exist_ok=True)
lock = lock_path.open("a+b")
if not lock_path.stat().st_size:
    lock.write(b"0")
    lock.flush()
lock.seek(0)
try:
    msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
except OSError:
    raise SystemExit("Another local project agent already owns the controller lock.")

def save(**values):
    values.update(controller_pid=os.getpid(), project=str(PROJECT),
                  provider="strata", model="strata", thinking="high",
                  updated_at=datetime.datetime.now().astimezone().isoformat())
    temporary = STATE.with_suffix(".tmp")
    temporary.write_text(json.dumps(values, ensure_ascii=False, indent=2), encoding="utf-8")
    temporary.replace(STATE)

def ready():
    try:
        with urllib.request.urlopen("http://127.0.0.1:8080/health", timeout=5) as response:
            return bool(json.load(response).get("loaded"))
    except Exception:
        return False

def source_hashes():
    paths = list((PROJECT / "Assets/Emberdelve").rglob("*.cs"))
    paths += list((PROJECT / "Tools").rglob("*.ps1"))
    return {str(p.relative_to(PROJECT)).replace("\\", "/"):
            hashlib.sha256(p.read_bytes()).hexdigest() for p in paths}

install = Path.home() / ".pi/agent/install"
version = (install / "current-version").read_text(encoding="utf-8-sig").strip()
package = install / "releases" / version / "node_modules/@earendil-works/pi-coding-agent"
manifest = json.loads((package / "package.json").read_text(encoding="utf-8"))
cli = package / manifest["bin"]["pi"]
node = Path("C:/Program Files/nodejs/node.exe")
save(state="waiting_for_model")
while True:
    while not ready():
        time.sleep(3)
    jobs = [p for p in sorted(REQUESTS.glob("*.json"))
            if not (RESULTS / (p.stem + ".json")).exists()]
    if not jobs:
        save(state="idle")
        time.sleep(3)
        continue
    request_path = jobs[0]
    request = json.loads(request_path.read_text(encoding="utf-8-sig"))
    job = request_path.stem
    run = ROOT / "runs" / job
    run.mkdir(parents=True, exist_ok=True)
    brief = Path(request["brief"])
    if not brief.is_absolute():
        brief = PROJECT / brief
    prompt = CONTRACT.read_text(encoding="utf-8") + "\n\n" + brief.read_text(encoding="utf-8-sig")
    prompt_path = run / "prompt.md"
    prompt_path.write_text(prompt, encoding="utf-8")
    before = source_hashes()
    (run / "source-before.json").write_text(json.dumps(before, indent=2), encoding="utf-8")
    command = [str(node), str(cli), "--provider", "strata", "--model", "strata",
               "--thinking", "high", "--print", "--mode", "json", "--approve",
               "--offline", "--no-context-files", "--no-skills", "--no-mcp",
               "--no-extensions", "--tools", "read,powershell,write,grep,find,ls",
               "--session-dir", str(ROOT / "sessions"), "--name", "Unity local QA " + job,
               "@" + str(prompt_path)]
    texts, errors, calls = [], [], 0
    with (run / "stderr.log").open("w", encoding="utf-8") as stderr, \
         (run / "events.jsonl").open("w", encoding="utf-8") as events:
        process = subprocess.Popen(command, cwd=PROJECT, stdin=subprocess.DEVNULL,
                                   stdout=subprocess.PIPE, stderr=stderr,
                                   text=True, encoding="utf-8", errors="replace",
                                   creationflags=subprocess.CREATE_NO_WINDOW)
        save(state="running", job=job, agent_pid=process.pid, run_dir=str(run), tool_calls=0)
        for raw in process.stdout:
            events.write(raw)
            events.flush()
            try:
                event = json.loads(raw)
            except json.JSONDecodeError:
                continue
            kind = event.get("type", "")
            if kind == "tool_execution_start":
                calls += 1
                save(state="running", job=job, agent_pid=process.pid, run_dir=str(run),
                     tool_calls=calls, last_tool=event.get("toolName"))
            elif kind == "message_end":
                message = event.get("message", {})
                if message.get("role") == "assistant":
                    blocks = message.get("content", [])
                    text = "\n".join(b.get("text", "") for b in blocks if b.get("type") == "text")
                    if text.strip():
                        texts.append(text)
                    if message.get("stopReason") in ("error", "aborted", "length"):
                        errors.append(message.get("errorMessage", message["stopReason"]))
        code = process.wait()  # User policy: no wall-clock timeout or healthy-worker restart.
    after = source_hashes()
    drift = sorted(k for k in set(before) | set(after) if before.get(k) != after.get(k))
    (run / "final.md").write_text("\n\n".join(texts), encoding="utf-8")
    result = dict(job=job, exit_code=code, run_dir=str(run), tool_calls=calls,
                  errors=errors, source_drift=drift,
                  state="review_pending" if code == 0 and not errors and not drift else "needs_attention")
    (RESULTS / (job + ".json")).write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    save(**result)
    print(json.dumps(result, ensure_ascii=False), flush=True)
    # Pause dispatch after any operational failure or forbidden source mutation.
    if result["state"] == "needs_attention":
        break