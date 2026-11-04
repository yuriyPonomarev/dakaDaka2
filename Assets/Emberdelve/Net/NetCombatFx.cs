using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process: presents what the SERVER reported about combat. Nothing here decides or applies anything: a swing is a log line
    /// (the offline visual needs the weapon and pattern assets, which the client resolves by id later), a shot is a small replica body that is
    /// advanced with the same fixed-step integrator the server uses (ProjectileBallistics.Advance from the parameters the server sent) and
    /// is retired where the server says the flight ended, a hit is a counted line. Health never changes here: it arrives through the
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

        public static int Swings { get; private set; }
        public static int Shots { get; private set; }
        public static int Hits { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Replicas.Clear();
            HitsOnPlayer.Clear();
            BlockedOnPlayer.Clear();
            Swings = Shots = Hits = 0;
        }

        /// <summary>Server-reported hits on a player (by server player id) this client has seen.</summary>
        public static int HitsOn(int playerId) { HitsOnPlayer.TryGetValue(playerId, out int n); return n; }

        /// <summary>Server-reported BLOCKED hits on a player this client has seen.</summary>
        public static int BlockedOn(int playerId) { BlockedOnPlayer.TryGetValue(playerId, out int n); return n; }

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
            Debug.Log(NetTuning.LogPrefix + "[client] server-shot id=" + IdOf(carrier) + " serial=" + shot.Serial + " weapon=" + Dash(shot.WeaponId) +
                      " charge=" + F(shot.Charge) + " speed=" + F(shot.Speed) + " gravity=" + F(shot.Gravity));

            if (Replicas.TryGetValue(shot.Serial, out NetArrowReplica old) && old != null) Object.Destroy(old.gameObject);

            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            body.name = "NetArrowReplica_" + shot.Serial;
            Collider collider = body.GetComponent<Collider>();
            if (collider != null) Object.Destroy(collider);
            body.transform.localScale = Vector3.one * Mathf.Max(0.05f, shot.Radius * 2f);
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
                replica.Finish(new Vector3(end.X, end.Y, end.Z));
            }

            Replicas.Remove(end.Serial);
            Debug.Log(NetTuning.LogPrefix + "[client] server-flight-end id=" + IdOf(carrier) + " serial=" + end.Serial +
                      " air=" + F(end.AirTime) + " replicaAir=" + F(replicaAir));
        }

        public static void OnHit(NetPlayerRig carrier, in CombatHitWire hit)
        {
            Hits++;
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
                      " guardBroken=" + hit.GuardBroken + " killed=" + hit.Killed);
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
            if (Elapsed > _maxTime + 1f) Destroy(gameObject); // the server's end event never came (lost client, shutdown)
        }
    }
}
