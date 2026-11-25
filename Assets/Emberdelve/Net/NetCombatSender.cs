using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Emberdelve.Ballistics;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
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
        private bool _drawCancelPending;
        private bool _guardHeld;
        private bool _attackHeld;
        private float _nextAttackRefresh;
        private float _nextHeldReloadRequest;

        private void ReleaseHeldAttack()
        {
            // Do not deliver queued refreshes after a modal UI or a released button.
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                var intent = _queue.Dequeue();
                if (intent != CombatIntentKind.AttackHold) _queue.Enqueue(intent);
            }
            if (_attackHeld && Enqueue(CombatIntentKind.AttackRelease)) _attackHeld = false;
        }

        private void CancelDrawInput()
        {
            bool cancel = _drawHeld || _drawCancelPending;
            int count = _queue.Count;
            for (int i = 0; i < count; i++)
            {
                var intent = _queue.Dequeue();
                if (intent == CombatIntentKind.DrawStart || intent == CombatIntentKind.DrawRelease)
                    cancel = true;
                else if (intent != CombatIntentKind.Reload) _queue.Enqueue(intent);
            }
            _drawHeld = false;
            // Separate priority slot: a full ordinary queue must not lose or delay cancellation.
            _drawCancelPending = cancel;
        }

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
            if ((!_drawCancelPending && _queue.Count == 0) || _owner == null || !_owner.IsSpawned) return;
            float now = Time.unscaledTime;
            if (now - _lastSendAt < CombatNetLimits.ClientMinIntervalSeconds) return;

            CombatIntentKind kind = _drawCancelPending ? CombatIntentKind.DrawCancel : _queue.Dequeue();
            if (_drawCancelPending) _drawCancelPending = false;
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
                    CancelDrawInput();
                    ReleaseHeldAttack();
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

            if (_rig.WeaponInputSuppressed || _rig.IsDead || _rig.IsExtracted || !input.GameplayInputEnabled
                || input.QuickWheelCaptured || InventoryInputFocus.OwnsEscape)
            {
                ReleaseHeldAttack();
                if (_guardHeld && Enqueue(CombatIntentKind.GuardUp)) _guardHeld = false;
                CancelDrawInput();
                return;
            }
            if (input.SwapPressed)
            {
                ReleaseHeldAttack();
                // Server swap lowers guard; forget the old latch so held RMB can raise the new kit.
                if (Enqueue(CombatIntentKind.SwapWeapon)) _guardHeld = false;
                CancelDrawInput();
                return;
            }
            // Replicated equipment is already available; do not burn the first click probing WrongWeapon.
            if (_rig.ActiveWeapon != null) _rangedMode = _rig.ActiveWeapon.IsRanged;

            var skills = _rig.GetComponent<Emberdelve.Skills.SkillCaster>();
            if (skills != null) skills.PollSkillInput();
            if (skills != null && skills.SuppressPrimary)
            {
                ReleaseHeldAttack();
                if (_guardHeld && Enqueue(CombatIntentKind.GuardUp)) _guardHeld = false;
                CancelDrawInput();
                return;
            }

            bool guard = input.SecondaryHeld && (!_rangedMode || (_rig.ActiveWeapon != null && _rig.ActiveWeapon.CanRangedGuard));
            if (guard != _guardHeld)
            {
                if (Enqueue(guard ? CombatIntentKind.GuardDown : CombatIntentKind.GuardUp)) _guardHeld = guard;
            }
            if (_rangedMode)
            {
                ReleaseHeldAttack();
                if (guard) { CancelDrawInput(); return; }
                bool heldReload = _rig.ActiveWeapon != null && _rig.ActiveWeapon.reloadWhileHeld;
                if (!heldReload)
                {
                    if (input.ReloadPressed) Enqueue(CombatIntentKind.Reload);
                }
                else if (!_drawHeld && !input.PrimaryPressed && input.ReloadHeld && Time.unscaledTime >= _nextHeldReloadRequest)
                {
                    var ranged = _rig.GetComponent<RangedCombat>();
                    // Wait for the server's accepted shot/ammo snapshot; never guess a spent round.
                    if (ranged != null && ranged.HasAuthoritativeAmmo && !ranged.IsReloading
                        && ranged.Magazine < _rig.ActiveWeapon.magazineSize && ranged.Reserve > 0
                        && Enqueue(CombatIntentKind.Reload))
                        _nextHeldReloadRequest = Time.unscaledTime + .25f;
                }
                if (input.PrimaryPressed && !_drawHeld) { _drawHeld = Enqueue(CombatIntentKind.DrawStart); }
                if (input.PrimaryReleased && _drawHeld) { Enqueue(CombatIntentKind.DrawRelease); _drawHeld = false; }
            }
            else if (input.PrimaryHeld)
            {
                if ((!_attackHeld || Time.unscaledTime >= _nextAttackRefresh) && Enqueue(CombatIntentKind.AttackHold))
                {
                    _attackHeld = true;
                    _nextAttackRefresh = Time.unscaledTime + 0.25f;
                }
            }
            else ReleaseHeldAttack();

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
            if (_spec.IsMeleeDuelOnly && otherDead)
            {
                // Continue into empty space using the same leased intent as real held input.
                if (_phase == 3)
                {
                    if (Time.unscaledTime - _phaseStartedAt < 3f)
                    {
                        if (Time.unscaledTime >= _nextAt && Enqueue(CombatIntentKind.AttackHold))
                            _nextAt = Time.unscaledTime + 0.25f;
                    }
                    else if (Enqueue(CombatIntentKind.AttackRelease)) NextPhase(4);
                    return;
                }
                if (Time.unscaledTime - _phaseStartedAt >= 2f) Finish("held-release-verified-window");
                return;
            }
            if (Time.unscaledTime - _phaseStartedAt >= 1.5f) Finish(otherDead ? "target-dead" : "presses-spent");
        }

        // melee-duel, defender: stands facing the attacker; the first hit lands unguarded, then it guards until a blocked hit was
        // reported (or Hold seconds), then it drops the guard and takes hits until dead; after death it tries to act (all must be refused).
        private void MeleeDefender(NetPlayerRig other)
        {
            // Blocks are physical: put the left-hand shield into the incoming slash by turning towards the side it comes from
            // (right-to-left: turn left, left-to-right: turn right; the CombatFlow gate measures both windows).
            _turnIntoStrike = TurnIntoStrike(other, _rig != null ? _rig.ActiveWeapon : null, _turnIntoStrike, ShieldTurnIntoStrike, ShieldTurnIntoStrike, ShieldTurnIntoHighCut, 0f, ref _pitchIntoStrike);
            Drive(0f, YawTo(_owner.transform.position, other.transform.position) + (_phase == 2 ? _turnIntoStrike : 0f), _phase == 2 ? _pitchIntoStrike : 0f);
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
        // Turn (degrees) that puts the blade / the shield into a horizontal slash; the sign follows the side the slash comes from.
        // Measured by the CombatFlow gate at 1.1 m and 1.5 m: a right-hand blade needs a deep left turn against a right-to-left slash.
        private const float BladeTurnLeft = 70f, BladeTurnRight = 50f, ShieldTurnIntoStrike = 40f;
        // A steep cut (Overhead by its measured path, like the reference short sword's opener) comes down from above: the guard is
        // raised into it. Measured by the CombatFlow gate ("REAL ... guard catches") at 1.1 m and 1.5 m: the blade catches it looking
        // 40 degrees up and 0-20 degrees to the right (re-measured 25 Nov after the cut poses changed: blade at pitch -40 catches
        // at yaw -5..20 at 1.1 m and -30..20 at 1.5 m), the shield looking 40 degrees up straight at the attacker (yaw -40..80).
        private const float HighGuardPitch = -40f, BladeTurnIntoHighCut = 5f, ShieldTurnIntoHighCut = 0f;
        // A cut "comes down from above" when its hit window opens with the blade raised this far (degrees, negative = up),
        // whatever kind its whole path is classified as.
        private const float RaisedBladePitch = -30f;

        private static bool ComesDownFromAbove(Emberdelve.Data.SwingPatternData pattern) =>
            pattern.kind == Emberdelve.Data.SwingKind.Overhead
            || (pattern.kind != Emberdelve.Data.SwingKind.Thrust && pattern.keys != null && pattern.keys.Length > 1 && pattern.keys[0].bladePitch <= RaisedBladePitch);
        // Before the attacker has swung, the defender expects the opener of the attacker's chain.
        private float _turnIntoStrike = float.NaN;
        private float _pitchIntoStrike = float.NaN;

        /// <summary>
        /// The guard pose for the attacker's current swing (or, before it swings, the opener of its chain): the turn for a
        /// right-to-left or left-to-right slash, and a raised view for a steep cut. <paramref name="pitch"/> is the view pitch to hold.
        /// </summary>
        private static float TurnIntoStrike(NetPlayerRig attacker, Emberdelve.Data.WeaponData sameKitWeapon, float current, float leftTurn, float rightTurn, float highCutTurn, float flatPitch, ref float pitch)
        {
            // A client does not always hold the remote attacker's real weapon data (a remote rig can report its default kit); the duel
            // scripts give both players the same class kit, so the defender's own weapon names the same patterns and the same chain.
            Emberdelve.Data.WeaponData theirs = attacker != null && attacker.Rig != null ? attacker.Rig.ActiveWeapon : null;
            Emberdelve.Data.SwingPatternData incoming = null;
            string id = null;
            if (attacker != null)
            {
                AvatarCombatWire pose = attacker.AvatarCombat.Value;
                if (pose.Swing) id = pose.Pattern.ToString();
            }
            foreach (Emberdelve.Data.WeaponData weapon in new[] { theirs, sameKitWeapon })
            {
                if (incoming != null || weapon == null || weapon.swingPatterns == null) continue;
                if (id != null)
                {
                    foreach (Emberdelve.Data.SwingPatternData p in weapon.swingPatterns)
                        if (p != null && p.patternId == id) { incoming = p; break; }
                }
                else if (float.IsNaN(current) && weapon.HasComboChain)
                {
                    int opener = weapon.comboSteps[0].patternIndex;
                    if (opener >= 0 && opener < weapon.swingPatterns.Length) incoming = weapon.swingPatterns[opener];
                }
            }
            if (float.IsNaN(current)) { current = -leftTurn; pitch = flatPitch; }
            if (float.IsNaN(pitch)) pitch = flatPitch;
            if (incoming != null)
            {
                if (ComesDownFromAbove(incoming)) { pitch = HighGuardPitch; return highCutTurn; }
                if (incoming.kind == Emberdelve.Data.SwingKind.HorizontalRightToLeft) { pitch = flatPitch; return -leftTurn; }
                if (incoming.kind == Emberdelve.Data.SwingKind.HorizontalLeftToRight) { pitch = flatPitch; return rightTurn; }
                return current;
            }
            if (id != null && id.EndsWith("_rl")) { pitch = flatPitch; return -leftTurn; }
            if (id != null && id.EndsWith("_lr")) { pitch = flatPitch; return rightTurn; }
            return current;
        }

        private void ParryDefender(NetPlayerRig other)
        {
            // Use the same inventory request as the UI: the response must come from blade contact.
            if (_phase == 1 && _rig != null && _rig.Defense.HasShield)
            {
                var inventory = GetComponent<NetInventoryClient>();
                var worn = _rig.Equipment.EquippedInstance(EquipmentSlot.OffHand);
                if (inventory != null && worn != null && inventory.TryCreateLocalCommand(
                    InventoryCommandKind.Unequip, 0, 0, Vector2Int.zero, EquipmentSlot.OffHand,
                    worn.Uid, System.Guid.Empty, out InventoryCommand command, out _))
                    inventory.Submit(_rig, command);
                return;
            }
            // Blocks are physical: the defender catches the chain's right-to-left opener on its blade by turning into the strike
            // (the CombatFlow gate measures the catching window, about 35-80 degrees to the left); squarely facing takes it in the side.
            _turnIntoStrike = TurnIntoStrike(other, _rig != null ? _rig.ActiveWeapon : null, _turnIntoStrike, BladeTurnLeft, BladeTurnRight, BladeTurnIntoHighCut, 21.6f, ref _pitchIntoStrike);
            Drive(0f, YawTo(_owner.transform.position, other.transform.position) + (_phase <= 2 ? _turnIntoStrike : 0f), _phase <= 2 ? _pitchIntoStrike : 0f);
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
                // The Warrior fixture starts with its sword. Request the ordinary server-owned
                // weapon swap once, then wait for replication before operating the crossbow.
                if (_rig.ActiveWeapon == null || !_rig.ActiveWeapon.IsRanged)
                {
                    if (_counter == 0 && Enqueue(CombatIntentKind.SwapWeapon)) _counter++;
                    return;
                }
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
                    NextPhase(20);
                    _nextAt = Time.unscaledTime;
                }

                return;
            }

            if (_phase == 20) // charged cancellation, duplicate cancel, then a stale release must never shoot
            {
                if (Time.unscaledTime < _nextAt) return;
                if (_counter == 0) { _drawHeld = Enqueue(CombatIntentKind.DrawStart); _nextAt = Time.unscaledTime + _params.Hold; }
                else if (_counter == 1) { CancelDrawInput(); _nextAt = Time.unscaledTime + .3f; }
                else if (_counter == 2) { Enqueue(CombatIntentKind.DrawCancel); _nextAt = Time.unscaledTime + .3f; }
                else if (_counter == 3) { Enqueue(CombatIntentKind.DrawRelease); _nextAt = Time.unscaledTime + .3f; }
                else { NextPhase(3); _nextAt = Time.unscaledTime; return; }
                _counter++;
                return;
            }

            if (_phase == 3) // full shots: start, hold, release, rest
            {
                if (_rig.ActiveWeapon != null && _rig.ActiveWeapon.magazineSize == 1)
                {
                    SingleShotReloadProof();
                    return;
                }
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

        private void SingleShotReloadProof()
        {
            if (Time.unscaledTime < _nextAt) return;
            CombatIntentKind intent;
            float delay;
            switch (_counter)
            {
                case 0: intent = CombatIntentKind.DrawStart; delay = _params.Hold; break;
                case 1: intent = CombatIntentKind.DrawRelease; delay = 1.5f; break;
                // An empty trigger must neither shoot nor implicitly reload.
                case 2: intent = CombatIntentKind.DrawStart; delay = .3f; break;
                case 3: intent = CombatIntentKind.DrawRelease; delay = .3f; break;
                case 4: intent = CombatIntentKind.Reload; delay = .35f; break;
                case 5: intent = CombatIntentKind.DrawStart; delay = .3f; break;
                case 6: intent = CombatIntentKind.DrawRelease; delay = .3f; break;
                // Repeated R must not restart the timer or credit a second bolt.
                case 7: intent = CombatIntentKind.Reload; delay = _rig.ActiveWeapon.reloadTime + 1f; break;
                case 8: intent = CombatIntentKind.DrawStart; delay = _params.Hold; break;
                case 9: intent = CombatIntentKind.DrawRelease; delay = 1.5f; break;
                default: Finish("single-shot-reload-done"); return;
            }
            if (!Enqueue(intent)) return;
            _counter++;
            _nextAt = Time.unscaledTime + delay;
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
