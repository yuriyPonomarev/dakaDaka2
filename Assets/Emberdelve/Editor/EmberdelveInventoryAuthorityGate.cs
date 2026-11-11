using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>Focused local inventory-command validation gate. It is not a network-authentication test.</summary>
    [InitializeOnLoad]
    public static class EmberdelveInventoryAuthorityGate
    {
        private const string Pending = "Emberdelve.InventoryAuthority.Pending";

        static EmberdelveInventoryAuthorityGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("InventoryAuthority_Gate").AddComponent<InventoryAuthorityHarness>();
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

    public sealed class InventoryAuthorityHarness : MonoBehaviour
    {
        private int _checks;
        private int _failures;
        private PlayerRig _rig;
        private Inventory _bag;
        private Equipment _equipment;
        private IInventoryAuthority _authority;
        private ItemCatalog _catalog;
        private int _inventoryEvents;

        private void Check(bool ok, string label)
        {
            _checks++;
            if (ok) return;
            _failures++;
            Debug.LogError("[InventoryAuthority] mismatch " + label);
        }

        private IEnumerator Start()
        {
            for (int i = 0; i < 10; i++) yield return null;
            try { Execute(); }
            catch (Exception e) { _failures++; Debug.LogException(e); }
            finally { GameEvents.InventoryChanged -= OnInventoryChanged; }

            Debug.Log("[Emberdelve][InventoryAuthority] " + (_failures == 0 ? "PASS" : "FAIL") +
                " checks=" + _checks + " failures=" + _failures);
            EmberdelveGateExit.Exit(_failures == 0 ? 0 : 1);
        }

        private void Execute()
        {
            _rig = FindFirstObjectByType<PlayerRig>();
            Check(_rig != null && !_rig.IsDead && _rig.IsInPlay && _rig.Inventory != null && _rig.Equipment != null,
                "live level rig has a live bag/equipment pair");
            if (_rig == null || _rig.IsDead || _rig.Inventory == null || _rig.Equipment == null) return;

            if (GameServices.TryGet<RunDirector>(out RunDirector director) && director.Config != null)
            {
                CharacterClassData archer = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
                Check(archer != null, "real Archer class data is available for the ammunition fixture");
                if (archer != null && _rig.Loadout != null && (_rig.Class != archer || _rig.Loadout.AppliedClass != archer))
                    _rig.ApplyClass(archer, director.Config);
                Check(archer != null && _rig.Class == archer && _rig.Loadout != null && _rig.Loadout.AppliedClass == archer,
                    "Archer test rig class and loadout are coherently applied through PlayerRig");
            }

            _bag = _rig.Inventory;
            _equipment = _rig.Equipment;
            _catalog = _bag.Catalog;
            string catalogReason = "missing catalog";
            Check(_catalog != null && !_catalog.IsTestCatalog && _catalog.IsValid(out catalogReason),
                "live bag has the valid production item catalog: " + catalogReason);
            if (_catalog == null) return;

            _authority = _bag.CommandAuthority;
            Check(_authority != null, "bag exposes its bound local command authority");
            if (_authority == null) return;

            _equipment.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);
            GameEvents.InventoryChanged += OnInventoryChanged;

            ItemData coin = Resolve("ember_coin");
            ItemData relic = Resolve("ashen_relic");
            ItemData boots = Resolve("footpad_boots");
            ItemData draught = Resolve("blood_draught");
            ItemData arrows = Resolve("arrow_bundle");
            if (coin == null || relic == null || boots == null || draught == null || arrows == null) return;

            CheckOwnerAndSequenceRules(coin, relic);
            CheckSwapAndMerge(coin, relic);
            CheckEquipmentIdentity(boots);
            CheckAtomicFullBagUnequip(coin, boots);
            CheckConsumableAuthority(coin, draught, arrows);
            CheckDeadAndExtractedOwnerRules();
        }

        private void CheckOwnerAndSequenceRules(ItemData coin, ItemData relic)
        {
            _bag.Clear();
            Check(_bag.TryAddAt(coin, 1, Vector2Int.zero, out string addReason), "place command-test coin: " + addReason);
            InventorySlot coinSlot = Find(coin);
            ulong before = _bag.Revision;
            InventoryCommand move = Create(InventoryCommandKind.Move, coinSlot.StackId, 0,
                new Vector2Int(2, 0), EquipmentSlot.None, GearUid(coinSlot), Guid.Empty);

            InventoryCommand wrongOwner = Copy(move, owner: _rig.PlayerId + 1);
            InventoryCommandResult wrongOwnerResult = _authority.Submit(_rig, wrongOwner);
            Check(!wrongOwnerResult.Accepted && wrongOwnerResult.Revision == before,
                "wrong payload owner is rejected before sequence consumption: " + Describe(wrongOwnerResult));

            PlayerRig foreign = CreateTemporaryRig("InventoryAuthority_ForeignRequester", false, false);
            InventoryCommandResult foreignResult = _authority.Submit(foreign, move);
            Check(!foreignResult.Accepted && foreignResult.Revision == before,
                "foreign requester is rejected even when the command claims the bound owner: " + Describe(foreignResult));
            Destroy(foreign.gameObject);

            InventoryCommandResult moved = _authority.Submit(_rig, move);
            InventorySlot movedSlot = default;
            Check(moved.Accepted && moved.Changed && moved.Revision == before + 1 &&
                  _bag.TryGetStack(coinSlot.StackId, out movedSlot) && movedSlot.Origin == new Vector2Int(2, 0),
                "correct owner uses the same sequence after pre-consume owner refusals: " + Describe(moved));

            ulong noOpRevision = _bag.Revision;
            InventoryCommand sameCell = Create(InventoryCommandKind.Move, coinSlot.StackId, 0,
                movedSlot.Origin, EquipmentSlot.None, GearUid(movedSlot), Guid.Empty);
            InventoryCommandResult noOp = _authority.Submit(_rig, sameCell);
            Check(noOp.Accepted && !noOp.Changed && noOp.Revision == noOpRevision,
                "same-cell move consumes a sequence but leaves revision unchanged: " + Describe(noOp));
            InventoryCommandResult replay = _authority.Submit(_rig, sameCell);
            Check(!replay.Accepted && replay.Revision == noOpRevision,
                "accepted command replay is rejected: " + Describe(replay));

            ulong futureSequence = sameCell.Sequence + 2;
            InventoryCommand future = Copy(sameCell, sequence: futureSequence);
            InventoryCommandResult futureResult = _authority.Submit(_rig, future);
            InventoryCommand older = Copy(sameCell, sequence: sameCell.Sequence + 1);
            InventoryCommandResult outOfOrder = _authority.Submit(_rig, older);
            Check(futureResult.Accepted && !outOfOrder.Accepted && outOfOrder.Revision == noOpRevision,
                "out-of-order command after a higher accepted sequence is refused: " + Describe(outOfOrder));

            InventoryCommand template = Create(InventoryCommandKind.Move, coinSlot.StackId, 0,
                movedSlot.Origin, EquipmentSlot.None, GearUid(movedSlot), Guid.Empty);
            InventoryCommand zeroId = Copy(template, source: 0);
            InventoryCommandResult zeroResult = _authority.Submit(_rig, zeroId);
            InventoryCommandResult afterZero = _authority.Submit(_rig, template);
            Check(!zeroResult.Accepted && afterZero.Accepted && !afterZero.Changed,
                "zero stack id is structurally refused without consuming its sequence; same-cell retry is accepted");

            InventoryCommand unknown = Create(InventoryCommandKind.Move, ulong.MaxValue, 0,
                Vector2Int.zero, EquipmentSlot.None, Guid.Empty, Guid.Empty);
            InventoryCommandResult unknownResult = _authority.Submit(_rig, unknown);
            InventoryCommandResult unknownReplay = _authority.Submit(_rig, unknown);
            Check(!unknownResult.Accepted && !unknownReplay.Accepted &&
                  unknownReplay.Reason.Contains("sequence"),
                "unknown nonzero id is rejected after consuming its sequence, so replay cannot retry it: " + Describe(unknownReplay));

            Check(_bag.TryAddAt(relic, 1, new Vector2Int(8, 0), out addReason), "place stack for retirement check: " + addReason);
            InventorySlot retired = Find(relic);
            int retiredIndex = _bag.IndexOfStack(retired.StackId);
            Check(_bag.TryRemoveAt(retiredIndex, 1) == 1, "retire the tested stack identity");
            InventoryCommand retiredCommand = Create(InventoryCommandKind.Move, retired.StackId, 0,
                new Vector2Int(9, 0), EquipmentSlot.None, GearUid(retired), Guid.Empty);
            InventoryCommandResult retiredResult = _authority.Submit(_rig, retiredCommand);
            Check(!retiredResult.Accepted && retiredResult.Revision == _bag.Revision,
                "retired stack identity is refused without retargeting another stack: " + Describe(retiredResult));

            ulong staleRevision = _bag.Revision;
            InventoryCommand staleTemplate = Create(InventoryCommandKind.Move, coinSlot.StackId, 0,
                movedSlot.Origin, EquipmentSlot.None, GearUid(movedSlot), Guid.Empty);
            InventoryCommand stale = Copy(staleTemplate, revision: staleRevision - 1);
            InventoryCommandResult staleResult = _authority.Submit(_rig, stale);
            InventoryCommandResult staleReplay = _authority.Submit(_rig, stale);
            InventoryCommand next = Create(InventoryCommandKind.Move, coinSlot.StackId, 0,
                movedSlot.Origin, EquipmentSlot.None, GearUid(movedSlot), Guid.Empty);
            InventoryCommandResult afterRefusal = _authority.Submit(_rig, next);
            Check(!staleResult.Accepted && !staleReplay.Accepted && afterRefusal.Accepted && !afterRefusal.Changed,
                "stale revision consumes its sequence, replay fails, and the next fresh command remains usable: " +
                Describe(staleResult) + "; next=" + Describe(afterRefusal));
        }

        private void CheckSwapAndMerge(ItemData coin, ItemData relic)
        {
            _bag.Clear();
            Check(_bag.TryAddAt(coin, 2, Vector2Int.zero, out string reason), "place coin stack for swap: " + reason);
            Check(_bag.TryAddAt(relic, 1, new Vector2Int(3, 0), out reason), "place relic stack for swap: " + reason);
            InventorySlot first = Find(coin);
            InventorySlot second = Find(relic);
            ulong before = _bag.Revision;
            InventoryCommand swap = Create(InventoryCommandKind.Swap, first.StackId, second.StackId,
                Vector2Int.zero, EquipmentSlot.None, GearUid(first), GearUid(second));
            InventoryCommandResult swapped = _authority.Submit(_rig, swap);
            Check(swapped.Accepted && swapped.Changed && swapped.Revision == before + 1 &&
                  _bag.TryGetStack(first.StackId, out InventorySlot movedFirst) && movedFirst.Origin == second.Origin &&
                  _bag.TryGetStack(second.StackId, out InventorySlot movedSecond) && movedSecond.Origin == first.Origin,
                "swap resolves both stable ids and commits once: " + Describe(swapped));

            _bag.Clear();
            Check(_bag.TryAddAt(coin, 3, Vector2Int.zero, out reason), "place merge target stack: " + reason);
            Check(_bag.TryAddAt(coin, 2, new Vector2Int(2, 0), out reason), "place merge source stack: " + reason);
            InventorySlot target = FindAt(new Vector2Int(0, 0));
            InventorySlot source = FindAt(new Vector2Int(2, 0));
            before = _bag.Revision;
            InventoryCommand merge = Create(InventoryCommandKind.Merge, source.StackId, target.StackId,
                Vector2Int.zero, EquipmentSlot.None, Guid.Empty, Guid.Empty);
            InventoryCommandResult merged = _authority.Submit(_rig, merge);
            Check(merged.Accepted && merged.Changed && merged.Revision == before + 1 &&
                  _bag.TryGetStack(target.StackId, out InventorySlot mergedTarget) && mergedTarget.Count == 5 &&
                  !_bag.TryGetStack(source.StackId, out _),
                "merge consumes the exact source stack and preserves the target identity: " + Describe(merged));
        }

        private void CheckEquipmentIdentity(ItemData boots)
        {
            _equipment.Clear();
            _bag.Clear();
            Check(_bag.TryAdd(boots, 1, out string reason), "add incoming gear to the bag: " + reason);
            InventorySlot incoming = Find(boots);
            Guid incomingUid = GearUid(incoming);
            InventoryCommand move = Create(InventoryCommandKind.Move, incoming.StackId, 0,
                new Vector2Int(4, 0), EquipmentSlot.None, incomingUid, Guid.Empty);
            InventoryCommandResult moved = _authority.Submit(_rig, move);
            InventorySlot movedGear = default;
            Check(moved.Accepted && _bag.TryGetStack(incoming.StackId, out movedGear) &&
                  movedGear.Instance != null && movedGear.Instance.Uid == incomingUid,
                "moving a live gear stack preserves its exact UID: " + Describe(moved));
            incoming = movedGear;
            ulong before = _bag.Revision;
            InventoryCommand wrongSource = Create(InventoryCommandKind.Equip, incoming.StackId, 0,
                Vector2Int.zero, EquipmentSlot.Feet, Guid.NewGuid(), Guid.Empty);
            InventoryCommandResult wrongSourceResult = _authority.Submit(_rig, wrongSource);
            Check(!wrongSourceResult.Accepted && wrongSourceResult.Revision == before && _equipment.EquippedInstance(EquipmentSlot.Feet) == null,
                "wrong source gear UID is refused without changing either side: " + Describe(wrongSourceResult));

            InventoryCommand equip = Create(InventoryCommandKind.Equip, incoming.StackId, 0,
                Vector2Int.zero, EquipmentSlot.Feet, incomingUid, Guid.Empty);
            InventoryCommandResult equipped = _authority.Submit(_rig, equip);
            Check(equipped.Accepted && equipped.Changed && equipped.Revision == before + 1 &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet) != null &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet).Uid == incomingUid,
                "equip moves the exact source UID into its authored slot: " + Describe(equipped));

            Check(_bag.TryAdd(boots, 1, out reason), "add replacement gear: " + reason);
            InventorySlot replacement = Find(boots);
            Guid wornUid = _equipment.EquippedInstance(EquipmentSlot.Feet).Uid;
            InventoryCommand wrongTarget = Create(InventoryCommandKind.Equip, replacement.StackId, 0,
                Vector2Int.zero, EquipmentSlot.Feet, GearUid(replacement), Guid.NewGuid());
            ulong replacementBefore = _bag.Revision;
            InventoryCommandResult wrongTargetResult = _authority.Submit(_rig, wrongTarget);
            Check(!wrongTargetResult.Accepted && wrongTargetResult.Revision == replacementBefore &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet).Uid == wornUid,
                "wrong target gear UID refuses replacement atomically: " + Describe(wrongTargetResult));

            InventoryCommand replace = Create(InventoryCommandKind.Equip, replacement.StackId, 0,
                Vector2Int.zero, EquipmentSlot.Feet, GearUid(replacement), wornUid);
            InventoryCommandResult replaced = _authority.Submit(_rig, replace);
            Check(replaced.Accepted && replaced.Changed && replaced.Revision == replacementBefore + 1 &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet).Uid == GearUid(replacement),
                "valid replacement checks the target UID and advances once: " + Describe(replaced));

            Guid currentUid = _equipment.EquippedInstance(EquipmentSlot.Feet).Uid;
            InventoryCommand wrongUnequip = Create(InventoryCommandKind.Unequip, 0, 0,
                Vector2Int.zero, EquipmentSlot.Feet, Guid.NewGuid(), Guid.Empty);
            ulong unequipBefore = _bag.Revision;
            InventoryCommandResult wrongUnequipResult = _authority.Submit(_rig, wrongUnequip);
            Check(!wrongUnequipResult.Accepted && wrongUnequipResult.Revision == unequipBefore &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet).Uid == currentUid,
                "wrong unequip UID is refused without changing gear: " + Describe(wrongUnequipResult));

            InventoryCommand unequip = Create(InventoryCommandKind.Unequip, 0, 0,
                Vector2Int.zero, EquipmentSlot.Feet, currentUid, Guid.Empty);
            InventoryCommandResult unequipped = _authority.Submit(_rig, unequip);
            Check(unequipped.Accepted && unequipped.Changed && unequipped.Revision == unequipBefore + 1 &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet) == null && _bag.HoldsInstanceUid(currentUid),
                "unequip returns the same gear UID to the bag and commits once: " + Describe(unequipped));
        }

        private void CheckAtomicFullBagUnequip(ItemData coin, ItemData boots)
        {
            _equipment.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);
            Check(_bag.TryAdd(boots, 1, out string reason), "add boots for full-bag test: " + reason);
            InventorySlot incoming = Find(boots);
            InventoryCommand equip = Create(InventoryCommandKind.Equip, incoming.StackId, 0,
                Vector2Int.zero, EquipmentSlot.Feet, GearUid(incoming), Guid.Empty);
            Check(_authority.Submit(_rig, equip).Accepted, "equip boots before filling the bag");

            _bag.Clear();
            _bag.ConfigureGrid(2, 2);
            foreach (Vector2Int cell in new[] { Vector2Int.zero, Vector2Int.right, Vector2Int.up, Vector2Int.one })
                Check(_bag.TryAddAt(coin, 8, cell, out reason), "fill exact bag cell " + cell + ": " + reason);

            Guid wornUid = _equipment.EquippedInstance(EquipmentSlot.Feet).Uid;
            string bagBefore = _bag.ToContainerSnapshot().ToJson();
            string gearBefore = _equipment.ToContainerSnapshot().ToJson();
            ulong revisionBefore = _bag.Revision;
            InventoryCommand command = Create(InventoryCommandKind.Unequip, 0, 0,
                Vector2Int.zero, EquipmentSlot.Feet, wornUid, Guid.Empty);
            InventoryCommandResult refused = _authority.Submit(_rig, command);
            Check(!refused.Accepted && refused.Revision == revisionBefore && _bag.Revision == revisionBefore &&
                  _bag.ToContainerSnapshot().ToJson() == bagBefore &&
                  _equipment.ToContainerSnapshot().ToJson() == gearBefore &&
                  _equipment.EquippedInstance(EquipmentSlot.Feet).Uid == wornUid,
                "full 2x2 bag refuses unequip with exact bag/gear snapshots intact: " + Describe(refused));
        }

        private void CheckConsumableAuthority(ItemData coin, ItemData draught, ItemData arrows)
        {
            _equipment.Clear();
            _bag.Clear();
            _bag.ConfigureGrid(10, 6);
            HealthComponent health = _rig.Health;
            health.SetHealth(health.MaxHealth);
            Check(_bag.TryAdd(draught, 1, out string reason), "add draught for full-health refusal: " + reason);
            InventorySlot fullDraught = Find(draught);
            Check(_bag.RequestHoldStack(fullDraught.StackId, out string holdReason), "hold consumable: " + holdReason);
            string bagBefore = _bag.ToContainerSnapshot().ToJson();
            ulong fullRevision = _bag.Revision;
            InventoryCommand fullUse = Create(InventoryCommandKind.Use, fullDraught.StackId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(fullDraught), Guid.Empty);
            InventoryCommandResult fullResult = _authority.Submit(_rig, fullUse);
            Check(!fullResult.Accepted && fullResult.Revision == fullRevision && _bag.Revision == fullRevision &&
                  _bag.ToContainerSnapshot().ToJson() == bagBefore,
                "full-health use refuses without consuming the exact stack or revision: " + Describe(fullResult));

            _bag.Clear();
            health.SetHealth(Mathf.Max(1f, health.MaxHealth - 20f));
            Check(_bag.TryAdd(draught, 2, out reason), "add two heal charges: " + reason);
            Check(_bag.TryAdd(coin, 1, out reason), "add a reentrant-command target: " + reason);
            InventorySlot healStack = Find(draught);
            Check(_bag.RequestHoldStack(healStack.StackId, out string healHoldReason), "hold consumable: " + healHoldReason);
            InventorySlot moveStack = Find(coin);
            InventoryCommand reentrant = Create(InventoryCommandKind.Move, moveStack.StackId, 0,
                new Vector2Int(7, 0), EquipmentSlot.None, GearUid(moveStack), Guid.Empty);
            InventoryCommand use = Create(InventoryCommandKind.Use, healStack.StackId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(healStack), Guid.Empty);
            ulong useBefore = _bag.Revision;
            int unitsBefore = _bag.TotalUnits;
            float healthBefore = health.CurrentHealth;
            InventoryCommandResult reentrantResult = default;
            bool healthCallbackSawCommittedBag = false;
            Action<float, float> healthObserver = (current, maximum) =>
            {
                if (current <= healthBefore) return;
                healthCallbackSawCommittedBag = _bag.Revision == useBefore + 1 && _bag.TotalUnits == unitsBefore - 1;
                reentrantResult = _authority.Submit(_rig, reentrant);
            };
            health.Changed += healthObserver;
            _inventoryEvents = 0;
            InventoryCommandResult used;
            try { used = _authority.Submit(_rig, use); }
            finally { health.Changed -= healthObserver; }
            Check(used.Accepted && used.Changed && used.Revision == useBefore + 1 &&
                  Mathf.Approximately(health.CurrentHealth, health.MaxHealth) &&
                  _bag.TryGetStack(healStack.StackId, out InventorySlot remainingHeal) && remainingHeal.Count == 1,
                "server-authored heal consumes exactly one charge and advances one revision: " + Describe(used));
            Check(healthCallbackSawCommittedBag && !reentrantResult.Accepted &&
                  (reentrantResult.Reason ?? "").Contains("already processing") && _inventoryEvents == 1,
                "health callback sees the committed bag, reentrant command is refused, then one bag event fires: " +
                $"callbackSaw={healthCallbackSawCommittedBag}, reentrant={Describe(reentrantResult)}, bagEvents={_inventoryEvents}");

            CheckThrowingHealthObserver(coin, draught, health);

            _bag.Clear();
            Check(_bag.TryAddAt(coin, 1, Vector2Int.zero, out reason), "place leading stack for compaction: " + reason);
            Check(_bag.TryAddAt(draught, 1, new Vector2Int(2, 0), out reason), "place use target after leader: " + reason);
            Check(_bag.TryAddAt(Resolve("ashen_relic"), 1, new Vector2Int(4, 0), out reason), "place trailing stack for compaction: " + reason);
            InventorySlot leading = FindAt(Vector2Int.zero);
            InventorySlot target = FindAt(new Vector2Int(2, 0));
            InventorySlot trailing = FindAt(new Vector2Int(4, 0));
            ulong targetId = target.StackId;
            int targetOldIndex = target.Index;
            Check(_bag.TryRemoveAt(_bag.IndexOfStack(leading.StackId), 1) == 1, "remove the earlier stack to compact indices");
            InventorySlot shifted = default;
            Check(_bag.TryGetStack(targetId, out shifted) && shifted.Index < targetOldIndex,
                "use target moved to a lower index while retaining its stack identity");
            health.SetHealth(Mathf.Max(1f, health.MaxHealth - 20f));
            Check(_bag.RequestHoldStack(targetId, out string shiftedHoldReason), "hold shifted consumable: " + shiftedHoldReason);
            InventoryCommand shiftedUse = Create(InventoryCommandKind.Use, targetId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(shifted), Guid.Empty);
            InventoryCommandResult shiftedResult = _authority.Submit(_rig, shiftedUse);
            Check(shiftedResult.Accepted && !_bag.TryGetStack(targetId, out _) &&
                  _bag.TryGetStack(trailing.StackId, out InventorySlot trailingStillPresent) &&
                  trailingStillPresent.Item == trailing.Item,
                "stable use target survives index compaction without consuming the trailing stack: " + Describe(shiftedResult));

            CheckAmmoUse(arrows);
            _bag.Clear();
            health.SetHealth(Mathf.Max(1f, health.MaxHealth - 20f));
            Check(_bag.TryAdd(draught, 2, out reason), "add legacy adapter heal charges: " + reason);
            Check(_bag.RequestHoldStack(Find(draught).StackId, out string adapterHoldReason), "hold adapter consumable: " + adapterHoldReason);
            int beforeAdapterUnits = _bag.TotalUnits;
            ulong beforeAdapterRevision = _bag.Revision;
            Check(_bag.TryUseSlot(Find(draught).Index) && _bag.TotalUnits == beforeAdapterUnits - 1 &&
                  _bag.Revision == beforeAdapterRevision + 1,
                "legacy TryUseSlot adapter submits one authority command using its shared sequence source");
            Check(_bag.TryAdd(coin, 1, out reason), "add follow-up command item after adapter: " + reason);
            InventorySlot adapterFollowup = Find(coin);
            InventoryCommand followup = Create(InventoryCommandKind.Move, adapterFollowup.StackId, 0,
                new Vector2Int(8, 0), EquipmentSlot.None, GearUid(adapterFollowup), Guid.Empty);
            Check(_authority.Submit(_rig, followup).Accepted,
                "authority command after legacy adapter uses the same increasing sequence allocator");
        }

        private void CheckThrowingHealthObserver(ItemData coin, ItemData draught, HealthComponent health)
        {
            _bag.Clear();
            health.SetHealth(Mathf.Max(1f, health.MaxHealth - 20f));
            Check(_bag.TryAdd(draught, 1, out string reason), "add draught for throwing-observer check: " + reason);
            Check(_bag.TryAdd(coin, 1, out reason), "add follow-up stack for throwing-observer check: " + reason);
            InventorySlot healStack = Find(draught);
            Check(_bag.RequestHoldStack(healStack.StackId, out string healHoldReason), "hold consumable: " + healHoldReason);
            float healthBefore = health.CurrentHealth;
            int unitsBefore = _bag.TotalUnits;
            ulong revisionBefore = _bag.Revision;
            _inventoryEvents = 0;
            InventoryCommand use = Create(InventoryCommandKind.Use, healStack.StackId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(healStack), Guid.Empty);
            bool throwingObserverRan = false;
            Action<float, float> throwingObserver = (current, maximum) =>
            {
                throwingObserverRan = true;
                throw new InvalidOperationException("expected inventory use observer exception");
            };
            health.Changed += throwingObserver;
            InventoryCommandResult result;
            try { result = _authority.Submit(_rig, use); }
            finally { health.Changed -= throwingObserver; }

            Check(result.Accepted && result.Changed && result.Revision == revisionBefore + 1 &&
                  _bag.TotalUnits == unitsBefore - 1 && Mathf.Approximately(health.CurrentHealth,
                      Mathf.Min(health.MaxHealth, healthBefore + draught.healAmount)),
                "throwing health observer leaves the committed use accepted with one consumed unit and applied heal: " + Describe(result));
            Check(throwingObserverRan && _inventoryEvents == 1,
                "throwing health observer still attempts the final inventory notification exactly once: ran=" + throwingObserverRan + ", events=" + _inventoryEvents);

            InventorySlot remainingCoin = Find(coin);
            InventoryCommand followup = Create(InventoryCommandKind.Move, remainingCoin.StackId, 0,
                new Vector2Int(8, 0), EquipmentSlot.None, GearUid(remainingCoin), Guid.Empty);
            InventoryCommandResult followupResult = _authority.Submit(_rig, followup);
            Check(followupResult.Accepted && followupResult.Changed,
                "authority remains usable after an observer exception: " + Describe(followupResult));
        }
        private void CheckAmmoUse(ItemData arrows)
        {
            RangedCombat ranged = _rig.GetComponent<RangedCombat>();
            WeaponData weapon = ranged != null ? ranged.Weapon : null;
            WeaponData activeWeapon = _rig.Loadout != null ? _rig.Loadout.ActiveWeapon : null;
            CharacterClassData archer = AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
            ClassLoadout.RangedAmmoState state = weapon != null && _rig.Loadout != null
                ? _rig.Loadout.GetAmmoState(weapon)
                : null;
            bool coherentArcher = archer != null && _rig.Class == archer && _rig.Loadout != null &&
                                  _rig.Loadout.AppliedClass == archer && activeWeapon == weapon;
            string fixtureDetails = "class=" + (_rig.Class != null ? _rig.Class.name : "null") +
                ", loadoutClass=" + (_rig.Loadout != null && _rig.Loadout.AppliedClass != null ? _rig.Loadout.AppliedClass.name : "null") +
                ", activeWeapon=" + (activeWeapon != null ? activeWeapon.name : "null") +
                ", rangedWeapon=" + (weapon != null ? weapon.name : "null") +
                ", maxReserve=" + (state != null ? state.maxReserve.ToString() : "missing");
            Check(coherentArcher && ranged != null && weapon != null && weapon.IsRanged && state != null && state.maxReserve > 0,
                "Archer fixture class/loadout and ranged reserve are coherent: " + fixtureDetails);
            if (!coherentArcher || ranged == null || weapon == null || state == null || state.maxReserve <= 0) return;

            ranged.AddAmmo(state.maxReserve);
            _bag.Clear();
            Check(_bag.TryAdd(arrows, 1, out string reason), "add arrow bundle for full-ammo refusal: " + reason);
            InventorySlot bundle = Find(arrows);
            Check(_bag.RequestHoldStack(bundle.StackId, out string holdReason), "hold consumable: " + holdReason);
            string bagBefore = _bag.ToContainerSnapshot().ToJson();
            ulong before = _bag.Revision;
            InventoryCommand full = Create(InventoryCommandKind.Use, bundle.StackId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(bundle), Guid.Empty);
            InventoryCommandResult fullResult = _authority.Submit(_rig, full);
            Check(!fullResult.Accepted && fullResult.Revision == before && ranged.Reserve == state.maxReserve &&
                  _bag.ToContainerSnapshot().ToJson() == bagBefore,
                "full reserve refuses ammo use without consuming its stack or revision: " + Describe(fullResult));

            EmberdelveKitFixtures.ResetAmmo(ranged, weapon, 0, state.maxReserve, ranged.Magazine);
            // Keep the full-reserve refusal snapshot intact above, then start an independent two-unit success fixture.
            _bag.Clear();
            Check(_bag.TryAdd(arrows, 2, out reason), "add two arrow bundles for a successful grant: " + reason);
            bundle = Find(arrows);
            Check(_bag.RequestHoldStack(bundle.StackId, out holdReason), "hold consumable: " + holdReason);
            _rig.SeedKit(KitSeedReason.Revision);
            Check(_rig.TryGetAuthorityKit(out KitView heldKit) && heldKit.HasActiveAmmo &&
                  heldKit.ActiveWeaponId == weapon.weaponId && heldKit.Reserve == ranged.Reserve && _rig.ActiveWeapon == null,
                "held ammunition hides the weapon but preserves its authoritative ammo pool through a kit revision");
            before = _bag.Revision;
            int unitsBefore = _bag.TotalUnits;
            int bundleCountBefore = bundle.Count;
            Check(bundleCountBefore == 2 && unitsBefore == 2,
                "successful ammo fixture starts with exactly two units in one stack: count=" + bundleCountBefore + ", total=" + unitsBefore);
            int reserveBefore = ranged.Reserve;
            InventoryCommand grant = Create(InventoryCommandKind.Use, bundle.StackId, 0,
                Vector2Int.zero, EquipmentSlot.None, GearUid(bundle), Guid.Empty);
            bool ammoCallbackSawCommittedBag = false;
            bool ammoCallbackStackPresent = false;
            int ammoCallbackStackCount = 0;
            Action<int, int, int> ammoObserver = (playerId, magazine, reserve) =>
            {
                if (playerId != _rig.PlayerId) return;
                ammoCallbackStackPresent = _bag.TryGetStack(bundle.StackId, out InventorySlot callbackStack);
                ammoCallbackStackCount = ammoCallbackStackPresent ? callbackStack.Count : 0;
                ammoCallbackSawCommittedBag = _bag.Revision == before + 1 &&
                    _bag.TotalUnits == unitsBefore - 1 && ammoCallbackStackPresent && ammoCallbackStackCount == bundleCountBefore - 1;
            };
            GameEvents.AmmoChanged += ammoObserver;
            InventoryCommandResult granted;
            try { granted = _authority.Submit(_rig, grant); }
            finally { GameEvents.AmmoChanged -= ammoObserver; }
            bool finalStackPresent = _bag.TryGetStack(bundle.StackId, out InventorySlot oneBundleLeft);
            Check(granted.Accepted && granted.Changed && granted.Revision == before + 1 &&
                  ranged.Reserve == Mathf.Min(state.maxReserve, reserveBefore + arrows.ammoGrant) &&
                  _bag.TotalUnits == unitsBefore - 1 && finalStackPresent && oneBundleLeft.Count == bundleCountBefore - 1,
                "server-authored ammo amount consumes one exact unit from the two-unit fixture: " + Describe(granted) +
                $", startCount={bundleCountBefore}, finalCount={(finalStackPresent ? oneBundleLeft.Count.ToString() : "missing")}, total={_bag.TotalUnits}");
            Check(ammoCallbackSawCommittedBag,
                "ammo callback sees revision/quantity committed before InventoryChanged: " +
                $"callbackRevision={_bag.Revision}, expectedRevision={before + 1}, callbackCount={(ammoCallbackStackPresent ? ammoCallbackStackCount.ToString() : "missing")}, expectedCount={bundleCountBefore - 1}, total={_bag.TotalUnits}, expectedTotal={unitsBefore - 1}");
            int grantedReserve = Mathf.Min(state.maxReserve, reserveBefore + arrows.ammoGrant);
            Check(_bag.HeldStackId == bundle.StackId && _rig.TryGetAuthorityKit(out KitView grantedKit) &&
                  grantedKit.HasActiveAmmo && grantedKit.Reserve == grantedReserve,
                "using held ammunition updates the server pool while the remaining bundle stays in hand");
            Check(_bag.RequestWeaponSet(_equipment.ActiveWeaponSet, out reason), "return to the selected weapon: " + reason);
            _rig.SeedKit(KitSeedReason.Revision);
            Check(_bag.HeldStackId == 0 && _rig.ActiveWeapon == weapon && ranged.Reserve == grantedReserve &&
                  _rig.TryGetAuthorityKit(out KitView returnedKit) && returnedKit.HasActiveAmmo && returnedKit.Reserve == grantedReserve,
                "returning to the weapon preserves exactly the granted reserve in both authority and presentation");
        }

        private void CheckDeadAndExtractedOwnerRules()
        {
            PlayerRig dead = CreateTemporaryRig("InventoryAuthority_DeadRequester", true, false);
            PlayerRig extracted = CreateTemporaryRig("InventoryAuthority_ExtractedRequester", false, true);
            try
            {
                CheckUnavailableOwner(dead, "dead players cannot change inventory", "dead owner refusal consumes a structurally valid sequence");
                CheckUnavailableOwner(extracted, "extracted players cannot change inventory", "extracted owner refusal consumes a structurally valid sequence");
            }
            finally
            {
                if (dead != null) Destroy(dead.gameObject);
                if (extracted != null) Destroy(extracted.gameObject);
            }
        }

        private void CheckUnavailableOwner(PlayerRig owner, string expectedReason, string label)
        {
            Inventory inventory = owner.Inventory;
            IInventoryAuthority authority = inventory.CommandAuthority;
            InventoryCommand command = new InventoryCommand(InventoryCommandKind.Move, owner.PlayerId, 1,
                inventory.Revision, 1, 0, Vector2Int.zero, EquipmentSlot.None, Guid.Empty, Guid.Empty);
            InventoryCommandResult result = authority.Submit(owner, command);
            InventoryCommandResult replay = authority.Submit(owner, command);
            Check(!result.Accepted && (result.Reason ?? "").Contains(expectedReason) && !replay.Accepted &&
                  (replay.Reason ?? "").Contains("sequence"), label + ": " + Describe(result) + "; replay=" + Describe(replay));
        }

        private PlayerRig CreateTemporaryRig(string name, bool dead, bool extracted)
        {
            GameObject go = new GameObject(name);
            PlayerRig rig = go.AddComponent<PlayerRig>();
            Inventory inventory = go.AddComponent<Inventory>();
            rig.WireRig();
            inventory.Initialize(rig);
            if (dead)
            {
                DamageInfo damage = new DamageInfo(10000f, Vector3.zero, Vector3.forward, Vector3.zero,
                    rig.CombatId, EntityKind.Enemy, DamageType.Physical, false, false, 0f);
                rig.Health.ApplyDamage(damage);
            }
            if (extracted) rig.MarkExtracted();
            return rig;
        }

        private InventoryCommand Create(InventoryCommandKind kind, ulong source, ulong target,
            Vector2Int cell, EquipmentSlot slot, Guid sourceUid, Guid targetUid)
        {
            bool created = _authority.TryCreateLocalCommand(kind, source, target, cell, slot,
                sourceUid, targetUid, out InventoryCommand command, out string reason);
            Check(created, "create " + kind + " command: " + reason);
            return command;
        }

        private static InventoryCommand Copy(InventoryCommand command, int? owner = null, ulong? sequence = null,
            ulong? revision = null, ulong? source = null, ulong? target = null)
        {
            return new InventoryCommand(command.Kind, owner ?? command.OwnerPlayerId, sequence ?? command.Sequence,
                revision ?? command.ExpectedRevision, source ?? command.SourceStackId, target ?? command.TargetStackId,
                command.DestinationCell, command.DestinationSlot,
                command.ExpectedSourceGearUid, command.ExpectedTargetGearUid);
        }

        private InventorySlot Find(ItemData item)
        {
            for (int i = 0; i < _bag.Slots.Count; i++)
                if (_bag.Slots[i].Item == item) return _bag.Slots[i];
            Check(false, "live bag stack exists for " + (item != null ? item.itemId : "null item"));
            return default;
        }

        private InventorySlot FindAt(Vector2Int cell) => _bag.SlotAtCell(cell);

        private static Guid GearUid(InventorySlot slot) => slot.Instance != null ? slot.Instance.Uid : Guid.Empty;

        private ItemData Resolve(string id)
        {
            bool ok = _catalog.TryResolveItem(id, out ItemData item, out string reason);
            Check(ok && item != null, "production catalog resolves " + id + ": " + reason);
            return item;
        }

        private string Describe(InventoryCommandResult result) =>
            $"accepted={result.Accepted}, changed={result.Changed}, revision={result.Revision}, reason={result.Reason}";

        private void OnInventoryChanged(int playerId)
        {
            if (_rig != null && playerId == _rig.PlayerId) _inventoryEvents++;
        }
    }
}
