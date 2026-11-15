using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Stats;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// D3c gate: two weapon sets with an authority-owned atomic swap, the utility belt bound to item identity, the per-item
    /// move-speed penalty, timed potions (use channel, heal over time), and the schema 3 to 4 migration that carries the new
    /// loadout fields.
    ///
    /// One batch entry in two phases that share one final line. Phase 1 (edit mode, no scene): every pure rule against literals
    /// written here - the literal version 3 equipment and loadout documents (weapon set 1 holding Weapon and OffHand) migrating to
    /// the literal version 6 bytes, Alt slots refused in a version 3 document, the held-factor and consumable arithmetic, belt
    /// text, item data defaults (all existing content unchanged). Phase 2 (play mode on the real level scene, because the command
    /// authority is bound to a live rig): speed with gear (exact multiplier), the swap through the authority (atomic; refused while
    /// swinging with nothing changed), the active set driving the combat weapon, the belt surviving a restore by identity, and the
    /// potion channel (cancel leaves no heal and keeps the potion; completion heals the exact total once).
    ///
    /// Batch entry: Emberdelve.EditorTools.EmberdelveLoadoutD3cGate.Run
    /// Final line: [Emberdelve][LoadoutD3c] PASS|FAIL checks=N failures=M, then EmberdelveGateExit.Exit (the only exit).
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveLoadoutD3cGate
    {
        private const string Pending = "Emberdelve.LoadoutD3c.Pending";
        internal const string ChecksKey = "Emberdelve.LoadoutD3c.Checks";
        internal const string FailuresKey = "Emberdelve.LoadoutD3c.Failures";

        // Literal version 3 documents (D3b): the weapon, off hand and cloak are worn. Checksums computed outside Unity.
        internal const string EquipmentV3 = @"{""schemaVersion"":3,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Weapon"",""x"":0,""y"":0,""item"":{""uid"":""7a1b2c3d4e5f60718293a4b5c6d7e8f9"",""itemId"":""ember_fang_blade"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""OffHand"",""x"":0,""y"":0,""item"":{""uid"":""8b2c3d4e5f60718293a4b5c6d7e8f90a"",""itemId"":""emberbulwark_shield"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:6cb45a72b4e05ded4f821795bf87350ea712b96b833e1325e6b9ee147d5de450""}";
        // The same equipment at version 6: only the version and the checksum change (the saved Weapon and OffHand ARE weapon set 1).
        internal const string EquipmentV6 = @"{""schemaVersion"":6,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Weapon"",""x"":0,""y"":0,""item"":{""uid"":""7a1b2c3d4e5f60718293a4b5c6d7e8f9"",""itemId"":""ember_fang_blade"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""OffHand"",""x"":0,""y"":0,""item"":{""uid"":""8b2c3d4e5f60718293a4b5c6d7e8f90a"",""itemId"":""emberbulwark_shield"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:970e0f20011910745025da11c63ac27fce2e05fbb33a587be59ba15ef4aa85f9""}";
        internal const string EquipmentV6Checksum = "sha256:970e0f20011910745025da11c63ac27fce2e05fbb33a587be59ba15ef4aa85f9";
        // A version 3 document that names AltWeapon, which version 3 never wrote (valid checksum, so the migration step itself must refuse it).
        internal const string EquipmentV3WithAlt = @"{""schemaVersion"":3,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""AltWeapon"",""x"":0,""y"":0,""item"":{""uid"":""7a1b2c3d4e5f60718293a4b5c6d7e8f9"",""itemId"":""ember_fang_blade"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:079a82469025202a8bd217f20c642327fff1cc8950b0cafb1f15e5aaf0b20ec3""}";
        internal const string LoadoutV3 = @"{""schemaVersion"":3,""bag"":{""schemaVersion"":3,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:6e24f89b713fca9d5bfea10bc57e42caf4ebe9c25346db874e14cf6acb257cc7""},""equipment"":{""schemaVersion"":3,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Weapon"",""x"":0,""y"":0,""item"":{""uid"":""7a1b2c3d4e5f60718293a4b5c6d7e8f9"",""itemId"":""ember_fang_blade"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""OffHand"",""x"":0,""y"":0,""item"":{""uid"":""8b2c3d4e5f60718293a4b5c6d7e8f90a"",""itemId"":""emberbulwark_shield"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:6cb45a72b4e05ded4f821795bf87350ea712b96b833e1325e6b9ee147d5de450""},""checksum"":""sha256:4423867e7eac79d43b747397681bd30900665e13959753026a36a9e3932ecfca""}";
        // The loadout at version 6: weaponSet 0 and an empty belt are added by the migration.
        internal const string LoadoutV6 = @"{""schemaVersion"":6,""bag"":{""schemaVersion"":6,""kind"":""bag"",""containerId"":""bag"",""width"":10,""height"":6,""entries"":[{""slot"":"""",""x"":0,""y"":0,""item"":{""uid"":"""",""itemId"":""ember_coin"",""quantity"":8,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:996fcaefb37fe2f6c157d33f6b7c03d299ffc907fb5259671f502cbd3faec96f""},""equipment"":{""schemaVersion"":6,""kind"":""equipment"",""containerId"":""equipment"",""width"":0,""height"":0,""entries"":[{""slot"":""Weapon"",""x"":0,""y"":0,""item"":{""uid"":""7a1b2c3d4e5f60718293a4b5c6d7e8f9"",""itemId"":""ember_fang_blade"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""OffHand"",""x"":0,""y"":0,""item"":{""uid"":""8b2c3d4e5f60718293a4b5c6d7e8f90a"",""itemId"":""emberbulwark_shield"",""quantity"":1,""rarity"":0,""affixes"":[]}},{""slot"":""Back"",""x"":0,""y"":0,""item"":{""uid"":""5d6e7f8091a2b3c4d5e6f708192a3b4c"",""itemId"":""ashen_cloak"",""quantity"":1,""rarity"":0,""affixes"":[]}}],""checksum"":""sha256:970e0f20011910745025da11c63ac27fce2e05fbb33a587be59ba15ef4aa85f9""},""weaponSet"":0,""belt"":["""","""","""","""","""",""""],""checksum"":""sha256:3dd5787583bfa4089aa4e56d1276ea05ce17b5d9390afce8d83407877e05aa82""}";
        internal const string LoadoutV6Checksum = "sha256:3dd5787583bfa4089aa4e56d1276ea05ce17b5d9390afce8d83407877e05aa82";

        private static int _n, _fail;
        private static readonly List<UnityEngine.Object> Cleanup = new List<UnityEngine.Object>();

        static EmberdelveLoadoutD3cGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("LoadoutD3c_Gate").AddComponent<LoadoutD3cHarness>();
                }
            };
        }

        private static void Check(bool ok, string text)
        {
            _n++;
            if (ok) return;
            _fail++;
            Debug.LogError("[LoadoutD3c] mismatch " + text);
        }

        private static bool Near(float a, float b, float tolerance)
        {
            return Mathf.Abs(a - b) <= tolerance;
        }

        public static void Run()
        {
            _n = 0;
            _fail = 0;
            try { PurePhase(); }
            catch (Exception e) { _fail++; Debug.LogException(e); }
            finally
            {
                foreach (UnityEngine.Object made in Cleanup) if (made != null) UnityEngine.Object.DestroyImmediate(made);
                Cleanup.Clear();
            }

            SessionState.SetInt(ChecksKey, _n);
            SessionState.SetInt(FailuresKey, _fail);
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        // ------------------------------------------------------------------ phase 1

        private static void PurePhase()
        {
            // ---- slot integers and the hand mapping are append-only
            Check((int)EquipmentSlot.Weapon == 1 && (int)EquipmentSlot.OffHand == 2 && (int)EquipmentSlot.AltWeapon == 12 && (int)EquipmentSlot.AltOffHand == 13,
                "Weapon 1, OffHand 2 unchanged; AltWeapon 12, AltOffHand 13 appended");
            Check(EquipmentSlots.WeaponSetCount == 2 && EquipmentSlots.MainHandOf(0) == EquipmentSlot.Weapon && EquipmentSlots.MainHandOf(1) == EquipmentSlot.AltWeapon &&
                  EquipmentSlots.OffHandOf(0) == EquipmentSlot.OffHand && EquipmentSlots.OffHandOf(1) == EquipmentSlot.AltOffHand &&
                  EquipmentSlots.MainHandOf(2) == EquipmentSlot.None && EquipmentSlots.OffHandOf(-1) == EquipmentSlot.None,
                "two weapon sets: set 0 is Weapon + OffHand, set 1 is AltWeapon + AltOffHand, anything else is None");
            Check(EquipmentSlots.SetOf(EquipmentSlot.Weapon) == 0 && EquipmentSlots.SetOf(EquipmentSlot.OffHand) == 0 && EquipmentSlots.SetOf(EquipmentSlot.AltWeapon) == 1 &&
                  EquipmentSlots.SetOf(EquipmentSlot.AltOffHand) == 1 && EquipmentSlots.SetOf(EquipmentSlot.Head) == -1 && EquipmentSlots.SetOf(EquipmentSlot.None) == -1,
                "SetOf names the set of a hand slot and -1 for every other slot");
            Check(EquipmentSlots.Accepts(EquipmentSlot.Weapon, EquipmentSlot.Weapon) && EquipmentSlots.Accepts(EquipmentSlot.Weapon, EquipmentSlot.AltWeapon) &&
                  EquipmentSlots.Accepts(EquipmentSlot.OffHand, EquipmentSlot.OffHand) && EquipmentSlots.Accepts(EquipmentSlot.OffHand, EquipmentSlot.AltOffHand),
                "a weapon item fits either main hand and an off-hand item either off hand");
            Check(!EquipmentSlots.Accepts(EquipmentSlot.Weapon, EquipmentSlot.AltOffHand) && !EquipmentSlots.Accepts(EquipmentSlot.OffHand, EquipmentSlot.AltWeapon) &&
                  !EquipmentSlots.Accepts(EquipmentSlot.Head, EquipmentSlot.AltWeapon) && !EquipmentSlots.Accepts(EquipmentSlot.AltWeapon, EquipmentSlot.Weapon) &&
                  !EquipmentSlots.Accepts(EquipmentSlot.AltOffHand, EquipmentSlot.AltOffHand),
                "a weapon is not an off-hand item, armor is not a weapon, and an item can never AUTHOR an Alt slot");
            Check(Enum.GetNames(typeof(InventoryCommandKind)).Length == 14
                && (int)InventoryCommandKind.SwapWeaponSet == 7 && (int)InventoryCommandKind.Drop == 8
                && (int)InventoryCommandKind.HoldQuickItem == 9 && (int)InventoryCommandKind.SelectWeaponSet == 10
                && (int)InventoryCommandKind.DropStack == 11 && (int)InventoryCommandKind.DropOne == 12
                && (int)InventoryCommandKind.Split == 13,
                "weapon-set command IDs remain stable when stack drop and split intents are appended");

            // ---- the schema: version 6, version 1 to 3 still readable
            Check(ContainerSnapshot.CurrentSchemaVersion == 6 && ContainerSnapshot.MinSupportedSchemaVersion == 1, "schema version 6, version 1 still readable");

            // ---- literal v3 -> v6
            Check(ContainerSnapshot.TryFromJson(EquipmentV3, out ContainerSnapshot gear3, out string reason), "the literal version 3 equipment parses: " + reason);
            Check(gear3 != null && gear3.schemaVersion == 6 && gear3.ToJson() == EquipmentV6 && gear3.checksum == EquipmentV6Checksum,
                "the literal version 3 equipment migrates to the literal version 6 bytes and its independently computed checksum");
            Check(gear3 != null && gear3.entries.Length == 3 && gear3.entries[0].slot == "Weapon" && gear3.entries[0].item.itemId == "ember_fang_blade" &&
                  gear3.entries[1].slot == "OffHand" && gear3.entries[1].item.itemId == "emberbulwark_shield" && gear3.entries[2].slot == "Back" &&
                  !gear3.ToJson().Contains("AltWeapon") && !gear3.ToJson().Contains("AltOffHand"),
                "the old Weapon and OffHand stay where they were: they ARE weapon set 1, set 2 is empty");
            Check(ContainerSnapshot.TryFromJson(EquipmentV6, out ContainerSnapshot gear4, out reason) && gear4.ToJson() == EquipmentV6, "the version 6 equipment parses as current and round-trips: " + reason);
            Check(!ContainerSnapshot.TryFromJson(EquipmentV3WithAlt, out _, out reason) && reason != null && reason.Contains("AltWeapon"),
                "a version 3 document that names AltWeapon is refused, not reinterpreted: " + reason);
            Check(!ContainerSnapshot.TryFromJson(EquipmentV3.Replace("ember_fang_blade", "ember_fang_blade2"), out _, out reason) && reason != null,
                "an edited version 3 equipment fails its own checksum: " + reason);

            Check(LoadoutSnapshot.TryFromJson(LoadoutV3, out LoadoutSnapshot loadout3, out reason), "the literal version 3 loadout parses: " + reason);
            Check(loadout3 != null && loadout3.schemaVersion == 6 && loadout3.ToJson() == LoadoutV6 && loadout3.checksum == LoadoutV6Checksum && loadout3.ChecksumMatches(),
                "the literal version 3 loadout migrates to the literal version 6 bytes and its independently computed checksum");
            Check(loadout3 != null && loadout3.weaponSet == 0 && loadout3.belt != null && loadout3.belt.Length == LoadoutSnapshot.BeltSlotCount &&
                  loadout3.belt[0] == "" && loadout3.belt[1] == "" && loadout3.belt[2] == "" && loadout3.belt[3] == "" &&
                  loadout3.equipment.schemaVersion == 6 && loadout3.bag.schemaVersion == 6,
                "the migrated loadout holds weapon set 1 live and an empty belt (a restore then binds the default keys, as it always did)");
            Check(LoadoutSnapshot.TryFromJson(LoadoutV6, out LoadoutSnapshot loadout4, out reason) && loadout4.ToJson() == LoadoutV6 && loadout4.ChecksumMatches(), "the version 6 loadout parses as current and round-trips: " + reason);
            // The structural read accepts a well-formed document; the wrapper checksum (which covers weaponSet and belt) is what catches the edit, and TryRestore checks it first.
            Check(LoadoutSnapshot.TryFromJson(LoadoutV6.Replace("\"weaponSet\":0", "\"weaponSet\":1"), out LoadoutSnapshot editedSet, out reason) && editedSet != null && !editedSet.ChecksumMatches() && editedSet.weaponSet == 1,
                "editing the weapon set leaves the stored checksum wrong: the checksum covers weaponSet (" + reason + ")");
            Check(LoadoutSnapshot.TryFromJson(LoadoutV6.Replace("\"belt\":[\"\",\"\",\"\",\"\",\"\",\"\"]", "\"belt\":[\"i:ember_coin\",\"\",\"\",\"\",\"\",\"\"]"), out LoadoutSnapshot editedBelt, out reason) && editedBelt != null && !editedBelt.ChecksumMatches(),
                "editing the belt leaves the stored checksum wrong: the checksum covers the belt (" + reason + ")");
            Check(!LoadoutSnapshot.TryFromJson(LoadoutV6.Replace("\"belt\":[\"\",\"\",\"\",\"\",\"\",\"\"]", "\"belt\":[\"\",\"\",\"\"]"), out _, out reason) && reason != null, "a belt with three keys is refused: " + reason);
            Check(!LoadoutSnapshot.TryFromJson(LoadoutV3.Replace("\"schemaVersion\":3,\"bag\"", "\"weaponSet\":0,\"schemaVersion\":3,\"bag\""), out _, out reason) && reason != null, "a version 3 loadout that already carries weaponSet is refused: " + reason);

            // ---- weapon set and belt validation
            string[] emptyBelt = { "", "", "", "", "", "" };
            Check(LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, emptyBelt, out reason) && LoadoutSnapshot.TryCheckWeaponSetAndBelt(1, emptyBelt, out reason), "weapon sets 0 and 1 with an empty belt are valid: " + reason);
            Check(!LoadoutSnapshot.TryCheckWeaponSetAndBelt(2, emptyBelt, out reason) && !LoadoutSnapshot.TryCheckWeaponSetAndBelt(-1, emptyBelt, out reason), "weapon set 2 and -1 are refused: " + reason);
            Check(!LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, new[] { "", "", "" }, out reason) && !LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, null, out reason), "a short or missing belt is refused: " + reason);
            Check(!LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, new[] { "x:blood_draught", "", "", "", "", "" }, out reason) && !LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, new[] { "", "u:XYZ", "", "", "", "" }, out reason) &&
                  !LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, new[] { "", "", "u:00000000000000000000000000000000", "", "", "" }, out reason) && !LoadoutSnapshot.TryCheckWeaponSetAndBelt(0, new[] { "", "", "", null, "", "" }, out reason),
                "an unknown prefix, a malformed uid, an all-zero uid and a null key are refused: " + reason);
            Check(LoadoutSnapshot.TryCheckWeaponSetAndBelt(1, new[] { "i:blood_draught", "u:0123456789abcdef0123456789abcdef", "", "i:arrow_bundle", "", "" }, out reason), "item and uid keys are valid belt text: " + reason);

            // ---- belt identity text
            Guid probe = Guid.Parse("0123456789abcdef0123456789abcdef");
            Check(new BeltIdentity(null, probe).ToSnapshotText() == "u:0123456789abcdef0123456789abcdef" && default(BeltIdentity).ToSnapshotText() == "" && default(BeltIdentity).IsEmpty,
                "a uid identity spells u: + 32 hex, an empty one spells nothing");
            ItemData sample = MakeItem("blood_draught_probe");
            Check(new BeltIdentity(sample, Guid.Empty).ToSnapshotText() == "i:blood_draught_probe" && new BeltIdentity(sample, probe).ToSnapshotText() == "u:0123456789abcdef0123456789abcdef",
                "a definition identity spells i: + the item id, and a uid wins over a definition");
            Check(LoadoutSnapshot.TryParseBeltText("u:0123456789abcdef0123456789abcdef", null, out BeltIdentity parsed, out reason) && parsed.Uid == probe &&
                  LoadoutSnapshot.TryParseBeltText("", null, out parsed, out reason) && parsed.IsEmpty, "belt text parses back to the identity it spells: " + reason);

            // ---- move-speed penalty: one factor, one division, one floor
            Check(Near(CombatFlowRules.HeldFactor(null, null, false, 0f), 1f, 1e-6f), "nothing held, nothing worn: factor 1");
            Check(Near(CombatFlowRules.HeldFactor(null, null, false, 30f), 0.9f, 1e-6f), "30 worn points: 1 - 30/300 = 0.9");
            Check(Near(CombatFlowRules.HeldFactor(null, null, false, 150f), 0.5f, 1e-6f) && Near(CombatFlowRules.HeldFactor(null, null, false, 300f), 0.5f, 1e-6f) &&
                  Near(CombatFlowRules.HeldFactor(null, null, false, 1000f), 0.5f, 1e-6f), "the floor is 0.5 however heavy the kit");
            Check(Near(CombatFlowRules.HeldFactor(null, null, false, -30f), 1f, 1e-6f) && Near(CombatFlowRules.HeldFactor(null, null, false, float.NaN), 1f, 1e-6f), "negative and NaN worn points count as zero");
            WeaponData sword = ScriptableObject.CreateInstance<WeaponData>();
            Cleanup.Add(sword);
            sword.heldMoveSpeedPenalty = 10f;
            Check(Near(CombatFlowRules.HeldFactor(sword, null, false), 1f - 10f / 300f, 1e-6f) && Near(CombatFlowRules.HeldFactor(sword, null, false, 0f), CombatFlowRules.HeldFactor(sword, null, false), 0f),
                "the three-argument form is the four-argument form with no worn penalty (existing callers unchanged)");
            Check(Near(CombatFlowRules.HeldFactor(sword, null, false, 30f), 1f - 40f / 300f, 1e-6f), "held and worn points add BEFORE the one division: 1 - (10 + 30)/300");

            // ---- consumable arithmetic
            Check(ConsumableRules.MoveCancelRatio == 0.6f && !ConsumableRules.IsMovingTooFast(0f) && !ConsumableRules.IsMovingTooFast(0.6f) && ConsumableRules.IsMovingTooFast(0.61f) &&
                  ConsumableRules.IsMovingTooFast(1.5f) && !ConsumableRules.IsMovingTooFast(float.NaN), "moving faster than 0.6 of walking speed interrupts a drink; exactly 0.6 and NaN do not");
            Check(ConsumableRules.Progress(0f, 2f) == 0f && ConsumableRules.Progress(1f, 2f) == 0.5f && ConsumableRules.Progress(2f, 2f) == 1f && ConsumableRules.Progress(3f, 2f) == 1f &&
                  ConsumableRules.Progress(5f, 0f) == 1f && ConsumableRules.Progress(-1f, 2f) == 0f, "channel progress is linear, clamped to 0..1, and 1 for a zero-length channel");
            Check(ConsumableRules.HealDelivered(45f, 4f, 0f) == 0f && Near(ConsumableRules.HealDelivered(45f, 4f, 1f), 11.25f, 1e-5f) && Near(ConsumableRules.HealDelivered(45f, 4f, 2f), 22.5f, 1e-5f) &&
                  ConsumableRules.HealDelivered(45f, 4f, 4f) == 45f && ConsumableRules.HealDelivered(45f, 4f, 9f) == 45f && ConsumableRules.HealDelivered(45f, 0f, 0f) == 45f &&
                  ConsumableRules.HealDelivered(0f, 4f, 2f) == 0f && ConsumableRules.HealDelivered(-5f, 4f, 2f) == 0f, "a heal over time is linear and EXACTLY the total once the duration is reached");
            float delivered = 0f, paid = 0f;
            for (int step = 1; step <= 40; step++)
            {
                float target = ConsumableRules.HealDelivered(45f, 4f, step * 0.1f);
                paid += target - delivered;
                delivered = target;
            }
            Check(Near(paid, 45f, 1e-3f) && delivered == 45f, "forty 0.1 s steps pay out 45 in total (" + paid + "), never more");

            // ---- item data defaults and the timed-use flag
            ItemData plain = MakeItem("probe_plain");
            plain.healAmount = 45;
            Check(plain.moveSpeedPenalty == 0f && plain.useChannelSeconds == 0f && plain.healOverTimeSeconds == 0f && !plain.HasTimedUse, "new ItemData defaults: no penalty, no channel, no heal over time, not timed");
            ItemData channel = MakeItem("probe_channel");
            channel.healAmount = 45;
            channel.useChannelSeconds = 2f;
            ItemData overTime = MakeItem("probe_hot");
            overTime.healAmount = 45;
            overTime.healOverTimeSeconds = 4f;
            ItemData noHeal = MakeItem("probe_noheal");
            noHeal.useChannelSeconds = 2f;
            Check(channel.HasTimedUse && overTime.HasTimedUse && !noHeal.HasTimedUse, "a channel or a heal over time makes a healing item timed; a channel on a non-healing item does not");

            string[] guids = AssetDatabase.FindAssets("t:ItemData", new[] { EmberdelvePaths.Data });
            int assets = 0, changed = 0, authored = 0;
            foreach (string guid in guids)
            {
                ItemData item = AssetDatabase.LoadAssetAtPath<ItemData>(AssetDatabase.GUIDToAssetPath(guid));
                if (item == null) continue;
                assets++;
                bool expected;
                if (item.itemId == "smoldering_tonic")
                {
                    authored++;
                    expected = item.moveSpeedPenalty == 0f && item.useChannelSeconds == 1.8f && item.healOverTimeSeconds == 4f;
                }
                else if (item.itemId == "blood_draught")
                {
                    authored++;
                    expected = item.moveSpeedPenalty == 0f && item.useChannelSeconds == 1.8f && item.healOverTimeSeconds == 4f;
                }
                else if (item.itemId == "linen_bandage")
                {
                    authored++;
                    expected = item.IsBandage && item.healAmount == 25 && item.moveSpeedPenalty == 0f && item.useChannelSeconds == 5f && item.healOverTimeSeconds == 0f;
                }
                else if (item.itemId == "graspwrap_gloves")
                {
                    authored++;
                    expected = item.moveSpeedPenalty == 3f && item.useChannelSeconds == 0f && item.healOverTimeSeconds == 0f;
                }
                else expected = item.moveSpeedPenalty == 0f && item.useChannelSeconds == 0f && item.healOverTimeSeconds == 0f;
                if (!expected)
                {
                    changed++;
                    Debug.LogError("[LoadoutD3c] unexpected authored D3c tuning: " + item.itemId);
                }
            }
            Check(assets >= 14 && authored == 4 && changed == 0, "both potions drink for1.8s then heal over4s; bandages apply for5s then heal25HP; gloves penalty remains3");
        }

        private static ItemData MakeItem(string id)
        {
            ItemData item = ScriptableObject.CreateInstance<ItemData>();
            item.itemId = id;
            item.displayName = id;
            Cleanup.Add(item);
            return item;
        }

        // The harness reads the phase-1 totals and finishes the single final line.
        internal static void Finish(int checks, int failures)
        {
            Debug.Log("[Emberdelve][LoadoutD3c] " + (failures == 0 ? "PASS" : "FAIL") + " checks=" + checks + " failures=" + failures);
            EmberdelveGateExit.Exit(failures == 0 ? 0 : 1);
        }
    }

    public sealed class LoadoutD3cHarness : MonoBehaviour
    {
        private int _checks;
        private int _failures;
        private PlayerRig _rig;
        private Inventory _bag;
        private Equipment _gear;
        private ClassLoadout _loadout;
        private IInventoryAuthority _authority;
        private ItemCatalog _catalog;
        private ItemInstanceAuthority _items;

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[LoadoutD3c] mismatch " + label);
        }

        private static bool Near(float a, float b, float tolerance)
        {
            return Mathf.Abs(a - b) <= tolerance;
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;
            try { Execute(); }
            catch (Exception e) { _failures++; Debug.LogException(e); }

            int checks = _checks + SessionState.GetInt(EmberdelveLoadoutD3cGate.ChecksKey, 0);
            int failures = _failures + SessionState.GetInt(EmberdelveLoadoutD3cGate.FailuresKey, 0);
            EmberdelveLoadoutD3cGate.Finish(checks, failures);
        }

        private ItemData Resolve(string id)
        {
            bool ok = _catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "catalog resolves " + id + ": " + reason);
            return item;
        }

        private ItemInstance Mint(ItemData item, AffixStatKey key, string affixId, float value)
        {
            AffixRecord[] records = { new AffixRecord(affixId, key, value) };
            bool made = ItemInstance.TryCreateNew(item, 1, AffixRarity.Uncommon, records, _items, out ItemInstance instance, out string reason);
            Check(made && instance != null, "mint " + item.itemId + " with " + affixId + "=" + value + ": " + reason);
            if (!made || instance == null) return null;
            bool added = _bag.TryAddInstance(instance, out reason);
            Check(added, "the " + item.itemId + " enters the bag: " + reason);
            return added ? instance : null;
        }

        private Guid WornUid(EquipmentSlot slot)
        {
            ItemInstance worn = _gear.EquippedInstance(slot);
            return worn != null ? worn.Uid : Guid.Empty;
        }

        private InventoryCommand Command(InventoryCommandKind kind, EquipmentSlot slot, Guid sourceUid, Guid targetUid)
        {
            bool created = _authority.TryCreateLocalCommand(kind, 0, 0, Vector2Int.zero, slot, sourceUid, targetUid, out InventoryCommand command, out string reason);
            Check(created, "create " + kind + " command: " + reason);
            return command;
        }

        private InventoryCommand SwapCommand()
        {
            ItemInstance current = _gear.MainHandInstance(_gear.ActiveWeaponSet);
            ItemInstance other = _gear.MainHandInstance(1 - _gear.ActiveWeaponSet);
            return Command(InventoryCommandKind.SwapWeaponSet, EquipmentSlot.None, current != null ? current.Uid : Guid.Empty, other != null ? other.Uid : Guid.Empty);
        }

        /// <summary>Everything a refused swap must leave alone, as one string.</summary>
        private string Snap()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("rev=").Append(_bag.Revision).Append(" set=").Append(_gear.ActiveWeaponSet).Append(" slot=").Append(_loadout.ActiveSlot);
            sb.Append(" weapon=").Append(_rig.ActiveWeapon != null ? _rig.ActiveWeapon.weaponId : "-");
            sb.Append(" off=").Append(_rig.OffhandWeapon != null ? _rig.OffhandWeapon.weaponId : "-");
            sb.Append(" hp=").Append(_rig.MaxHealth).Append(" pen=").Append(_gear.MoveSpeedPenaltyPoints).Append(" units=").Append(_bag.TotalUnits).Append(" gear{");
            for (int s = 1; s < Equipment.SlotCount; s++)
            {
                ItemInstance worn = _gear.EquippedInstance((EquipmentSlot)s);
                sb.Append(worn != null ? worn.UidText : "-").Append(';');
            }
            return sb.Append('}').ToString();
        }

        private void Execute()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead && _rig.Inventory != null && _rig.Equipment != null && _rig.Loadout != null && _rig.Movement != null && _rig.Health != null,
                "live level rig has a bag, equipment, loadout, movement and health");
            if (_rig == null || _rig.IsDead || _rig.Inventory == null || _rig.Equipment == null || _rig.Loadout == null || _rig.Movement == null || _rig.Health == null) return;

            _bag = _rig.Inventory;
            _gear = _rig.Equipment;
            _loadout = _rig.Loadout;
            _catalog = _bag.Catalog;
            string reason = "no catalog";
            Check(_catalog != null && !_catalog.IsTestCatalog && _catalog.IsValid(out reason), "the live bag uses the valid production catalog: " + reason);
            if (_catalog == null || !_catalog.TryGetAuthority(out _items, out reason)) return;
            _authority = _bag.CommandAuthority;
            Check(_authority != null, "the bag exposes its bound authority");
            if (_authority == null) return;

            if (_loadout.ActiveSlot != 0) _loadout.ForceSwitchToSlot(0);
            _gear.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);
            Check(_gear.ActiveWeaponSet == 0 && _loadout.ActiveSlot == 0 && _loadout.WeaponInSlot(0) != null && _loadout.WeaponInSlot(1) != null && _loadout.WeaponInSlot(0) != _loadout.WeaponInSlot(1),
                "the level's class holds two different weapons: set 1 in hand, set 2 ready");
            if (_loadout.WeaponInSlot(0) == null || _loadout.WeaponInSlot(1) == null) return;

            float baseHp = _rig.MaxHealth;
            SpeedWithGear();
            SwapThroughAuthority(baseHp);
            ObserverFailures(baseHp);
            BeltByIdentity();
            PotionChannel();
            BandageChannel();

            if (GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is LocalCombatAuthority clocked) clocked.SetClock(null);
            _gear.Clear();
            _bag.Clear();
            Check(_gear.ActiveWeaponSet == 0 && _loadout.ActiveSlot == 0 && Near(_rig.MaxHealth, baseHp, 0.0001f) && Near(_gear.MoveSpeedPenaltyPoints, 0f, 0f), "after clearing, the weapon set, health and penalty are the baseline");
        }

        // ------------------------------------------------------------------ 1. move speed follows the worn gear, exactly

        private float HeldPoints()
        {
            WeaponData main = _rig.ActiveWeapon;
            WeaponData off = _rig.OffhandWeapon;
            bool offContributes = off != null && off.IsShield && !(main != null && main.twoHanded);
            return (main != null ? Mathf.Max(0f, main.heldMoveSpeedPenalty) : 0f) + (offContributes ? Mathf.Max(0f, off.heldMoveSpeedPenalty) : 0f);
        }

        /// <summary>The ground-speed ratio the rule promises for the given worn points: (1 - (held+worn)/300, floored) / (1 - held/300).</summary>
        private float ExpectedRatio(float worn)
        {
            float held = HeldPoints();
            float with = Mathf.Max(0.5f, 1f - (held + worn) / 300f);
            float without = Mathf.Max(0.5f, 1f - held / 300f);
            return with / without;
        }

        private void SpeedWithGear()
        {
            ItemData cuirass = Resolve("boiled_cuirass");
            if (cuirass == null) return;
            float authored = cuirass.moveSpeedPenalty;
            try
            {
                _gear.Clear();
                _bag.Clear();
                float s0 = _rig.Movement.ResolveGroundSpeed(false, false);
                float f0 = _rig.GetMoveFlowFactor();
                Check(s0 > 0f && f0 > 0f && Near(_gear.MoveSpeedPenaltyPoints, 0f, 0f), "the bare character walks (" + s0 + ") with no worn penalty");

                // existing content: the authored cuirass has no penalty, so wearing it changes nothing about speed
                Check(Near(authored, 0f, 0f), "the authored cuirass carries no move-speed penalty (D3c authors none)");
                ItemInstance plain = Mint(cuirass, AffixStatKey.MaxHealth, "affix_max_health", 10f);
                if (plain == null) return;
                Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(plain.Uid), out string why), "the cuirass is worn: " + why);
                Check(Near(_gear.MoveSpeedPenaltyPoints, 0f, 0f) && Near(_rig.Movement.ResolveGroundSpeed(false, false), s0, 1e-5f) && Near(_rig.GetMoveFlowFactor(), f0, 1e-6f),
                    "with penalty 0 the worn cuirass leaves ground speed exactly as it was");
                Check(_gear.TryUnequip(EquipmentSlot.Chest, out why), "the cuirass comes off: " + why);

                // a penalty of 30 points
                cuirass.moveSpeedPenalty = 30f;
                Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(plain.Uid), out why), "the cuirass is worn again: " + why);
                Check(Near(_gear.MoveSpeedPenaltyPoints, 30f, 0f), "the equipment reports 30 worn penalty points (" + _gear.MoveSpeedPenaltyPoints + ")");
                float s1 = _rig.Movement.ResolveGroundSpeed(false, false);
                Check(Near(s1 / s0, ExpectedRatio(30f), 1e-4f), "ground speed is exactly (1 - (held+30)/300) / (1 - held/300) of the bare speed: " + (s1 / s0) + " vs " + ExpectedRatio(30f));
                Check(s1 < s0, "the worn gear slows walking");
                _gear.RefreshStats();
                _gear.RefreshStats();
                Check(Near(_gear.MoveSpeedPenaltyPoints, 30f, 0f) && Near(_rig.Movement.ResolveGroundSpeed(false, false), s1, 1e-6f), "repeated refreshes add nothing: the penalty counts exactly once");

                // replacing the piece swaps the contribution, it does not stack
                cuirass.moveSpeedPenalty = 45f;
                _gear.RefreshStats();
                Check(Near(_gear.MoveSpeedPenaltyPoints, 45f, 0f) && Near(_rig.Movement.ResolveGroundSpeed(false, false) / s0, ExpectedRatio(45f), 1e-4f), "a changed authored value replaces the old one (45, not 30 + 45)");

                // the floor
                cuirass.moveSpeedPenalty = 100f;
                _gear.RefreshStats();
                Check(Near(_rig.Movement.ResolveGroundSpeed(false, false) / s0, ExpectedRatio(100f), 1e-4f), "a heavy piece follows the same formula: " + (_rig.Movement.ResolveGroundSpeed(false, false) / s0));
                cuirass.moveSpeedPenalty = 1000f;
                _gear.RefreshStats();
                Check(Near(_rig.Movement.ResolveGroundSpeed(false, false) / s0, ExpectedRatio(1000f), 1e-4f) && ExpectedRatio(1000f) < ExpectedRatio(100f), "an absurd penalty hits the floor instead of stopping the character");

                // off: back to exactly the bare speed
                Check(_gear.TryUnequip(EquipmentSlot.Chest, out why), "the cuirass comes off: " + why);
                Check(Near(_gear.MoveSpeedPenaltyPoints, 0f, 0f) && Near(_rig.Movement.ResolveGroundSpeed(false, false), s0, 1e-5f) && Near(_rig.GetMoveFlowFactor(), f0, 1e-6f),
                    "unequipping restores the exact bare speed");
            }
            finally
            {
                cuirass.moveSpeedPenalty = authored;
                _gear.Clear();
                _bag.Clear();
            }
        }

        // ------------------------------------------------------------------ 2. weapon sets: the swap is an atomic authority command

        // The authority measures weapon cooldown on its own clock. The gate skews that clock forward before every swing (and again when it
        // ends one) so an earlier swing's cooldown has run out; production code and the cooldown rule are untouched.
        private float _clockSkew;

        private bool TryBeginSwing(out LocalCombatAuthority local)
        {
            local = null;
            if (!GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) || authority == null) return false;
            local = authority as LocalCombatAuthority;
            WeaponData weapon = _rig.ActiveWeapon;
            if (local == null || weapon == null || !weapon.UsesSweptMelee) return false;

            _clockSkew += 30f;
            float skew = _clockSkew;
            local.SetClock(() => Time.time + skew);
            _rig.TryGetAimPose(out _, out float yaw, out float pitch);
            MeleeSwingIntent intent = new MeleeSwingIntent(_rig.CombatId, weapon.weaponId, 0, 0, yaw, pitch, CombatRegistry.NextSequence(_rig.CombatId));
            bool began = authority.TryBeginMeleeSwingAsOwner(intent, out _, out string why);
            Check(began, "a swing begins on the authority: " + why);
            return began;
        }

        private void EndSwing(LocalCombatAuthority local)
        {
            if (local == null) return;
            _clockSkew += 30f;
            float skew = _clockSkew;
            local.SetClock(() => Time.time + skew);
            local.StepSwings();
        }

        private void SwapThroughAuthority(float baseHp)
        {
            _gear.Clear();
            _bag.Clear();
            WeaponData setOne = _loadout.WeaponInSlot(0);
            WeaponData setTwo = _loadout.WeaponInSlot(1);
            Check(_rig.ActiveWeapon == setOne && _gear.ActiveWeaponSet == 0, "set 1 is live: its weapon is the combat weapon");

            // ---- refused while swinging: nothing changes, the key path and the direct API agree
            if (TryBeginSwing(out LocalCombatAuthority local))
            {
                Check(_loadout.IsActionLocked, "the swing locks the loadout");
                string before = Snap();
                InventoryCommand locked = SwapCommand();
                InventoryCommandResult refused = _authority.Submit(_rig, locked);
                Check(!refused.Accepted && !refused.Changed && refused.Revision == _bag.Revision && Snap() == before, "a swap during a swing is refused through the authority and changes nothing: " + refused.Reason);
                Check(!_gear.TrySwapWeaponSet(out string why) && Snap() == before, "the direct swap is refused the same way and changes nothing: " + why);
                Check(!_loadout.RequestWeaponSetSwap() && Snap() == before, "the swap key is refused too and queues nothing");
                EndSwing(local);
                Check(!_loadout.IsActionLocked, "the swing is over: the loadout is free again");
            }

            // ---- a legal swap: one revision, set 2 becomes the combat weapon
            ulong revision = _bag.Revision;
            InventoryCommand swap = SwapCommand();
            InventoryCommandResult result = _authority.Submit(_rig, swap);
            Check(result.Accepted && result.Changed && result.Revision == revision + 1 && _bag.Revision == revision + 1, "the swap is accepted and moves the revision by exactly one: " + result.Reason);
            Check(_gear.ActiveWeaponSet == 1 && _loadout.ActiveSlot == 1 && _rig.ActiveWeapon == setTwo && _rig.ActiveWeapon != setOne, "the active set drives the combat weapon: set 2's weapon is now in hand");

            // ---- replay and stale identity: refused, nothing changes
            string afterSwap = Snap();
            result = _authority.Submit(_rig, swap);
            Check(!result.Accepted && !result.Changed && Snap() == afterSwap, "replaying the command is refused and changes nothing: " + result.Reason);
            InventoryCommand invented = Command(InventoryCommandKind.SwapWeaponSet, EquipmentSlot.None, Guid.NewGuid(), Guid.Empty);
            result = _authority.Submit(_rig, invented);
            Check(!result.Accepted && !result.Changed && Snap() == afterSwap, "a swap naming an identity that is not in the active main hand is refused and changes nothing: " + result.Reason);

            // ---- back again
            revision = _bag.Revision;
            result = _authority.Submit(_rig, SwapCommand());
            Check(result.Accepted && result.Changed && result.Revision == revision + 1 && _gear.ActiveWeaponSet == 0 && _loadout.ActiveSlot == 0 && _rig.ActiveWeapon == setOne, "swapping back restores set 1 with one more revision");

            // ---- worn gear of the inactive set does not count; swapping brings it in and takes the other out
            ItemData blade = Resolve("ember_fang_blade");
            if (blade == null || blade.grantedWeapon == null) return;
            ItemInstance bladeInstance = Mint(blade, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            if (bladeInstance == null) return;
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(bladeInstance.Uid), EquipmentSlot.AltWeapon, out string equipWhy), "the blade is worn in the second main hand: " + equipWhy);
            Check(WornUid(EquipmentSlot.AltWeapon) == bladeInstance.Uid && _loadout.WeaponInSlot(1) == blade.grantedWeapon && _rig.ActiveWeapon == setOne && _gear.ActiveWeaponSet == 0,
                "set 2 now holds the blade's weapon while set 1 stays in hand");
            Check(Near(_rig.MaxHealth, baseHp, 0.0001f), "the inactive set's +10 max health is not counted (" + _rig.MaxHealth + ")");

            revision = _bag.Revision;
            result = _authority.Submit(_rig, SwapCommand());
            Check(result.Accepted && result.Revision == revision + 1 && _gear.ActiveWeaponSet == 1 && _rig.ActiveWeapon == blade.grantedWeapon, "swapping to the worn blade makes ITS weapon the combat weapon: " + result.Reason);
            Check(Near(_rig.MaxHealth, baseHp + 10f, 0.0001f), "the blade's +10 counts exactly once while its set is live (" + _rig.MaxHealth + ")");
            _gear.RefreshStats();
            _gear.RefreshStats();
            Check(Near(_rig.MaxHealth, baseHp + 10f, 0.0001f), "repeated refreshes add nothing");

            // a snapshot carries the live set and restores it
            LoadoutSnapshot saved = LoadoutSnapshot.Capture(_bag, _gear);
            string json = saved.ToJson();
            Check(saved.weaponSet == 1 && json.Contains("\"weaponSet\":1") && json.Contains("\"slot\":\"AltWeapon\""), "the loadout snapshot records weapon set 2 and the worn AltWeapon");
            result = _authority.Submit(_rig, SwapCommand());
            Check(result.Accepted && _gear.ActiveWeaponSet == 0 && _rig.ActiveWeapon == setOne && Near(_rig.MaxHealth, baseHp, 0.0001f), "swapping back drops the blade's contribution in the same step");
            Check(LoadoutSnapshot.TryRestoreJson(json, _bag, _gear, null, out string restoreWhy), "the snapshot restores: " + restoreWhy);
            Check(_gear.ActiveWeaponSet == 1 && _loadout.ActiveSlot == 1 && _rig.ActiveWeapon == blade.grantedWeapon && WornUid(EquipmentSlot.AltWeapon) == bladeInstance.Uid && Near(_rig.MaxHealth, baseHp + 10f, 0.0001f),
                "a restore brings back set 2 live, the blade in its hand, and its stats exactly once");

            // U5A: hands are items. Unwearing the second weapon leaves that hand empty (the kit item it replaced is in the bag), and with set 2 live the player falls back to set 1, never to nothing
            Check(_gear.TryUnequip(EquipmentSlot.AltWeapon, out equipWhy), "the blade comes off: " + equipWhy);
            Check(_loadout.WeaponInSlot(1) == null && _gear.ActiveWeaponSet == 0 && _rig.ActiveWeapon == setOne && Near(_rig.MaxHealth, baseHp, 0.0001f), "set 2 is empty again and the player is back on set 1 (the hand is never empty)");
            Check(!_authority.Submit(_rig, SwapCommand()).Accepted && _gear.ActiveWeaponSet == 0 && _rig.ActiveWeapon == setOne, "there is no second weapon to swap to");

            _gear.Clear();
            _bag.Clear();
        }

        // Review regressions: observers cannot expose partial swaps or replay a heal payout.
        private void ObserverFailures(float baseHp)
        {
            ItemData blade = Resolve("ember_fang_blade");
            ItemInstance instance = Mint(blade, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            if (instance == null) return;
            Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(instance.Uid), EquipmentSlot.AltWeapon, out string why), "observer fixture equips the alternate blade: " + why);
            ulong revision = _bag.Revision;
            bool finalState = true;
            int observations = 0;
            Action inspect = () =>
            {
                observations++;
                finalState &= _bag.Revision == revision + 1 && _gear.ActiveWeaponSet == 1 && _loadout.ActiveSlot == 1 &&
                    _rig.ActiveWeapon == blade.grantedWeapon && Near(_rig.MaxHealth, baseHp + 10f, 0.001f);
            };
            Action<int, WeaponData> weaponObserver = (id, weapon) => { if (id == _rig.PlayerId) inspect(); };
            Action<int, int, int> ammoObserver = (id, magazine, reserve) => { if (id == _rig.PlayerId) inspect(); };
            Action<float, float> healthObserver = (current, max) => inspect();
            Action<int> inventoryObserver = id => { if (id == _rig.PlayerId) inspect(); };
            Action<int, WeaponData> throwing = (id, weapon) => { if (id == _rig.PlayerId) throw new InvalidOperationException("expected D3c observer exception"); };
            GameEvents.ActiveWeaponChanged += throwing;
            GameEvents.ActiveWeaponChanged += weaponObserver;
            GameEvents.AmmoChanged += ammoObserver;
            GameEvents.InventoryChanged += inventoryObserver;
            _rig.Health.Changed += healthObserver;
            bool accepted = false;
            try { accepted = _authority.Submit(_rig, SwapCommand()).Accepted; }
            catch (InvalidOperationException) { } // The old implementation escapes here after changing the hand.
            finally
            {
                GameEvents.ActiveWeaponChanged -= weaponObserver;
                GameEvents.ActiveWeaponChanged -= throwing;
                GameEvents.AmmoChanged -= ammoObserver;
                GameEvents.InventoryChanged -= inventoryObserver;
                _rig.Health.Changed -= healthObserver;
            }
            Check(accepted && finalState && observations >= 3, "ammo, health, weapon and inventory observers see committed hands, stats and revision even when an observer throws");
            Check(_bag.Revision == revision + 1 && Near(_rig.MaxHealth, baseHp + 10f, 0.001f), "throwing weapon observer cannot interrupt the swap commit");
            if (_loadout.ActiveSlot != 0) _loadout.ForceSwitchToSlot(0);
            _gear.Clear();
            _bag.Clear();

            // Nested active-hand equip/unequip must wait for the enclosing transfer's stats and revision, too.
            ItemInstance activeBlade = Mint(blade, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            if (activeBlade != null)
            {
                ulong expectedRevision = _bag.Revision + 1;
                float expectedHp = baseHp + 10f;
                Guid expectedUid = activeBlade.Uid;
                bool finalHand = true;
                int handNotices = 0, bridgedNotices = 0;
                Action inspectHand = () =>
                {
                    handNotices++;
                    finalHand &= _bag.Revision == expectedRevision && _gear.ActiveWeaponSet == 0 &&
                        WornUid(EquipmentSlot.Weapon) == expectedUid && Near(_rig.MaxHealth, expectedHp, 0.001f);
                };
                Action<int, WeaponData> handObserver = (id, weapon) => { if (id == _rig.PlayerId) inspectHand(); };
                Action<int, float, float> bridgedThrow = (id, current, maxHealth) => { if (id == _rig.PlayerId) throw new InvalidOperationException("expected D3c observer exception"); };
                Action<int, float, float> bridgedObserver = (id, current, maxHealth) => { if (id == _rig.PlayerId) { bridgedNotices++; inspectHand(); } };
                GameEvents.ActiveWeaponChanged += throwing;
                GameEvents.ActiveWeaponChanged += handObserver;
                GameEvents.PlayerHealthChanged += bridgedThrow;
                GameEvents.PlayerHealthChanged += bridgedObserver;
                try
                {
                    Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(activeBlade.Uid), EquipmentSlot.Weapon, out why), "active-hand transfer completes despite throwing observers: " + why);
                    Check(finalHand && handNotices >= 2 && bridgedNotices > 0, "hand and bridged health observers all see the completed equip, including listeners after a thrower");
                    expectedRevision = _bag.Revision + 1;
                    expectedHp = baseHp;
                    expectedUid = Guid.Empty;
                    finalHand = true;
                    handNotices = bridgedNotices = 0;
                    Check(_gear.TryUnequip(EquipmentSlot.Weapon, out why), "active-hand removal completes despite throwing observers: " + why);
                    Check(finalHand && handNotices >= 2 && bridgedNotices > 0, "hand and bridged health observers all see the completed unequip");
                }
                finally
                {
                    GameEvents.ActiveWeaponChanged -= throwing;
                    GameEvents.ActiveWeaponChanged -= handObserver;
                    GameEvents.PlayerHealthChanged -= bridgedThrow;
                    GameEvents.PlayerHealthChanged -= bridgedObserver;
                }
            }
            _gear.Clear();
            _bag.Clear();

            // Exhaust local command creation without changing the authority's replay history.
            var nextSequence = _authority.GetType().GetField("_nextLocalSequence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Check(nextSequence != null, "local authority exposes the sequence fixture field");
            if (nextSequence != null)
            {
                object previous = nextSequence.GetValue(_authority);
                string before = Snap();
                try
                {
                    nextSequence.SetValue(_authority, 0UL);
                    Check(!_loadout.RequestWeaponSetSwap() && Snap() == before, "the swap key cannot bypass a refusing authority when command sequences are exhausted");
                }
                finally { nextSequence.SetValue(_authority, previous); }
            }
            if (_loadout.ActiveSlot != 0) _loadout.ForceSwitchToSlot(0);

            Check(GearScore.SlotWeight(EquipmentSlot.AltWeapon) == 2 && GearScore.SlotWeight(EquipmentSlot.AltOffHand) == 1, "alternate hands use the same gear-score weights as the original hands");
            instance = Mint(blade, AffixStatKey.MaxHealth, "affix_max_health", 10f);
            if (instance != null)
            {
                int scoreBefore = GearScore.ForEquipment(_gear); // U5A: the live set's kit items already score; the inactive set must add nothing on top
                Check(_gear.TryEquipFromInventory(_bag.IndexOfInstance(instance.Uid), EquipmentSlot.AltWeapon, out why), "score fixture equips alternate blade: " + why);
                Check(GearScore.ForEquipment(_gear) == scoreBefore, "inactive weapon set does not add to the active kit's gear score");
                Check(_authority.Submit(_rig, SwapCommand()).Accepted && GearScore.ForEquipment(_gear) > 0, "alternate weapon contributes gear score while held");
                _loadout.ForceSwitchToSlot(0);
            }
            _gear.Clear();
            _bag.Clear();
        }

        // ------------------------------------------------------------------ 3. the belt follows the item, not the index

        private ulong StackOf(ItemData item)
        {
            int index = _bag.IndexOfItem(item);
            return index >= 0 ? _bag.Slots[index].StackId : 0UL;
        }

        private void BeltByIdentity()
        {
            ItemData draught = Resolve("blood_draught");
            ItemData bandages = Resolve("linen_bandage");
            if (draught == null || bandages == null) return;

            _gear.Clear();
            _bag.Clear();
            Check(_bag.TryAdd(bandages, 3, out string why) && _bag.TryAdd(draught, 3, out why), "three bandages and three draughts are in the bag: " + why);
            Check(_bag.Hotbar.TryAssign(0, StackOf(draught), out why) && _bag.Hotbar.TryAssign(1, StackOf(bandages), out why), "key 1 holds the draught, key 2 the bandages (the opposite of bag order): " + why);
            Check(_bag.Hotbar.IdentityAt(0).Definition == draught && _bag.Hotbar.IdentityAt(1).Definition == bandages && _bag.Hotbar.IdentityAt(2).IsEmpty && _bag.Hotbar.IdentityAt(3).IsEmpty,
                "the belt reads as identities: draught, bandages, nothing, nothing");

            // reorder the bag: the belt still points at the same items
            ulong draughtStack = StackOf(draught), bandageStack = StackOf(bandages);
            Check(_bag.TrySwap(0, 1, out why) && _bag.Hotbar.StackIdAt(0) == bandageStack && _bag.Hotbar.StackIdAt(1) == draughtStack, "physical slots exchange their items: " + why);
            Check(_bag.TrySwap(0, 1, out why), "restore the belt arrangement before saving: " + why);
            Check(_bag.Hotbar.StackIdAt(0) == draughtStack && _bag.Hotbar.StackIdAt(1) == bandageStack && _bag.Hotbar.IdentityAt(0).Definition == draught && _bag.Hotbar.IdentityAt(1).Definition == bandages,
                "after the reorder the belt still points at the same two items");

            // snapshot -> restore into a differently ordered bag: identities, not indices or old stack ids
            LoadoutSnapshot saved = LoadoutSnapshot.Capture(_bag, _gear);
            string json = saved.ToJson();
            Check(saved.belt != null && saved.belt.Length == 6 && saved.belt[0] == "i:blood_draught" && saved.belt[1] == "i:linen_bandage" && saved.belt[2] == "" && saved.belt[3] == "" &&
                  json.Contains("\"belt\":[\"i:blood_draught\",\"i:linen_bandage\",\"\",\"\",\"\",\"\"]"), "the snapshot spells the belt by item id: " + string.Join(",", saved.belt ?? new string[0]));
            Check(_bag.TrySwap(0, 1, out why), "the bag is reordered again before the restore: " + why);
            _bag.Clear();
            Check(LoadoutSnapshot.TryRestoreJson(json, _bag, _gear, null, out why), "the loadout restores: " + why);
            Check(_bag.Hotbar.IdentityAt(0).Definition == draught && _bag.Hotbar.IdentityAt(1).Definition == bandages && _bag.Hotbar.IdentityAt(2).IsEmpty && _bag.Hotbar.IdentityAt(3).IsEmpty,
                "after the restore key 1 is the draught again and key 2 the bandages (not the default seeding by bag order)");
            Check(_bag.Hotbar.StackIdAt(0) == StackOf(draught) && _bag.Hotbar.StackIdAt(0) != draughtStack, "the binding resolves to the NEW live stack: a retired stack id is never revived");

            // keys 1-4 still work: key 1 drinks the bound draught
            float max = _rig.Health.MaxHealth;
            _rig.Health.SetHealth(Mathf.Max(1f, max - 60f));
            float low = _rig.Health.CurrentHealth;
            int units = _bag.TotalUnits;
            Check(_bag.RequestHoldStack(_bag.Hotbar.StackIdAt(0), out why), "draw the bound draught before using: " + why);
            bool startedDrink = _bag.Hotbar.TryUse(0, out why);
            ConsumableChannel beltDrink = ConsumableChannel.Ensure(_rig.gameObject);
            var previousSpeed = beltDrink.SpeedRatioOverride;
            try
            {
                beltDrink.SpeedRatioOverride = () => 0f;
                beltDrink.Tick(draught.useChannelSeconds);
                beltDrink.Tick(draught.healOverTimeSeconds);
            }
            finally { beltDrink.SpeedRatioOverride = previousSpeed; }
            Check(startedDrink && _bag.TotalUnits == units - 1 && Near(_rig.Health.CurrentHealth, Mathf.Min(max, low + draught.healAmount), 0.001f), "key 1 uses exactly the bound draught (+" + draught.healAmount + "): " + why);

            // A full loadout explicitly saves its belt, including empty keys. Bare bag restores retain their separate default policy.
            int returnedCell = 0;
            foreach (var entry in saved.bag.entries)
                if (QuickBeltRules.TryGetSlot(new Vector2Int(entry.x, entry.y), saved.bag.width, out _))
                { entry.x = returnedCell++; entry.y = 0; }
            saved.belt = new[] { "", "", "", "", "", "" };
            saved.Seal();
            string emptyBeltJson = saved.ToJson();
            ulong liveStack = _bag.Hotbar.StackIdAt(0);
            ItemData liveDefinition = _bag.Hotbar.IdentityAt(0).Definition;
            Check(liveStack != 0 && liveDefinition != null, "a live belt exists before the empty-belt restore");
            Check(LoadoutSnapshot.TryRestoreJson(emptyBeltJson, _bag, _gear, null, out why), "a loadout with an empty belt restores over a live belt: " + why);
            Check(_bag.Hotbar.StackIdAt(0) == 0 && _bag.Hotbar.StackIdAt(1) == 0 && _bag.Hotbar.StackIdAt(2) == 0 && _bag.Hotbar.StackIdAt(3) == 0, "an explicitly empty saved belt clears all live bindings");
            Check(LoadoutSnapshot.Capture(_bag, _gear).ToJson() == emptyBeltJson, "empty-belt snapshot restores and captures byte for byte");
            _bag.Clear();
            Check(LoadoutSnapshot.TryRestoreJson(emptyBeltJson, _bag, _gear, null, out why), "a loadout with an empty belt restores into a bag with no belt: " + why);
            Check(_bag.Slots.Count > 0 && _bag.Hotbar.HasSeeded && _bag.Hotbar.StackIdAt(0) == 0, "an empty full-loadout belt stays empty with no lazy auto-fill");

            _gear.Clear();
            _bag.Clear();
        }

        // ------------------------------------------------------------------ 4. potions: use channel, heal over time

        private float _low;

        private ulong Stock(ItemData draught, int count)
        {
            _rig.GetComponent<ConsumableChannel>()?.Abort("fixture reset");
            _bag.Clear();
            _bag.TryAdd(draught, count);
            float max = _rig.Health.MaxHealth;
            _rig.Health.SetHealth(Mathf.Max(1f, max - 60f));
            _low = _rig.Health.CurrentHealth;
            ulong stack = StackOf(draught);
            Check(_bag.Hotbar.TryAssign(0, stack, out string why) && _bag.RequestHoldStack(stack, out why), "place and draw the stocked draught before use: " + why);
            return stack;
        }

        private void HitOnce()
        {
            _rig.Health.ApplyDamage(new DamageInfo(1f, Vector3.zero, Vector3.forward, Vector3.zero, 999, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
        }

        private void PotionChannel()
        {
            ItemData draught = Resolve("blood_draught");
            if (draught == null) return;
            float authoredChannel = draught.useChannelSeconds, authoredOverTime = draught.healOverTimeSeconds;
            ConsumableChannel channel = ConsumableChannel.Ensure(_rig.gameObject);
            bool wasEnabled = channel.enabled;
            channel.enabled = false;
            channel.SpeedRatioOverride = () => 0f;
            try
            {
                _gear.Clear();
                Check(_rig.Health.MaxHealth > 70f && draught.healAmount == 45, "the live character has room for a 45 point potion (max " + _rig.Health.MaxHealth + ")");
                float total = draught.healAmount;

                // Production potion: the visible drink finishes before consuming or healing.
                Check(Near(authoredChannel, 1.8f, 0.0001f) && authoredOverTime == 4f, "Blood Draught authors a 1.8-second drink then four-second heal");
                ulong stack = Stock(draught, 3);
                ulong revision = _bag.Revision;
                Check(_bag.TryUseStack(stack) && channel.IsChanneling && channel.ChannelStackId == stack && Near(channel.ChannelDuration, authoredChannel, 0.0001f), "an ordinary draught starts the authored drink");
                channel.Tick(authoredChannel * 0.5f);
                Check(channel.IsChanneling && _bag.TotalUnits == 3 && _bag.Revision == revision && Near(_rig.Health.CurrentHealth, _low, 0.0001f), "halfway through the real potion nothing is consumed or healed");
                channel.Tick(authoredChannel * 0.5f);
                Check(!channel.IsChanneling && channel.ActiveHealCount == 1 && _bag.TotalUnits == 2 && _bag.Revision == revision + 1 && Near(_rig.Health.CurrentHealth, _low, 0.001f),
                    "the authored drink consumes once and schedules healing without instant payout");
                channel.Tick(2f);
                Check(Near(_rig.Health.CurrentHealth, _low + total * .5f, .001f), "half of the authored gradual heal arrives after two seconds");
                channel.Tick(2f);
                Check(channel.ActiveHealCount == 0 && Near(_rig.Health.CurrentHealth, _low + total, .001f), "all45HP arrives after four seconds");
                stack = Stock(draught, 1);
                Check(_bag.TryUseStack(stack), "the last potion starts drinking");
                channel.Tick(authoredChannel);
                Check(_bag.IndexOfStack(stack) < 0 && channel.ActiveHealCount == 1, "the potion stack retires before its heal ticks");
                Check(_bag.RequestWeaponSet(0, out _), "weapons can be drawn after the potion was consumed");
                channel.Tick(4f);
                Check(Near(_rig.Health.CurrentHealth, _low + total, .001f), "healing survives stack retirement and switching back to weapons");

                // ---- a channel with a heal over time
                draught.useChannelSeconds = 2f;
                draught.healOverTimeSeconds = 4f;

                // Damage cannot interrupt the committed drink
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                int cancelled = channel.CancelledDrinks;
                Check(_bag.TryUseStack(stack) && channel.IsChanneling && channel.ChannelItem == draught, "using the draught starts a drink");
                Check(_bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.Health.CurrentHealth, _low, 0.0001f), "nothing is consumed, committed or healed while the player is drinking");
                channel.Tick(1f);
                Check(channel.IsChanneling && Near(channel.ChannelProgress, 0.5f, 1e-4f), "halfway through the drink");
                HitOnce();
                Check(channel.IsChanneling && channel.CancelledDrinks == cancelled && _bag.TotalUnits == 2 && _bag.Revision == revision, "being hit cannot cancel drinking or consume early");
                float afterHit = _rig.Health.CurrentHealth;
                channel.Tick(1f);
                Check(!channel.IsChanneling && _bag.TotalUnits == 1 && _bag.Revision == revision + 1 && Near(_rig.Health.CurrentHealth, afterHit, 0.0001f), "the damaged drink consumes exactly once at its original deadline");
                channel.Tick(4f);
                Check(Near(_rig.Health.CurrentHealth, afterHit + total, 0.0001f) && channel.ActiveHealCount == 0, "damage does not prevent the completed drink from paying its full heal");

                // Both creeping and running leave the drink active
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                cancelled = channel.CancelledDrinks;
                Check(_bag.TryUseStack(stack), "a second drink starts");
                channel.SpeedRatioOverride = () => 0.6f;
                channel.Tick(0.5f);
                Check(channel.IsChanneling, "creeping at exactly 0.6 of walking speed does not interrupt");
                channel.SpeedRatioOverride = () => 0.9f;
                channel.Tick(0.1f);
                Check(channel.IsChanneling && channel.CancelledDrinks == cancelled && _bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.Health.CurrentHealth, _low, 0.0001f),
                    "running cannot cancel the drink or consume it early");
                channel.SpeedRatioOverride = () => 0f;

                // LMB is the held item's use button and must not cancel its own drink.
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                cancelled = channel.CancelledDrinks;
                Check(_bag.TryUseStack(stack), "a third drink starts");
                _rig.Input.PrimaryPressedOverride = true;
                channel.Tick(0.1f);
                _rig.Input.PrimaryPressedOverride = null;
                Check(channel.IsChanneling && channel.CancelledDrinks == cancelled && _bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.Health.CurrentHealth, _low, 0.0001f),
                    "the use button keeps the held potion channel running without consuming it early");
                Check(!channel.Cancel("player request") && channel.IsChanneling, "manual cancellation is refused while drinking");
                channel.Abort("fixture reset");

                // A running weapon swing must reject both drawing and using a bag potion.
                stack = Stock(draught, 2);
                Check(_bag.RequestWeaponSet(0, out string selectReason), "draw weapon set 1 before the swing: " + selectReason);
                revision = _bag.Revision;
                bool swingStarted = TryBeginSwing(out LocalCombatAuthority local);
                Check(swingStarted, "the held-item rejection fixture executes a real weapon swing");
                if (swingStarted)
                {
                    try
                    {
                        Check(!_bag.RequestHoldStack(stack, out _) && _bag.HeldStackId == 0,
                            "an attack in progress rejects drawing a potion");
                        Check(!_bag.TryUseStack(stack) && !channel.IsChanneling && _bag.TotalUnits == 2 && _bag.Revision == revision,
                            "an attack cannot use an unheld potion or consume inventory");
                    }
                    finally { EndSwing(local); }
                }

                // The drink locks hands and inventory commands until completion.
                stack = Stock(draught, 2);
                ItemData torch = Resolve("pitch_torch");
                Check(torch != null && _bag.TryAdd(torch, 1), "stock a different quick item for locked-hands requests");
                ulong torchStack = StackOf(torch);
                Check(_bag.Hotbar.TryAssign(1, torchStack, out _), "place the alternate utility before the drink locks inventory");
                _bag.TryGetStack(torchStack, out InventorySlot heldTorch);
                revision = _bag.Revision;
                cancelled = channel.CancelledDrinks;
                Check(_bag.TryUseStack(stack), "the locked-hands drink starts");
                Check(!_bag.RequestWeaponSet(1, out selectReason) && !_bag.RequestWeaponSet(0, out _) &&
                      !_bag.RequestHoldStack(torchStack, out _) && channel.IsChanneling && channel.CancelledDrinks == cancelled &&
                      _bag.HeldStackId == stack && _bag.TotalUnits == 3 && _bag.Revision == revision,
                    "weapon and quick-item switches are rejected without interrupting or mutating the drink: " + selectReason);
                bool dropCreated = _authority.TryCreateLocalCommand(InventoryCommandKind.Drop, torchStack, 0,
                    Vector2Int.zero, EquipmentSlot.None, heldTorch.Instance != null ? heldTorch.Instance.Uid : Guid.Empty, Guid.Empty,
                    out InventoryCommand drop, out string dropReason);
                InventoryCommandResult dropResult = dropCreated ? _authority.Submit(_rig, drop) : default;
                Check(dropCreated && !dropResult.Accepted && (dropResult.Reason ?? "").Contains("drinking") && channel.IsChanneling && _bag.TotalUnits == 3 && _bag.Revision == revision &&
                      _bag.TryGetStack(torchStack, out _) && _bag.HeldStackId == stack,
                    "a valid drop request is rejected while drinking with every stack intact: " + (dropCreated ? dropResult.Reason : dropReason));

                // one drink at a time
                stack = Stock(draught, 3);
                Check(_bag.TryUseStack(stack) && channel.IsChanneling, "a drink starts");
                string busy = Snap();
                Check(!_bag.TryUseStack(stack) && channel.IsChanneling && Snap() == busy, "a second use while drinking is refused and changes nothing");
                Check(!channel.Cancel("player request") && channel.IsChanneling && Snap() == busy, "manual cancellation preserves the running drink and inventory");
                revision = _bag.Revision;
                channel.enabled = true;
                channel.enabled = false;
                Check(!channel.IsChanneling && _bag.TotalUnits == 3 && _bag.Revision == revision && Near(_rig.Health.CurrentHealth, _low, 0.0001f), "disabling the component aborts lifecycle work without consuming or healing");

                // ---- completion: consumed once, heal over time pays out the exact total once
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                int completed = channel.CompletedDrinks;
                Check(_bag.TryUseStack(stack), "the drink for completion starts");
                channel.Tick(0.5f);
                channel.Tick(0.5f);
                channel.Tick(0.5f);
                Check(channel.IsChanneling && _bag.TotalUnits == 2, "still drinking at 1.5 of 2 seconds, nothing consumed");
                channel.Tick(0.5f);
                Check(!channel.IsChanneling && channel.CompletedDrinks == completed + 1 && _bag.TotalUnits == 1 && _bag.Revision == revision + 1, "the drink completes: exactly one unit consumed, exactly one revision");
                Check(channel.ActiveHealCount == 1 && Near(channel.PendingHeal, total, 0.0001f) && Near(_rig.Health.CurrentHealth, _low, 0.0001f), "completion starts the heal over time; nothing is paid out yet (pending " + channel.PendingHeal + ")");
                for (int second = 1; second <= 4; second++)
                {
                    channel.Tick(1f);
                    Check(Near(_rig.Health.CurrentHealth, _low + total * second / 4f, 0.01f), "after " + second + " s of the heal: " + _rig.Health.CurrentHealth + " of " + (_low + total * second / 4f));
                    if (second == 2) HitOnce();
                    if (second == 2) _low -= 1f;
                }
                Check(channel.ActiveHealCount == 0 && channel.PendingHeal == 0f && Near(_rig.Health.CurrentHealth, _low + total, 0.01f), "after 4 s exactly the full 45 arrived (a hit after the drink does not stop it)");
                float healed = _rig.Health.CurrentHealth;
                channel.Tick(20f);
                Check(Near(_rig.Health.CurrentHealth, healed, 0.0001f) && _bag.TotalUnits == 1 && channel.CompletedDrinks == completed + 1, "nothing more is paid out afterwards: the total is delivered once");

                // A failed health observer must not replay a payout on the next tick.
                stack = Stock(draught, 2);
                Check(_bag.TryUseStack(stack), "observer fixture starts a drink");
                channel.Tick(2f);
                Action<float, float> throwingHeal = (current, maxHealth) => { throw new InvalidOperationException("expected D3c observer exception"); };
                _rig.Health.Changed += throwingHeal;
                try { channel.Tick(1f); }
                catch (InvalidOperationException) { } // Old code leaves Delivered unchanged after paying HP.
                finally { _rig.Health.Changed -= throwingHeal; }
                channel.Tick(3f);
                channel.Tick(5f);
                Check(channel.ActiveHealCount == 0 && Near(_rig.Health.CurrentHealth, _low + total, 0.01f), "a throwing health observer never repeats healing or leaves an expired payout active");

                // Completion still publishes the inventory fact when presentation throws.
                stack = Stock(draught, 2);
                Check(_bag.TryUseStack(stack), "toast observer fixture starts a drink");
                int notices = 0;
                Action<int> notice = id => { if (id == _rig.PlayerId) notices++; };
                Action<int, string> badToast = (id, text) => { if (id == _rig.PlayerId) throw new InvalidOperationException("expected D3c observer exception"); };
                GameEvents.InventoryChanged += notice;
                GameEvents.ToastRaised += badToast;
                try { channel.Tick(2f); }
                catch (InvalidOperationException) { }
                finally { GameEvents.InventoryChanged -= notice; GameEvents.ToastRaised -= badToast; }
                Check(notices == 1 && _bag.TotalUnits == 1, "a throwing completion toast cannot suppress the committed inventory notification");
                channel.Tick(4f);

                // ---- channel only: the whole heal lands at completion
                draught.healOverTimeSeconds = 0f;
                stack = Stock(draught, 2);
                completed = channel.CompletedDrinks;
                Check(_bag.TryUseStack(stack), "a channel-only drink starts");
                channel.Tick(1.9f);
                Check(channel.IsChanneling && Near(_rig.Health.CurrentHealth, _low, 0.0001f), "nothing lands before the channel ends");
                channel.Tick(0.15f);
                Check(!channel.IsChanneling && _bag.TotalUnits == 1 && Near(_rig.Health.CurrentHealth, _low + total, 0.001f) && channel.ActiveHealCount == 0 && channel.CompletedDrinks == completed + 1,
                    "the channel ends: one unit consumed, the whole 45 healed once");

                // A heal from another source cannot turn a committed drink into a free cancellation.
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                completed = channel.CompletedDrinks;
                Check(_bag.TryUseStack(stack), "the full-health-at-completion fixture starts while injured");
                _rig.Health.SetHealth(_rig.Health.MaxHealth);
                channel.Tick(draught.useChannelSeconds);
                channel.Tick(5f);
                Check(!channel.IsChanneling && _bag.TotalUnits == 1 && _bag.Revision == revision + 1 &&
                      channel.CompletedDrinks == completed + 1 && Near(_rig.Health.CurrentHealth, _rig.Health.MaxHealth, 0.0001f),
                    "healing to full during the drink still consumes exactly one potion at completion");

                // ---- heal over time without a channel: consumed now, delivered over time
                draught.useChannelSeconds = 0f;
                draught.healOverTimeSeconds = 3f;
                stack = Stock(draught, 2);
                revision = _bag.Revision;
                Check(_bag.TryUseStack(stack), "a heal-over-time potion is used");
                Check(!channel.IsChanneling && _bag.TotalUnits == 1 && _bag.Revision == revision + 1 && channel.ActiveHealCount == 1 && Near(channel.PendingHeal, total, 0.0001f) && Near(_rig.Health.CurrentHealth, _low, 0.0001f),
                    "with no channel the unit is consumed at once and the heal starts");
                channel.Tick(1.5f);
                Check(Near(_rig.Health.CurrentHealth, _low + total / 2f, 0.01f), "half way through: half the heal");
                channel.Tick(1.5f);
                channel.Tick(5f);
                Check(Near(_rig.Health.CurrentHealth, _low + total, 0.01f) && channel.ActiveHealCount == 0 && _bag.TotalUnits == 1, "the full 45 arrived once and no more");
            }
            finally
            {
                draught.useChannelSeconds = authoredChannel;
                draught.healOverTimeSeconds = authoredOverTime;
                channel.Abort("gate over");
                channel.SpeedRatioOverride = null;
                channel.enabled = wasEnabled;
                _rig.Input.PrimaryPressedOverride = null;
                if (_gear.ActiveWeaponSet != 0) _loadout.ForceSwitchToSlot(0);
                _bag.Clear();
            }

            Check(draught.useChannelSeconds == authoredChannel && draught.healOverTimeSeconds == authoredOverTime, "the draught asset is back to its authored values");
        }
        private void BandageChannel()
        {
            ItemData bandage = Resolve("linen_bandage");
            Check(bandage != null && bandage.IsBandage && bandage.healAmount == 25 && bandage.useChannelSeconds == 5f && bandage.healOverTimeSeconds == 0f,
                "linen bandage authors25HP after5s application with no heal-over-time");
            if (bandage == null) return;
            ConsumableChannel channel = ConsumableChannel.Ensure(_rig.gameObject);
            bool enabled = channel.enabled; channel.enabled = false;
            channel.SpeedRatioOverride = () => 0f;
            try
            {
                ulong stack = Stock(bandage, 2); ulong revision = _bag.Revision;
                Check(_bag.TryUseStack(stack), "bandage application starts");
                channel.SpeedRatioOverride = () => .6f; channel.Tick(1f);
                Check(channel.IsChanneling, "bandaging permits movement at exactly0.6 walking speed");
                channel.SpeedRatioOverride = () => .9f; channel.Tick(.1f);
                Check(!channel.IsChanneling && _bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.CurrentHealth, _low, .001f),
                    "faster movement cancels bandaging without consumption or healing");
                channel.SpeedRatioOverride = () => 0f;
                stack = Stock(bandage, 2); revision = _bag.Revision;
                Check(_bag.TryUseStack(stack), "bandage starts before a hit"); channel.Tick(1f); HitOnce();
                Check(!channel.IsChanneling && _bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.CurrentHealth, _low - 1f, .001f),
                    "damage cancels bandaging and only the hit changes health");
                stack = Stock(bandage, 2); revision = _bag.Revision;
                Check(_bag.TryUseStack(stack) && channel.Cancel("player request"), "bandage permits manual cancellation");
                Check(!channel.IsChanneling && _bag.TotalUnits == 2 && _bag.Revision == revision && Near(_rig.CurrentHealth, _low, .001f), "manual cancellation costs no bandage or health");
                stack = Stock(bandage, 2);
                Check(_bag.TryUseStack(stack) && _bag.RequestWeaponSet(0, out _), "choosing a weapon set cancels bandaging");
                Check(!channel.IsChanneling && _bag.TotalUnits == 2 && _bag.HeldStackId == 0 && Near(_rig.CurrentHealth, _low, .001f), "weapon switch preserves bandages and health");
                stack = Stock(bandage, 2);
                ItemData torch = Resolve("pitch_torch"); Check(torch != null && _bag.TryAdd(torch, 1), "stock a second held item");
                ulong torchStack = StackOf(torch);
                Check(_bag.Hotbar.TryAssign(1, torchStack, out _), "place the second utility on the belt");
                Check(_bag.TryUseStack(stack) && _bag.RequestHoldStack(torchStack, out _), "choosing another utility cancels bandaging");
                Check(!channel.IsChanneling && _bag.TotalUnits == 3 && _bag.HeldStackId == torchStack && Near(_rig.CurrentHealth, _low, .001f), "item switch preserves every unit and health");
                stack = Stock(bandage, 1); revision = _bag.Revision; int completed = channel.CompletedDrinks;
                Check(_bag.TryUseStack(stack), "the uninterrupted bandage starts");
                _bag.TryGetStack(stack, out InventorySlot bandageStack);
                Vector2Int origin = bandageStack.Origin;
                bool builtMove = _authority.TryCreateLocalCommand(InventoryCommandKind.Move, stack, 0, new Vector2Int(5, 3),
                    EquipmentSlot.None, Guid.Empty, Guid.Empty, out InventoryCommand move, out string moveReason);
                InventoryCommandResult moveResult = builtMove ? _authority.Submit(_rig, move) : default;
                Check(builtMove && !moveResult.Accepted && (moveResult.Reason ?? "").Contains("applying") && channel.IsChanneling &&
                    _bag.TotalUnits == 1 && _bag.Revision == revision && _bag.TryGetStack(stack, out InventorySlot unmoved) && unmoved.Origin == origin,
                    "a valid inventory move is blocked during bandage application without mutation: " + (builtMove ? moveResult.Reason : moveReason));
                channel.Tick(4.9f);
                Check(channel.IsChanneling && _bag.TotalUnits == 1 && _bag.Revision == revision && Near(_rig.CurrentHealth, _low, .001f), "bandaging pays nothing before five seconds");
                channel.Tick(.11f);
                Check(!channel.IsChanneling && _bag.TotalUnits == 0 && _bag.Revision == revision + 1 && channel.CompletedDrinks == completed + 1 && channel.ActiveHealCount == 0 && Near(_rig.CurrentHealth, _low + 25f, .001f),
                    "completed bandaging consumes exactly once and instantly heals25HP");
                channel.Tick(10f);
                Check(channel.CompletedDrinks == completed + 1 && Near(_rig.CurrentHealth, _low + 25f, .001f), "completed bandage never pays twice");
            }
            finally
            {
                channel.Abort("bandage fixture end"); channel.SpeedRatioOverride = null; channel.enabled = enabled;
                _bag.Clear();
            }
        }
    }
}
