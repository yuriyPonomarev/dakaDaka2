using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.AI;
using Emberdelve.Combat;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Feedback;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.UI;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// R2 + R7 gate: several players in ONE run of Level01 (offline, one process). Proves the single-player assumptions are
    /// gone: interactions name their actor (loot, reach, line of sight, alive state), parties decide hostility (PvP between
    /// parties through the real swept melee and the real ranged path, friendly fire off/on inside a party), per-player events
    /// never reach another player's HUD, enemies pick the nearest hostile player, extraction and death are per player and a run
    /// goes on for everybody else. Entry: <c>Emberdelve.EditorTools.EmberdelveMultiPlayerGate.Run</c>.
    /// </summary>
    [InitializeOnLoad]
    public static class EmberdelveMultiPlayerGate
    {
        private const string Pending = "Emberdelve.MultiPlayer.Pending";

        static EmberdelveMultiPlayerGate()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(Pending, false))
                {
                    SessionState.SetBool(Pending, false);
                    new GameObject("MultiPlayer_Gate").AddComponent<MultiPlayerHarness>();
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

    public sealed class MultiPlayerHarness : MonoBehaviour
    {
        private static readonly Vector3 Origin = new Vector3(3000f, 0f, 3000f);

        private int _n, _fail;
        private readonly List<UnityEngine.Object> _cleanup = new List<UnityEngine.Object>();
        private readonly List<GameObject> _props = new List<GameObject>();

        private RunDirector _director;
        private LocalCombatAuthority _authority;
        private PlayerRig _p1, _p2, _p3, _p4;
        private ItemData _coin;

        // every per-player event the harness saw, as (name, player id)
        private readonly List<KeyValuePair<string, int>> _events = new List<KeyValuePair<string, int>>();
        private readonly List<int> _runEnded = new List<int>();

        private void Check(bool ok, string text)
        {
            _n++;
            if (!ok)
            {
                _fail++;
                Debug.LogError("[MultiPlayer] mismatch " + text);
            }
        }

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

            UnhookEvents();
            CombatTeams.FriendlyFire = false;
            foreach (UnityEngine.Object o in _cleanup) if (o != null) Destroy(o);
            foreach (GameObject g in _props) if (g != null) Destroy(g);

            Debug.Log("[Emberdelve][MultiPlayer] " + (_fail == 0 ? "PASS" : "FAIL") + " checks=" + _n + " failures=" + _fail);
            EmberdelveGateExit.Exit(_fail == 0 ? 0 : 1);
        }

        // -------------------------------------------------------------------------------------- helpers

        private T Make<T>() where T : ScriptableObject
        {
            T x = ScriptableObject.CreateInstance<T>();
            _cleanup.Add(x);
            return x;
        }

        private void Place(PlayerRig rig, Vector3 feet)
        {
            CharacterController controller = rig.GetComponent<CharacterController>();
            if (controller != null) controller.enabled = false;
            rig.transform.SetPositionAndRotation(new Vector3(feet.x, feet.y + 0.15f, feet.z), Quaternion.identity);
            if (controller != null) controller.enabled = true;
            Physics.SyncTransforms();
        }

        private static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.normalized;
        }

        private ChestInteractable MakeCoinChest(string name, Vector3 feet, int seed)
        {
            LootTable table = Make<LootTable>();
            table.guaranteeFirstEntry = true;
            table.maxRolls = 1;
            table.entries = new[] { new LootEntry { item = _coin, minAmount = 5, maxAmount = 5, weight = 1, maxCopies = 1 } };

            GameObject go = new GameObject(name);
            _props.Add(go);
            go.transform.position = new Vector3(feet.x, feet.y + 0.15f, feet.z);
            BoxCollider box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 0.5f, 1f);
            ChestInteractable chest = go.AddComponent<ChestInteractable>();
            chest.Configure(ChestVariant.Supply, table, seed, _director);
            return chest;
        }

        private void HookEvents()
        {
            GameEvents.PlayerHealthChanged += OnHealth;
            GameEvents.PlayerDamaged += OnDamaged;
            GameEvents.DamageDealt += OnDealt;
            GameEvents.InventoryChanged += OnInventory;
            GameEvents.ItemCollected += OnCollected;
            GameEvents.ToastRaised += OnToast;
            GameEvents.PlayerRunStateChanged += OnPlayerRunState;
            GameEvents.RunEnded += OnRunEnded;
        }

        private void UnhookEvents()
        {
            GameEvents.PlayerHealthChanged -= OnHealth;
            GameEvents.PlayerDamaged -= OnDamaged;
            GameEvents.DamageDealt -= OnDealt;
            GameEvents.InventoryChanged -= OnInventory;
            GameEvents.ItemCollected -= OnCollected;
            GameEvents.ToastRaised -= OnToast;
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;
            GameEvents.RunEnded -= OnRunEnded;
        }

        private void OnHealth(int id, float c, float m) { _events.Add(new KeyValuePair<string, int>("health", id)); }
        private void OnDamaged(int id, DamageInfo d) { _events.Add(new KeyValuePair<string, int>("damaged", id)); }
        private void OnDealt(int id, HitFeedback f) { _events.Add(new KeyValuePair<string, int>("dealt", id)); }
        private void OnInventory(int id) { _events.Add(new KeyValuePair<string, int>("inventory", id)); }
        private void OnCollected(int id, ItemData i, int t) { _events.Add(new KeyValuePair<string, int>("collected", id)); }
        private void OnToast(int id, string m) { _events.Add(new KeyValuePair<string, int>("toast", id)); }
        private void OnPlayerRunState(int id, RunState s) { _events.Add(new KeyValuePair<string, int>("runstate:" + s, id)); }
        private void OnRunEnded(int id, RunResult r) { _runEnded.Add(id); }

        private int EventsFor(string name, int id)
        {
            int count = 0;
            for (int i = 0; i < _events.Count; i++) if (_events[i].Key == name && _events[i].Value == id) count++;
            return count;
        }

        private int EventsOf(string name)
        {
            int count = 0;
            for (int i = 0; i < _events.Count; i++) if (_events[i].Key == name) count++;
            return count;
        }

        private bool OnlyThesePlayers(params int[] ids)
        {
            for (int i = 0; i < _events.Count; i++)
            {
                if (_events[i].Value == GameEvents.NoPlayer) continue;
                bool ok = false;
                for (int k = 0; k < ids.Length; k++) if (ids[k] == _events[i].Value) ok = true;
                if (!ok) return false;
            }
            return true;
        }

        private static object Field(object o, string name)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            return f != null ? f.GetValue(o) : null;
        }

        private static float LethalHit(PlayerRig rig, float amount)
        {
            rig.ApplyDamage(new DamageInfo(amount, rig.transform.position, Vector3.forward, rig.transform.position + Vector3.forward, 0,
                EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            return rig.CurrentHealth;
        }

        // -------------------------------------------------------------------------------------- the gate

        private IEnumerator Execute()
        {
            _p1 = FindFirstObjectByType<PlayerRig>();
            _director = FindFirstObjectByType<RunDirector>();
            _authority = FindFirstObjectByType<LocalCombatAuthority>();
            Check(_p1 != null && _director != null && _authority != null, "scene has a rig, a run director and the combat authority");
            if (_p1 == null || _director == null || _authority == null) yield break;

            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;
            _p1.Movement.enabled = false;
            _coin = Make<ItemData>();
            _coin.itemId = "multiplayer_gate_coin";
            _coin.kind = ItemKind.Currency;
            _coin.maxStack = 80;
            _coin.value = 7;

            HookEvents();

            yield return Identity();
            yield return PerActorInteractions();
            yield return PvpAndFriendlyFire();
            yield return HudIsolation();
            yield return EnemyTargeting();
            yield return ExtractionAndDeath();
        }

        // ---- 1. players, parties, teams -----------------------------------------------------------------

        private IEnumerator Identity()
        {
            Check(_director.Players.Count == 1 && _director.LocalSession != null && _director.LocalSession.Rig == _p1, "the run starts with one session: the local rig");
            Check(_p1.IsLocallyControlled && _p1.Kind == EntityKind.LocalPlayer && _p1.PartyId == 0, "the scene rig is the locally controlled player of party 0");

            Vector3 here = _p1.transform.position;
            int beforeNullClass = _director.Players.Count;
            PlayerRig rejectedClass = _director.SpawnPlayer(here + new Vector3(1f, 0f, 0f), Quaternion.identity, 0, null, out string nullClassReason);
            Check(rejectedClass == null && nullClassReason == "no class selected" && _director.Players.Count == beforeNullClass,
                "the trusted explicit-class spawn rejects a missing class before adding a session");

            CharacterClassData archerClass = UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");
            CharacterClassData warriorClass = UnityEditor.AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Warrior.asset");
            _p2 = _director.SpawnPlayer(here + new Vector3(2f, 0f, 0f), Quaternion.identity, 0, archerClass, out string r2);
            _p3 = _director.SpawnPlayer(here + new Vector3(4f, 0f, 0f), Quaternion.identity, 1, out string r3);
            _p4 = _director.SpawnPlayer(here + new Vector3(6f, 0f, 0f), Quaternion.identity, 0, out string r4);
            Check(_p2 != null && _p3 != null && _p4 != null, $"three more rigs spawn and join ({r2}/{r3}/{r4})");
            if (_p2 == null || _p3 == null || _p4 == null) yield break;
            Check(archerClass != null && warriorClass != null && _director.Players[1].Class == archerClass
                && _p2.Class == archerClass && _p1.Class == warriorClass && _director.Players[0].Class == warriorClass,
                "trusted per-player class selection keeps P2 Archer and P1 Warrior independently");
            foreach (PlayerRig r in new[] { _p2, _p3, _p4 }) r.Movement.enabled = false;
            yield return null;

            Check(_director.Players.Count == 4, "four sessions after the joins");
            Check(_p1.PlayerId != _p2.PlayerId && _p2.PlayerId != _p3.PlayerId && _p1.PlayerId != _p3.PlayerId && _p1.PlayerId != _p4.PlayerId, "every rig has its own player id");
            Check(_p1.CombatId != _p2.CombatId && _p2.CombatId != _p3.CombatId, "and its own combat id");
            Check(_p2.Kind == EntityKind.RemotePlayer && _p3.Kind == EntityKind.RemotePlayer && _p1.Kind == EntityKind.LocalPlayer, "only the locally controlled rig is a LocalPlayer");
            Check(PlayerView.LocalPlayerId == _p1.PlayerId && PlayerView.Shows(_p1.PlayerId) && !PlayerView.Shows(_p2.PlayerId) && PlayerView.Shows(GameEvents.NoPlayer),
                "the viewer is bound to the local player only (and shows global events)");
            Check(_p1.PartyId == 0 && _p2.PartyId == 0 && _p3.PartyId == 1 && _p4.PartyId == 0, "parties assigned at spawn: P1,P2,P4 in party 0, P3 in party 1");
            Check(_p1.TeamId == _p2.TeamId && _p1.TeamId != _p3.TeamId && _p1.TeamId != CombatTeams.MonsterTeam && _p3.TeamId != CombatTeams.MonsterTeam,
                $"party mates share a team, parties differ, nobody is on the monster team ({_p1.TeamId}/{_p2.TeamId}/{_p3.TeamId})");

            // hostility matrix
            CombatTeams.FriendlyFire = false;
            Check(CombatTeams.AreHostile(_p1, _p3) && CombatTeams.AreHostile(_p3, _p1), "different parties are hostile both ways (PvP)");
            Check(!CombatTeams.AreHostile(_p1, _p2) && !CombatTeams.AreHostile(_p2, _p1), "party mates are friendly with friendly fire off");
            Check(!CombatTeams.AreHostile(_p1, _p1), "nobody is hostile to itself");
            CombatTeams.FriendlyFire = true;
            Check(CombatTeams.AreHostile(_p1, _p2) && !CombatTeams.AreHostile(_p1, _p1), "friendly fire on makes party mates hostile but never self");
            CombatTeams.FriendlyFire = false;
            EnemyAgent anyEnemy = FindFirstObjectByType<EnemyAgent>();
            Check(anyEnemy != null && CombatTeams.AreHostile(anyEnemy, _p1) && CombatTeams.AreHostile(anyEnemy, _p2) && CombatTeams.AreHostile(anyEnemy, _p3)
                  && CombatTeams.AreHostile(_p1, anyEnemy), "monsters are hostile to every player and every player to monsters");
            Check(GameServices.TryGet<ILocalPlayerSource>(out ILocalPlayerSource source) && source.LocalPlayerId == _p1.PlayerId, "the director names the local player");
            Check(_director.Config != null && !_director.Config.friendlyFire && _director.Config.maxPartySize == 3, "data defaults: friendly fire off, party cap 3");

            // party cap: party 0 holds P1, P2, P4 already
            PlayerRig fifth = _director.SpawnPlayer(here + new Vector3(8f, 0f, 0f), Quaternion.identity, 0, out string capReason);
            Check(fifth == null && !string.IsNullOrEmpty(capReason) && capReason.Contains("full"), $"a fourth member of a party is refused ({capReason})");
            Check(_director.Players.Count == 4 && FindObjectsByType<PlayerRig>(FindObjectsSortMode.None).Length == 4, "the refused rig left nothing behind");

            // each rig's input is its own (disabling one never disables another)
            _p2.SetInputEnabled(true);
            _p3.SetInputEnabled(false);
            Check(_p2.Input.InteractHeldOverride == null, "overrides are per rig");
            _p2.Input.InteractHeldOverride = true;
            Check(_p1.Input.InteractHeldOverride == null && _p3.Input.InteractHeldOverride == null, "an override on one rig is not on another");
            _p2.Input.InteractHeldOverride = null;
            _p2.SetInputEnabled(false);
            yield return null;

            // the auto party assignment picks the first party with room
            Check(_director.Players[1].PartyId == 0 && _director.Players[2].PartyId == 1, "sessions record each party");
        }

        // ---- 2. chests, pickups: the actor decides -------------------------------------------------------

        private IEnumerator PerActorInteractions()
        {
            _events.Clear();
            Vector3 spot = Origin + new Vector3(0f, 0f, 0f);
            Place(_p1, spot + new Vector3(-30f, 0f, 0f));
            Place(_p2, spot);
            Place(_p3, spot + new Vector3(30f, 0f, 0f));
            Place(_p4, spot + new Vector3(60f, 0f, 0f));
            yield return null;

            _p1.Inventory.Clear(); _p2.Inventory.Clear(); _p3.Inventory.Clear();
            int p1Units = _p1.Inventory.TotalUnits;
            int p3Units = _p3.Inventory.TotalUnits;

            // chest beside P2
            ChestInteractable chest = MakeCoinChest("MP_Chest", spot + new Vector3(1.2f, 0f, 0f), 11);
            Physics.SyncTransforms();
            Check(chest.CanInteract(_p2, out string why), $"P2 beside the chest can open it ({why})");
            Check(!chest.CanInteract(_p1, out string farReason) && farReason == "Too far away", $"P1, 30 m away, cannot ({farReason})");
            Check(!chest.Open(_p1) && !chest.Opened && chest.RemainingUnits == 0, "P1 opening from afar changes nothing (the chest is not even rolled)");
            Check(_p1.Inventory.TotalUnits == p1Units, "P1's bag is untouched");

            // authority reach: just inside and just outside 3 m of the chest's closest point
            ChestInteractable rangeChest = MakeCoinChest("MP_RangeChest", spot + new Vector3(0f, 0f, 100f), 12);
            Physics.SyncTransforms();
            Place(_p3, spot + new Vector3(0f, 0f, 100f + 3.30f));
            Check(rangeChest.CanInteract(_p3, out string insideReason), $"an actor 2.9 m from the chest surface may interact ({insideReason})");
            Place(_p3, spot + new Vector3(0f, 0f, 100f + 3.55f));
            Check(!rangeChest.CanInteract(_p3, out string outsideReason) && outsideReason == "Too far away", $"an actor 3.1 m from it may not ({outsideReason})");

            // line of sight: a level wall between P2 and a chest
            ChestInteractable hidden = MakeCoinChest("MP_HiddenChest", spot + new Vector3(0f, 0f, 200f + 2.4f), 13);
            GameObject wall = new GameObject("MP_Wall");
            _props.Add(wall);
            wall.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            wall.transform.position = spot + new Vector3(0f, 1f, 200f + 1.2f);
            wall.AddComponent<BoxCollider>().size = new Vector3(4f, 4f, 0.3f);
            Physics.SyncTransforms();
            Place(_p4, spot + new Vector3(0f, 0f, 200f));
            Check(!hidden.CanInteract(_p4, out string sightReason) && sightReason == "No line of sight", $"a wall between actor and chest blocks it ({sightReason})");
            Destroy(wall);
            yield return null;
            Physics.SyncTransforms();
            Check(hidden.CanInteract(_p4, out string clearReason), $"and the same chest opens once the wall is gone ({clearReason})");
            Place(_p4, spot + new Vector3(60f, 0f, 0f));

            // loot goes to the interacting actor only
            _events.Clear();
            Check(chest.Open(_p2) && chest.Opened, "P2 opens the chest");
            Check(_p2.Inventory.TotalUnits == 5 && _p2.Inventory.RecoveredValue == 5 * 7, $"the 5 coins landed in P2's bag only ({_p2.Inventory.TotalUnits} units, value {_p2.Inventory.RecoveredValue})");
            Check(_p1.Inventory.TotalUnits == p1Units && _p3.Inventory.TotalUnits == p3Units && _p1.Inventory.RecoveredValue == 0 && _p3.Inventory.RecoveredValue == 0,
                "P1 and P3 got nothing and recovered no value");
            Check(chest.Remaining.Count == 0 && chest.TakenCount == 5, "the chest accounts for the 5 units");
            Check(EventsFor("inventory", _p2.PlayerId) > 0 && EventsFor("collected", _p2.PlayerId) > 0 && OnlyThesePlayers(_p2.PlayerId),
                "every bag/collection/toast event of that open carries P2's id and nobody else's");
            Check(EventsFor("inventory", _p1.PlayerId) == 0 && EventsFor("inventory", _p3.PlayerId) == 0, "no inventory event for the other players");

            // a bystander that is not a player of this run cannot take loot, and there is no fallback to the local rig
            FakeActor fake = new FakeActor { Id = 987654, Alive = true, Bag = _p1.Inventory, Origin = _p2.InteractionOrigin };
            ChestInteractable second = MakeCoinChest("MP_Chest2", spot + new Vector3(-1.2f, 0f, 0f), 14);
            Physics.SyncTransforms();
            int secondRemainingBefore = second.Remaining.Count;
            Check(!second.CanInteract(fake, out string strangerReason) && strangerReason == "Not a player of this run",
                "the run host rejects a stranger before the chest interaction query succeeds");
            Check(!second.Open(fake) && !second.Opened && !second.IsEmpty && second.Remaining.Count == secondRemainingBefore,
                "a stranger cannot open, roll, or consume a chest");
            Check(_p1.Inventory.TotalUnits == p1Units && _director.CollectLoot(fake, _coin, 3) == false && _director.CollectLootCounted(fake, _coin, 3) == 0,
                "the run host gives a stranger nothing (no hidden fallback to the local rig)");
            Check(!_director.TryCollectLootInstance(fake, null, out _), "no instance for a stranger either");

            // An interface implementation cannot borrow a live player's id while claiming a different bag/origin.
            FakeActor forgedP2 = new FakeActor
            {
                Id = _p2.PlayerId,
                Alive = true,
                Bag = _p1.Inventory,
                Origin = _p2.InteractionOrigin,
            };
            PlayerSession forgedSession = _director.SessionOf(forgedP2);
            int forgedLootAccepted = _director.CollectLootCounted(forgedP2, _coin, 3);
            bool forgedCanExtract = _director.ExtractionAllowed(forgedP2, out string forgedExtractionReason);
            bool forgedExtractionBegan = _director.BeginExtraction(forgedP2, out _);
            Check(forgedSession == null && forgedLootAccepted == 0 && !forgedCanExtract && !forgedExtractionBegan
                && _director.TryGetSession(_p2.PlayerId, out PlayerSession authenticP2) && authenticP2.IsRunning,
                $"matching P2's player id is not enough to impersonate P2 ({forgedExtractionReason})");
            Check(!second.CanInteract(forgedP2, out string forgedChestReason) && forgedChestReason == "Not a player of this run"
                && !second.Open(forgedP2) && !second.Opened && !second.IsEmpty && second.Remaining.Count == secondRemainingBefore,
                "an impostor with P2's id is rejected before the chest rolls or consumes loot");
            Vector3 validForgedOrigin = forgedP2.Origin;
            forgedP2.Origin = new Vector3(float.NaN, 0f, 0f);
            Check(!InteractionRules.Validate(forgedP2, second, 3f, out string invalidOriginReason)
                && invalidOriginReason == "Invalid interaction origin",
                "interaction authority refuses a non-finite actor origin");
            forgedP2.Origin = validForgedOrigin;
            Check(!InteractionRules.Validate(forgedP2, second, float.PositiveInfinity, out string invalidReachReason)
                && invalidReachReason == "Invalid interaction reach",
                "interaction authority refuses a non-finite reach");
            Check(InteractionRules.ReachOf(new FakeReachSource { InteractionReach = float.PositiveInfinity })
                == InteractionRules.DefaultReach, "non-finite configured reach falls back to the safe default");

            // P2's bag full -> the rest stays for the NEXT actor, who takes it into their own bag
            second.Open(_p2);
            Check(_p2.Inventory.TotalUnits == 10, "P2 can open the second chest too");

            // pickups: each goes to the actor that takes it
            Place(_p3, spot + new Vector3(30f, 0f, 0f));
            _director.SpawnLoot(_coin, 5, _p3.transform.position + Vector3.forward * 1.2f);
            LootPickup pickup = FindNearestPickup(_p3.transform.position);
            Check(pickup != null, "a coin pickup lies beside P3");
            if (pickup != null)
            {
                Check(!pickup.CanInteract(_p1, out string pickFar) && pickFar == "Too far away", "P1 cannot take the pickup beside P3");
                pickup.OnInteractInstant(_p1);
                Check(_p1.Inventory.TotalUnits == p1Units && pickup != null, "P1's attempt changed nothing");
                int before = _p3.Inventory.TotalUnits;
                pickup.OnInteractInstant(_p3);
                Check(_p3.Inventory.TotalUnits == before + 5 && _p3.Inventory.RecoveredValue == 5 * 7 && _p1.Inventory.TotalUnits == p1Units,
                    "P3 took it into P3's bag only");
            }

            // a gear instance pickup (a real enemy drop table, rolled through the raid session) goes to the actor as the same instance
            ItemInstance gear = null;
            string rollNote = "";
            ItemCatalog catalog = _director.Config != null ? _director.Config.itemCatalog : null;
            foreach (EnemyAgent source in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None))
            {
                if (gear != null || source.Data == null || source.Data.dropTable == null) continue;
                for (int k = 0; k < 300 && gear == null; k++)
                {
                    List<LootDrop> drops = new List<LootDrop>();
                    if (!_director.Session.TryRollEnemyDrops(source.Data, "mp_gate_" + k, catalog, drops, out string rollReason)) { rollNote = rollReason; break; }
                    for (int d = 0; d < drops.Count; d++) if (drops[d].IsRolled) { gear = drops[d].Instance; break; }
                }
            }
            Check(gear != null, "a gear instance could be rolled from a real drop table " + rollNote);
            if (gear != null)
            {
                string snapshot = gear.ToSnapshot().ToJson();
                LootPickup gearPickup = _director.SpawnLootInstance(gear, _p3.transform.position + Vector3.forward * 1.2f);
                Physics.SyncTransforms();
                gearPickup.OnInteractInstant(_p2);
                Check(gearPickup != null && !_p2.Inventory.HoldsInstanceUid(gear.Uid), "P2, far from it, cannot take P3's gear pickup");
                gearPickup.OnInteractInstant(_p3);
                int slot = _p3.Inventory.IndexOfInstance(gear.Uid);
                Check(slot >= 0 && ReferenceEquals(_p3.Inventory.InstanceAt(slot), gear) && gear.ToSnapshot().ToJson() == snapshot,
                    "P3 received that very instance (same UID and rolls)");
                Check(!_p1.Inventory.HoldsInstanceUid(gear.Uid) && !_p2.Inventory.HoldsInstanceUid(gear.Uid), "no other bag holds it");
            }

            // a REAL chest of the level (raid issuer, real tables) opened by P3 pays P3
            ChestInteractable real = null;
            for (int i = 0; i < _director.Chests.Chests.Count && real == null; i++)
            {
                ChestInteractable c = _director.Chests.Chests[i];
                if (c.Variant == ChestVariant.Supply && !c.Opened && c.Remaining.Count == 0) real = c;
            }
            Check(real != null, "an unopened real supply chest exists");
            if (real != null)
            {
                Place(_p3, real.transform.position);
                Physics.SyncTransforms();
                int p3UnitsBeforeReal = _p3.Inventory.TotalUnits;
                int p1Value = _p1.Inventory.RecoveredValue;
                Check(real.Open(_p3), "P3 opens a real level chest");
                Check(_p3.Inventory.TotalUnits > p3UnitsBeforeReal && _p1.Inventory.RecoveredValue == p1Value && _p2.Inventory.RecoveredValue == 5 * 7 * 2,
                    "the real chest paid P3 and nobody else");
            }

            // a dead actor cannot interact (P4's death is also covered by the enemy section)
            ChestInteractable deadChest = MakeCoinChest("MP_DeadChest", spot + new Vector3(60f, 0f, 3f), 15);
            Physics.SyncTransforms();
            Check(deadChest.CanInteract(_p4, out string aliveReason), $"P4 can reach this chest while alive ({aliveReason})");
            LethalHit(_p4, 100000f);
            yield return null;
            bool deadAllowed = deadChest.CanInteract(_p4, out string deadReason);
            Check(_p4.IsDead && !deadAllowed && deadReason.Contains("dead"), $"a dead actor is refused ({deadReason})");
            Check(!deadChest.Open(_p4) && !deadChest.Opened, "and cannot open it");
            Check(_director.TryGetSession(_p4.PlayerId, out PlayerSession s4) && s4.State == RunState.Dead && s4.HasResult && s4.Result.Outcome == RunState.Dead && s4.Result.PlayerId == _p4.PlayerId,
                "P4's death ended P4's run with its own result");
            Check(_director.State == RunState.Running, "the run itself goes on after one player's death");
            Check(EventsFor("runstate:Dead", _p4.PlayerId) == 1 && _runEnded.Count == 1 && _runEnded[0] == _p4.PlayerId, "only P4's viewers were told");
            yield return null;
        }

        private LootPickup FindNearestPickup(Vector3 position)
        {
            LootPickup best = null;
            float bestDistance = float.MaxValue;
            foreach (LootPickup p in FindObjectsByType<LootPickup>(FindObjectsSortMode.None))
            {
                float d = (p.transform.position - position).sqrMagnitude;
                if (d < bestDistance) { bestDistance = d; best = p; }
            }
            return best;
        }

        private sealed class FakeActor : IInteractionActor
        {
            public int Id;
            public bool Alive;
            public Inventory Bag;
            public Vector3 Origin;
            public int PlayerId => Id;
            public int PartyId => 0;
            public bool IsAlive => Alive;
            public Vector3 InteractionOrigin => Origin;
            public Inventory Inventory => Bag;
        }

        private sealed class FakeReachSource : IInteractionRulesSource
        {
            public float InteractionReach;
            float IInteractionRulesSource.InteractionReach => InteractionReach;
        }

        // ---- 3. PvP, friendly fire ---------------------------------------------------------------------

        private IEnumerator PvpAndFriendlyFire()
        {
            _events.Clear();
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) a.enabled = false;

            Vector3 spot = Origin + new Vector3(0f, 0f, 400f);
            Place(_p1, spot);
            Place(_p2, spot + new Vector3(0f, 0f, 60f));
            Place(_p3, spot + new Vector3(0f, 0f, 90f));
            yield return null;
            Vector3 fwd = Flat(_p1.AimDirection);
            Check(Mathf.Abs(_p1.AimDirection.y) < 0.02f, "P1 looks level");

            MeleeCombat melee = _p1.GetComponent<MeleeCombat>();
            _authority.SetClock(null);
            _authority.AutoStepSwings = true;

            // --- swept melee, rival party: P3 takes the damage
            Place(_p3, spot + fwd * 1.4f);
            yield return null;
            float p3Health = _p3.CurrentHealth;
            Check(melee != null && melee.enabled && melee.TryBeginSwing(0), "P1 begins a real swept swing at rival P3");
            yield return WaitSwing(melee);
            yield return new WaitForSeconds(0.2f);
            Check(_p3.CurrentHealth < p3Health, $"P3 (rival party) took damage from the real swing ({p3Health:F0} -> {_p3.CurrentHealth:F0})");
            Check(EventsFor("damaged", _p3.PlayerId) >= 1 && EventsFor("health", _p3.PlayerId) >= 1 && EventsFor("dealt", _p1.PlayerId) >= 1,
                "the events carry the owners: P3 damaged/health, P1 dealt");
            Check(EventsFor("damaged", _p1.PlayerId) == 0 && EventsFor("health", _p1.PlayerId) == 0 && EventsFor("dealt", _p3.PlayerId) == 0 && EventsFor("damaged", _p2.PlayerId) == 0,
                "nothing was raised for P1 (as victim), P3 (as attacker) or P2");
            Check(_p3.CurrentHealth > 0, "a single swing did not kill P3");
            yield return new WaitForSeconds(1.3f);

            // --- swept melee, same party, friendly fire OFF: nothing happens
            Place(_p3, spot + new Vector3(0f, 0f, 90f));
            Place(_p2, spot + fwd * 1.4f);
            yield return null;
            CombatTeams.FriendlyFire = false;
            float p2Health = _p2.CurrentHealth;
            Check(melee.TryBeginSwing(0), "P1 swings at a party mate (the begin itself is allowed)");
            yield return WaitSwing(melee);
            yield return new WaitForSeconds(0.2f);
            Check(Mathf.Approximately(_p2.CurrentHealth, p2Health), $"no damage inside a party with friendly fire off ({_p2.CurrentHealth:F0})");
            yield return new WaitForSeconds(1.3f);

            // --- same party, friendly fire ON: damage
            CombatTeams.FriendlyFire = true;
            Check(melee.TryBeginSwing(0), "P1 swings at the party mate again with friendly fire on");
            yield return WaitSwing(melee);
            yield return new WaitForSeconds(0.2f);
            Check(_p2.CurrentHealth < p2Health, $"friendly fire on: the party mate takes damage ({p2Health:F0} -> {_p2.CurrentHealth:F0})");
            CombatTeams.FriendlyFire = false;
            yield return new WaitForSeconds(1.3f);

            // --- ranged path (the warrior's crossbow): rival hit, party mate not (FF off), party mate hit (FF on)
            Place(_p2, spot + new Vector3(0f, 0f, 60f));
            Check(_p1.Loadout.SwitchToSlot(1) && _p1.ActiveWeapon != null && _p1.ActiveWeapon.IsRanged, "P1 takes the crossbow");
            yield return null;
            RangedCombat ranged = _p1.GetComponent<RangedCombat>();
            Place(_p3, spot + fwd * 8f);
            Physics.SyncTransforms();
            yield return null;
            p3Health = _p3.CurrentHealth;
            Check(ranged != null && ranged.enabled, "P1 has the real ranged combat component");
            FireAndResolveProjectile(_p1, _p3, p3Health, true, "rival crossbow bolt");
            yield return new WaitForSeconds(1.6f);
            yield return ReloadCrossbowForNextShot(_p1, "friendly-fire-off preparation");

            Place(_p3, spot + new Vector3(0f, 0f, 90f));
            Place(_p2, spot + fwd * 8f);
            Physics.SyncTransforms();
            yield return null;
            p2Health = _p2.CurrentHealth;
            FireAndResolveProjectile(_p1, _p2, p2Health, false, "party crossbow bolt with friendly fire off");
            yield return new WaitForSeconds(1.6f);
            yield return ReloadCrossbowForNextShot(_p1, "friendly-fire-on preparation");
            CombatTeams.FriendlyFire = true;
            p2Health = _p2.CurrentHealth;
            FireAndResolveProjectile(_p1, _p2, p2Health, true, "party crossbow bolt with friendly fire on");
            CombatTeams.FriendlyFire = false;
            _p1.Loadout.SwitchToSlot(0);
            yield return new WaitForSeconds(1.0f);
            Place(_p2, spot + new Vector3(0f, 0f, 60f));
        }

        private IEnumerator ReloadCrossbowForNextShot(PlayerRig shooter, string label)
        {
            var ranged = shooter.GetComponent<RangedCombat>();
            int magazine = ranged.Magazine, reserve = ranged.Reserve;
            Check(magazine == 0 && reserve > 0, label + " has an empty crossbow and a real reserve bolt");
            bool accepted = _authority.TryStartReload(shooter.CombatSender, shooter.CombatId, out string reason);
            Check(accepted, label + " starts sender-bound reload: " + reason);
            if (!accepted) yield break;
            yield return null;
            float waited = 0f;
            float limit = Mathf.Max(shooter.ActiveWeapon.reloadTime, ranged.ReloadDuration) + 3f;
            while ((ranged.IsReloading || ranged.Magazine == magazine) && waited < limit)
            { waited += Time.deltaTime; yield return null; }
            Check(!ranged.IsReloading && ranged.Magazine == magazine + 1 && ranged.Reserve == reserve - 1,
                label + " completes exactly one reserve-to-magazine transfer");
        }

        private void FireAndResolveProjectile(PlayerRig shooter, PlayerRig target, float healthBefore,
            bool shouldDamage, string label)
        {
            WeaponData weapon = shooter != null ? shooter.ActiveWeapon : null;
            if (shooter == null || target == null || weapon == null || !weapon.IsRanged)
            {
                Check(false, label + ": shooter, target, or authoritative ranged weapon missing");
                return;
            }

            AttackIntent intent = new AttackIntent(shooter.CombatId, shooter.Kind, weapon, 1f,
                shooter.GetMuzzlePosition(), shooter.AimDirection, weapon.rangedRange,
                CombatRegistry.NextSequence(shooter.CombatId), Time.time);
            bool launched = _authority.LaunchProjectileAsOwner(intent);
            Check(launched, label + " launches through LocalCombatAuthority: " + _authority.LastAttackRejectReason);
            if (launched)
            {
                // Resolve the actual in-flight projectile over simulated frames instead of expecting a shot
                // launched this frame to cross an 8 m lane before the next rendered frame.
                for (int frame = 0; frame < 300; frame++)
                {
                    if (shouldDamage && target.CurrentHealth < healthBefore) break;
                    _authority.AdvanceProjectiles(1f / 60f);
                }
            }

            bool damaged = target.CurrentHealth < healthBefore;
            Check(shouldDamage ? damaged : Mathf.Approximately(target.CurrentHealth, healthBefore),
                label + (shouldDamage ? " damages the expected target" : " leaves the party mate unharmed")
                + $" ({healthBefore:F0} -> {target.CurrentHealth:F0})");
        }

        private IEnumerator WaitSwing(MeleeCombat melee)
        {
            float guard = 0f;
            while (melee.IsSwinging && guard < 3f) { guard += Time.deltaTime; yield return null; }
        }

        // ---- 4. the real HUD follows only its own player ---------------------------------------------------

        private IEnumerator HudIsolation()
        {
            HudController hud = FindFirstObjectByType<HudController>();
            Check(hud != null, "the scene HUD exists");
            if (hud == null) yield break;

            Text health = Field(hud, "_healthLabel") as Text;
            Text toast = Field(hud, "_toastLabel") as Text;
            Text prompt = Field(hud, "_promptLabel") as Text;
            Check(health != null && toast != null && prompt != null, "the HUD's health, toast and prompt labels are reachable");
            if (health == null || toast == null || prompt == null) yield break;

            // make sure the local label shows P1's own number first
            GameEvents.RaisePlayerHealth(_p1.PlayerId, _p1.CurrentHealth, _p1.MaxHealth);
            string own = health.text;
            Check(own == Mathf.CeilToInt(_p1.CurrentHealth) + " / " + Mathf.CeilToInt(_p1.MaxHealth), $"the HUD reads P1's health ({own})");

            // another player's health, prompt, toast, damage: the HUD does not change
            float vignetteBefore = (float)Field(hud, "_vignetteTimer");
            GameEvents.RaisePlayerHealth(_p3.PlayerId, 3f, 77f);
            GameEvents.RaiseInteractionPrompt(_p3.PlayerId, "P3 only prompt");
            GameEvents.RaiseToast(_p3.PlayerId, "P3 only toast");
            GameEvents.RaisePlayerDamaged(_p3.PlayerId, new DamageInfo(5f, Vector3.zero, Vector3.forward, Vector3.zero, 0, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Check(health.text == own, $"P3's health event did not touch P1's health bar ({health.text})");
            Check(prompt.text != "P3 only prompt" && toast.text != "P3 only toast", "P3's prompt and toast did not reach P1's HUD");
            Check((float)Field(hud, "_vignetteTimer") <= vignetteBefore + 1e-4f, "P3's damage did not flash P1's damage vignette");

            // the same events for the LOCAL player do update it
            GameEvents.RaisePlayerHealth(_p1.PlayerId, 5f, 120f);
            GameEvents.RaiseInteractionPrompt(_p1.PlayerId, "P1 prompt");
            GameEvents.RaiseToast(_p1.PlayerId, "P1 toast");
            Check(health.text == "5 / 120" && prompt.text == "P1 prompt" && toast.text == "P1 toast", "P1's own events update P1's HUD");

            // a global toast reaches everybody
            GameEvents.RaiseToast("for everyone");
            Check(toast.text == "for everyone", "a global toast (no owner) shows on the HUD");

            // restore the readout to the truth
            GameEvents.RaisePlayerHealth(_p1.PlayerId, _p1.CurrentHealth, _p1.MaxHealth);
            yield return new WaitForSecondsRealtime(.6f);
            GameEvents.RaisePlayerDamaged(_p1.PlayerId, new DamageInfo(0f, Vector3.zero, Vector3.forward, Vector3.zero, 0, EntityKind.Enemy, DamageType.Physical, true, false, 1f));
            Check((float)Field(hud, "_vignetteTimer") <= 0f && toast.text == "Blocked",
                "a fully absorbed hit shows the block cue without flashing red");
            GameEvents.RaisePlayerDamaged(_p1.PlayerId, new DamageInfo(0f, Vector3.zero, Vector3.forward, Vector3.zero, 0, EntityKind.Enemy, DamageType.Physical, false, false, 0f));
            Check((float)Field(hud, "_vignetteTimer") <= 0f && toast.text != "Hit  -0 HP",
                "a zero-damage event without a block flag leaves the vignette off");

            var audio = FindFirstObjectByType<WorldAudio>();
            Check(audio != null && Camera.main != null, "defense presentation has world audio and a camera");
            if (audio != null && Camera.main != null)
            {
                WorldAudio.BindListener(_p1.Look.AimTransform);
                Vector3 contact = _p1.AimOrigin + _p1.AimDirection;
                int sparks = Array.FindAll(FindObjectsByType<ImpactFlash>(FindObjectsSortMode.None), x => x.name == "ParrySpark").Length;
                int before = audio.PlayedCount;
                GameEvents.RaiseGuardContact(_p1.PlayerId, contact, false);
                var voices = audio.GetComponentsInChildren<AudioSource>();
                Check(audio.PlayedCount == before + 1 && Array.Exists(voices, v => v.clip != null && v.clip.name == "sfx_block"),
                    "a normal block plays the distinct block clip once");
                Check(Array.FindAll(FindObjectsByType<ImpactFlash>(FindObjectsSortMode.None), x => x.name == "ParrySpark").Length == sparks,
                    "a normal block does not emit the parry burst");
                GameEvents.RaiseGuardContact(_p1.PlayerId, contact, true);
                Check(audio.PlayedCount == before + 2 && Array.Exists(voices, v => v.clip != null && v.clip.name == "sfx_parry"),
                    "a parry plays the distinct parry clip once");
                Check(Array.FindAll(FindObjectsByType<ImpactFlash>(FindObjectsSortMode.None), x => x.name == "ParrySpark").Length == sparks + 5
                    && toast.text == "Parry" && (float)Field(hud, "_vignetteTimer") <= 0f,
                    "a parry emits a burst, identifies itself in the HUD and keeps the damage vignette off");
            }
            GameEvents.RaisePlayerDamaged(_p1.PlayerId, new DamageInfo(5f, Vector3.zero, Vector3.forward, Vector3.zero, 0, EntityKind.Enemy, DamageType.Physical, true, false, .3f, guardBroken: true));
            Check((float)Field(hud, "_vignetteTimer") > 0f && toast.text == "GUARD BROKEN  -5 HP",
                "a broken guard with actual HP loss still flashes red");
            yield return null;
        }

        // ---- 5. enemies choose the nearest hostile player ----------------------------------------------------

        private IEnumerator EnemyTargeting()
        {
            EnemyAgent agent = null;
            foreach (EnemyAgent a in FindObjectsByType<EnemyAgent>(FindObjectsSortMode.None)) { agent = a; break; }
            Check(agent != null && agent.Data != null, "an enemy exists");
            if (agent == null) yield break;

            // The previous section left P4 dead; bring a LIVING fourth candidate by using P2/P3/P1 only for the nearest/dead logic:
            // P4 is dead and nearest to prove dead players are skipped.
            Vector3 spot = Origin + new Vector3(0f, 0f, 800f);
            CharacterController ac = agent.GetComponent<CharacterController>();
            agent.transform.position = new Vector3(spot.x, agent.transform.position.y, spot.z);
            Physics.SyncTransforms();

            Place(_p4, spot + new Vector3(3f, 0f, 0f));      // nearest but dead
            Place(_p3, spot + new Vector3(8f, 0f, 0f));      // nearest living (rival party)
            Place(_p1, spot + new Vector3(11f, 0f, 0f));
            Place(_p2, spot + new Vector3(14f, 0f, 0f));
            Check(_p4.IsDead, "P4 is dead");
            _p1.Inventory.Clear();
            yield return null;

            agent.enabled = true;
            yield return null; yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p3.CombatId, $"the enemy targets the nearest LIVING hostile player, P3, not the dead P4 or the local rig ({agent.CurrentTargetCombatId} vs P3 {_p3.CombatId})");

            // P3 walks away: the enemy retargets to the next nearest (P1, the local player, is not special)
            Place(_p3, spot + new Vector3(40f, 0f, 0f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p1.CombatId, "with P3 out of range the enemy picks P1");

            // P1 out of range too: P2 (a different rig of the local party) is chosen, proving no 'local player' bias
            Place(_p1, spot + new Vector3(0f, 0f, 40f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p2.CombatId, "and then P2");

            // nobody in range: no target
            Place(_p2, spot + new Vector3(40f, 0f, 40f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == 0, "with everyone out of perception range there is no target");

            // aggro: whoever hurt it last is preferred for a while, unless a rival is clearly closer
            Place(_p1, spot + new Vector3(6f, 0f, 0f));
            Place(_p2, spot + new Vector3(7f, 0f, 0f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p1.CombatId, "P1 is the nearest of two");
            Place(_p1, spot + new Vector3(7.5f, 0f, 0f));
            Place(_p2, spot + new Vector3(7f, 0f, 0f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p1.CombatId, "a rival only slightly closer does not steal the target (no flicker)");
            Place(_p1, spot + new Vector3(12f, 0f, 0f));
            yield return null; yield return null;
            Check(agent.CurrentTargetCombatId == _p2.CombatId, "a clearly closer rival does");

            // the static helper agrees and a friendly seeker is never offered (an enemy has no friends: all players are hostile)
            Check(CombatRegistry.TryGetNearestHostilePlayer(agent, agent.transform.position, 100f, 0, 0.75f, out ICombatEntity nearest) && nearest == (ICombatEntity)_p2,
                "TryGetNearestHostilePlayer returns the nearest living hostile player");

            agent.enabled = false;
            yield return null;
        }

        // ---- 6. extraction and death are per player ------------------------------------------------------------

        private IEnumerator ExtractionAndDeath()
        {
            _events.Clear();
            _runEnded.Clear();
            DungeonLayoutData layout = _director.Layout;
            ExtractionPoint gate = FindFirstObjectByType<ExtractionPoint>();
            Check(layout != null && gate != null, "the level has an extraction gate");
            if (layout == null || gate == null) yield break;
            Vector3 gateCenter = layout.CellCenterWorld(layout.extractionCell);

            // P1 has no loot, P2 has loot (10 coins x 7), P3 has loot. All three stand at the gate.
            _p1.Inventory.Clear();
            Check(_p1.Inventory.RecoveredValue == 0 && _p2.Inventory.RecoveredValue > 0 && _p3.Inventory.RecoveredValue > 0, "P1 carries nothing, P2 and P3 carry value");
            Check(_director.Config.requireLootToExtract, "the loot rule is on");

            foreach (PlayerRig r in new[] { _p1, _p2, _p3 })
            {
                Place(r, gateCenter - Vector3.forward * 1.2f);
            }
            yield return null;
            Check(!gate.CanInteract(_p1, out string r1) && r1.Contains("relic"), $"the rule is evaluated per actor: P1 (no loot) is refused ({r1})");
            Check(gate.CanInteract(_p2, out string r2), $"P2 (carrying value) is allowed ({r2})");
            Check(!_director.BeginExtraction(_p1, out string direct1) && direct1.Contains("relic"), "the director refuses P1 directly too");
            Check(_director.State == RunState.Running && _director.TryGetSession(_p1.PlayerId, out PlayerSession s1) && s1.IsRunning, "P1's refused attempt left everyone Running");
            FakeActor stranger = new FakeActor { Id = 4242, Alive = true, Bag = _p2.Inventory, Origin = _p2.InteractionOrigin };
            Check(!gate.CanInteract(stranger, out string rs) && rs.Contains("another floor"), $"a stranger has no admitted extraction floor ({rs})");
            Check(!_director.BeginExtraction(stranger, out string strangerReason) && strangerReason.Contains("Not a player"),
                $"a stranger cannot extract through the director ({strangerReason})");

            // a REAL hold through P2's own Interactor extracts P2 only
            Place(_p1, Origin + new Vector3(500f, 0f, 0f));
            Place(_p3, Origin + new Vector3(500f, 0f, 30f));
            Place(_p2, gateCenter - Vector3.forward * 1.2f);
            yield return null; yield return null;
            Interactor interactor = _p2.GetComponent<Interactor>();
            _p2.SetInputEnabled(true);
            yield return null;
            Check(interactor != null && ReferenceEquals(interactor.CurrentTarget, gate), "P2's interactor targets the gate");
            _events.Clear();
            _p2.Input.InteractHeldOverride = true;
            float guard = 0f;
            while (_director.LocalSession != null && _director.TryGetSession(_p2.PlayerId, out PlayerSession live) && live.IsRunning && guard < gate.HoldDuration + 3f)
            {
                guard += Time.deltaTime;
                yield return null;
            }
            _p2.Input.InteractHeldOverride = null;
            Check(_director.TryGetSession(_p2.PlayerId, out PlayerSession s2) && s2.State == RunState.Extracted && s2.HasResult && s2.Result.Outcome == RunState.Extracted
                  && s2.Result.PlayerId == _p2.PlayerId && s2.Result.LootValue == _p2.Inventory.RecoveredValue && s2.Result.LootValue > 0,
                $"P2 extracted with its own result (loot {(s2 != null ? s2.Result.LootValue : -1)})");
            Check(_p2.IsExtracted && !_p2.IsInPlay, "P2's rig left the dungeon");
            Check(_director.State == RunState.Running, "the run goes on for the others");
            Check(_director.TryGetSession(_p1.PlayerId, out PlayerSession a1) && a1.IsRunning && _director.TryGetSession(_p3.PlayerId, out PlayerSession a3) && a3.IsRunning, "P1 and P3 are still running");
            Check(EventsFor("runstate:Extracted", _p2.PlayerId) == 1 && EventsOf("runstate:Extracted") == 1 && _runEnded.Count == 1 && _runEnded[0] == _p2.PlayerId,
                "only P2's viewers were told (one state change, one result)");
            Check(_p2.Input.InteractHeldOverride == null, "override cleared");
            Check(EventsFor("runstate:Dead", _p1.PlayerId) == 0 && EventsFor("runstate:Dead", _p3.PlayerId) == 0, "no state change for P1 or P3");

            // an extracted player is out of the fight: nobody is hostile to them, loot and the gate refuse them
            Check(!CombatTeams.AreHostile(_p3, _p2) && !CombatTeams.AreHostile(_p2, _p3), "an extracted player is nobody's enemy");
            Check(!gate.CanInteract(_p2, out string again) && again.Contains("another floor"), $"P2 no longer has an admitted extraction floor ({again})");
            Check(!_director.BeginExtraction(_p2, out string finishedReason) && finishedReason.Contains("finished"),
                $"P2 cannot extract again through the director ({finishedReason})");
            Check(_director.CollectLootCounted(_p2, _coin, 1) == 0, "and takes no more loot");
            EnemyAgent agent = FindFirstObjectByType<EnemyAgent>();
            Place(_p3, Origin + new Vector3(500f, 0f, 300f));
            Check(CombatRegistry.TryGetNearestHostilePlayer(agent, _p2.CombatCenter, 5f, 0, 0.75f, out ICombatEntity found) == false || found != (ICombatEntity)_p2,
                "monsters do not target the extracted player");

            // P1 tries after P2 left: the rule is still per actor. P1 gets loot, then extracts for real
            Place(_p1, Origin + new Vector3(500f, 0f, 600f));
            _director.SpawnLoot(_coin, 1, _p1.transform.position + Vector3.forward * 1.1f);
            Physics.SyncTransforms();
            LootPickup coin = FindNearestPickup(_p1.transform.position);
            coin.OnInteractInstant(_p1);
            Check(_p1.Inventory.RecoveredValue == 7, "P1 recovers a relic of its own");
            Place(_p1, gateCenter - Vector3.forward * 1.2f);
            yield return null;
            Check(gate.CanInteract(_p1, out string ok1), $"now P1 may extract ({ok1})");

            // P3 dies while P1 is still in: the run does not end
            _events.Clear();
            LethalHit(_p3, 100000f);
            yield return null;
            Check(_p3.IsDead && _director.TryGetSession(_p3.PlayerId, out PlayerSession d3) && d3.State == RunState.Dead && d3.Result.Outcome == RunState.Dead && d3.Result.PlayerId == _p3.PlayerId,
                "P3 died: its own result says Dead");
            Check(_director.State == RunState.Running && _director.TryGetSession(_p1.PlayerId, out PlayerSession still1) && still1.IsRunning, "the run did not end: P1 is still in it");
            Check(EventsFor("runstate:Dead", _p3.PlayerId) == 1 && EventsOf("runstate:Dead") == 1, "only P3's viewers were told about P3's death");
            Check(!gate.CanInteract(_p3, out string deadGate) && !string.IsNullOrEmpty(deadGate), $"the dead P3 cannot use the gate ({deadGate})");

            // P1 extracts through the gate: now nobody is left, the run ends, and the final state is Extracted (someone escaped)
            _runEnded.Clear();
            gate.OnHoldComplete(_p1);
            yield return null;
            Check(_director.TryGetSession(_p1.PlayerId, out PlayerSession e1) && e1.State == RunState.Extracted && e1.Result.LootValue == 7, "P1 extracted with its own 7 value");
            Check(_runEnded.Count == 1 && _runEnded[0] == _p1.PlayerId, "P1's result event");
            Check(_director.State == RunState.Extracted, $"every player is out of the run: it ends as Extracted ({_director.State})");
            Check(RunContext.HasResult && RunContext.LastResult.PlayerId == _p1.PlayerId && RunContext.LastResult.Outcome == RunState.Extracted,
                "the lobby handoff holds the LOCAL player's result");
            Check(_director.Players.Count == 4 && _director.Players[0].Result.Outcome == RunState.Extracted && _director.Players[1].Result.Outcome == RunState.Extracted
                  && _director.Players[2].Result.Outcome == RunState.Dead && _director.Players[3].Result.Outcome == RunState.Dead, "four per-player results: E, E, D, D");
            yield return null;
        }
    }
}
