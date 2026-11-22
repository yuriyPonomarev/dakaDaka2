import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("harness", Path(__file__).resolve().parents[1] / "harness.py")
h = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(h)
SETS = json.loads((h.ROOT / "Tools/gate-sets.json").read_text())["sets"]


class SelectionTests(unittest.TestCase):
    def plan(self, *paths):
        return h.select(paths, SETS)

    def test_docs_and_empty_do_not_launch_unity(self):
        for paths in [[], ["Docs/CURRENT_WORK.md", "AGENTS.md"]]:
            p = self.plan(*paths)
            self.assertEqual(p["gates"], [])
            self.assertFalse(p["editMode"])

    def test_written_instructions_and_task_reports_do_not_schedule_unity(self):
        docs = ["Tasks/GAME_AUDIO_20261122.md", ".agents/skills/unity-3d-prompts/SKILL.md",
                ".cursor/rules/unity-visual-verification.mdc", "Tools/LocalTesting/unity-tester/references/graphical-review.md"]
        self.assertFalse(self.plan(*docs)["editMode"])
        self.assertEqual(self.plan(*docs)["gates"], [])
        for path in ["Tasks/example.cs", ".agents/skills/tool.py"]:
            self.assertTrue(self.plan(*docs, path)["requiresFullApproval"])

    def test_audio_routes_combine_with_other_changes(self):
        base = "Assets/Emberdelve/"
        player = base + "Runtime/Feedback/PlayerActionAudio.cs"
        expected = {"NetCombat", "NetInventory", "NetRoyale"}
        self.assertEqual(set(self.plan(player)["gates"]), expected)
        self.assertEqual(self.plan(player)["editModeFilter"], "AudioTests")
        self.assertEqual(self.plan(player, "Assets/Other/new.cs")["editModeFilter"], "Emberdelve")
        for asset in ["ArrowImpact_01.wav.meta", "manifest.json"]:
            bank = base + "Resources/Emberdelve/Audio/Sfx/" + asset
            plan = self.plan(player, bank, "Tasks/GAME_AUDIO_20261122.md")
            self.assertEqual(set(plan["gates"]), expected | {"NetDoor", "NetSkill"})
            self.assertTrue(plan["editMode"])
            self.assertFalse(plan["requiresFullApproval"])
        self.assertTrue(self.plan(player, "Packages/manifest.json")["requiresFullApproval"])

    def test_unknown_or_shared_change_fails_broad(self):
        for path in ["Packages/manifest.json", "Assets/Other/new.cs", "Assets/Emberdelve/Runtime/Player/PlayerRig.cs"]:
            p = self.plan(path)
            self.assertTrue(p["requiresFullApproval"])
            self.assertEqual(p["gates"], SETS["all"])

    def test_gate_fix_only_selects_its_gate(self):
        self.assertEqual(self.plan("Assets/Emberdelve/Editor/EmberdelveNetMovementGate.cs")["gates"], ["NetMovement"])

    def test_royale_is_in_network_and_full_sets_and_owns_its_gate(self):
        self.assertIn("NetRoyale", SETS["net"])
        self.assertIn("NetRoyale", SETS["all"])
        p = self.plan("Assets/Emberdelve/Editor/EmberdelveNetRoyaleGate.cs")
        self.assertEqual(p["gates"], ["NetRoyale"])
        self.assertFalse(p["requiresFullApproval"])

    def test_map_change_includes_three_floor_handoff(self):
        p = self.plan("Assets/Emberdelve/Runtime/Level/RoyaleFloorCatalogs.cs")
        self.assertIn("NetRoyale", p["gates"])
        self.assertTrue(p["editMode"])

    def test_cache_selection_uses_existing_short_process_gates(self):
        p = self.plan("Assets/Emberdelve/Editor/EmberdelvePlayerBuildCache.cs")
        self.assertEqual(set(p["gates"]), {"NetProof", "NetAdmission"})
        self.assertTrue(p["editMode"] and p["harnessTests"])

    def test_metadata_routes_like_asset_and_duplicates_removed(self):
        p = self.plan("Assets/Emberdelve/Net/NetServer.cs", "Assets/Emberdelve/Net/NetServer.cs.meta")
        self.assertEqual(set(p["gates"]), set(SETS["net"]))
        self.assertEqual(len(p["gates"]), len(set(p["gates"])))

    def test_test_only_uses_editmode_without_gates(self):
        p = self.plan("Assets/Emberdelve/Tests/EditMode/NetRules/NetApprovalTests.cs")
        self.assertTrue(p["editMode"])
        self.assertEqual(p["gates"], [])

    def test_weapon_visual_change_keeps_gameplay_guard_checks(self):
        p = self.plan("Assets/Emberdelve/Runtime/Player/WeaponVisualRig.cs")
        self.assertTrue({"NetCombat", "ProjectileBlock", "SweptMelee", "Visual"}.issubset(p["gates"]))

    def test_enemy_presentation_does_not_replay_inventory_and_skills(self):
        p = self.plan("Assets/Emberdelve/Net/NetEnemyViews.cs")
        self.assertEqual(set(p["gates"]), {"NetCombat", "Visual"})

    def test_inventory_selection_keeps_drop_and_outcome_coverage(self):
        p = self.plan("Assets/Emberdelve/Net/NetInventoryServer.cs")
        self.assertTrue({"NetInventory", h.DROP, "NetExtraction", "Hands", "NetCombat"}.issubset(p["gates"]))

    def test_world_lights_select_interaction_join_and_visual_checks(self):
        for path in ["Assets/Emberdelve/Runtime/Interaction/WorldLightInteractable.cs", "Assets/Emberdelve/Net/NetWorldLights.cs.meta"]:
            p = self.plan(path)
            self.assertEqual(set(p["gates"]), {"NetDoor", "NetFinal", "Visual"})
            self.assertTrue(p["editMode"])
            self.assertFalse(p["requiresFullApproval"])

    def test_lighting_routes_rendering_and_player_setup(self):
        for path in ["Assets/Emberdelve/Runtime/Feedback/WorldLightAppearance.cs", "Assets/Emberdelve/Resources/Emberdelve/LocalLightHaze.shader.meta", "Assets/Emberdelve/Runtime/Level/MoonlitRuinsEnvironment.cs"]:
            self.assertEqual(set(self.plan(path)["gates"]), {"Visual", "NetFinal"})

    def test_spatial_audio_keeps_combat_and_lighting_checks(self):
        for name in ["WorldAudio", "WorldFootsteps", "FlameAudio", "DungeonAmbience", "ProceduralAudio", "FeedbackDirector"]:
            p = self.plan("Assets/Emberdelve/Runtime/Feedback/" + name + ".cs")
            self.assertEqual(set(p["gates"]), {"NetCombat", "NetDoor", "NetFinal", "Visual"})
            self.assertTrue(p["editMode"])

    def test_impact_shader_keeps_contact_and_player_render_checks(self):
        for path in ["Resources/Emberdelve/ImpactFlash.shader", "Runtime/Feedback/ImpactFlash.cs", "Runtime/Feedback/WallStopFeedback.cs"]:
            self.assertEqual(set(self.plan("Assets/Emberdelve/" + path)["gates"]), {"Visual", "NetCombat", "CombatFlow"})

    def test_wayfinding_has_no_map_mutations_but_checks_hud_and_join(self):
        p = self.plan("Assets/Emberdelve/Runtime/Level/DungeonWayfinding.cs")
        self.assertEqual(set(p["gates"]), {"CombatHud", "NetFinal"})
        self.assertTrue(p["editMode"])

    def test_traversal_rejected(self):
        for path in ["../secret.cs", "/tmp/x", "C:/outside.cs"]:
            with self.assertRaises(ValueError):
                self.plan(path)


class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.email", "harness@example.invalid")
        self.git("config", "user.name", "Harness Test")
        (self.root / "a.cs").write_text("initial")
        (self.root / "gone.cs").write_text("initial")
        self.git("add", ".")
        self.git("commit", "-qm", "fixture")

    def tearDown(self):
        self.temp.cleanup()

    def git(self, *args):
        subprocess.run(["git", "-C", str(self.root), *args], check=True, capture_output=True)

    def test_git_includes_index_worktree_untracked_delete_rename(self):
        (self.root / "a.cs").write_text("staged")
        self.git("add", "a.cs")
        (self.root / "a.cs").write_text("initial")  # staged change must still be included
        self.git("mv", "gone.cs", "renamed.cs")
        (self.root / "new space.cs").write_text("new")
        self.assertEqual(set(h.changed(self.root, "HEAD")), {"a.cs", "gone.cs", "renamed.cs", "new space.cs"})

    def test_checkpoint_detects_bytes_not_mtime_and_deletions(self):
        import os
        before = h.snapshot(self.root)
        checkpoint = Path(self.temp.name).parent / (self.root.name + "-checkpoint.json")
        try:
            checkpoint.write_text(json.dumps(before))
            stat = (self.root / "a.cs").stat()
            (self.root / "a.cs").write_text("changed")
            os.utime(self.root / "a.cs", ns=(stat.st_atime_ns, stat.st_mtime_ns))
            (self.root / "gone.cs").unlink()
            self.assertEqual(set(h.changed(self.root, str(checkpoint))), {"a.cs", "gone.cs"})
        finally:
            checkpoint.unlink(missing_ok=True)

    def test_invalid_ref_fails_instead_of_empty_plan(self):
        with self.assertRaises(subprocess.CalledProcessError):
            h.changed(self.root, "missing-reference")


if __name__ == "__main__":
    unittest.main()
