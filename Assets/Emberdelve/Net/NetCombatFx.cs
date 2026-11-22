using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process: presents what the SERVER reported about combat. Nothing here decides or applies anything: a swing is a log line
    /// (the offline visual needs the weapon and pattern assets, which the client resolves by id later), a shot is a small replica body that is
    /// advanced with the same fixed-step integrator the server uses (ProjectileBallistics.Advance from the parameters the server sent) and
    /// is retired where the server says the flight ended, and an enemy hit can emit blood at the server's point. Health never changes here: it arrives through the
    /// player's replicated Vitals variable and the enemy vitals snapshots.
    ///
    ///   [client] server-swing id=.. weapon=.. pattern=.. step=.. windup=.. active=.. recovery=..
    ///   [client] server-shot id=.. serial=.. weapon=.. charge=.. speed=.. gravity=..
    ///   [client] server-flight-end id=.. serial=.. air=.. replicaAir=..
    ///   [client] server-hit src=.. attacker=.. target=.. enemy=.. amount=.. loc=.. blocked=.. guardBroken=.. killed=..
    /// The wording is deliberate: a client reports what the server did, it never logs "a hit applied here".
    /// </summary>
    public static class NetCombatFx
    {
        private static readonly Dictionary<uint, NetArrowReplica> Replicas = new Dictionary<uint, NetArrowReplica>();
        private static readonly Dictionary<int, int> HitsOnPlayer = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> BlockedOnPlayer = new Dictionary<int, int>();
        private static readonly BloodHitWindow BloodEvents = new BloodHitWindow();
        private static readonly Emberdelve.Feedback.SoundEventWindow SoundEvents = new Emberdelve.Feedback.SoundEventWindow();
        private const string BloodPrefabPath = "Emberdelve/NetBloodHit";

        public static int Swings { get; private set; }
        public static int Shots { get; private set; }
        public static int Hits { get; private set; }
        public static int EnemyHits { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Replicas.Clear();
            HitsOnPlayer.Clear();
            BlockedOnPlayer.Clear();
            BloodEvents.Clear();
            SoundEvents.Clear();
            Swings = Shots = Hits = EnemyHits = 0;
        }

        /// <summary>Server-reported hits on a player (by server player id) this client has seen.</summary>
        public static int HitsOn(int playerId) { HitsOnPlayer.TryGetValue(playerId, out int n); return n; }

        /// <summary>Server-reported BLOCKED hits on a player this client has seen.</summary>
        public static int BlockedOn(int playerId) { BlockedOnPlayer.TryGetValue(playerId, out int n); return n; }

        public static void ClearBloodHistory() { BloodEvents.Clear(); SoundEvents.Clear(); }

        public static void OnSwing(NetPlayerRig carrier, in CombatSwingWire swing)
        {
            Swings++;
            Debug.Log(NetTuning.LogPrefix + "[client] server-swing id=" + IdOf(carrier) + " weapon=" + Dash(swing.WeaponId) + " pattern=" + Dash(swing.PatternId) +
                      " step=" + swing.ComboStep + " windup=" + F(swing.Windup) + " active=" + F(swing.Active) + " recovery=" + F(swing.Recovery) +
                      " trajectory=" + swing.TrajectoryKind + " height=" + swing.HeightBand + " yaw=" + F(swing.AimYaw) + " pitch=" + F(swing.AimPitch) +
                      " minyaw=" + F(swing.MinBladeYaw) + " maxyaw=" + F(swing.MaxBladeYaw) + " reach=" + F(swing.Reach) +
                      " committed=" + swing.Committed + " riposte=" + swing.Riposte);
        }

        public static void OnShot(NetPlayerRig carrier, in CombatShotWire shot)
        {
            Shots++;
            if (carrier != null && carrier.Rig != null)
                Emberdelve.Feedback.WorldAudio.Play(Emberdelve.Feedback.WorldSound.Bow, carrier.Rig.AimOrigin, .8f);
            Debug.Log(NetTuning.LogPrefix + "[client] server-shot id=" + IdOf(carrier) + " serial=" + shot.Serial + " weapon=" + Dash(shot.WeaponId) +
                      " charge=" + F(shot.Charge) + " speed=" + F(shot.Speed) + " gravity=" + F(shot.Gravity));

            if (Replicas.TryGetValue(shot.Serial, out NetArrowReplica old) && old != null) Object.Destroy(old.gameObject);

            Emberdelve.Data.WeaponData weapon = null;
            var catalog = carrier != null && carrier.Rig != null && carrier.Rig.Inventory != null
                ? carrier.Rig.Inventory.Catalog : null;
            if (catalog != null)
                foreach (var item in catalog.Items)
                    if (item != null && item.grantedWeapon != null && item.grantedWeapon.weaponId == shot.WeaponId)
                    { weapon = item.grantedWeapon; break; }
            float length = weapon != null ? Mathf.Max(.2f, weapon.projectileLength) : .7f;
            GameObject body = new GameObject("NetArrowReplica_" + shot.Serial);
            GameObject model = ProjectileVisual.CreateModel(weapon, length);
            model.transform.SetParent(body.transform, false);
            // Replica position is the authority's leading tip, not the model's center.
            model.transform.localPosition = Vector3.back * (length * .5f);
            NetArrowReplica replica = body.AddComponent<NetArrowReplica>();
            replica.Begin(in shot);
            Replicas[shot.Serial] = replica;
        }

        public static void OnFlightEnd(NetPlayerRig carrier, in CombatFlightEndWire end)
        {
            float replicaAir = -1f;
            if (Replicas.TryGetValue(end.Serial, out NetArrowReplica replica) && replica != null)
            {
                replicaAir = replica.Elapsed;
                Vector3 stop = new Vector3(end.X, end.Y, end.Z);
                // The wire reports the exact stop but no reason. Only audible contact with local level geometry
                // supplies a client wall cue; a range/time endpoint in open air stays silent.
                if (Physics.CheckSphere(stop, .08f, LayerMask.GetMask(GameConfig.LevelLayerName), QueryTriggerInteraction.Ignore))
                    Emberdelve.Feedback.WorldAudio.Play(Emberdelve.Feedback.WorldSound.ArrowImpact, stop, .55f);
                replica.Finish(stop);
            }

            Replicas.Remove(end.Serial);
            Debug.Log(NetTuning.LogPrefix + "[client] server-flight-end id=" + IdOf(carrier) + " serial=" + end.Serial +
                      " air=" + F(end.AirTime) + " replicaAir=" + F(replicaAir));
        }

        public static void OnHit(NetPlayerRig carrier, in CombatHitWire hit)
        {
            if (BloodEvents.Accept(hit.EventId, hit.TargetIsEnemy, hit.Amount, hit.PointX, hit.PointY, hit.PointZ))
            {
                GameObject prefab = Resources.Load<GameObject>(BloodPrefabPath);
                if (prefab != null)
                {
                    GameObject effect = Object.Instantiate(prefab, new Vector3(hit.PointX, hit.PointY, hit.PointZ), Quaternion.identity);
                    ParticleSystem particles = effect.GetComponentInChildren<ParticleSystem>();
                    if (particles != null)
                    {
                        particles.Play(); particles.Emit(18);
                        Debug.Log(NetTuning.LogPrefix + "[client] blood-event accepted id=" + hit.EventId + " target=" + hit.Target + " point=" + F(hit.PointX) + "," + F(hit.PointY) + "," + F(hit.PointZ));
                    }
                    Object.Destroy(effect, 2f);
                }
            }
            Hits++;
            bool newSound = SoundEvents.Accept(hit.EventId);
            if (float.IsFinite(hit.PointX) && float.IsFinite(hit.PointY) && float.IsFinite(hit.PointZ))
            {
                Vector3 point = new Vector3(hit.PointX, hit.PointY, hit.PointZ);
                if (hit.Blocked && !hit.GuardBroken && hit.Amount == 0f)
                {
                    if (newSound) Emberdelve.Feedback.FeedbackDirector.PlayGuardContact(point, hit.Parried);
                }
                else if (hit.Blocked || (float.IsFinite(hit.Amount) && hit.Amount > 0f))
                {
                    if (newSound)
                    {
                        Emberdelve.Feedback.WorldAudio.Play(hit.GuardBroken ? Emberdelve.Feedback.WorldSound.ShieldBreak :
                            hit.Blocked ? Emberdelve.Feedback.WorldSound.Metal : hit.Source != (byte)CombatHitSource.Melee
                            ? Emberdelve.Feedback.WorldSound.ArrowImpact : PlayerImpactSound(hit.Attacker), point);
                        if (hit.Amount > 0f && hit.TargetIsEnemy)
                            Emberdelve.Feedback.WorldAudio.Play(hit.Killed ? Emberdelve.Feedback.WorldSound.EnemyDeath : Emberdelve.Feedback.WorldSound.EnemyHurt, point, .6f);
                        else if (hit.Amount > 0f && PlayerView.Shows(hit.Target))
                            Emberdelve.Feedback.WorldAudio.PlayLocal(Emberdelve.Feedback.WorldSound.Hurt, .65f);
                    }
                    Color color = hit.Blocked ? new Color(.55f, .75f, 1f)
                        : hit.TargetIsEnemy ? new Color(1f, .85f, .45f) : new Color(1f, .4f, .35f);
                    Emberdelve.Feedback.ImpactFlash.Spawn(point, color, hit.Blocked ? .22f : .16f);
                }
            }
            // Client-only owner health is updated from replicated vitals and does not raise DamageInfo.Damaged.
            // Use this server-originated hit report for the owner's block animation, guarded by the resolved target id.
            if (hit.Blocked && !hit.TargetIsEnemy)
            {
                IReadOnlyList<NetPlayerRig> rigs = NetPlayerRig.Spawned;
                for (int i = 0; i < rigs.Count; i++)
                {
                    NetPlayerRig targetRig = rigs[i];
                    if (targetRig == null || targetRig.Role != NetPlayerRole.Owner || targetRig.ServerPlayerId.Value != hit.Target) continue;
                    // Client-only owners do not receive the authority's local GuardContact event.
                    if (hit.Parried && !hit.GuardBroken && hit.Amount == 0f)
                        GameEvents.RaiseToast(hit.Target, "Parry");
                    if (targetRig.Rig != null && targetRig.Rig.Loadout != null)
                    {
                        bool shieldBlock = targetRig.Rig.Defense.HasShield;
                        FirstPersonArmsDriver arms = targetRig.Rig.Loadout.Arms;
                        if (arms != null) arms.PlayConfirmedBlockImpact(shieldBlock);
                        else if (!shieldBlock) targetRig.Rig.Loadout.Visual?.PlayBlockImpact();
                    }
                    break;
                }
            }

            if (hit.TargetIsEnemy) EnemyHits++;
            if (!hit.TargetIsEnemy)
            {
                HitsOnPlayer.TryGetValue(hit.Target, out int n);
                HitsOnPlayer[hit.Target] = n + 1;
                if (hit.Blocked)
                {
                    BlockedOnPlayer.TryGetValue(hit.Target, out int b);
                    BlockedOnPlayer[hit.Target] = b + 1;
                }
            }

            Debug.Log(NetTuning.LogPrefix + "[client] server-hit src=" + SourceName(hit.Source) + " attacker=" + hit.Attacker + " target=" + hit.Target +
                      " enemy=" + hit.TargetIsEnemy + " amount=" + F(hit.Amount) + " loc=" + LocationName(hit.Location) + " blocked=" + hit.Blocked +
                      " guardBroken=" + hit.GuardBroken + " killed=" + hit.Killed + " parried=" + hit.Parried);
        }

        private static Emberdelve.Feedback.WorldSound PlayerImpactSound(int attacker)
        {
            foreach (NetPlayerRig actor in NetPlayerRig.Spawned)
                if (actor != null && actor.ServerPlayerId.Value == attacker && actor.Rig != null)
                    return Emberdelve.Feedback.WorldAudio.ImpactFor(actor.AvatarCombat.Value.IsValid ? actor.AvatarCombat.Value.Main.ToString() : null);
            return Emberdelve.Feedback.WorldSound.Impact;
        }

        // The wire carries bytes; the report names the enums exactly as the server's combat-hit line does (src=Melee, loc=Arms).
        private static string SourceName(byte source)
        {
            return System.Enum.IsDefined(typeof(CombatHitSource), (int)source) ? ((CombatHitSource)source).ToString() : "Unknown" + source;
        }

        private static string LocationName(byte location)
        {
            return System.Enum.IsDefined(typeof(HitLocation), (int)location) ? ((HitLocation)location).ToString() : "Unknown" + location;
        }

        private static int IdOf(NetPlayerRig rig) { return rig != null ? rig.ServerPlayerId.Value : 0; }
        private static string Dash(string text) { return string.IsNullOrEmpty(text) ? "-" : text; }
        private static string F(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    }

    /// <summary>U3B3, client: a replica of a server projectile, stepped with the server's integrator from the launch parameters.</summary>
    public sealed class NetArrowReplica : MonoBehaviour
    {
        private BallisticState _state;
        private float _gravity;
        private float _maxTime;
        private float _accumulator;
        private bool _finished;

        /// <summary>Seconds the replica has flown (fixed steps taken).</summary>
        public float Elapsed { get; private set; }

        public void Begin(in CombatShotWire shot)
        {
            Vector3 dir = new Vector3(shot.DirX, shot.DirY, shot.DirZ);
            if (dir.sqrMagnitude < 1e-6f) dir = Vector3.forward;
            dir.Normalize();
            transform.rotation = Quaternion.LookRotation(dir);
            _state = new BallisticState(shot.OriginX, shot.OriginY, shot.OriginZ, dir.x * shot.Speed, dir.y * shot.Speed, dir.z * shot.Speed);
            _gravity = shot.Gravity;
            _maxTime = shot.MaxFlightTime > 0f ? shot.MaxFlightTime : 10f;
            transform.position = new Vector3(shot.OriginX, shot.OriginY, shot.OriginZ);
        }

        public void Finish(Vector3 where)
        {
            _finished = true;
            transform.position = where;
            Destroy(gameObject, 0.5f);
        }

        private void Update()
        {
            if (_finished) return;
            _accumulator += Time.deltaTime;
            int steps = ProjectileBallistics.ConsumeSteps(ref _accumulator, 8);
            for (int i = 0; i < steps; i++)
            {
                _state = ProjectileBallistics.Advance(in _state, _gravity, ProjectileBallistics.StepSeconds);
                Elapsed += ProjectileBallistics.StepSeconds;
            }

            transform.position = new Vector3(_state.PX, _state.PY, _state.PZ);
            Vector3 velocity = new Vector3(_state.VX, _state.VY, _state.VZ);
            if (velocity.sqrMagnitude > .0001f) transform.rotation = Quaternion.LookRotation(velocity);
            if (Elapsed > _maxTime + 1f) Destroy(gameObject); // the server's end event never came (lost client, shutdown)
        }
    }
}
