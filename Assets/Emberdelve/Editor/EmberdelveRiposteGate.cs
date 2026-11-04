using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// U3C3 gate: parry then riposte, on the REAL LocalCombatAuthority (the code the host and the dedicated server run) with an injected,
    /// hand-stepped clock and plain fake fighters. Every expectation is derived from the weapon assets: the window, the multiplier, the
    /// strike count, the strike gap and the guard's impact resist come from the sword's data, and the damage of a riposte strike is compared with a
    /// plain swing of the same pattern measured here (so the number is whatever the data produces, not a typed-in result).
    ///
    /// Run: Unity -batchmode -nographics -projectPath . -logFile Logs/u3c3_riposte.log -executeMethod Emberdelve.EditorTools.EmberdelveRiposteGate.Run
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveRiposteGate
    {
        private const string Pending = "Emberdelve.RiposteGate.Pending";

        static EmberdelveRiposteGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("Riposte_Gate").AddComponent<RiposteHarness>();
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

    public sealed class RiposteHarness : MonoBehaviour
    {
        private const string Tag = "[RiposteGate]";
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 5000f);

        private int _checks;
        private int _failures;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<Duel> _duels = new List<Duel>();
        private LocalCombatAuthority _authority;
        private float _clock = 5000f;

        private WeaponData _sword;
        private WeaponData _rapier;
        private WeaponData _maul;
        private WeaponData _shieldData;
        private WeaponData _swordNoShield;   // the sword with the off-hand shield allowance off: a shield in hand then refuses the riposte

        // ------------------------------------------------------------------------------------------------
        // fakes
        // ------------------------------------------------------------------------------------------------

        private sealed class Fighter : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider, IHitShapeProvider, IGuardBreakDefender, IGuardStateSource, IRiposteKit, IWeaponChangeSource
        {
            public int CombatId { get; set; }
            public EntityKind Kind { get; set; } = EntityKind.LocalPlayer;
            public int TeamId { get; set; }
            public Vector3 Eye;
            public float Yaw;
            public float Pitch;
            public WeaponData Weapon;
            public bool Dead;
            public bool Guard;
            public bool Shield;
            public float ShieldResist = 20f;
            public int Epoch;
            public float Taken;
            public LocalCombatAuthority Authority;
            public readonly List<DamageInfo> Received = new List<DamageInfo>();

            public Vector3 CombatCenter => Eye + Vector3.down * 0.3f;
            public Collider HeadCollider => null;
            public WeaponData ActiveWeapon => Weapon;
            public bool IsDead => Dead;
            public float CurrentHealth => 100000f - Taken;
            public float MaxHealth => 100000f;
            public bool IsGuarding => Guard && !Dead;
            public bool GuardHasShield => Shield;
            public int WeaponChangeCount => Epoch;

            public void ApplyDamage(DamageInfo damage)
            {
                Received.Add(damage);
                Taken += damage.Amount;
            }

            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = Eye;
                yawDegrees = Yaw;
                pitchDegrees = Pitch;
                return true;
            }

            public void CollectHitShapes(List<HitShape> shapes)
            {
                Vector3 c = CombatCenter;
                shapes.Add(new HitShape(HitLocation.Body, c, c, 0.3f));
            }

            public bool TryResolveGuard(in DamageInfo incoming, CombatFlowTuning tuning, out GuardResult result)
            {
                bool swinging = Authority != null && Authority.IsSwingInProgress(CombatId);
                bool staggered = Authority != null && Authority.IsGuardStaggered(CombatId);
                Vector3 toSource = incoming.SourcePosition - CombatCenter;
                toSource.y = 0f;
                if (toSource.sqrMagnitude < 1e-4f) toSource = -incoming.Direction;
                toSource.Normalize();
                Vector3 facing = Quaternion.Euler(0f, Yaw, 0f) * Vector3.forward;
                GuardInput input = new GuardInput
                {
                    Raised = Guard && !Dead && !staggered && !swinging,
                    BlockReduction = 0.5f,
                    ArcDegrees = 120f,
                    BearingDegrees = Vector3.Angle(facing, toSource),
                    HasShield = Shield,
                    GuardHeight = BladeHeight.Mid,
                    AttackHeight = incoming.AttackHeight,
                    HeightTolerance = Shield ? tuning.shieldHeightTolerance : tuning.bladeHeightTolerance,
                    ImpactPower = incoming.ImpactPower,
                    ImpactResist = Shield ? ShieldResist : (Weapon != null ? Weapon.impactResist : 0f),
                    BreakReductionScale = tuning.guardBreakReductionScale
                };
                GuardVerdict verdict = BladeGuardRules.Resolve(in input);
                result = verdict.Mitigated ? new GuardResult(verdict.Reduction, verdict.Broken) : default(GuardResult);
                return verdict.Mitigated;
            }
        }

        private sealed class Duel : IDisposable
        {
            public Fighter A;   // the attacker, at the origin facing +Z
            public Fighter B;   // the defender, 2 m ahead facing -Z
            public void Dispose()
            {
                if (A != null) CombatRegistry.Unregister(A.CombatId, A);
                if (B != null) CombatRegistry.Unregister(B.CombatId, B);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // plumbing
        // ------------------------------------------------------------------------------------------------

        private void Check(bool ok, string label)
        {
            _checks++;
            if (!ok)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL {label}");
            }
        }

        private void NearRel(float expected, float actual, string label)
        {
            Check(Mathf.Abs(expected - actual) <= Mathf.Max(1e-3f, Mathf.Abs(expected) * 2e-3f), $"{label} (expected {expected:F4}, got {actual:F4})");
        }

        private Duel NewDuel(WeaponData weaponA, WeaponData weaponB, bool aIsEnemy)
        {
            foreach (Duel stale in _duels) stale.Dispose();
            _duels.Clear();
            Duel d = new Duel();
            d.A = new Fighter
            {
                CombatId = CombatRegistry.AllocateId(),
                Weapon = weaponA,
                Eye = Origin + new Vector3(0f, 1.8f, 0f),
                Yaw = 0f,
                Kind = aIsEnemy ? EntityKind.Enemy : EntityKind.LocalPlayer,
                TeamId = aIsEnemy ? CombatTeams.MonsterTeam : CombatTeams.TeamForParty(2),
                Authority = _authority
            };
            d.B = new Fighter
            {
                CombatId = CombatRegistry.AllocateId(),
                Weapon = weaponB,
                Eye = Origin + new Vector3(0f, 1.8f, 2f),
                Yaw = 180f,
                Kind = EntityKind.LocalPlayer,
                TeamId = CombatTeams.PlayerTeam,
                Authority = _authority
            };
            CombatRegistry.Register(d.A.CombatId, d.A);
            CombatRegistry.Register(d.B.CombatId, d.B);
            _duels.Add(d);
            return d;
        }

        private void Advance(float seconds)
        {
            float end = _clock + seconds;
            while (_clock < end - 1e-6f)
            {
                _clock += Mathf.Min(0.01f, end - _clock);
                _authority.StepSwings();
            }
        }

        private static CombatSenderId Sender(Fighter f) => EmberdelveKitFixtures.SenderFor(f.CombatId);

        private MeleeSwingIntent Intent(Fighter f, int pattern = 0)
        {
            return new MeleeSwingIntent(f.CombatId, f.Weapon.weaponId, pattern, 0, f.Yaw, f.Pitch, CombatRegistry.NextSequence(f.CombatId));
        }

        private bool Swing(Fighter f, int pattern, out int id)
        {
            return _authority.TryBeginMeleeSwingAsOwner(Intent(f, pattern), out id, out _);
        }

        private MeleeSwingStatus RunToEnd(int id)
        {
            MeleeSwingStatus status = default;
            for (int i = 0; i < 600; i++)
            {
                _authority.TryGetMeleeSwingStatus(id, out status);
                if (status.Finished) break;
                Advance(0.01f);
            }

            _authority.TryGetMeleeSwingStatus(id, out status);
            return status;
        }

        private MeleeSwingHit HitOn(MeleeSwingStatus status, Fighter f)
        {
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == f.CombatId) return status.Hits[i];
            return default;
        }

        private int CountOn(MeleeSwingStatus status, Fighter f)
        {
            int n = 0;
            for (int i = 0; i < status.Hits.Count; i++) if (status.Hits[i].TargetCombatId == f.CombatId) n++;
            return n;
        }

        /// <summary>A plain, unguarded, chain-step-0 swing of one pattern by B at A: the number the data produces for that pattern.</summary>
        private float BaselineAmount(WeaponData weapon, int pattern)
        {
            Duel d = NewDuel(weapon, weapon, true);
            Check(Swing(d.B, pattern, out int id), $"baseline swing of pattern {pattern} accepted");
            MeleeSwingStatus status = RunToEnd(id);
            Check(CountOn(status, d.A) == 1, $"baseline pattern {pattern} hits once (hits={status.Hits.Count}, finished={status.Finished})");
            float amt = HitOn(status, d.A).Amount;
            Advance(5f);
            return amt;
        }

        /// <summary>A swings pattern 0 at the guarding B; returns the swing status after it ended.</summary>
        private MeleeSwingStatus BlockedHit(Duel d)
        {
            d.B.Guard = true;
            d.A.Guard = false;
            Check(Swing(d.A, 0, out int id), "attacker swing accepted");
            return RunToEnd(id);
        }

        private bool BeginRiposte(Duel d, out int swingId, out int strikes)
        {
            d.B.Guard = false;
            return ((IRiposteAuthority)_authority).TryBeginRiposte(Sender(d.B), Intent(d.B), out swingId, out strikes, out _);
        }

        private RiposteResult ContinueRiposte(Duel d, out int swingId)
        {
            return ((IRiposteAuthority)_authority).TryContinueRiposte(Sender(d.B), Intent(d.B), out swingId, out _);
        }

        // ------------------------------------------------------------------------------------------------
        // run
        // ------------------------------------------------------------------------------------------------

        private IEnumerator Start()
        {
            yield return null;
            yield return null;

            Section("setup and data", SetupAndData);
            Section("parry recorded, riposte of two strikes (enemy attacker)", () => HappyPath(true));
            Section("parry recorded, riposte of two strikes (player attacker, PvP)", () => HappyPath(false));
            Section("the window", Window);
            Section("forged and unauthorised requests", Forged);
            Section("a hit between the block and the press", HitBetween);
            Section("shield and non-sword guards never parry", NoParryKits);
            Section("the riposte does not break a holding guard", HoldingGuard);
            Section("action locks and kit changes", Locks);
            Section("the follow-up strike", FollowUp);

            foreach (Duel d in _duels) d.Dispose();
            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            _authority.SetClock(null);

            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private void Section(string name, Action body)
        {
            int checksBefore = _checks;
            int failuresBefore = _failures;
            try { body(); }
            catch (Exception e)
            {
                _failures++;
                Debug.LogError($"{Tag} FAIL section '{name}' threw");
                Debug.LogException(e);
            }

            Debug.Log($"{Tag} section '{name}': {_checks - checksBefore} checks, {_failures - failuresBefore} failures");
        }

        private void SetupAndData()
        {
            _sword = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_AshenShortSword.asset");
            _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            _maul = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_WardenMaul.asset");
            _shieldData = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberbulwarkShield.asset");
            Check(_sword != null && _rapier != null && _maul != null && _shieldData != null, "weapon assets are available");
            if (_sword == null || _rapier == null || _maul == null || _shieldData == null) throw new InvalidOperationException("weapon assets missing");

            GameConfig config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(config);
            config.levelMask = LayerMask.GetMask(GameConfig.LevelLayerName);
            config.hitLocations = null;

            GameObject go = new GameObject("RiposteGate_Authority");
            _cleanup.Add(go);
            _authority = go.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, config);
            _authority.SetClock(() => _clock);
            _authority.AutoStepSwings = false;
            // the same fixture opt-ins as the swept-melee gate: a pattern per case at step 0, no impact-zone bands (the contact point
            // is the same for a plain swing and a riposte strike here, but the numbers then do not depend on it at all)
            _authority.AllowLegacyPatternSelection = true;
            _authority.ImpactZonesEnabled = false;

            Check(_sword.CanRiposteWeapon && _rapier.CanRiposteWeapon, "the sword and the rapier are flagged in data");
            Check(!_maul.CanRiposteWeapon && !_shieldData.CanRiposteWeapon, "the maul and the shield are not");
            Check(_sword.riposteWithOffhandShield && !_rapier.riposteWithOffhandShield, "the sword (the warrior's, with a shield) is allowed a shield in the off hand in data");
            _swordNoShield = UnityEngine.Object.Instantiate(_sword);
            _swordNoShield.riposteWithOffhandShield = false;
            _cleanup.Add(_swordNoShield);
            Check(_sword.RiposteStrikeCount == 2, "the sword's riposte is two strikes (data)");
            Check(_sword.riposteWindow > 0f && _sword.riposteDamageMultiplier > 1f && _sword.riposteStrikeGap >= 0f, "window, multiplier and gap are authored");
            Check(_sword.impactResist > _sword.impactPower * Mathf.Max(_sword.swingPatterns[0].impactPowerScale, _sword.swingPatterns[1].impactPowerScale),
                "data fact: a sword guard resists a sword's riposte strikes (resist above their impact power)");
        }

        // ------------------------------------------------------------------------------------------------

        private void HappyPath(bool aIsEnemy)
        {
            float mult = RiposteRules.StrikeMultiplier(_sword.riposteDamageMultiplier);
            float base0 = BaselineAmount(_sword, 0);
            float base1 = BaselineAmount(_sword, 1);
            Check(base0 > 0f && base1 > 0f, $"baselines {base0:F3} / {base1:F3}");
            Advance(5f);

            Duel d = NewDuel(_sword, _sword, aIsEnemy);
            MeleeSwingStatus blocked = BlockedHit(d);
            MeleeSwingHit onB = HitOn(blocked, d.B);
            Check(CountOn(blocked, d.B) == 1 && onB.Blocked && !onB.GuardBroken, "the sword block held (blocked, guard not broken)");
            Check(onB.Amount == 0f && d.B.Taken == 0f, "a successful block took ZERO HP (reported amount and health lost)");
            Check(_authority.Riposte.HasLiveParry(d.B.CombatId), "the server recorded a parry for the defender");
            Check(_authority.Riposte.ParriedAttacker(d.B.CombatId) == d.A.CombatId, "... against the attacker");
            Check(!_authority.Riposte.HasLiveParry(d.A.CombatId), "the attacker has no parry");
            float elapsed = _clock - _authority.Riposte.BlockTimeOf(d.B.CombatId);
            Check(elapsed < _sword.riposteWindow, $"the swing ended inside the window ({elapsed:F2} s of {_sword.riposteWindow:F2} s)");

            int swingsBefore = _authority.SwingsStarted;
            Check(BeginRiposte(d, out int id1, out int strikes), "press inside the window: riposte accepted");
            Check(strikes == _sword.RiposteStrikeCount, "the strike count is the data's");
            _authority.TryGetMeleeSwingStatus(id1, out MeleeSwingStatus s1);
            Check(s1.RiposteStrike == 1, "the first swing is riposte strike 1");
            Check(s1.StepMultiplier > 0f && Mathf.Approximately(s1.StepMultiplier, mult), $"its step multiplier is the data's x{mult}");
            s1 = RunToEnd(id1);
            Check(CountOn(s1, d.A) == 1, "strike 1 hit the attacker");
            MeleeSwingHit h1 = HitOn(s1, d.A);
            NearRel(base0 * mult, h1.Amount, "strike 1 damage = the plain swing of its pattern x the multiplier");
            Check(!h1.Blocked, "the unguarded attacker took it in full");

            int notYet = 0;
            RiposteResult result = RiposteResult.NotYet;
            int id2 = 0;
            float strike1End = _clock;
            for (int i = 0; i < 300; i++)
            {
                result = ContinueRiposte(d, out id2);
                if (result != RiposteResult.NotYet) break;
                notYet++;
                Advance(0.01f);
            }

            Check(result == RiposteResult.Started, "the second strike started");
            Check(_sword.riposteStrikeGap <= 0f || notYet > 0, "... but only after the data's gap");
            Check(_clock - strike1End >= _sword.riposteStrikeGap - 0.011f, $"the gap held ({_clock - strike1End:F3} s of {_sword.riposteStrikeGap:F3} s)");
            _authority.TryGetMeleeSwingStatus(id2, out MeleeSwingStatus s2);
            Check(s2.RiposteStrike == 2, "the second swing is riposte strike 2");
            s2 = RunToEnd(id2);
            Check(CountOn(s2, d.A) == 1, "strike 2 hit the attacker");
            NearRel(base1 * mult, HitOn(s2, d.A).Amount, "strike 2 damage = the plain swing of its pattern x the multiplier");
            Check(d.A.Received.Count == 2, "the attacker took exactly the two strikes");
            NearRel(h1.Amount + HitOn(s2, d.A).Amount, d.A.Taken, "the health it lost is the two reported amounts");
            Check(_authority.SwingsStarted == swingsBefore + 2, "exactly two swings were started by the riposte");

            Check(ContinueRiposte(d, out _) == RiposteResult.Refused, "no third strike");
            Check(!BeginRiposte(d, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.AlreadyUsed, "a second press on the same parry is refused (AlreadyUsed)");
            Check(_authority.SwingsStarted == swingsBefore + 2, "... and starts nothing");
            Check(_authority.Riposte.RipostesStarted >= 1, "the tracker counted the riposte");
        }

        private void Window()
        {
            float window = _sword.riposteWindow;

            Duel late = NewDuel(_sword, _sword, true);
            BlockedHit(late);
            float elapsed = _clock - _authority.Riposte.BlockTimeOf(late.B.CombatId);
            Advance(window - elapsed + 0.05f);
            int swings = _authority.SwingsStarted;
            Check(!BeginRiposte(late, out _, out _), "a press after the window is refused");
            Check(_authority.LastRiposteVerdict == RiposteVerdict.WindowClosed, $"... because the window closed ({_authority.LastRiposteVerdict})");
            Check(_authority.SwingsStarted == swings, "... and nothing was swung");
            Check(late.A.Received.Count == 0, "... and the attacker took nothing");

            Advance(5f);
            Duel inTime = NewDuel(_sword, _sword, true);
            BlockedHit(inTime);
            elapsed = _clock - _authority.Riposte.BlockTimeOf(inTime.B.CombatId);
            Advance(Mathf.Max(0f, window - elapsed - 0.05f));
            Check(BeginRiposte(inTime, out _, out _), "a press just inside the window is accepted");
        }

        private void Forged()
        {
            Advance(5f);
            Duel d = NewDuel(_sword, _sword, true);
            IRiposteAuthority ra = _authority;
            int swings = _authority.SwingsStarted;
            int refusedBefore = _authority.Riposte.Refused;
            int senderRefusals = _authority.SenderRefusalCount;

            Check(!BeginRiposte(d, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.NoParry, "a riposte request with no parry on record is refused (NoParry)");
            Check(_authority.Riposte.Refused > refusedBefore, "... and counted");
            Check(ContinueRiposte(d, out _) == RiposteResult.Refused && _authority.LastRiposteVerdict == RiposteVerdict.NoGrant, "a follow-up strike with no riposte running is refused (NoGrant)");

            // a parry exists, but the request comes from a sender that does not own the entity
            BlockedHit(d);
            d.B.Guard = false;
            Check(!ra.TryBeginRiposte(CombatSenderId.None, Intent(d.B), out _, out _, out string r1) && r1.Length > 0, "a request with no sender identity is refused");
            Check(!ra.TryBeginRiposte(CombatSenderId.ForPlayer(4242), Intent(d.B), out _, out _, out _), "a request from another player's session is refused");
            Check(!ra.TryBeginRiposte(CombatSenderId.ForPlayer(4242), Intent(d.A), out _, out _, out _), "... also for the attacker's entity");
            Check(_authority.SenderRefusalCount >= senderRefusals + 3, "the sender refusals were counted");
            Check(_authority.Riposte.HasLiveParry(d.B.CombatId), "the refused forgeries left the parry alone");
            Check(_authority.SwingsStarted == swings + 1, "no riposte swing was started by any forgery (only the attacker's own swing)");

            // the attacker cannot riposte on the defender's parry
            Check(!ra.TryBeginRiposte(Sender(d.A), Intent(d.A), out _, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.NoParry, "the attacker has no parry of its own");

            // the real owner still can
            Check(BeginRiposte(d, out _, out _), "the defender's own request is accepted afterwards");
        }

        private void HitBetween()
        {
            Advance(5f);
            WeaponData longWindow = UnityEngine.Object.Instantiate(_sword);
            longWindow.riposteWindow = 3f;
            _cleanup.Add(longWindow);

            Duel d = NewDuel(_sword, longWindow, true);
            BlockedHit(d);
            Check(_authority.Riposte.HasLiveParry(d.B.CombatId), "parry recorded");
            d.B.Guard = false;
            Advance(0.4f);
            Check(Swing(d.A, 0, out int again), "the attacker swings again");
            MeleeSwingStatus second = RunToEnd(again);
            Check(CountOn(second, d.B) == 1 && !HitOn(second, d.B).Blocked, "the unguarded defender takes the second hit in full");
            Check(!_authority.Riposte.HasLiveParry(d.B.CombatId), "the damage cancelled the parry");
            float elapsed = _clock - _authority.Riposte.BlockTimeOf(d.B.CombatId);
            Check(elapsed < longWindow.riposteWindow, "it is still inside the window, so only the hit can be the reason");
            Check(!BeginRiposte(d, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.HitSinceBlock, "the riposte is refused (HitSinceBlock)");
        }

        private void NoParryKits()
        {
            Advance(5f);
            Duel shield = NewDuel(_sword, _swordNoShield, true);
            shield.B.Shield = true;
            int blocksBefore = _authority.Riposte.BlocksRecorded;
            MeleeSwingStatus status = BlockedHit(shield);
            Check(CountOn(status, shield.B) == 1 && HitOn(status, shield.B).Blocked, "the shield block held");
            Check(_authority.Riposte.BlocksRecorded == blocksBefore && !_authority.Riposte.HasLiveParry(shield.B.CombatId), "a shield block records no parry (shields never parry)");
            Check(!BeginRiposte(shield, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.NoParry, "a riposte on a shield block is refused");

            Advance(5f);
            Duel maul = NewDuel(_sword, _maul, true);
            BlockedHit(maul);
            Check(!_authority.Riposte.HasLiveParry(maul.B.CombatId), "a guard held with a weapon that is not a sword records no parry");

            // a shield picked up AFTER a sword block: refused by the kit rule
            Advance(5f);
            Duel swapped = NewDuel(_sword, _swordNoShield, true);
            BlockedHit(swapped);
            swapped.B.Shield = true;
            Check(!BeginRiposte(swapped, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.KitCannotRiposte, "a shield contributing to the guard at the press refuses the riposte");

            // the warrior's sword and shield: the sword's data allows the shield, the block parries, the block took 0 HP
            Advance(5f);
            Duel both = NewDuel(_sword, _sword, true);
            both.B.Shield = true;
            int blocks = _authority.Riposte.BlocksRecorded;
            MeleeSwingStatus bothStatus = BlockedHit(both);
            Check(CountOn(bothStatus, both.B) == 1 && HitOn(bothStatus, both.B).Blocked && both.B.Taken == 0f, "sword and shield: the block held and took 0 HP");
            Check(_authority.Riposte.BlocksRecorded == blocks + 1 && _authority.Riposte.HasLiveParry(both.B.CombatId), "sword and shield: the sword's block is a parry (data allows the shield)");
            Check(BeginRiposte(both, out _, out int bothStrikes) && bothStrikes == _sword.RiposteStrikeCount, "sword and shield: the riposte is accepted");
        }

        private void HoldingGuard()
        {
            float mult = RiposteRules.StrikeMultiplier(_sword.riposteDamageMultiplier);
            float base0 = BaselineAmount(_sword, 0);
            float base1 = BaselineAmount(_sword, 1);
            Advance(5f);

            Duel d = NewDuel(_sword, _sword, false);
            BlockedHit(d);
            Check(BeginRiposte(d, out int id1, out _), "riposte accepted");
            d.A.Guard = true;   // the attacker raises its sword guard (it is not swinging any more)
            int breaksBefore = _authority.GuardBreaksApplied;
            int blocksOnA = _authority.Riposte.BlocksRecorded;

            MeleeSwingStatus s1 = RunToEnd(id1);
            MeleeSwingHit h1 = HitOn(s1, d.A);
            Check(CountOn(s1, d.A) == 1 && h1.Blocked && !h1.GuardBroken, "strike 1 is resolved like a blocked hit: the guard holds");
            NearRel(base0 * mult * (1f - BladeGuardRules.HeldGuardReduction), h1.Amount, "... the held guard takes all of it (0 HP), whatever the multiplier");

            RiposteResult r = RiposteResult.NotYet;
            int id2 = 0;
            for (int i = 0; i < 300 && r == RiposteResult.NotYet; i++)
            {
                r = ContinueRiposte(d, out id2);
                if (r == RiposteResult.NotYet) Advance(0.01f);
            }

            Check(r == RiposteResult.Started, "strike 2 started against the holding guard");
            MeleeSwingStatus s2 = RunToEnd(id2);
            MeleeSwingHit h2 = HitOn(s2, d.A);
            Check(CountOn(s2, d.A) == 1 && h2.Blocked && !h2.GuardBroken, "strike 2: the guard holds");
            NearRel(base1 * mult * (1f - BladeGuardRules.HeldGuardReduction), h2.Amount, "strike 2 into the holding guard: 0 HP");
            Check(_authority.GuardBreaksApplied == breaksBefore, "no guard break was applied");
            Check(!_authority.IsGuardStaggered(d.A.CombatId), "the attacker's guard is not staggered");
            Check(_authority.Riposte.BlocksRecorded > blocksOnA && _authority.Riposte.HasLiveParry(d.A.CombatId), "the attacker's own block of a riposte strike is a parry of its own");
        }

        private void Locks()
        {
            Advance(5f);
            Duel dead = NewDuel(_sword, _sword, true);
            BlockedHit(dead);
            dead.B.Dead = true;
            Check(!BeginRiposte(dead, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.Dead, "a dead defender cannot riposte");

            Advance(5f);
            Duel busy = NewDuel(_sword, _sword, true);
            BlockedHit(busy);
            busy.B.Guard = false;
            Check(Swing(busy.B, 0, out _), "the defender starts a swing of its own");
            Check(!BeginRiposte(busy, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.Busy, "a swing in progress locks the riposte (Busy)");

            Advance(5f);
            Duel swap = NewDuel(_sword, _sword, true);
            BlockedHit(swap);
            swap.B.Epoch++;
            Check(!BeginRiposte(swap, out _, out _) && _authority.LastRiposteVerdict == RiposteVerdict.WeaponChanged, "a weapon change since the block refuses the riposte");

            Advance(5f);
            Duel guarding = NewDuel(_sword, _sword, true);
            BlockedHit(guarding);
            guarding.B.Guard = true;
            int swings = _authority.SwingsStarted;
            Check(!((IRiposteAuthority)_authority).TryBeginRiposte(Sender(guarding.B), Intent(guarding.B), out _, out _, out _), "a begin while the guard is still up is refused by the swing rule");
            Check(_authority.SwingsStarted == swings, "... starting nothing");
            Check(_authority.Riposte.HasLiveParry(guarding.B.CombatId), "... and the parry survives a swing the authority refused");
        }

        private void FollowUp()
        {
            // too late for the second strike
            Advance(5f);
            Duel late = NewDuel(_sword, _sword, true);
            BlockedHit(late);
            Check(BeginRiposte(late, out int id1, out _), "riposte accepted");
            RunToEnd(id1);
            Advance(_sword.riposteStrikeGap + RiposteTracker.ContinueGraceSeconds + 0.1f);
            int swings = _authority.SwingsStarted;
            Check(ContinueRiposte(late, out _) == RiposteResult.Refused && _authority.LastRiposteVerdict == RiposteVerdict.GrantExpired, "a follow-up after the grace is refused (GrantExpired)");
            Check(_authority.SwingsStarted == swings, "... and swings nothing");

            // a hit on the defender between the strikes interrupts the riposte
            Advance(5f);
            Duel hit = NewDuel(_sword, _sword, true);
            BlockedHit(hit);
            Check(BeginRiposte(hit, out int h1, out _), "riposte accepted");
            hit.A.Guard = false;
            Advance(0.3f);
            Check(Swing(hit.A, 0, out int counter), "the attacker swings back while strike 1 runs");
            RunToEnd(h1);
            RunToEnd(counter);
            Check(hit.B.Received.Count >= 2, "the defender took the counter-hit (after the blocked one)");
            Check(ContinueRiposte(hit, out _) == RiposteResult.Refused && _authority.LastRiposteVerdict == RiposteVerdict.NoGrant, "the damage interrupted the riposte: no second strike (NoGrant)");

            // an unrelated entity cannot ride on a riposte it does not have
            Advance(5f);
            Duel other = NewDuel(_sword, _sword, true);
            Check(ContinueRiposte(other, out _) == RiposteResult.Refused, "a follow-up for an entity that never started a riposte is refused");
        }
    }
}
