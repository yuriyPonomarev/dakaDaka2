using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Player;
using Emberdelve.Skills;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Checks authority-owned, time-of-flight arrows against live blade geometry. Run with
    /// -executeMethod Emberdelve.EditorTools.EmberdelveProjectileBlockGate.Run.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveProjectileBlockGate
    {
        private const string Pending = "Emberdelve.ProjectileBlockGate.Pending";
        private const string CapturePending = "Emberdelve.ProjectileBlockGate.CapturePending";

        static EmberdelveProjectileBlockGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode) return;
                bool capture = SessionState.GetBool(CapturePending, false);
                if (!capture && !SessionState.GetBool(Pending, false)) return;
                SessionState.SetBool(Pending, false);
                SessionState.SetBool(CapturePending, false);
                ProjectileBlockHarness harness = new GameObject(capture ? "ProjectileBlock_LiveCapture" : "ProjectileBlock_Gate")
                    .AddComponent<ProjectileBlockHarness>();
                harness.CaptureMode = capture;
            };
        }

        public static void Run()
        {
            SessionState.SetBool(CapturePending, false);
            SessionState.SetBool(Pending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }

        /// <summary>Renders a live authority-driven guard interception in the actual game view to Logs/.</summary>
        public static void RunCapture()
        {
            SessionState.SetBool(Pending, false);
            SessionState.SetBool(CapturePending, true);
            EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");
            EditorApplication.EnterPlaymode();
        }
    }

    public sealed class ProjectileBlockHarness : MonoBehaviour
    {
        private const string Tag = "[ProjectileBlockGate]";
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<Shooter> _shooters = new List<Shooter>();
        private int _checks;
        private int _failures;
        private float _clock = 200f;
        private PlayerRig _defender;
        private LocalCombatAuthority _authority;
        private WeaponData _sword;
        private WeaponData _rapier;
        private WeaponData _bow;
        private WeaponData _crossbow;
        public bool CaptureMode;
        private WeaponData _originalOffhand;

        private sealed class Shooter : ICombatEntity, IActiveWeaponProvider, IAimPoseProvider
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public WeaponData ActiveWeapon { get; set; }
            public Vector3 AimOrigin { get; set; }
            public Vector3 AimDirection { get; set; }
            public Vector3 CombatCenter => AimOrigin;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public bool TryGetAimPose(out Vector3 eye, out float yawDegrees, out float pitchDegrees)
            {
                eye = AimOrigin;
                return SwingGeometry.TryAimAngles(AimDirection, out yawDegrees, out pitchDegrees);
            }
            public void ApplyDamage(DamageInfo damage) { }
        }

        private sealed class AimlessShooter : ICombatEntity, IActiveWeaponProvider
        {
            public int CombatId { get; set; }
            public WeaponData ActiveWeapon { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter { get; set; }
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100f;
            public float MaxHealth => 100f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        private sealed class IgnoredProjectileCollider : MonoBehaviour, ICombatEntity
        {
            public int CombatId { get; set; }
            public EntityKind Kind => EntityKind.Enemy;
            public int TeamId => CombatTeams.MonsterTeam;
            public Vector3 CombatCenter => transform.position;
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 1f;
            public float MaxHealth => 1f;
            public void ApplyDamage(DamageInfo damage) { }
        }

        private void Check(bool condition, string message)
        {
            _checks++;
            if (condition) return;
            _failures++;
            Debug.LogError($"{Tag} FAIL {message}");
        }

        private IEnumerator Start()
        {
            yield return null;
            yield return null;
            if (CaptureMode)
            {
                yield return CaptureDiagnostic();
                yield break;
            }
            try { Execute(); }
            catch (Exception exception) { _failures++; Debug.LogException(exception); }
            finally
            {
                foreach (Shooter shooter in _shooters) CombatRegistry.Unregister(shooter.CombatId, shooter);
                foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
            }
            Debug.Log($"{Tag} {(_failures == 0 ? "PASS" : "FAIL")} checks={_checks} failures={_failures}");
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private IEnumerator CaptureDiagnostic()
        {
            bool success = false;
            bool restoreSecondaryOverride = false;
            bool? previousSecondaryOverride = null;
            string capturePath = null;
            try
            {
                _defender = FindFirstObjectByType<PlayerRig>();
                _sword = _defender != null ? _defender.MainHandWeapon : null;
                _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
                _bow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset");
                Camera liveCamera = Camera.main;
                if (_defender == null || _sword == null || _rapier == null || _bow == null || liveCamera == null)
                {
                    Debug.LogError($"{Tag} live capture setup missing player, sword, bow, rapier or MainCamera");
                }
                else
                {
                    _defender.Loadout.ForceSwitchToSlot(0);
                    _defender.SetOffhandWeapon(_rapier);
                    _defender.Loadout.RebuildWeaponVisual(_sword);
                    if (_defender.Input != null)
                    {
                        previousSecondaryOverride = _defender.Input.SecondaryHeldOverride;
                        restoreSecondaryOverride = true;
                        _defender.Input.SecondaryHeldOverride = true;
                    }
                    _defender.SetGuardHeld(true);

                    GameConfig config = ScriptableObject.CreateInstance<GameConfig>();
                    _cleanup.Add(config);
                    config.hitMask = 0;
                    config.levelMask = 0;
                    GameObject authorityObject = new GameObject("ProjectileBlockCapture_Authority");
                    _cleanup.Add(authorityObject);
                    _authority = authorityObject.AddComponent<LocalCombatAuthority>();
                    typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, config);
                    _authority.SetClock(() => _clock);
                    _authority.SimulateBallistics = false; // D2: this gate's literals assert straight-line geometry and the legacy range falloff; the ballistic sim has its own gate (EmberdelveProjectileSimGate).

                    WeaponData slowDiagnosticBow = Instantiate(_bow);
                    slowDiagnosticBow.name = "ProjectileBlock_LiveDiagnosticBow";
                    slowDiagnosticBow.projectileSpeed = 2f;
                    _cleanup.Add(slowDiagnosticBow);

                    if (!_defender.TryGetProjectileGuardBlade(out GuardBladeBox blade))
                    {
                        Debug.LogError($"{Tag} live capture could not resolve the equipped sword blade pose");
                    }
                    else if (!_defender.IsGuarding)
                    {
                        Debug.LogError($"{Tag} live capture input did not maintain guard");
                    }
                    else
                    {
                        Vector3 shooterOrigin = blade.Center - _defender.transform.right * 1.8f;
                        Vector3 direction = (blade.Center - shooterOrigin).normalized;
                        Shooter shooter = MakeShooter(shooterOrigin, direction, slowDiagnosticBow);
                        if (!Launch(shooter, 1))
                        {
                            Debug.LogError($"{Tag} live capture authority launch was rejected: {_authority.LastAttackRejectReason}");
                        }
                        else
                        {
                            int launchFrame = Time.frameCount;
                            float launchTime = Time.realtimeSinceStartup;
                            ProjectileVisual diagnosticArrow = FindFirstObjectByType<ProjectileVisual>();
                            Vector3 launchTip = diagnosticArrow != null ? diagnosticArrow.TipPosition : shooterOrigin;
                            Debug.Log($"{Tag} live arrow launched frame={launchFrame} time={launchTime:F3} tip={launchTip}");

                            bool inCaptureWindow = false;
                            float bladeGap = float.PositiveInfinity;
                            float approachDeadline = Time.realtimeSinceStartup + 3f;
                            GuardBladeBox currentBlade = blade;
                            while (Time.realtimeSinceStartup < approachDeadline)
                            {
                                yield return null;
                                diagnosticArrow = FindFirstObjectByType<ProjectileVisual>();
                                if (diagnosticArrow == null || !_defender.IsGuarding
                                    || !_defender.TryGetProjectileGuardBlade(out currentBlade)) continue;
                                bladeGap = DistanceToGuardBlade(diagnosticArrow.TipPosition, currentBlade);
                                if (bladeGap >= 0.35f && bladeGap <= 0.6f)
                                {
                                    inCaptureWindow = true;
                                    break;
                                }
                            }

                            if (!inCaptureWindow || diagnosticArrow == null)
                            {
                                Debug.LogError($"{Tag} live arrow did not remain in flight at the 0.35-0.60 m blade approach window; lastGap={bladeGap:F3}");
                            }
                            else if (_defender.Loadout.Visual == null
                                     || !_defender.Loadout.Visual.TryGetGuardBladeBox(out GuardBladeBox visibleBlade)
                                     || (visibleBlade.Center - currentBlade.Center).sqrMagnitude >= 0.0004f
                                     || Quaternion.Angle(visibleBlade.Rotation, currentBlade.Rotation) >= 0.2f)
                            {
                                Debug.LogError($"{Tag} live capture guard is not maintained or visible blade differs from authority pose");
                            }
                            else
                            {
                                float captureTime = Time.realtimeSinceStartup;
                                Vector3 captureTip = diagnosticArrow.TipPosition;
                                Debug.Log($"{Tag} live arrow capture frame={Time.frameCount} time={captureTime:F3} tip={captureTip} " +
                                          $"bladeGap={bladeGap:F3} guarding={_defender.IsGuarding} framesAdvanced={Time.frameCount - launchFrame}");
                                string logDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Logs"));
                                Directory.CreateDirectory(logDirectory);
                                capturePath = Path.Combine(logDirectory, "ProjectileBlock_LiveDiagnostic.png");
                                if (File.Exists(capturePath)) File.Delete(capturePath);
                                ScreenCapture.CaptureScreenshot(capturePath);
                                float fileDeadline = Time.realtimeSinceStartup + 3f;
                                while ((!File.Exists(capturePath) || new FileInfo(capturePath).Length == 0)
                                       && Time.realtimeSinceStartup < fileDeadline)
                                    yield return null;
                                success = File.Exists(capturePath) && new FileInfo(capturePath).Length > 0;
                                if (success) Debug.Log($"{Tag} live diagnostic screenshot saved: {capturePath}");
                                else Debug.LogError($"{Tag} live screenshot capture did not produce a nonempty PNG before timeout: {capturePath}");
                            }
                        }
                    }
                }
            }
            finally
            {
                if (restoreSecondaryOverride && _defender != null && _defender.Input != null)
                    _defender.Input.SecondaryHeldOverride = previousSecondaryOverride;
                foreach (Shooter shooter in _shooters) CombatRegistry.Unregister(shooter.CombatId, shooter);
                foreach (UnityEngine.Object item in _cleanup) if (item != null) Destroy(item);
                if (_defender != null) _defender.SetGuardHeld(false);
            }
            EmberdelveGateExit.Exit(success ? 0 : 1);
        }

        private static float DistanceToGuardBlade(Vector3 point, GuardBladeBox blade)
        {
            Vector3 local = Quaternion.Inverse(blade.Rotation) * (point - blade.Center);
            Vector3 closest = new Vector3(Mathf.Clamp(local.x, -blade.HalfExtents.x, blade.HalfExtents.x),
                Mathf.Clamp(local.y, -blade.HalfExtents.y, blade.HalfExtents.y),
                Mathf.Clamp(local.z, -blade.HalfExtents.z, blade.HalfExtents.z));
            return Vector3.Distance(local, closest);
        }

        private void Execute()
        {
            _defender = FindFirstObjectByType<PlayerRig>();
            _sword = _defender != null ? _defender.MainHandWeapon : null;
            _rapier = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberRapier.asset");
            _bow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_HollowBow.asset");
            _crossbow = AssetDatabase.LoadAssetAtPath<WeaponData>(EmberdelvePaths.Data + "/Weapon_EmberCrossbow.asset");
            Check(_defender != null && _sword != null && _rapier != null && _bow != null && _crossbow != null, "player and projectile weapons are available");
            if (_defender == null || _sword == null || _rapier == null || _bow == null || _crossbow == null) return;
            _defender.Loadout.ForceSwitchToSlot(0);
            Check(_defender.ActiveWeapon == _sword && _sword.CanBlock, "the defender has a guard-capable sword");
            _originalOffhand = _defender.OffhandWeapon;
            _defender.SetOffhandWeapon(_rapier); // replace the Warrior's shield so blade contact can be tested alone
            Check(!_defender.Defense.HasShield, "real offhand API removes the contributing shield for blade tests");

            GameConfig config = ScriptableObject.CreateInstance<GameConfig>();
            _cleanup.Add(config);
            config.hitMask = 0;
            config.levelMask = 0;
            GameObject authorityObject = new GameObject("ProjectileBlockGate_Authority");
            _cleanup.Add(authorityObject);
            _authority = authorityObject.AddComponent<LocalCombatAuthority>();
            typeof(LocalCombatAuthority).GetField("config", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(_authority, config);
            _authority.SetClock(() => _clock);
            _authority.SimulateBallistics = false; // D2: this gate's literals assert straight-line geometry and the legacy range falloff; the ballistic sim has its own gate (EmberdelveProjectileSimGate).

            AimlessShooter forgedOrigin = new AimlessShooter
            {
                CombatId = CombatRegistry.AllocateId(), ActiveWeapon = _bow,
                CombatCenter = _defender.transform.position - _defender.transform.forward * 20f
            };
            CombatRegistry.Register(forgedOrigin.CombatId, forgedOrigin);
            AttackIntent forgedLaunch = new AttackIntent(forgedOrigin.CombatId, forgedOrigin.Kind, _bow, 1f,
                _defender.GetMuzzlePosition(), _defender.AimDirection, _bow.rangedRange, 1, _clock);
            Check(!_authority.LaunchProjectileAsOwner(forgedLaunch),
                "an armed entity without authoritative aim cannot forge its muzzle origin to launch an arrow");
            CombatRegistry.Unregister(forgedOrigin.CombatId, forgedOrigin);

            // Armor must preserve the impact provenance used by the defender's blade-block decision.
            DamageInfo armorProbe = new DamageInfo(10f, Vector3.zero, Vector3.forward, Vector3.back, 1,
                EntityKind.Enemy, DamageType.Pierce, true, false, 0.5f, impactPower: 4f,
                guardBroken: true, rangedProjectile: true, guardBladeContact: true);
            DamageInfo armoredProbe = DamageResolver.ApplyArmorRating(armorProbe, 50f);
            Check(armoredProbe.RangedProjectile && armoredProbe.GuardBladeContact && armoredProbe.GuardBroken,
                "armor mitigation preserves projectile blade-contact and guardbreak provenance");

            // D1c3: the binary head-collider classifier (x1.8, "head collider contact") is gone from the projectile path. It was
            // replaced by the authority's hit-volume location (same table as melee); these two checks replaced the old
            // "disabled/enabled head collider classifies a headshot" pair. Location behaviour: EmberdelveRangedLocationGate.
            Check(typeof(LocalCombatAuthority).GetMethod("IsProjectileHeadshot", BindingFlags.Static | BindingFlags.NonPublic) == null,
                "the projectile path no longer has a head-collider headshot classifier");

            // Authority geometry does not depend on a renderer or cosmetic GameObject.
            _defender.Loadout.DiscardInheritedVisuals();
            _defender.SetGuardHeld(true);
            bool headlessBladeAvailable = _defender.TryGetProjectileGuardBlade(out GuardBladeBox liveBlade);
            Check(_defender.Loadout.Visual == null && headlessBladeAvailable,
                "a guarding blade is resolved headlessly without WeaponVisualRig");
            if (!headlessBladeAvailable)
                liveBlade = GuardBladePose.Resolve(_defender.AimOrigin,
                    Quaternion.LookRotation(_defender.AimDirection.normalized, Vector3.up), true);
            ResetHealth();

            ProjectileVisual geometryArrow = ProjectileVisual.SpawnArrow(Vector3.zero, Vector3.forward * 10f, _bow, true);
            Check(geometryArrow != null && Vector3.Distance(geometryArrow.TipPosition, Vector3.zero) < 0.001f,
                "visible bow arrow leading tip matches authority launch position");
            if (geometryArrow != null) Destroy(geometryArrow.gameObject);

            // The ordinary bow arrow follows the same flight solver and is caught only by actual sword contact.
            _defender.SetGuardHeld(true);
            Vector3 bowBladeCenter = GuardBladePose.Resolve(_defender.AimOrigin,
                Quaternion.LookRotation(_defender.AimDirection.normalized, Vector3.up), true).Center;
            Vector3 bowTowardBody = (_defender.CombatCenter - bowBladeCenter).normalized;
            Shooter bowShooter = MakeShooter(bowBladeCenter - bowTowardBody * 8f, bowTowardBody, _bow);
            Check(Launch(bowShooter, 1), "ordinary bow arrow launch is accepted");
            DamageInfo bowBlock = ResolveDamage(bowShooter, 1f);
            Check(bowBlock.IsValid && bowBlock.Blocked, "sword physically intercepts a flying bow arrow");
            Check(bowBlock.GuardBladeContact && bowBlock.Location == HitLocation.Unspecified && !bowBlock.Headshot,
                "D1c3: a blade-intercepted arrow is stopped before any body location is decided (Unspecified, no headshot)");

            _defender.SetGuardHeld(false);
            Vector3 bowBodyOrigin = _defender.CombatCenter + _defender.transform.forward * 8f;
            Shooter bowNearMiss = MakeShooter(bowBodyOrigin, (_defender.CombatCenter - bowBodyOrigin).normalized, _bow);
            _defender.SetGuardHeld(true);
            ResetHealth();
            Check(Launch(bowNearMiss, 1), "ordinary bow body-line launch is accepted");
            DamageInfo bowBodyHit = ResolveDamage(bowNearMiss, 1f);
            Check(bowBodyHit.IsValid && !bowBodyHit.Blocked, "bow arrow that misses sword still damages body");
            Check(bowBodyHit.Location == HitLocation.Body && !bowBodyHit.Headshot,
                "D1c3: the torso-line arrow takes the Body location from the player's own hit volumes (x1.0), not a claim");

            // A projectile already in flight is caught when the guard is raised before it reaches the blade.
            _defender.SetGuardHeld(false);
            Vector3 bodyPath = (_defender.CombatCenter - liveBlade.Center).normalized;
            Shooter raisingShooter = MakeShooter(liveBlade.Center - bodyPath * 8f, bodyPath);
            Check(Launch(raisingShooter, 1), "valid arrow launch into a future blade intercept is accepted");
            float beforeRaiseImpact = _defender.CurrentHealth;
            _authority.AdvanceProjectiles(0.12f);
            Check(Mathf.Abs(_defender.CurrentHealth - beforeRaiseImpact) < 0.001f, "flight does not apply damage before it reaches the defender");
            _defender.SetGuardHeld(true);
            DamageInfo caught = ResolveDamage(raisingShooter, 1f);
            Check(caught.IsValid && caught.Blocked, "raising the blade during flight blocks at live blade contact");

            // Correct facing alone cannot stop a shot through the torso that misses the physically narrow blade.
            Vector3 bodyForward = _defender.transform.forward;
            Vector3 bodyOrigin = _defender.CombatCenter + bodyForward * 8f;
            Shooter nearMiss = MakeShooter(bodyOrigin, (_defender.CombatCenter - bodyOrigin).normalized);
            ResetHealth();
            Check(Launch(nearMiss, 1), "body-line near-miss arrow launch is accepted");
            DamageInfo nearMissDamage = ResolveDamage(nearMiss, 2f);
            Check(nearMissDamage.IsValid && !nearMissDamage.Blocked,
                "a correctly faced guard does not block a projectile that misses the blade");

            // Moving the live guard pose away after launch must also move its collision volume before impact.
            liveBlade = GuardBladePose.Resolve(_defender.AimOrigin,
                Quaternion.LookRotation(_defender.AimDirection.normalized, Vector3.up), true);
            bodyPath = (_defender.CombatCenter - liveBlade.Center).normalized;
            Shooter movingGuard = MakeShooter(liveBlade.Center - bodyPath * 8f, bodyPath);
            _defender.SetGuardHeld(true);
            ResetHealth();
            Check(Launch(movingGuard, 1), "moving-guard projectile launch is accepted");
            _authority.AdvanceProjectiles(0.12f);
            Quaternion originalAimRotation = _defender.Look.AimTransform.rotation;
            _defender.Look.AimTransform.rotation = Quaternion.AngleAxis(35f, Vector3.up) * originalAimRotation;
            DamageInfo movedGuard = ResolveDamage(movingGuard, 2f);
            Check(movedGuard.IsValid && !movedGuard.Blocked,
                "moving the guard off the flight path during travel allows the body hit");
            _defender.Look.AimTransform.rotation = originalAimRotation;

            // A rear projectile has neither a blade intersection nor the ordinary forward guard arc.
            Vector3 rearOrigin = _defender.CombatCenter - bodyForward * 8f;
            Shooter rear = MakeShooter(rearOrigin, (_defender.CombatCenter - rearOrigin).normalized);
            ResetHealth();
            Check(Launch(rear, 1), "rear attack projectile launch is accepted");
            DamageInfo rearDamage = ResolveDamage(rear, 2f);
            Check(rearDamage.IsValid && !rearDamage.Blocked, "a projectile from behind is not caught by the forward blade guard");

            // Level geometry is ordered with blade and body contacts and stops an otherwise valid flight.
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "ProjectileBlockGate_Wall";
            wall.transform.position = _defender.CombatCenter + bodyForward * 3f;
            wall.transform.localScale = new Vector3(3f, 3f, 0.2f);
            _cleanup.Add(wall);
            config.levelMask = 1 << wall.layer;
            Physics.SyncTransforms();
            ResetHealth();
            float beforeWall = _defender.CurrentHealth;
            Shooter occluded = MakeShooter(_defender.CombatCenter + bodyForward * 8f, -bodyForward);
            Check(Launch(occluded, 1), "wall-occluded arrow launch is accepted");
            ResolveDamage(occluded, 2f);
            Check(Mathf.Abs(_defender.CurrentHealth - beforeWall) < 0.001f, "wall occlusion stops the arrow before the defender");

            Shooter blockedMuzzle = MakeShooter(_defender.CombatCenter + bodyForward * 8f, -bodyForward);
            GameObject muzzleWall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            muzzleWall.name = "ProjectileBlockGate_MuzzleWall";
            muzzleWall.layer = wall.layer;
            muzzleWall.transform.position = blockedMuzzle.AimOrigin;
            muzzleWall.transform.localScale = Vector3.one * 0.3f;
            _cleanup.Add(muzzleWall);
            Physics.SyncTransforms();
            Check(!Launch(blockedMuzzle, 1), "a projectile cannot launch while its source sphere overlaps a wall");
            CombatRegistry.Unregister(blockedMuzzle.CombatId, blockedMuzzle);
            _shooters.Remove(blockedMuzzle);
            muzzleWall.GetComponent<Collider>().enabled = false;
            Destroy(muzzleWall);
            Physics.SyncTransforms();

            // Fill the NonAlloc query with contacts that resolve to the instigator, then ensure
            // a later wall is still found by the saturation fallback and ordered before the player.
            Shooter saturated = MakeShooter(_defender.CombatCenter + bodyForward * 8f, -bodyForward);
            GameObject ignoredRoot = new GameObject("ProjectileBlockGate_IgnoredContacts");
            ignoredRoot.layer = wall.layer;
            IgnoredProjectileCollider ignored = ignoredRoot.AddComponent<IgnoredProjectileCollider>();
            ignored.CombatId = saturated.CombatId;
            for (int i = 0; i < 70; i++)
            {
                GameObject ignoredCollider = GameObject.CreatePrimitive(PrimitiveType.Cube);
                ignoredCollider.name = "ProjectileBlockGate_IgnoredContact";
                ignoredCollider.layer = wall.layer;
                ignoredCollider.transform.SetParent(ignoredRoot.transform);
                ignoredCollider.transform.position = saturated.AimOrigin - bodyForward;
                ignoredCollider.transform.localScale = Vector3.one * 0.05f;
            }
            _cleanup.Add(ignoredRoot);
            Physics.SyncTransforms();
            ResetHealth();
            float beforeSaturatedWall = _defender.CurrentHealth;
            Check(Launch(saturated, 1), "saturated-query wall test launches normally");
            ResolveDamage(saturated, 2f);
            Check(Mathf.Abs(_defender.CurrentHealth - beforeSaturatedWall) < 0.001f,
                "a saturated physics result still finds the nearest wall before the defender");
            config.levelMask = 0;
            Destroy(wall);
            Destroy(ignoredRoot);
            Physics.SyncTransforms();

            // Inactive scene components stay in CombatRegistry during fixture resets, but must not remain
            // analytic projectile blockers after their GameObjects leave the active scene.
            Vector3 inactiveBlockerPoint = _defender.CombatCenter + bodyForward * 4f;
            GameObject inactiveBlockerObject = new GameObject("ProjectileBlockGate_InactiveCombatBody");
            inactiveBlockerObject.transform.position = inactiveBlockerPoint;
            IgnoredProjectileCollider inactiveBlocker = inactiveBlockerObject.AddComponent<IgnoredProjectileCollider>();
            inactiveBlocker.CombatId = CombatRegistry.AllocateId();
            CombatRegistry.Register(inactiveBlocker.CombatId, inactiveBlocker);
            inactiveBlockerObject.SetActive(false);
            _cleanup.Add(inactiveBlockerObject);
            Shooter inactiveBlockerShot = MakeShooter(_defender.CombatCenter + bodyForward * 8f, -bodyForward);
            _defender.SetGuardHeld(false);
            ResetHealth();
            float beforeInactiveBlocker = _defender.CurrentHealth;
            Check(Launch(inactiveBlockerShot, 1), "body shot toward an inactive registered component launches");
            ResolveDamage(inactiveBlockerShot, 2f);
            Check(_defender.CurrentHealth < beforeInactiveBlocker,
                "inactive registered components do not remain ghost blockers in projectile flight");
            CombatRegistry.Unregister(inactiveBlocker.CombatId, inactiveBlocker);
            // Dropping guard while an arrow travels removes the interception, even though it began guarded.
            Vector3 loweringForward = _defender.AimDirection.normalized;
            liveBlade = GuardBladePose.Resolve(_defender.AimOrigin, Quaternion.LookRotation(loweringForward, Vector3.up), true);
            _defender.SetGuardHeld(true);
            bodyPath = (_defender.CombatCenter - liveBlade.Center).normalized;
            Shooter loweringShooter = MakeShooter(liveBlade.Center - bodyPath * 8f, bodyPath);
            ResetHealth();
            Check(Launch(loweringShooter, 1), "second live-flight arrow launch is accepted");
            _authority.AdvanceProjectiles(0.12f);
            _defender.SetGuardHeld(false);
            DamageInfo lowered = ResolveDamage(loweringShooter, 2f);
            Check(lowered.IsValid && !lowered.Blocked, "lowering guard before impact lets the projectile hit the body");

            // Same shared authored shape drives the rendered blockout blade under camera pitch/yaw.
            _defender.Loadout.RebuildWeaponVisual(_sword);
            _defender.SetGuardHeld(true);
            if (_defender.Loadout.Visual != null
                && _defender.Loadout.Visual.TryGetGuardBladeBox(out GuardBladeBox visualBlade)
                && _defender.TryGetProjectileGuardBlade(out GuardBladeBox authorityBlade))
            {
                Check((visualBlade.Center - authorityBlade.Center).sqrMagnitude < 0.0004f
                      && Quaternion.Angle(visualBlade.Rotation, authorityBlade.Rotation) < 0.2f,
                    "visible raised blade center and rotation match authority pose");
            }
            else Check(false, "visible guard pose is available after rebuilding the weapon visual");

            _defender.SetGuardHeld(false);
            Check(_defender.Loadout.ForceSwitchToSlot(1), "weapon swap to non-guardable crossbow succeeds outside an attack");
            _defender.SetGuardHeld(true);
            Check(!_defender.TryGetProjectileGuardBlade(out _), "ranged/non-guardable weapon has no blade interception shape");
            _defender.Loadout.ForceSwitchToSlot(0);
            _defender.SetGuardHeld(true);

            // The guard pose is unavailable throughout an authority-owned swing, regardless of visual flags.
            if (_sword.UsesSweptMelee && _defender.TryGetAimPose(out _, out float yaw, out float pitch))
            {
                int pattern = _sword.HasComboChain ? _sword.comboSteps[0].patternIndex : 0;
                MeleeSwingIntent swing = new MeleeSwingIntent(_defender.CombatId, _sword.weaponId, pattern, 0,
                    yaw, pitch, CombatRegistry.NextSequence(_defender.CombatId));
                // D1c5: a swing cannot begin while the guard is up, so the guard is lowered for the begin and raised again
                // afterwards (a client that raises it mid swing): the blade must still be unavailable throughout the swing.
                _defender.SetGuardHeld(false);
                bool began = _authority.TryBeginMeleeSwingAsOwner(swing, out _, out string swingReason);
                Check(began, "authoritative swing starts for live guard eligibility test (" + swingReason + ")");
                _defender.SetGuardHeld(true);
                if (began)
                    Check(!_defender.TryGetProjectileGuardBlade(out _), "an active authority swing disables projectile blade guard");
            }
            else Check(false, "sword has an aim-resolvable swing pattern for the active-swing guard test");

            // D1c5: the guard flag no longer reduces damage while the defender's own swing runs, so the swing is retired on the
            // authority clock before the shield's directional block semantics are probed (they were probed mid swing before).
            _clock += _sword.cooldown * 4f;
            _authority.StepSwings();
            _defender.SetGuardHeld(false);
            _defender.SetOffhandWeapon(_originalOffhand);
            Check(_defender.Defense.HasShield && !_defender.CanParry, "restored offhand shield keeps block without enabling parry");
            _defender.SetGuardHeld(true);
            Vector3 shieldFront = _defender.transform.position + _defender.transform.forward * 3f;
            DamageInfo shieldFrontHit = new DamageInfo(10f, _defender.CombatCenter, -_defender.transform.forward,
                shieldFront, 9001, EntityKind.Enemy, DamageType.Pierce, false, false, 0f, rangedProjectile: true);
            Check(_defender.TryResolveGuard(shieldFrontHit, CombatFlowTuning.Default, out _),
                "shield retains its existing directional projectile block semantics");
            DamageInfo shieldRearHit = new DamageInfo(10f, _defender.CombatCenter, _defender.transform.forward,
                _defender.transform.position - _defender.transform.forward * 3f, 9001, EntityKind.Enemy,
                DamageType.Pierce, false, false, 0f, rangedProjectile: true);
            Check(!_defender.TryResolveGuard(shieldRearHit, CombatFlowTuning.Default, out _),
                "shield still rejects projectile damage from behind");
            _defender.SetGuardHeld(false);

            // Let the authority retire the preceding sword swing before changing kits for the rapier check.
            _clock += _sword.cooldown * 4f;
            _authority.StepSwings();

            // Equip the Archer's actual secondary rapier to verify its authored weapon mitigation works
            // even though the Archer class has no passive block reduction and the bow cannot block.
            CharacterClassData archerKit = AssetDatabase.LoadAssetAtPath<CharacterClassData>(EmberdelvePaths.Data + "/Class_Archer.asset");
            Check(archerKit != null, "Archer class data is available for rapier guard coverage");
            if (archerKit != null)
            {
                _defender.ApplyClass(archerKit, config);
                _defender.SetOffhandWeapon(_rapier);
                bool rapierEquipped = _defender.Loadout.ForceSwitchToSlot(1) && _defender.ActiveWeapon == _rapier;
                Check(rapierEquipped && _rapier.CanBlock && _defender.Defense.CanBlock,
                    "Archer rapier primary override enables its guard without enabling bow block");
                if (rapierEquipped)
                {
                    _defender.SetGuardHeld(true);
                    Check(_defender.TryGetProjectileGuardBlade(out GuardBladeBox rapierBlade), "rapier exposes the same physical blade guard shape");
                    if (_defender.TryGetProjectileGuardBlade(out rapierBlade))
                    {
                        Vector3 towardBody = (_defender.CombatCenter - rapierBlade.Center).normalized;
                        Shooter rapierHit = MakeShooter(rapierBlade.Center - towardBody * 8f, towardBody);
                        ResetHealth();
                        Check(Launch(rapierHit, 1), "projectile launch against rapier blade is accepted");
                        DamageInfo rapierBlock = ResolveDamage(rapierHit, 2f);
                        Check(rapierBlock.IsValid && rapierBlock.Blocked, "rapier blade intercepts the flying projectile");
                    }
                }

                _defender.SetGuardHeld(false);
                _defender.Loadout.ForceSwitchToSlot(0);
                Check(!_defender.Defense.CanBlock, "switching back to Archer bow does not inherit rapier block override");
                SkillData multiShot = AssetDatabase.LoadAssetAtPath<SkillData>(EmberdelvePaths.Data + "/Skill_ArcherMultiShot.asset");
                bool castBegan = false;
                int activationId = 0;
                string castReason = multiShot == null ? "missing skill asset" : string.Empty;
                if (multiShot != null)
                    castBegan = _authority.TryBeginSkillCastAsOwner(_defender.CombatId, multiShot.skillId,
                        out activationId, out castReason);
                Check(castBegan, "MultiShot activation opens for projectile-slot replay test (" + castReason + ")");
                if (castBegan)
                {
                    Vector3 muzzle = _defender.GetMuzzlePosition();
                    Vector3 aim = _defender.AimDirection;
                    int projectileCount = multiShot.ProjectilesPerCast;
                    Shooter hostileCandidate = MakeShooter(muzzle + aim * 2f, aim);
                    float healthBeforeHitOnlySubmit = hostileCandidate.CurrentHealth;
                    SkillIntent hitOnlyBypass = new SkillIntent(_defender.CombatId, _defender.Kind, multiShot,
                        _defender.ActiveWeapon, muzzle, aim, _defender.ActiveWeapon.rangedRange,
                        CombatRegistry.NextSequence(_defender.CombatId), _clock, default, activationId,
                        _defender.AbilityEffectScale);
                    bool bypassAccepted = _authority.SubmitSkillAsOwner(hitOnlyBypass,
                        new HitCandidate(hostileCandidate.CombatId, hostileCandidate.CombatCenter, false), out string bypassReason);
                    Check(!bypassAccepted && hostileCandidate.CurrentHealth == healthBeforeHitOnlySubmit
                          && bypassReason.Contains("projectile skills must launch"),
                        "public hit-only SubmitSkill cannot apply instant projectile damage");
                    CombatRegistry.Unregister(hostileCandidate.CombatId, hostileCandidate);
                    _shooters.Remove(hostileCandidate);
                    bool allAccepted = true;
                    for (int i = 0; i < projectileCount; i++)
                    {
                        float t = projectileCount <= 1 ? 0f : i / (projectileCount - 1f) - 0.5f;
                        Vector3 direction = projectileCount <= 1 ? aim
                            : Quaternion.AngleAxis(t * Mathf.Clamp(multiShot.spreadDegrees, 0f, 170f), Vector3.up) * aim;
                        SkillIntent shot = new SkillIntent(_defender.CombatId, _defender.Kind, multiShot,
                            _defender.ActiveWeapon, muzzle, direction, _defender.ActiveWeapon.rangedRange,
                            CombatRegistry.NextSequence(_defender.CombatId), _clock, default, activationId,
                            _defender.AbilityEffectScale);
                        allAccepted &= _authority.LaunchSkillProjectileAsOwner(shot, i);
                    }
                    Check(allAccepted, "each canonical MultiShot spread slot launches once");
                    _authority.AdvanceProjectiles(1f);
                    SkillIntent replay = new SkillIntent(_defender.CombatId, _defender.Kind, multiShot,
                        _defender.ActiveWeapon, muzzle, aim, _defender.ActiveWeapon.rangedRange,
                        CombatRegistry.NextSequence(_defender.CombatId), _clock, default, activationId,
                        _defender.AbilityEffectScale);
                    Check(!_authority.LaunchSkillProjectileAsOwner(replay, 0),
                        "finished flights do not reopen launched spread slots while activation remains open");
                    Check(_authority.CompleteSkillCastAsOwner(_defender.CombatId, activationId),
                        "MultiShot activation closes after replay assertion");
                    _authority.AdvanceProjectiles(0f);
                }
            }
        }

        private Shooter MakeShooter(Vector3 origin, Vector3 direction, WeaponData weapon = null)
        {
            Shooter shooter = new Shooter
            {
                CombatId = CombatRegistry.AllocateId(), ActiveWeapon = weapon != null ? weapon : _crossbow,
                AimOrigin = origin, AimDirection = direction.normalized
            };
            CombatRegistry.Register(shooter.CombatId, shooter);
            _shooters.Add(shooter);
            return shooter;
        }

        private bool Launch(Shooter shooter, int sequence)
        {
            AttackIntent intent = new AttackIntent(shooter.CombatId, shooter.Kind, shooter.ActiveWeapon, 1f,
                shooter.AimOrigin, shooter.AimDirection, shooter.ActiveWeapon.rangedRange, sequence, _clock);
            return _authority.LaunchProjectileAsOwner(intent);
        }

        private void ResetHealth() => _defender.Health.Configure(100000, 0f, 0f);

        private DamageInfo ResolveDamage(Shooter shooter, float seconds)
        {
            DamageInfo resolved = DamageInfo.Invalid;
            Action<DamageInfo> listener = damage => resolved = damage;
            _defender.Health.Damaged += listener;
            try
            {
                _authority.AdvanceProjectiles(seconds);
                _clock += seconds;
            }
            finally { _defender.Health.Damaged -= listener; }
            CombatRegistry.Unregister(shooter.CombatId, shooter);
            _shooters.Remove(shooter);
            return resolved;
        }
    }
}
