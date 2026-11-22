using System.Collections.Generic;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Net.Rules;
using Emberdelve.Player;
using UnityEngine;

namespace Emberdelve.AI
{
    /// <summary>Server/offline adventurer intent. Navigation chooses directions; normal player
    /// movement, weapons and bound combat authority perform all movement and damage.</summary>
    [DisallowMultipleComponent]
    public sealed class RoyaleBotController : MonoBehaviour
    {
        private RunDirector _director;
        private PlayerRig _rig;
        private MeleeCombat _melee;
        private RangedCombat _ranged;
        private NavGridPathfinder _navigation;
        private readonly List<Vector3> _route = new List<Vector3>();
        private readonly List<Vector3> _patrol = new List<Vector3>();
        private int _waypoint, _patrolIndex, _pathFailures;
        private float _repath, _doorCooldown, _decision;
        private PlayerRig _target;
        private LootPickup _lootTarget;
        private float _lootScan;
        private DescentPortalSlot _descentTarget;
        private DescentPortalSlot _heldDescent;
        private double _descentStartedAt;
        private float _descentStartHealth;
        private float _portalScan, _exitScan;
        private ExtractionPoint _exitTarget;
        private ExtractionHoldWindow _exitHold;
        private HealthComponent _holdHealth;
        private bool _holdingExit;
        private double _intentClock;
        public bool SeekingExtraction { get; private set; }
        public bool HoldingExtraction => _holdingExit;
        public bool SeekingDescent { get; private set; }
        public bool SeekingSafeZone { get; private set; }
        public int Floor { get; private set; }
        public int TargetPlayerId => _target != null ? _target.PlayerId : -1;

        public bool Configure(RunDirector director, PlayerRig rig, int floor, NavGridData navigation,
            IReadOnlyList<Vector3> patrol)
        {
            var session = director != null ? director.SessionOf(rig) : null;
            if (NetRole.IsClientOnly || session == null || !session.IsBot || navigation == null
                || rig == null || rig.Movement == null || rig.Look == null || rig.gameObject != gameObject
                || floor < 1 || floor > 3 || (floor != session.Floor && !(rig.IsInFloorTransit && floor == session.Floor + 1))) return false;
            StopIntent();
            _director = director; _rig = rig; Floor = floor;
            _navigation = new NavGridPathfinder(navigation);
            _melee = rig.GetComponent<MeleeCombat>(); _ranged = rig.GetComponent<RangedCombat>();
            rig.SetInputEnabled(false); rig.Movement.ExternalDriven = true;
            if (_melee != null) _melee.RemoteDriven = true;
            if (_ranged != null) _ranged.RemoteDriven = true;
            _patrol.Clear();
            if (patrol != null) foreach (var point in patrol) if (_navigation.IsWalkableWorld(point)) _patrol.Add(point);
            _patrolIndex = _patrol.Count > 0 ? Mathf.Abs(rig.PlayerId % _patrol.Count) : 0;
            _decision = _repath = 0f;
            return true;
        }

        private void FixedUpdate() => Step(Time.fixedDeltaTime);
        private void OnDisable() => StopIntent();

        public void Step(float dt)
        {
            if (float.IsNaN(dt) || float.IsInfinity(dt) || dt <= 0f || dt > .1f) return;
            var session = _director != null && _rig != null ? _director.SessionOf(_rig) : null;
            if (!NetRole.RunsSimulation || session == null || !session.IsBot || !session.IsRunning
                || session.Floor != Floor || _rig.IsDead || !_rig.IsInPlay || _navigation == null
                || !_director.IsRoyaleFloorOpen(Floor) || _director.State != RunState.Running)
            { StopIntent(); return; }
            _intentClock += dt;
            _decision -= dt; _repath -= dt; _doorCooldown -= dt; _lootScan -= dt; _portalScan -= dt; _exitScan -= dt;
            var encounter = _director.FloorCoordinator?.EncounterFor(Floor);
            Vector2 zoneCenter = default;
            float safeRadius = 0f;
            if (encounter != null && _director.TryGetRoyaleFloorClock(Floor, out float clock, out _)
                && encounter.Swarm.TrySample(clock + 10f, out var zone))
            {
                zoneCenter = new Vector2((float)zone.X, (float)zone.Z);
                safeRadius = Mathf.Max(0f, (float)zone.Radius - 4f);
                // Enter retreat before the boundary reaches us; leave only after moving farther in.
                float threshold = SeekingSafeZone ? safeRadius + .5f : Mathf.Max(0f, (float)zone.Radius - 2f);
                bool retreat = Vector2.Distance(new Vector2(transform.position.x, transform.position.z), zoneCenter) > threshold;
                if (retreat != SeekingSafeZone) { SeekingSafeZone = retreat; _repath = 0f; }
            }
            else if (SeekingSafeZone) { SeekingSafeZone = false; _repath = 0f; }
            if (SeekingSafeZone) _target = null;
            if (!CanTarget(_target)) _target = null;
            if (!SeekingSafeZone && _decision <= 0f)
            {
                _decision = .25f;
                PlayerRig nearest = null; float distance = 24f * 24f;
                foreach (var other in _director.Players)
                {
                    if (!CanTarget(other.Rig)) continue;
                    float candidate = (other.Rig.transform.position - transform.position).sqrMagnitude;
                    if (candidate < distance && CanSee(other.Rig)) { nearest = other.Rig; distance = candidate; }
                }
                if (nearest != _target) { _target = nearest; _repath = 0f; }
            }
            if (!IsAvailablePortal(_descentTarget)) _descentTarget = null;
            if (_descentTarget == null && encounter != null && _portalScan <= 0f)
            {
                _portalScan = .5f;
                float nearest = 24f * 24f;
                foreach (var group in encounter.Groups)
                    foreach (var slot in group.GetComponentsInChildren<DescentPortalSlot>())
                    {
                        if (!IsAvailablePortal(slot)) continue;
                        float distance = (slot.transform.position - _rig.AimOrigin).sqrMagnitude;
                        if (distance < nearest && CanSeePoint(slot.transform, slot.transform.position))
                        { nearest = distance; _descentTarget = slot; }
                    }
            }
            bool urgent = _director.TryGetRoyaleFloorClock(Floor, out _, out float remaining) && remaining <= 60f;
            bool descend = _descentTarget != null && (_target == null || SeekingSafeZone || urgent);
            if (descend != SeekingDescent) { SeekingDescent = descend; _repath = 0f; }
            if (SeekingDescent)
            {
                // Encounter portals are inside the final safe circle. A known exit is a safe
                // retreat goal too; last-minute survival takes precedence over chasing a fight.
                _target = null;
                if (_descentTarget.CanInteract(_rig, out _))
                {
                    if (!_descentTarget.RequiresHold || StepDescentHold())
                        if (_descentTarget.Group.TryEnter(_rig, _descentTarget.Slot, out _)) StopIntent();
                    return;
                }
            }
            _heldDescent = null;
            if (_exitTarget != null && (!_exitTarget.isActiveAndEnabled || _exitTarget.Floor != Floor)) _exitTarget = null;
            var finalExit = encounter?.FinalExit;
            if (_exitTarget == null && _exitScan <= 0f && Floor == 3 && finalExit != null && finalExit.isActiveAndEnabled
                && (finalExit.transform.position - _rig.AimOrigin).sqrMagnitude < 24f * 24f
                && CanSeePoint(finalExit.transform, finalExit.transform.position)) _exitTarget = finalExit;
            bool extract = _exitTarget != null && (_target == null || SeekingSafeZone || urgent)
                && _director.ExtractionAllowed(_rig, out _);
            if (extract != SeekingExtraction) { SeekingExtraction = extract; _repath = 0f; }
            if (SeekingExtraction)
            {
                _target = null;
                if (_exitTarget.CanInteract(_rig, out _))
                {
                    StepExtractionHold();
                    return;
                }
            }
            CancelExtractionHold();
            if (SeekingSafeZone || SeekingDescent || SeekingExtraction || _target != null) _lootTarget = null;
            else
            {
                if (!CanSeekLoot(_lootTarget, encounter)) _lootTarget = null;
                if (_lootTarget == null && _lootScan <= 0f)
                {
                    _lootScan = 1f;
                    float nearest = 12f * 12f;
                    foreach (var pickup in FindObjectsByType<LootPickup>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    {
                        if (!CanSeekLoot(pickup, encounter)) continue;
                        float distance = (pickup.HomePosition - transform.position).sqrMagnitude;
                        if (distance < nearest && CanSeePoint(pickup.transform, pickup.HomePosition))
                        { nearest = distance; _lootTarget = pickup; }
                    }
                    if (_lootTarget != null) _repath = 0f;
                }
                if (_lootTarget != null && _lootTarget.CanInteract(_rig, out _))
                {
                    // This controller runs only for the authority's admitted bot. The existing
                    // pickup transaction revalidates its exact actor against the loot host and
                    // owns UID/partial-stack conservation. Public network loot sync observes it.
                    if (NetRole.IsOffline) _lootTarget.OnInteractInstant(_rig);
                    else _lootTarget.TryCollectFromNetwork(_rig);
                    _lootTarget = null; _lootScan = 2f; _repath = 0f;
                }
            }
            bool visible = _target != null && CanSee(_target);
            Vector3 destination = _target != null ? _target.transform.position : transform.position;
            if (SeekingExtraction) destination = _exitTarget.transform.position;
            else if (SeekingDescent) destination = _descentTarget.transform.position;
            else if (_lootTarget != null) destination = _lootTarget.HomePosition;
            else if (!SeekingSafeZone && _target == null && _patrol.Count > 0)
            {
                destination = _patrol[_patrolIndex];
                if (FlatDistance(transform.position, destination) < .8f)
                { _patrolIndex = (_patrolIndex + 1) % _patrol.Count; destination = _patrol[_patrolIndex]; _repath = 0f; }
            }
            if (_repath <= 0f)
            {
                _repath = .6f; _waypoint = 0;
                bool found = SeekingSafeZone && !SeekingDescent && !SeekingExtraction
                    ? _navigation.TryFindPathInsideCircle(transform.position, zoneCenter, safeRadius, _route)
                    : _navigation.TryFindPath(transform.position, destination, _route);
                if (found)
                {
                    _pathFailures = 0;
                    // The route starts at the occupied cell centre, not at our actual position.
                    // Following that point again on every refresh would reverse ongoing movement.
                    if (_route.Count > 1) _waypoint = 1;
                }
                else if ((!SeekingSafeZone || SeekingDescent || SeekingExtraction) && ++_pathFailures >= 3 && _target == null)
                {
                    _exitTarget = null; SeekingExtraction = false; _exitScan = 3f;
                    _lootTarget = null; _lootScan = 3f;
                    _descentTarget = null; SeekingDescent = false; _portalScan = 3f;
                    if (_patrol.Count > 0) _patrolIndex = (_patrolIndex + 1) % _patrol.Count;
                    _pathFailures = 0;
                }
            }
            while (_waypoint < _route.Count && FlatDistance(transform.position, _route[_waypoint]) < .45f) _waypoint++;
            Vector3 direction = _waypoint < _route.Count ? _route[_waypoint] - transform.position : Vector3.zero;
            direction.y = 0f; direction = direction.normalized;
            Vector3 aim = visible ? _target.AimOrigin - _rig.AimOrigin : direction;
            float yaw = aim.sqrMagnitude > .001f ? Mathf.Atan2(aim.x, aim.z) * Mathf.Rad2Deg : transform.eulerAngles.y;
            float pitch = aim.sqrMagnitude > .001f ? -Mathf.Atan2(aim.y, new Vector2(aim.x, aim.z).magnitude) * Mathf.Rad2Deg : 0f;
            _rig.Look.ApplyAimPose(yaw, pitch);
            float range = _target != null ? Vector3.Distance(transform.position, _target.transform.position) : float.PositiveInfinity;
            var weapon = _rig.ActiveWeapon;
            if (weapon != null && weapon.IsRanged && _ranged != null && _ranged.Magazine == 0
                && _ranged.Reserve == 0 && _rig.Equipment != null)
            {
                int alternative = 1 - _rig.Equipment.ActiveWeaponSet;
                var backup = _rig.Equipment.WeaponOfSet(alternative);
                if (backup != null && !backup.IsRanged && _rig.Inventory.RequestWeaponSet(alternative, out _))
                    weapon = _rig.ActiveWeapon;
            }
            float desiredRange = weapon != null && weapon.IsRanged ? 9f : Mathf.Max(.7f, (weapon != null ? weapon.reach : 1f) * .75f);
            bool attacking = visible && range <= desiredRange
                && (weapon == null || !weapon.IsRanged || (_ranged != null && (_ranged.Magazine > 0 || _ranged.Reserve > 0)));
            if (attacking) direction = Vector3.zero;
            if (_melee != null) { _melee.SetRemoteGuard(false); _melee.SetRemoteAttack(attacking && weapon != null && !weapon.IsRanged); }
            if (_ranged != null)
            {
                if (!attacking || weapon == null || !weapon.IsRanged) _ranged.CancelDraw();
                else if (_ranged.Magazine == 0) _ranged.ServerRequestReload();
                else if (!_ranged.IsDrawing) _ranged.ServerBeginDraw(out _);
                else if (_ranged.Charge >= .95f) _ranged.ServerReleaseDraw(out _, out _, out _);
            }
            TryOpenDoor(direction);
            Vector3 local = Quaternion.Inverse(Quaternion.Euler(0f, yaw, 0f)) * direction;
            var frame = new MoveInputFrame { MoveX = local.x, MoveY = local.z, Yaw = yaw, Pitch = pitch,
                Flags = MoveInputFrame.MakeFlags(false, false, false) };
            _rig.Movement.Step(in frame, dt);
        }

        private bool StepDescentHold()
        {
            if (_melee != null) { _melee.SetRemoteAttack(false); _melee.SetRemoteGuard(false); }
            if (_ranged != null) _ranged.CancelDraw();
            float health = _rig.CurrentHealth;
            if (_heldDescent != _descentTarget || health < _descentStartHealth)
            {
                _heldDescent = _descentTarget;
                _descentStartedAt = _intentClock;
                _descentStartHealth = health;
                CancelExtractionHold();
            }
            return _intentClock - _descentStartedAt >= _descentTarget.HoldDuration;
        }

        private void StepExtractionHold()
        {
            if (_melee != null) { _melee.SetRemoteAttack(false); _melee.SetRemoteGuard(false); }
            if (_ranged != null) _ranged.CancelDraw();
            var position = transform.position;
            if (!_holdingExit)
            {
                if (!ExtractionHoldWindow.TryStart(1, _intentClock, _exitTarget.HoldDuration,
                    position.x, position.y, position.z, out _exitHold)) return;
                _holdingExit = true;
                _holdHealth = _rig.Health;
                if (_holdHealth != null) _holdHealth.Damaged += OnExtractionDamage;
            }
            var verdict = _exitHold.Evaluate(1, _intentClock, position.x, position.y, position.z);
            if (verdict == ExtractionHoldVerdict.TooEarly) return;
            CancelExtractionHold();
            // Only the admitted authority bot reaches here; no client-owner RPC impersonation.
            // The ordinary exit and director revalidate floor, reach, deadline and result state.
            if (verdict == ExtractionHoldVerdict.Valid && _exitTarget.CanInteract(_rig, out _))
                _director.BeginExtraction(_rig, out _);
        }

        private void OnExtractionDamage(DamageInfo damage) => CancelExtractionHold();
        private void CancelExtractionHold()
        {
            if (_holdHealth != null) _holdHealth.Damaged -= OnExtractionDamage;
            _holdHealth = null; _holdingExit = false;
        }

        private bool CanTarget(PlayerRig candidate)
        {
            if (candidate == null || candidate == _rig || candidate.PartyId == _rig.PartyId || candidate.IsDead || !candidate.IsInPlay) return false;
            var session = _director.SessionOf(candidate);
            return session != null && session.IsRunning && session.Floor == Floor && CombatTeams.AreHostile(_rig, candidate);
        }

        private bool IsAvailablePortal(DescentPortalSlot slot) => slot != null && slot.isActiveAndEnabled
            && slot.Group != null && slot.Group.Floor == Floor && !slot.Group.SlotUsed(slot.Slot)
            && slot.Group.IsAvailableToParty(_rig.PartyId) && _director.CanQueueDescent(_rig, Floor, out _);

        private bool CanSee(PlayerRig target)
            => CanSeePoint(target.transform, target.AimOrigin);

        private bool CanSeekLoot(LootPickup pickup, RoyaleFloorEncounter encounter)
        {
            if (pickup == null || !pickup.isActiveAndEnabled || pickup.IsSpent || pickup.Item == null
                || !_navigation.IsWalkableWorld(pickup.HomePosition)) return false;
            bool room = _rig.Inventory.FindFreeOrigin(pickup.Item.EffectiveFootprint).x >= 0;
            if (!room && pickup.Instance == null && !pickup.Item.IsEquippable)
                foreach (var slot in _rig.Inventory.Slots)
                    if (slot.Instance == null && slot.Item == pickup.Item && slot.Count < pickup.Item.StackLimitForFootprint)
                    { room = true; break; }
            if (!room) return false;
            // Never follow a visible item into the approaching zone or another floor's grid.
            return encounter == null || !_director.TryGetRoyaleFloorClock(Floor, out float elapsed, out _)
                || !encounter.Swarm.TrySample(elapsed + 10f, out var zone)
                || Vector2.Distance(new Vector2(pickup.HomePosition.x, pickup.HomePosition.z),
                    new Vector2((float)zone.X, (float)zone.Z)) <= Mathf.Max(0f, (float)zone.Radius - 2f);
        }

        private bool CanSeePoint(Transform target, Vector3 point)
        {
            Vector3 delta = point - _rig.AimOrigin;
            foreach (var hit in Physics.RaycastAll(_rig.AimOrigin, delta.normalized, delta.magnitude,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                if (!hit.transform.IsChildOf(transform) && !hit.transform.IsChildOf(target)) return false;
            return true;
        }

        private void TryOpenDoor(Vector3 direction)
        {
            if (_doorCooldown > 0f || direction.sqrMagnitude < .1f) return;
            if (!Physics.Raycast(transform.position + Vector3.up, direction, out var hit, 2f,
                Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return;
            var door = hit.collider.GetComponentInParent<DungeonDoorInteractable>();
            if (door == null || !door.AllowsEnemyOpening || door.IsOpen || !door.CanInteract(_rig, out _)) return;
            _doorCooldown = 1f;
            if (NetRole.IsOffline) door.OnInteractInstant(_rig);
            else if (GameServices.TryGet<IDoorAiAuthority>(out var authority))
            {
                // The current network door protocol uses a global ID. Never route a staged-floor
                // intent to a different floor with the same ID; floor-scoped wire identity is pending.
                foreach (var other in FindObjectsByType<DungeonDoorInteractable>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                    if (other != door && other.DoorId == door.DoorId) return;
                authority.RequestEnemyDoorOpen(door.DoorId);
            }
        }

        private void StopIntent()
        {
            _heldDescent = null;
            CancelExtractionHold();
            SeekingExtraction = false; _exitTarget = null; _exitScan = 0f;
            SeekingSafeZone = false;
            SeekingDescent = false; _descentTarget = null; _portalScan = 0f;
            _lootTarget = null; _lootScan = 0f;
            _target = null; _route.Clear(); _waypoint = 0; _repath = 0f;
            if (_melee != null) { _melee.SetRemoteAttack(false); _melee.SetRemoteGuard(false); }
            if (_ranged != null) _ranged.CancelDraw();
        }
        private static float FlatDistance(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;
    }
}
