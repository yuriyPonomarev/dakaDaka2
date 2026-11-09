using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process, owner of a player object only (added by NetPlayerRig.OnNetworkSpawn): the producer of the combat intents. It is
    /// the ONLY thing a client does about weapon combat: it ASKS the server (NetPlayerRig.CombatIntentRpc, reliable and ordered: attack press,
    /// guard down / up, draw start / release, weapon-set swap) and shows what the server answers (NetOwnerCombatView) and replicates (health,
    /// swing / shot / hit events). MeleeCombat and RangedCombat are disabled on a client view, the combat authority does not exist in a client
    /// process and HealthComponent refuses damage there, so a press is executed exactly once, on the server. The intent carries no aim, charge,
    /// target or damage: the aim is the movement stream's, the charge is the server's own clock.
    ///
    /// Sequence: ONE counter per connection for every kind, starting at 1; it advances only when a request is actually sent. Throttle: a short
    /// queue (CombatNetLimits.ClientQueueLimit) drained at most once per CombatNetLimits.ClientMinIntervalSeconds, in order; a press that
    /// does not fit is dropped on the client (counted). That keeps an honest player under the server's gate and out of its reject ledger.
    ///
    /// Input sources, in this order: a scripted driver (-ember-net-script melee-duel | ranged-shot | combat-abuse | enemy-hit; works headless, steers the
    /// movement stream through NetInputSender.DriveFromCombatScript, and reads its numbers from the start file, see CombatScriptParams), the
    /// real EmberdelveInput of a windowed owner. A scripted client takes no real input. combat-abuse is a hostile probe and exists in
    /// development builds and the Editor only.
    ///   [client] combat-intent sent seq=N kind=K
    ///   [client] script melee-duel|ranged-shot|combat-abuse start|done ...
    ///   [client] combat-sender final sent=.. dropped=.. lastSeq=.. clientBlockedDamage=.. serverHitsSeen=..
    /// </summary>
    public sealed class NetCombatSender : MonoBehaviour
    {
        private const float DefaultStartDelay = 1.5f;
        private const float StartFilePollSeconds = 0.25f;
        private const float ScriptTimeoutSeconds = 90f;
        private const float AwayTimeoutSeconds = 10f;
        private const float SettleSeconds = 0.6f;

        private NetPlayerRig _owner;
        private PlayerRig _rig;
        private NetLaunchArgs _args;
        private NetScriptSpec _spec;
        private NetOwnerCombatView _view;
        private bool _useRealInput;

        private readonly Queue<CombatIntentKind> _queue = new Queue<CombatIntentKind>();
        private uint _sequence;
        private float _lastSendAt = -100f;
        private int _sent;
        private int _dropped;

        // real input
        private bool _rangedMode;
        private bool _drawHeld;
        private bool _guardHeld;

        // scripts
        private CombatScriptParams _params = CombatScriptParams.Default;
        private float _spawnedAt;
        private float _nextStartPoll;
        private bool _scriptStarted;
        private bool _scriptDone;
        private float _scriptStartedAt;
        private float _phaseStartedAt;
        private int _phase;
        private bool _lower;
        private int _counter;
        private float _nextAt;
        private int _enemyHitBaseline;

        public uint LastSequence => _sequence;
        public int Sent => _sent;
        public int Dropped => _dropped;
        public bool CanOpenInventory => !_drawHeld;

        public void Configure(NetPlayerRig owner, NetLaunchArgs args)
        {
            _owner = owner;
            _rig = owner != null ? owner.Rig : null;
            _args = args;
            _spawnedAt = Time.unscaledTime;
            _view = owner != null ? owner.GetComponent<NetOwnerCombatView>() : null;
            if (_view != null) _view.Resulted += OnResult;

            NetScriptSpec spec = args != null ? args.ScriptSpec : null;
            if (spec != null && spec.IsCombatScript)
            {
                _spec = spec;
#if !(DEVELOPMENT_BUILD || UNITY_EDITOR)
                if (NetScriptSpec.IsDevelopmentOnly(spec.Steps[0].Kind))
                {
                    Debug.Log(NetTuning.LogPrefix + "[client] script " + spec.Text + " unavailable (not a development build)");
                    _spec = null;
                }
#endif
            }

            _useRealInput = (args == null || args.ScriptSpec == null) && !Application.isBatchMode && _rig != null && _rig.Input != null;
            Debug.Log(NetTuning.LogPrefix + "[client] combat sender started script=" + (_spec != null ? _spec.Text : "-") + " realInput=" + _useRealInput +
                      " minInterval=" + CombatNetLimits.ClientMinIntervalSeconds.ToString("F2", CultureInfo.InvariantCulture));
        }

        private void OnDestroy()
        {
            if (_view != null) _view.Resulted -= OnResult;
        }

        public void LogFinal()
        {
            Debug.Log(NetTuning.LogPrefix + "[client] combat-sender final sent=" + _sent + " dropped=" + _dropped + " lastSeq=" + _sequence +
                      " clientBlockedDamage=" + HealthComponent.ClientBlockedDamage + " serverHitsSeen=" + NetCombatFx.Hits);
        }

        // ---- send path -----------------------------------------------------------------------------

        /// <summary>Queues one intent (in order). False when the queue is full and the intent was dropped.</summary>
        public bool Enqueue(CombatIntentKind kind)
        {
            if (_owner == null || !_owner.IsSpawned) return false;
            if (_queue.Count >= CombatNetLimits.ClientQueueLimit)
            {
                _dropped++;
                return false;
            }

            _queue.Enqueue(kind);
            return true;
        }

        private void Pump()
        {
            if (_queue.Count == 0 || _owner == null || !_owner.IsSpawned) return;
            float now = Time.unscaledTime;
            if (now - _lastSendAt < CombatNetLimits.ClientMinIntervalSeconds) return;

            CombatIntentKind kind = _queue.Dequeue();
            _lastSendAt = now;
            _sequence++;
            _sent++;
            _owner.CombatIntentRpc((int)kind, _sequence);
            Debug.Log(NetTuning.LogPrefix + "[client] combat-intent sent seq=" + _sequence + " kind=" + kind);
        }

        private void OnResult(CombatResultWire result)
        {
            // Real-input weapon detection: the client does not know which hand the server wields, the server's refusal tells it.
            if (result.OutcomeKind == CombatOutcome.Refused && result.ReasonCode == CombatRefusalCode.WrongWeapon)
            {
                if (result.Kind == (byte)CombatIntentKind.AttackPress) _rangedMode = true;
                else if (result.Kind == (byte)CombatIntentKind.DrawStart) _rangedMode = false;
            }
        }

        private void Update()
        {
            if (_owner == null || !_owner.IsSpawned) return;

            if (_spec != null) RunScript();
            else if (_useRealInput)
            {
                var inventory = GetComponent<NetInventoryClient>();
                if (inventory != null && inventory.IsOpen)
                {
                    if (_guardHeld && Enqueue(CombatIntentKind.GuardUp)) _guardHeld = false;
                }
                else RealInput();
            }

            Pump();
        }

        private void RealInput()
        {
            if (_rig == null || _rig.Input == null) return;
            Emberdelve.InputLayer.EmberdelveInput input = _rig.Input;

            if (input.SwapPressed) Enqueue(CombatIntentKind.SwapWeapon);

            if (_rangedMode)
            {
                if (input.PrimaryPressed && !_drawHeld) { _drawHeld = Enqueue(CombatIntentKind.DrawStart); }
                if (input.PrimaryReleased && _drawHeld) { Enqueue(CombatIntentKind.DrawRelease); _drawHeld = false; }
            }
            else if (input.PrimaryPressed)
            {
                Enqueue(CombatIntentKind.AttackPress);
            }

            bool guard = input.SecondaryHeld;
            if (guard != _guardHeld)
            {
                if (Enqueue(guard ? CombatIntentKind.GuardDown : CombatIntentKind.GuardUp)) _guardHeld = guard;
            }
        }

        // ---- scripted drivers ------------------------------------------------------------------------

        private bool StartConditionMet()
        {
            string file = _args != null ? _args.ScriptStartFile : "";
            if (string.IsNullOrEmpty(file)) return Time.unscaledTime - _spawnedAt >= DefaultStartDelay;

            float now = Time.unscaledTime;
            if (now < _nextStartPoll) return false;
            _nextStartPoll = now + StartFilePollSeconds;
            return File.Exists(file);
        }

        /// <summary>The other player's view: the spawned, non-owned player object with the lowest server id (deterministic with two players).</summary>
        private NetPlayerRig OtherPlayer()
        {
            NetPlayerRig best = null;
            IReadOnlyList<NetPlayerRig> all = NetPlayerRig.Spawned;
            for (int i = 0; i < all.Count; i++)
            {
                NetPlayerRig rig = all[i];
                if (rig == null || !rig.IsSpawned || rig.IsOwner || rig.ServerPlayerId.Value <= 0) continue;
                if (best == null || rig.ServerPlayerId.Value < best.ServerPlayerId.Value) best = rig;
            }

            return best;
        }

        private static float YawTo(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            d.y = 0f;
            return d.sqrMagnitude < 1e-6f ? 0f : Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static float Flat(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private NetInputSender Stream => _owner != null ? _owner.GetComponent<NetInputSender>() : null;

        private void Drive(float moveY, float yaw, float pitch)
        {
            NetInputSender stream = Stream;
            if (stream != null) stream.DriveFromCombatScript(moveY, yaw, pitch);
        }

        private float CurrentPitch { get { NetInputSender stream = Stream; return stream != null ? stream.ViewPitch : 0f; } }

        private void NextPhase(int phase)
        {
            _phase = phase;
            _phaseStartedAt = Time.unscaledTime;
            _counter = 0;
        }

        private void RunScript()
        {
            if (_scriptDone) return;
            bool enemyHitScript = _spec.IsEnemyHitOnly;
            NetPlayerRig other = enemyHitScript ? null : OtherPlayer();

            if (!_scriptStarted)
            {
                if ((!enemyHitScript && other == null) || !StartConditionMet()) return;
                if (enemyHitScript && !NetEnemyViews.TryGetNearestAliveProxy(_owner.transform.position, out _, out _)) return;
                string file = _args != null ? _args.ScriptStartFile : "";
                if (!string.IsNullOrEmpty(file))
                {
                    try
                    {
                        string text = File.ReadAllText(file);
                        if (!CombatScriptParams.TryParse(text, out _params, out string error))
                        {
                            Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " parameter error: " + error + " (defaults used)");
                            _params = CombatScriptParams.Default;
                        }
                    }
                    catch (System.Exception e)
                    {
                        Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " start file unreadable: " + e.Message + " (defaults used)");
                        _params = CombatScriptParams.Default;
                    }
                }

                _scriptStarted = true;
                _scriptStartedAt = Time.unscaledTime;
                if (enemyHitScript)
                {
                    _enemyHitBaseline = NetCombatFx.EnemyHits;
                    NextPhase(1);
                    Debug.Log(NetTuning.LogPrefix + "[client] script enemy-hit start id=" + _owner.ServerPlayerId.Value);
                    return;
                }
                _lower = _owner.ServerPlayerId.Value < other.ServerPlayerId.Value;
                NextPhase(_params.HasWalk ? 0 : 1);
                Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " start role=" + Role() + " id=" + _owner.ServerPlayerId.Value +
                          " other=" + other.ServerPlayerId.Value + " dist=" + F(Flat(_owner.transform.position, other.transform.position)));
            }

            if ((!enemyHitScript && other == null) || Time.unscaledTime - _scriptStartedAt > ScriptTimeoutSeconds)
            {
                Finish("timeout");
                return;
            }

            // optional prelude: walk along a fixed yaw (phase 0) then the role's script (phase 1+)
            if (_phase == 0)
            {
                Drive(1f, _params.WalkYaw, 0f);
                if (Time.unscaledTime - _phaseStartedAt >= _params.WalkSeconds) NextPhase(1);
                return;
            }

            if (enemyHitScript) EnemyAttacker();
            else if (_spec.IsMeleeDuelOnly) { if (_lower) MeleeAttacker(other); else MeleeDefender(other); }
            else if (_spec.IsMeleeParryOnly) { if (_lower) MeleeAttacker(other); else ParryDefender(other); }
            else if (_spec.IsRangedShotOnly) { if (_lower) RangedShooter(other); else Stand(other); }
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            else if (_spec.IsCombatAbuseOnly) { if (_lower) Stand(other); else Abuse(other); }
#endif
        }

        private string Role()
        {
            if (_spec.IsMeleeDuelOnly || _spec.IsMeleeParryOnly) return _lower ? "attacker" : "defender";
            if (_spec.IsRangedShotOnly) return _lower ? "shooter" : "target";
            return _lower ? "victim" : "abuser";
        }

        private void Finish(string why)
        {
            _scriptDone = true;
            Drive(0f, _owner.transform.eulerAngles.y, CurrentPitch);
            Debug.Log(NetTuning.LogPrefix + "[client] script " + _spec.Text + " done role=" + Role() + " why=" + why + " sent=" + _sent + " dropped=" + _dropped);
        }

        /// <summary>Stand still facing the other player and do nothing else (the target of a shot, the victim of an abuser).</summary>
        private void Stand(NetPlayerRig other)
        {
            Vector3 me = _owner.transform.position, them = other.transform.position;
            if (_params.Away > 0f && _params.HasAwayYaw && Flat(me, them) < _params.Away && Time.unscaledTime - _scriptStartedAt < AwayTimeoutSeconds)
            {
                Drive(1f, _params.AwayYaw, 0f); // the target of a shot walks to the shooting distance first
                return;
            }

            Drive(0f, YawTo(me, them), 0f);
        }

        private void EnemyAttacker()
        {
            if (NetCombatFx.EnemyHits > _enemyHitBaseline)
            {
                Finish("enemy-hit-reported");
                return;
            }
            if (!NetEnemyViews.TryGetNearestAliveProxy(_owner.transform.position, out _, out Vector3 target))
            {
                Drive(0f, _owner.transform.eulerAngles.y, CurrentPitch);
                return;
            }

            Vector3 me = _owner.transform.position;
            float yaw = YawTo(me, target);
            float dist = Flat(me, target);
            if (dist > _params.Stop)
            {
                Drive(1f, yaw, 0f);
                return;
            }

            Drive(0f, yaw, 0f);
            if (Time.unscaledTime - _phaseStartedAt < SettleSeconds) return;
            if (_counter >= _params.Presses)
            {
                Finish("enemy-presses-spent");
                return;
            }
            if (Time.unscaledTime >= _nextAt && Enqueue(CombatIntentKind.AttackPress))
            {
                _counter++;
                _nextAt = Time.unscaledTime + _params.Interval;
            }
        }

        // melee-duel, attacker: approach to the stop distance, then press every interval until the other is dead or the presses are spent.
        private void MeleeAttacker(NetPlayerRig other)
        {
            Vector3 me = _owner.transform.position, them = other.transform.position;
            float yaw = YawTo(me, them);
            float dist = Flat(me, them);
            bool otherDead = other.Rig != null && other.Rig.IsDead;

            if (_phase == 1)
            {
                Drive(dist > _params.Stop ? 1f : 0f, yaw, 0f);
                if (dist <= _params.Stop && Time.unscaledTime - _phaseStartedAt >= SettleSeconds)
                {
                    NextPhase(2);
                    _nextAt = Time.unscaledTime;
                }

                return;
            }

            if (_phase == 2)
            {
                Drive(dist > _params.Stop + 0.3f ? 1f : 0f, yaw, 0f);
                if (otherDead) { NextPhase(3); return; }
                if (_counter >= _params.Presses) { NextPhase(3); return; }
                if (Time.unscaledTime >= _nextAt)
                {
                    if (Enqueue(CombatIntentKind.AttackPress)) _counter++;
                    _nextAt = Time.unscaledTime + _params.Interval;
                }

                return;
            }

            Drive(0f, yaw, 0f);
            if (Time.unscaledTime - _phaseStartedAt >= 1.5f) Finish(otherDead ? "target-dead" : "presses-spent");
        }

        // melee-duel, defender: stands facing the attacker; the first hit lands unguarded, then it guards until a blocked hit was
        // reported (or Hold seconds), then it drops the guard and takes hits until dead; after death it tries to act (all must be refused).
        private void MeleeDefender(NetPlayerRig other)
        {
            Drive(0f, YawTo(_owner.transform.position, other.transform.position), 0f);
            bool dead = _rig != null && _rig.IsDead;
            int myId = _owner.ServerPlayerId.Value;

            if (dead && _phase < 4)
            {
                NextPhase(4);
                _nextAt = Time.unscaledTime + 0.5f;
            }

            switch (_phase)
            {
                case 1: // wait for the first damage
                    if (_rig != null && _rig.CurrentHealth < _rig.MaxHealth - 0.01f && NetCombatFx.HitsOn(myId) > 0)
                    {
                        Enqueue(CombatIntentKind.GuardDown);
                        NextPhase(2);
                    }

                    break;

                case 2: // guarding
                    if (NetCombatFx.BlockedOn(myId) >= 1 || Time.unscaledTime - _phaseStartedAt >= _params.Hold)
                    {
                        Enqueue(CombatIntentKind.GuardUp);
                        NextPhase(3);
                    }

                    break;

                case 3: // unguarded until dead
                    break;

                case 4: // dead: every action must be refused
                    if (Time.unscaledTime >= _nextAt)
                    {
                        CombatIntentKind[] attempts = { CombatIntentKind.AttackPress, CombatIntentKind.GuardDown, CombatIntentKind.SwapWeapon, CombatIntentKind.DrawStart };
                        if (_counter < attempts.Length) Enqueue(attempts[_counter]);
                        _counter++;
                        _nextAt = Time.unscaledTime + 0.4f;
                        if (_counter >= attempts.Length + 2) Finish("post-mortem-done");
                    }

                    break;
            }
        }

        // melee-parry, defender (FIX2): guards at once; after the first blocked hit was reported it releases the guard and presses it again
        // (the riposte press, well inside the window), then waits for the two strikes to play out.
        private void ParryDefender(NetPlayerRig other)
        {
            Drive(0f, YawTo(_owner.transform.position, other.transform.position), 0f);
            int myId = _owner.ServerPlayerId.Value;
            switch (_phase)
            {
                case 1:
                    Enqueue(CombatIntentKind.GuardDown);
                    NextPhase(2);
                    break;
                case 2:
                    if (NetCombatFx.BlockedOn(myId) >= 1 || Time.unscaledTime - _phaseStartedAt >= _params.Hold)
                    {
                        Enqueue(CombatIntentKind.GuardUp);
                        NextPhase(3);
                        _nextAt = Time.unscaledTime + 0.15f;
                    }

                    break;
                case 3:
                    if (Time.unscaledTime >= _nextAt)
                    {
                        Enqueue(CombatIntentKind.GuardDown);
                        NextPhase(4);
                    }

                    break;
                case 4:
                    if (Time.unscaledTime - _phaseStartedAt >= 3f) Finish("parry-done");
                    break;
            }
        }

        // ranged-shot, shooter: walk away to the distance, face the target with a drop-compensated pitch, shoot Shots full-charge arrows,
        // then one draw released too early (the server must refuse it).
        private void RangedShooter(NetPlayerRig other)
        {
            Vector3 me = _owner.transform.position, them = other.transform.position;
            float toward = YawTo(me, them);
            float dist = Flat(me, them);

            if (_phase == 1)
            {
                bool far = _params.Away <= 0f || dist >= _params.Away;
                if (far || Time.unscaledTime - _phaseStartedAt > AwayTimeoutSeconds)
                {
                    NextPhase(2);
                    return;
                }

                if (_params.HasAwayYaw) Drive(1f, _params.AwayYaw, 0f);
                else Drive(0f, toward, 0f); // the target walks away; wait for it
                return;
            }

            float pitch = AimPitch(dist, me, them);
            Drive(0f, toward, pitch);

            if (_phase == 2) // settle: the stream must carry the final yaw / pitch before the first draw
            {
                if (Time.unscaledTime - _phaseStartedAt >= SettleSeconds)
                {
                    Debug.Log(NetTuning.LogPrefix + "[client] script ranged-shot aim dist=" + F(dist) + " pitch=" + F(pitch) + " speed=" + F(_params.ProjectileSpeed) +
                              " gravity=" + F(_params.ProjectileGravity));
                    NextPhase(3);
                    _nextAt = Time.unscaledTime;
                }

                return;
            }

            if (_phase == 3) // full shots: start, hold, release, rest
            {
                if (_counter >= _params.Shots * 3) { NextPhase(4); return; }
                if (Time.unscaledTime < _nextAt) return;
                int step = _counter % 3;
                if (step == 0) { Enqueue(CombatIntentKind.DrawStart); _nextAt = Time.unscaledTime + _params.Hold; }
                else if (step == 1) { Enqueue(CombatIntentKind.DrawRelease); _nextAt = Time.unscaledTime + 1.5f; }
                else { _nextAt = Time.unscaledTime; }
                _counter++;
                return;
            }

            if (_phase == 4) // the early release
            {
                if (Time.unscaledTime < _nextAt) return;
                if (_counter == 0) { Enqueue(CombatIntentKind.DrawStart); _nextAt = Time.unscaledTime + _params.Early; _counter = 1; }
                else if (_counter == 1) { Enqueue(CombatIntentKind.DrawRelease); _nextAt = Time.unscaledTime + 1.5f; _counter = 2; }
                else Finish("shots-done");
            }
        }

        /// <summary>Unity pitch (degrees, positive = looking down) that aims at the other player with the weapon's drop compensated.</summary>
        private float AimPitch(float flatDistance, Vector3 me, Vector3 them)
        {
            if (_params.HasPitch) return _params.Pitch;
            float eye = _rig != null ? _rig.AimOrigin.y - _owner.transform.position.y : 1.6f;
            float delta = (them.y + _params.AimHeight) - (me.y + eye);
            if (_params.ProjectileSpeed > 0f &&
                ProjectileBallistics.SolveLowPitch(_params.ProjectileSpeed, _params.ProjectileGravity, flatDistance, delta, out float radians))
                return -radians * Mathf.Rad2Deg;
            return -Mathf.Atan2(delta, Mathf.Max(0.01f, flatDistance)) * Mathf.Rad2Deg;
        }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        private const int AbuseForged = 3;
        private const int AbuseFlood = 40;

        /// <summary>
        /// Development probe: everything a hostile client can do to the combat channel, in one burst, in this order: forged requests on the
        /// other player's object (identity), one valid request, its replay, a malformed kind, then a flood of well formed guard toggles. The
        /// server must count every reject on THIS connection (shared ledger), leave the victim untouched and disconnect this client once the
        /// excess reaches its threshold; whatever is sent after that is dropped.
        /// </summary>
        private void Abuse(NetPlayerRig victim)
        {
            if (_phase != 1) return;
            NextPhase(2);

            for (uint i = 1; i <= AbuseForged; i++) victim.CombatIntentRpc((int)CombatIntentKind.AttackPress, i);
            for (uint valid = 1; valid <= CombatNetLimits.AbuseProbeValidAttackPresses; valid++)
                _owner.CombatIntentRpc((int)CombatIntentKind.AttackPress, valid);   // valid (sequence 1 for the shipped count of one)
            _owner.CombatIntentRpc((int)CombatIntentKind.AttackPress, 1);   // replay
            _owner.CombatIntentRpc(99, 2);                                  // invalid kind
            for (uint seq = 3; seq < 3 + AbuseFlood; seq++)
                _owner.CombatIntentRpc((int)((seq & 1u) == 0 ? CombatIntentKind.GuardDown : CombatIntentKind.GuardUp), seq);
            Debug.Log(NetTuning.LogPrefix + "[client] script combat-abuse sent target=" + victim.ServerPlayerId.Value + " forged=" + AbuseForged +
                      " valid=" + CombatNetLimits.AbuseProbeValidAttackPresses + " replay=1 invalid=1 flood=" + AbuseFlood);
            _scriptDone = true;
        }
#endif

        private static string F(float value) { return value.ToString("F3", CultureInfo.InvariantCulture); }
    }
}
