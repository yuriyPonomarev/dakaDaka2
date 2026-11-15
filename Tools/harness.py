"""Content-based change planning. Standard library only; plan never launches Unity."""
import argparse
import datetime as dt
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
DROP = "Emberdelve.EditorTools.EmberdelveNetInventoryGate.RunInventoryDropProofOnly"


def git(root, *args):
    return subprocess.run(["git", "-C", str(root), *args], check=True, capture_output=True).stdout


def paths_from_git(data):
    return [p.decode("utf-8") for p in data.split(b"\0") if p]


def snapshot(root):
    paths = paths_from_git(git(root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"))
    files = {}
    for name in sorted(set(paths)):
        p = root / name
        if p.is_symlink():
            raise ValueError("Symlink cannot be checkpointed safely: " + name)
        if p.is_file():
            with p.open("rb") as stream:
                digest = hashlib.sha256()
                for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(chunk)
                files[name] = digest.hexdigest()
        elif p.is_dir():
            raise ValueError("Nested repository/submodule needs explicit handling: " + name)
    return {"schema": 1, "root": str(root.resolve()), "files": files,
            "note": "Comparison checkpoint only; not test acceptance."}


def changed(root, since):
    candidate = Path(since)
    if candidate.is_file():
        old = json.loads(candidate.read_text(encoding="utf-8-sig"))
        if old.get("schema") != 1 or Path(old.get("root", "")).resolve() != root.resolve() or not isinstance(old.get("files"), dict):
            raise ValueError("Invalid checkpoint or checkpoint belongs to another project")
        current = snapshot(root)["files"]
        return sorted(p for p in old["files"].keys() | current.keys() if old["files"].get(p) != current.get(p))
    # Include staged, unstaged, committed changes since ref, deleted, both sides of renames, and untracked.
    git(root, "rev-parse", "--verify", since + "^{commit}")
    result = set()
    for args in [("diff", "--no-renames", "--name-only", "-z", "--cached", since, "--"),
                 ("diff", "--no-renames", "--name-only", "-z", "--"),
                 ("ls-files", "-z", "--others", "--exclude-standard")]:
        result.update(paths_from_git(git(root, *args)))
    return sorted(result)


def select(paths, sets):
    selected, reasons = set(), []
    edit, harness_tests, broad = False, False, False
    def add(names):
        for name in names:
            selected.update(sets[name] if name in sets else [name])
    for raw in sorted(set(paths)):
        p = raw.replace("\\", "/")
        if p.startswith("/") or ".." in p.split("/") or re.match(r"^[A-Za-z]:", p):
            raise ValueError("Expected project-relative path: " + raw)
        p = p.removesuffix(".meta")
        gates, why = [], ""
        if p.startswith("Docs/") or p in ("AGENTS.md", "README.md", ".gitignore", ".gitattributes"):
            why = "documentation only"
        elif p.startswith("Tools/tests/") or p in ("Tools/harness.py", "Tools/Get-WorkStatus.ps1", "Tools/Configure-TestFirewall.ps1"):
            harness_tests, why = True, "harness tooling tests"
        elif p.startswith("Assets/Emberdelve/Tests/") and p.endswith(".cs"):
            edit, why = True, "EditMode tests changed; no automatic gameplay replay"
        elif p in ("Tools/Run-All.ps1", "Tools/Run-EmberdelveEditModeTests.ps1", "Tools/gate-sets.json") or p.startswith("Assets/Emberdelve/Editor/EmberdelvePlayerBuildCache"):
            gates, why, edit, harness_tests = ["NetProof", "NetAdmission"], "runner/cache: focused real-process smoke", True, True
        elif p in ("Assets/Emberdelve/Runtime/Interaction/WorldLightInteractable.cs", "Assets/Emberdelve/Net/NetWorldLights.cs"):
            gates, why, edit = ["NetDoor", "NetFinal", "Visual"], "world-light interaction, authority and late join; focused light tests also required", True
        elif p in ("Assets/Emberdelve/Runtime/Feedback/WorldLightAppearance.cs", "Assets/Emberdelve/Resources/Emberdelve/LocalLightHaze.shader", "Assets/Emberdelve/Runtime/Level/MoonlitRuinsEnvironment.cs"):
            gates, why, edit = ["Visual", "NetFinal"], "lighting presentation and player/late-join camera setup", True
        elif p in ("Assets/Emberdelve/Runtime/Feedback/WorldAudio.cs", "Assets/Emberdelve/Runtime/Feedback/WorldFootsteps.cs", "Assets/Emberdelve/Runtime/Feedback/FlameAudio.cs", "Assets/Emberdelve/Runtime/Feedback/DungeonAmbience.cs", "Assets/Emberdelve/Runtime/Feedback/ProceduralAudio.cs", "Assets/Emberdelve/Runtime/Feedback/FeedbackDirector.cs"):
            gates, why, edit = ["NetCombat", "NetDoor", "NetFinal", "Visual"], "spatial audio, combat presentation and lit/covered room state; audio fixtures also required", True
        elif p in ("Assets/Emberdelve/Resources/Emberdelve/ImpactFlash.shader", "Assets/Emberdelve/Runtime/Feedback/ImpactFlash.cs", "Assets/Emberdelve/Runtime/Feedback/WallStopFeedback.cs"):
            gates, why, edit = ["Visual", "NetCombat", "CombatFlow"], "retained impact shader and contact presentation", True
        elif p == "Assets/Emberdelve/Runtime/Level/DungeonWayfinding.cs":
            gates, why, edit = ["CombatHud", "NetFinal"], "read-only occupied-room navigation; no geometry or placement mutation", True
        elif p.startswith("Assets/Emberdelve/Editor/"):
            name = Path(p).stem
            match = re.fullmatch(r"Emberdelve(.+)Gate", name)
            if match and match[1] in sets["all"]:
                gates, why = [match[1]], "only this scenario implementation changed"
            elif name == "EmberdelveRuntimeSmokeTest":
                gates, why = ["RuntimeSmokeTest.RunCombatFlow"], "only combat smoke fixture changed"
            elif name == "EmberdelveWindowsBuild":
                gates, why, edit = ["NetProof", "NetAdmission", "NetFinal"], "both player build profiles changed", True
            else:
                gates, why, edit, broad = ["all"], "shared editor/build infrastructure: conservative full coverage", True, True
        elif p.startswith("Assets/Emberdelve/Runtime/Player/") and Path(p).stem in ("FirstPersonArmsDriver", "WeaponVisualRig"):
            gates, why, edit = ["Hands", "Visual", "NetCombat", "ProjectileBlock", "SweptMelee"], "hands and gameplay guard geometry", True
        elif p.startswith("Assets/Emberdelve/Runtime/Level/"):
            gates, why, edit = ["NetMovement", "NetFinal", "NetDoor", "NetInventory", "NetRoyale", "RuntimeSmokeTest.RunCombatFlow"], "level geometry, authoritative placement and three-floor handoffs", True
        elif p.startswith("Assets/Emberdelve/Runtime/Items/"):
            gates, why, edit = ["combat", "NetInventory", DROP, "NetExtraction", "NetCombat", "NetMovement", "NetSkill"], "inventory/equipment affects stats, ownership and outcomes", True
        elif p.startswith("Assets/Emberdelve/Net/") or p.startswith("Assets/Emberdelve/NetRules/"):
            name = Path(p).stem
            if name in ("NetEnemyViews", "EnemyHitPresentationQueue", "BloodHitWindow", "NetCombatFx"):
                gates, why = ["NetCombat", "Visual"], "replicated enemy presentation"
            elif name.startswith(("NetCombat", "CombatIntent", "CombatVitals", "ServerDraw")):
                gates, why = ["NetCombat", "CombatAuthority", "SweptMelee", "ProjectileBlock", "Riposte"], "authoritative combat"
            elif name.startswith(("NetSkill", "SkillIntent", "SkillState", "SkillAbuse", "SkillNet")):
                gates, why = ["NetSkill", "SkillAuthority", "SkillCooldown", "SkillPower"], "authoritative skills"
            elif name.startswith(("NetInventory", "InventoryIntent", "FloorLoot", "Corpse", "NetworkLoadout")):
                gates, why = ["NetInventory", DROP, "NetExtraction", "Hands", "NetCombat"], "inventory ownership and equipment effects"
            elif name.startswith(("NetExtraction", "RunResult", "RaidClock", "ExtractionHold", "NetOwnerRunResult")):
                gates, why = ["NetExtraction", "NetInventory"], "raid outcomes and corpse ownership"
            elif name.startswith(("NetMovement", "NetInput", "NetOwnerPrediction", "MoveInput", "InputSanitizer", "Reconcile", "Prediction", "ServerMove")):
                gates, why = ["NetMovement", "NetFinal", "NetCombat"], "movement prediction, joins and combat positioning"
            else:
                gates, why = ["net"], "shared network code/protocol: all network scenarios"
            edit = True
        elif p.startswith("Assets/Emberdelve/Runtime/AI/"):
            gates, why, edit = ["EnemySweep", "NetCombat", "NetDoor", "Visual", "RuntimeSmokeTest.RunCombatFlow"], "AI, telegraphs and doors", True
        else:
            gates, why, edit, broad = ["all"], "unmapped/shared input: do not silently omit coverage", True, True
        add(gates)
        reasons.append({"path": raw, "reason": why, "gates": gates})
    # Stable suite order and no duplicate subsets.
    ordered = [x for x in sets["all"] if x in selected]
    ordered += sorted(selected - set(ordered))
    return {"schema": 1, "files": sorted(set(paths)), "gates": ordered, "editMode": edit,
            "harnessTests": harness_tests, "requiresFullApproval": broad or len(ordered) == len(sets["all"]),
            "reasons": reasons, "note": "Heuristic routing, not a coverage proof. No passing test result is reused."}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    check = sub.add_parser("checkpoint")
    check.add_argument("output", type=Path)
    for command in ("plan", "run"):
        p = sub.add_parser(command)
        p.add_argument("--since", default="HEAD", help="Git commit or content checkpoint JSON")
        p.add_argument("--files", nargs="+", help="Explicit slice (does NOT claim all worktree changes are covered)")
        p.add_argument("--json", action="store_true")
        p.add_argument("--allow-full", action="store_true", help="Explicitly permit conservative all-suite selection")
        p.add_argument("--no-build-cache", action="store_true")
    args = parser.parse_args(argv)
    if args.command == "checkpoint":
        if args.output.exists():
            raise ValueError("Checkpoint exists; choose a new path")
        data = snapshot(ROOT)  # capture before creating output
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(data, indent=2), encoding="utf-8")
        print("Comparison checkpoint written (not acceptance):", args.output)
        return 0
    sets = json.loads((ROOT / "Tools/gate-sets.json").read_text(encoding="utf-8-sig"))["sets"]
    plan = select(args.files if args.files is not None else changed(ROOT, args.since), sets)
    plan["scope"] = "explicit-files" if args.files is not None else args.since
    print(json.dumps(plan, indent=2) if args.json else
          f"Files={len(plan['files'])}; EditMode={plan['editMode']}; gates={len(plan['gates'])}; harnessTests={plan['harnessTests']}\n" +
          ", ".join(plan["gates"][:12]) + (" ... (use --json for all)" if len(plan["gates"]) > 12 else "") + ("\nFull selection: run requires --allow-full." if plan["requiresFullApproval"] else ""), flush=True)
    if args.command == "plan":
        return 0
    if plan["requiresFullApproval"] and not args.allow_full:
        print("No tests launched. Inspect plan --json; use --allow-full only when intended.", file=sys.stderr)
        return 2
    evidence = ROOT / "Logs/harness" / (dt.datetime.now().strftime("%Y%m%d-%H%M%S-%f") + "-plan.json")
    evidence.parent.mkdir(parents=True, exist_ok=True)
    evidence.write_text(json.dumps(plan, indent=2), encoding="utf-8")
    if plan["harnessTests"]:
        subprocess.run([sys.executable, "-m", "unittest", "discover", "-s", "Tools/tests", "-v"], cwd=ROOT, check=True)
    if not plan["editMode"] and not plan["gates"]:
        print("No Unity run required. Plan:", evidence)
        return 0
    command = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "Tools/Run-All.ps1"]
    command += ["-Set", ",".join(plan["gates"])] if plan["gates"] else ["-SkipGates"]
    if plan["editMode"]:
        command.append("-EditMode")
    if args.no_build_cache:
        command.append("-NoBuildCache")
    command += ["-SelectionPlan", str(evidence)]
    return subprocess.run(command, cwd=ROOT).returncode


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (ValueError, OSError, subprocess.CalledProcessError) as exc:
        print(str(exc), file=sys.stderr)
        sys.exit(2)
