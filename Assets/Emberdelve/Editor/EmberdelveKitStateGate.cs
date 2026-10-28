using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R1c2 gate: the combat authority owns each player's kit state (kit epoch, mana, ammunition, reload, cooldowns, open cast).
    /// Real Level01 run, P1 warrior and P2 archer. Proves: the rig's mana/ammo components are a DISPLAY of the authority record
    /// (forged edits cannot let a cast or a shot through and are overwritten); regeneration and reload advance on the
    /// authority's injected clock and not on frames; seeding at class apply, death, respawn, weapon change and equipment
    /// revision resets or preserves exactly what it should (no free refill on a mere equipment change or weapon swap);
    /// only the owner may seed; the HUD-facing components equal the record.
    /// Entry: <c>Emberdelve.EditorTools.EmberdelveKitStateGate.Run</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveKitStateGate
    {
        private const string Pending = "Emberdelve.KitState.Pending";

        static EmberdelveKitStateGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("KitState_Gate").AddComponent<KitStateHarness>();
                }
            };
        }

        public static void Run()
        {
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class KitStateHarness : MonoBehaviour
    {
        private static readonly Vector3 Spot = new Vector3(3000f, 0f, 3800f);

        private int _n, _fail;
        private float _clock;

        private RunDirector _director;
        private LocalCombatAuthority _authority;
        private PlayerRig _p1, _p2;
        private CombatSenderId _s1, _s2;
        private SkillData _cry;

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[KitState] mismatch " + text);
            }
        }

        private static bool Near(float a, float b, float eps = 0.01f) => Mathf.Abs(a - b) <= eps;
        private static bool Has(string text, string part) => text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            IEnumerator run = Execute();
            while (true)
            {
                bool more;
                try { more = run.MoveNext(); }
                catch (Exception e) { _fail++; Debug.LogException(e); break; }
                if (!more) break;
                yield return run.Current;
            }

            if (_authority != null) _authority.SetClock(null);
            Debug.Log("[Emberdelve][KitState] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EmberdelveGateExit.Exit(_fail == 0 ? 0 : 1);
        }

        private KitView View(PlayerRig rig)
        {
            _authority.TryGetKit(rig.CombatId, out KitView view);
            return view;
        }

        private void Advance(float seconds)
        {
            _clock += seconds;
            _authority.AdvanceKitsTo(_clock);
        }

        private void Place(PlayerRig rig, Vector3 feet)
        {
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.SetPositionAndRotation(new Vector3(feet.x, feet.y + 0.15f, feet.z), Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
        }

        private IEnumerator Execute()
        {
            _p1 = FindFirstObjectByType<PlayerRig>();
            _director = FindFirstObjectByType<RunDirector>();
            _authority = FindFirstObjectByType<LocalCombatAuthority>();
            Check(_p1 != null && _director != null && _authority != null, "scene has a rig, a run director and the combat authority");
            if (_p1 == null || _director == null || _authority == null) yield break;

            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;
            _p1.Movement.enabled = false;

            CharacterClassData archerClass = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset");
            _cry = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_WarriorBattleCry.asset");
            Check(archerClass != null && _cry != null, "class and skill assets available");
            if (archerClass == null || _cry == null) yield break;

            _p2 = _director.SpawnPlayer(Spot + new Vector3(0f, 0f, 30f), Quaternion.identity, 1, archerClass, out string spawnReason);
            Check(_p2 != null, "a rival archer joins the run (" + spawnReason + ")");
            if (_p2 == null) yield break;
            _p2.Movement.enabled = false;
            Place(_p1, Spot);
            yield return null;
            yield return null;

            _s1 = _p1.CombatSender;
            _s2 = _p2.CombatSender;

            yield return RecordAndViews();

            _clock = Time.time;
            _authority.SetClock(() => _clock);
            _authority.AdvanceKitsTo(_clock);

            yield return ForgedEdits();
            yield return RegenOnTheAuthorityClock();
            yield return ReloadOnTheAuthorityClock();
            yield return Seeding();
            yield return SeedOwnership();
        }

        // ---- 1. the record exists and the rig displays it -------------------------------------------------

        private IEnumerator RecordAndViews()
        {
            bool hasK1 = _authority.TryGetKit(_p1.CombatId, out KitView v1); bool hasK2 = _authority.TryGetKit(_p2.CombatId, out KitView v2);
            Check(hasK1 && hasK2, "both players have an authority kit record after joining");
            if (v1.MaxMana == 0) yield break;

            Check(v1.Epoch >= 1 && v2.Epoch >= 1, "kit epoch comes from the record (" + v1.Epoch + "/" + v2.Epoch + ")");
            Check(v1.MaxMana == _p1.Mana.MaxMana && Near(v1.Mana, _p1.Mana.CurrentMana) && _p1.Mana.IsAuthorityView,
                "P1's mana component is a display of the record (" + _p1.Mana.CurrentMana + "/" + _p1.Mana.MaxMana + ")");
            Check(v2.MaxMana == _p2.Mana.MaxMana && Near(v2.Mana, _p2.Mana.CurrentMana) && _p2.Mana.IsAuthorityView, "and so is P2's");
            Check(_p1.TryGetAuthorityKit(out KitView rv) && rv.Epoch == v1.Epoch && Near(rv.Mana, v1.Mana), "the rig exposes the last record it was pushed");

            RangedCombat bow = _p2.GetComponent<RangedCombat>();
            Check(bow != null && bow.IsAuthorityView && v2.HasActiveAmmo && v2.ActiveWeaponId == _p2.ActiveWeapon.weaponId
                  && bow.Magazine == v2.Magazine && bow.Reserve == v2.Reserve && v2.Magazine == v2.MagazineSize,
                "P2's ammunition display equals the record (" + bow.Magazine + "/" + bow.Reserve + ")");
            Check(!v1.HasActiveAmmo, "P1 holds a melee weapon: no active ammunition pool");
            Check(v1.ManaRegen == _p1.Mana.RegenPerSecond && v1.ManaRegenDelay == _p1.Mana.RegenDelay, "regen rules are the record's");
            yield return null;
        }

        // ---- 2. forged client edits ---------------------------------------------------------------------

        private IEnumerator ForgedEdits()
        {
            int cost = _cry.EffectiveManaCost;
            Check(_p1.ActiveWeapon != null && !_p1.ActiveWeapon.IsRanged, "P1 holds the sword");

            // Mana: the record is empty, the rig's own component is edited to look rich.
            EmberdelveKitFixtures.SpendMana(_p1.Mana, (int)View(_p1).Mana);
            Check(View(_p1).Mana < cost && _p1.Mana.CurrentMana < cost, "P1's pool is drained below Battle Cry's cost");
            _p1.Mana.Restore(1000f);
            _p1.Mana.SetMax(9999);
            Check(_p1.Mana.CurrentMana > cost, "forged: the rig's ManaComponent now claims enough mana (" + _p1.Mana.CurrentMana + ")");

            int open = _authority.OpenActivationCount;
            float recordMana = View(_p1).Mana;
            bool ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out int act, out string reason);
            Check(!ok && act == 0 && Has(reason, "not enough mana"), "the cast is refused against the record, not the rig (" + reason + ")");
            Check(Near(View(_p1).Mana, recordMana) && _authority.OpenActivationCount == open, "the refusal changed nothing in the record");

            _authority.AdvanceKitsTo(_clock);
            Check(Near(_p1.Mana.CurrentMana, View(_p1).Mana) && _p1.Mana.MaxMana == View(_p1).MaxMana,
                "the next authority push overwrote the forged mana edit (" + _p1.Mana.CurrentMana + "/" + _p1.Mana.MaxMana + ")");

            // Ammunition: P2's record magazine is empty, the component is edited and re-initialized to look full.
            RangedCombat bow = _p2.GetComponent<RangedCombat>();
            WeaponData bowWeapon = _p2.ActiveWeapon;
            EmberdelveKitFixtures.ConsumeAmmo(bow, View(_p2).Magazine);
            Check(View(_p2).Magazine == 0 && bow.Magazine == 0, "P2's record magazine is empty");
            bow.RefundAmmo(5);
            bow.Initialize(bowWeapon, 99, 99, -1);
            Check(View(_p2).Magazine == 0, "re-initializing the weapon does not refill the record (a known pool wins over a seed)");

            WeaponData w = _p2.ActiveWeapon;
            AttackIntent shot = new AttackIntent(_p2.CombatId, _p2.Kind, w, 1f, _p2.GetMuzzlePosition(), _p2.AimDirection, w.rangedRange,
                CombatRegistry.NextSequence(_p2.CombatId), Time.time);
            int accepted = _authority.AcceptedCount;
            ok = _authority.LaunchProjectile(_s2, shot, out reason);
            Check(!ok && Has(reason, "ammunition") && _authority.AcceptedCount == accepted && View(_p2).Magazine == 0,
                "a shot is refused for the empty record even though the rig shows rounds (" + reason + ")");

            SkillData multi = AssetDatabase.LoadAssetAtPath<SkillData>(DataPath("Skill_ArcherMultiShot.asset"));
            if (multi != null)
            {
                ok = _authority.TryBeginSkillCast(_s2, _p2.CombatId, multi.skillId, out act, out reason);
                Check(!ok && Has(reason, "ammunition"), "and so is a Multi-shot cast (" + reason + ")");
            }

            _authority.AdvanceKitsTo(_clock);
            Check(bow.Magazine == 0, "the next push put the forged rounds back to the record's 0");

            // Restore both records for the next sections.
            EmberdelveKitFixtures.RefillMana(_p1);
            EmberdelveKitFixtures.RefundAmmo(bow, 99);
            yield return null;
        }

        private static string DataPath(string file) => EmberdelvePaths.Data + "/" + file;

        // ---- 3. regeneration advances on the authority clock -----------------------------------------------

        private IEnumerator RegenOnTheAuthorityClock()
        {
            EmberdelveKitFixtures.RefillMana(_p1);
            Check(EmberdelveKitFixtures.SpendMana(_p1.Mana, 30), "P1 spends 30 mana");
            KitView v0 = View(_p1);
            float regen = v0.ManaRegen, delay = v0.ManaRegenDelay;
            Check(regen > 0f && Near(v0.Mana, v0.MaxMana - 30f) && v0.ManaSinceSpend == 0f, "regen rules present, record at max-30 with a fresh spend clock");

            yield return null; yield return null; yield return null;
            Check(Near(View(_p1).Mana, v0.Mana) && Near(_p1.Mana.CurrentMana, v0.Mana), "frames passing do not regenerate: the authority clock did not move");

            Advance(delay * 0.5f);
            Check(Near(View(_p1).Mana, v0.Mana) && Near(_p1.Mana.RegenDelayRemaining, delay * 0.5f, 0.02f),
                "half the delay elapsed on the authority clock: no regen yet, delay remaining " + _p1.Mana.RegenDelayRemaining);

            float dt = delay * 0.5f + 1f;
            Advance(dt);
            float expected = Mathf.Min(v0.MaxMana, v0.Mana + regen * dt);
            Check(Near(View(_p1).Mana, expected, 0.05f) && Near(_p1.Mana.CurrentMana, View(_p1).Mana),
                "past the delay the record regenerates by regen x dt on the injected clock (" + View(_p1).Mana + " vs " + expected + ") and the rig shows it");

            _authority.HostEditKit(_p1.CombatId, e => e.RefillMana());
            yield return null;
        }

        // ---- 4. reload advances on the authority clock -----------------------------------------------------

        private IEnumerator ReloadOnTheAuthorityClock()
        {
            RangedCombat bow = _p2.GetComponent<RangedCombat>();
            KitView full = View(_p2);
            Check(full.Magazine == full.MagazineSize, "P2's magazine is full");
            Check(!_authority.TryStartReload(_s2, _p2.CombatId, out string reason) && Has(reason, "nothing"), "a full magazine does not reload (" + reason + ")");

            EmberdelveKitFixtures.ConsumeAmmo(bow, full.MagazineSize);
            KitView empty = View(_p2);
            Check(empty.Magazine == 0 && empty.Reserve > 0, "emptied: reserve " + empty.Reserve);

            Check(!_authority.TryStartReload(_s1, _p2.CombatId, out reason) && Has(reason, "sender") && !View(_p2).Reloading,
                "another player cannot start P2's reload (" + reason + ")");
            Check(_authority.TryStartReload(_s2, _p2.CombatId, out reason), "the owner starts the reload (" + reason + ")");
            KitView started = View(_p2);
            Check(started.Reloading && bow.IsReloading && Near(started.ReloadRemaining, started.ReloadTime, 0.02f), "reloading, timer = reload time");
            Check(!_authority.TryStartReload(_s2, _p2.CombatId, out reason), "a second start while reloading is refused");

            yield return null; yield return null;
            Check(View(_p2).Reloading && Near(View(_p2).ReloadRemaining, started.ReloadTime, 0.02f), "frames passing do not advance the reload: the authority clock did not move");

            Advance(started.ReloadTime * 0.5f);
            Check(View(_p2).Reloading && Near(bow.ReloadProgress, 0.5f, 0.03f), "half the reload time: still reloading, progress " + bow.ReloadProgress);

            Advance(started.ReloadTime * 0.5f + 0.01f);
            int taken = Mathf.Min(empty.MagazineSize, empty.Reserve);
            KitView done = View(_p2);
            Check(!done.Reloading && !bow.IsReloading && done.Magazine == taken && done.Reserve == empty.Reserve - taken
                  && bow.Magazine == done.Magazine && bow.Reserve == done.Reserve,
                "the authority finished the reload and moved the rounds (" + done.Magazine + "/" + done.Reserve + ")");
            yield return null;
        }

        // ---- 5. seeding at class apply, death, respawn, weapon and revision --------------------------------

        private IEnumerator Seeding()
        {
            // --- cooldown display equals the authority; death/respawn epochs
            EmberdelveKitFixtures.RefillMana(_p1);
            bool ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out int act, out string reason);
            Check(ok && act > 0, "P1 casts Battle Cry on the record (" + reason + ")");
            _authority.CompleteSkillCast(_s1, _p1.CombatId, act);
            Check(_authority.TryGetSkillCooldown(_p1.CombatId, _cry.skillId, out float cooldown) && Near(cooldown, _cry.EffectiveCooldown, 0.05f),
                "the authority reports the cooldown it stamped (" + cooldown + ")");
            yield return null;
            int slot = -1;
            for (int i = 0; i < 2; i++) if (_p1.Skills.ActiveSkill(i) == _cry) slot = i;
            Check(slot >= 0 && Near(_p1.Skills.CooldownRemaining(slot), cooldown, 0.05f), "the HUD-facing cooldown equals the authority's (" + (slot >= 0 ? _p1.Skills.CooldownRemaining(slot) : -1f) + ")");
            ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out act, out reason);
            Check(!ok && Has(reason, "cooldown"), "an immediate second cast is on cooldown (" + reason + ")");

            EmberdelveKitFixtures.SpendMana(_p1.Mana, 20);
            float manaBefore = View(_p1).Mana;
            int epoch0 = View(_p1).Epoch;

            _p1.Skills.HandleDeath();
            KitView dead = View(_p1);
            Check(dead.Epoch == epoch0 + 1 && dead.Frozen && !dead.ManaRegenAllowed && !_p1.Mana.RegenerationAllowed, "death: new epoch, regen suspended, rig displays it");
            Check(Near(dead.Mana, manaBefore), "death does not change the pool");
            Check(_authority.TryGetSkillCooldown(_p1.CombatId, _cry.skillId, out cooldown) && cooldown == 0f, "death cleared the cooldown (epoch)");

            _p1.Skills.HandleKitRestored();
            KitView back = View(_p1);
            Check(back.Epoch == epoch0 + 2 && !back.Frozen && back.ManaRegenAllowed && _p1.Mana.RegenerationAllowed, "respawn: another epoch, regen resumes");
            Check(Near(back.Mana, manaBefore) && back.Mana < back.MaxMana, "respawn is not a refill (" + back.Mana + "/" + back.MaxMana + ")");
            EmberdelveKitFixtures.RefillMana(_p1);
            ok = _authority.TryBeginSkillCast(_s1, _p1.CombatId, _cry.skillId, out act, out reason);
            Check(ok, "after respawn the skill can be cast again without waiting out the old cooldown (" + reason + ")");
            if (ok) _authority.CompleteSkillCast(_s1, _p1.CombatId, act);

            // --- equipment revision: clamp-only
            // Precondition: the pool must sit above the 10-point ceiling used below (Warrior: 70 - 32 cast - 30 spend left only 8,
            // which a ceiling of 10 cannot clamp). Refill through the host seam, then spend a known 30 (pool 40 of 70).
            EmberdelveKitFixtures.RefillMana(_p1);
            EmberdelveKitFixtures.SpendMana(_p1.Mana, 30);
            KitView before = View(_p1);
            _p1.RefreshEquipmentStats();
            Check(Near(View(_p1).Mana, before.Mana) && View(_p1).Epoch == before.Epoch, "a plain equipment refresh changes neither the pool nor the epoch");

            KitSeed bigger = _p1.CaptureKitSeed(KitSeedReason.Revision);
            bigger.MaxMana = before.MaxMana + 50;
            bigger.Mana = 99999f;
            Check(_authority.SeedKit(_s1, _p1.CombatId, in bigger, out reason), "a Revision seed with a higher ceiling is accepted (" + reason + ")");
            Check(View(_p1).MaxMana == before.MaxMana + 50 && Near(View(_p1).Mana, before.Mana), "the ceiling rose, the pool did not (no free refill from gear)");
            KitSeed smaller = bigger;
            smaller.MaxMana = 10;
            _authority.SeedKit(_s1, _p1.CombatId, in smaller, out reason);
            Check(View(_p1).MaxMana == 10 && Near(View(_p1).Mana, 10f), "a lower ceiling clamps the pool down");
            smaller.MaxMana = before.MaxMana;
            _authority.SeedKit(_s1, _p1.CombatId, in smaller, out reason);
            Check(View(_p1).MaxMana == before.MaxMana && Near(View(_p1).Mana, 10f), "restoring the ceiling does not pay the difference back");
            Check(Near(_p1.Mana.CurrentMana, View(_p1).Mana) && _p1.Mana.MaxMana == before.MaxMana, "the rig displays the clamped values");

            // --- weapon change: pools are kept
            ClassLoadout loadout = _p1.Loadout;
            Check(loadout.SwitchToSlot(1) && _p1.ActiveWeapon != null && _p1.ActiveWeapon.IsRanged, "P1 takes the crossbow");
            yield return null;
            RangedCombat crossbow = _p1.GetComponent<RangedCombat>();
            KitView cb = View(_p1);
            Check(cb.HasActiveAmmo && cb.ActiveWeaponId == _p1.ActiveWeapon.weaponId && cb.Magazine == cb.MagazineSize && crossbow.Magazine == cb.Magazine,
                "the crossbow pool is in the record, full, and displayed (" + cb.Magazine + ")");
            EmberdelveKitFixtures.ConsumeAmmo(crossbow, 1);
            int left = View(_p1).Magazine;
            Check(left == cb.MagazineSize - 1, "one round spent on the record");

            KitSeed forgedWeapon = _p1.CaptureKitSeed(KitSeedReason.Weapon);
            for (int i = 0; i < forgedWeapon.Pools.Length; i++) { forgedWeapon.Pools[i].Magazine = 99; forgedWeapon.Pools[i].Reserve = 99; }
            _authority.SeedKit(_s1, _p1.CombatId, in forgedWeapon, out reason);
            Check(View(_p1).Magazine == left, "a Weapon seed claiming a full magazine does not refill a pool the authority holds");

            Check(loadout.SwitchToSlot(0) && !View(_p1).HasActiveAmmo, "back to the sword: no active pool");
            yield return null;
            Check(loadout.SwitchToSlot(1), "and the crossbow again");
            yield return null;
            Check(View(_p1).Magazine == left && crossbow.Magazine == left, "swapping away and back kept the spent round (" + View(_p1).Magazine + ")");
            loadout.SwitchToSlot(0);
            yield return null;

            // --- class apply: the run-start reset
            RangedCombat bow = _p2.GetComponent<RangedCombat>();
            EmberdelveKitFixtures.SpendMana(_p2.Mana, 40);
            EmberdelveKitFixtures.ConsumeAmmo(bow, View(_p2).Magazine);
            int epochP2 = View(_p2).Epoch;
            CharacterClassData archerClass = _p2.Class;
            _p2.ApplyClass(archerClass, _director.Config);
            yield return null;
            KitView fresh = View(_p2);
            Check(fresh.Epoch > epochP2, "class apply: new kit epoch (" + epochP2 + " -> " + fresh.Epoch + ")");
            Check(fresh.Mana == fresh.MaxMana && _p2.Mana.CurrentMana == fresh.Mana, "class apply refills the pool (a run start) and the rig shows it");
            Check(fresh.Magazine == fresh.MagazineSize && bow.Magazine == fresh.Magazine && fresh.Reserve == Mathf.Min(archerClass.startingReserveAmmo, archerClass.maxReserveAmmo),
                "class apply rebuilt the ammunition pool from the class (" + fresh.Magazine + "/" + fresh.Reserve + ")");
            yield return null;
        }

        // ---- 6. only the owner may seed ---------------------------------------------------------------------

        private IEnumerator SeedOwnership()
        {
            KitView before = View(_p1);
            KitSeed seed = _p1.CaptureKitSeed(KitSeedReason.ClassApply);
            bool ok = _authority.SeedKit(_s2, _p1.CombatId, in seed, out string reason);
            Check(!ok && Has(reason, "sender") && View(_p1).Epoch == before.Epoch && Near(View(_p1).Mana, before.Mana), "P2 cannot seed P1's kit (" + reason + ")");
            ok = _authority.SeedKit(CombatSenderId.TrustedHost, _p1.CombatId, in seed, out reason);
            Check(!ok && View(_p1).Epoch == before.Epoch, "nor can the trusted host");
            ok = _authority.SeedKit(CombatSenderId.None, _p1.CombatId, in seed, out reason);
            Check(!ok && View(_p1).Epoch == before.Epoch, "nor a sender-less command");
            Check(_authority.KitCount >= 2, "the record table holds both players");
            yield return null;
        }
    }
}
