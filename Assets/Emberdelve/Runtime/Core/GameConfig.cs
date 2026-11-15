using UnityEngine;
using Emberdelve.Items;

namespace Emberdelve.Core
{
    /// <summary>
    /// Single tuning asset for the vertical slice: scene names, objective rules, interaction
    /// distances and the layer masks every physics query uses. Change values here, not in code.
    /// </summary>
    [CreateAssetMenu(menuName = "Emberdelve/01 Game Config", fileName = "EmberdelveGameConfig")]
    public class GameConfig : ScriptableObject
    {
        public const string DefaultLobbySceneName = "Lobby";
        public const string DefaultDungeonSceneName = "Emberdelve_ModularCrypts";
        public const string LegacyDungeonSceneName = "Emberdelve_Level01";

        [Header("Scenes")]
        public string lobbySceneName = DefaultLobbySceneName;
        public string dungeonSceneName = DefaultDungeonSceneName;

        [Header("Objective")]
        public string levelTitle = "The Ashen Halls";
        public string objectiveText = "Recover at least one relic, then hold the escape at the Ember Gate.";
        public bool requireLootToExtract = true;
        public float extractionHoldDuration = 3f;
        [Tooltip("U4B WIP: enable only with timeout finalization implemented. When enabled, the server denies extraction and new joins at expiry.")]
        public bool enableRaidCountdown;
        [Tooltip("U4B: server-owned maximum raid duration; defaults to the design's 10 minutes.")]
        public int raidDurationSeconds = Emberdelve.Rules.RaidCountdownRules.DefaultDurationSeconds;

        [Header("Interaction")]
        public float interactionRange = 2.4f;
        public float interactSphereRadius = 0.45f;
        [Tooltip("R2: the authority accepts an interaction only when the acting player is within this many metres of the target (closest point) with a clear line of sight. The client prompt range (interactionRange) is shorter.")]
        public float interactionAuthorityRange = 3f;

        [Header("Players and parties (R7)")]
        [Tooltip("Largest party the run admits (DND_PARITY: up to 3).")]
        [Range(1, 3)] public int maxPartySize = 3;
        [Tooltip("Members of one party can hurt each other. OFF by default (open DND_PARITY question); players of different parties always can (PvP).")]
        public bool friendlyFire = false;

        [Header("Inventory")]
        [Tooltip("Carried items live in a width x height cell grid. A 2x3 sword needs a 2x3 hole.")]
        public int inventoryGridWidth = 10;
        public int inventoryGridHeight = 5;
        [Tooltip("Used when a grid shape has not been chosen, e.g. an older class asset asking for a bag size.")]
        public int inventoryCapacity = 8;

        [Header("Items")]
        [Tooltip("The one runtime item catalog (R3): every ItemData and the affix catalog. Built by the asset library.")]
        public ItemCatalog itemCatalog;

        [Tooltip("U5A: the bare-fist weapon the main hand falls back to when nothing is held. Not an item.")]
        public Emberdelve.Data.WeaponData unarmedWeapon;

        [Header("Look")]
        [Range(0.1f, 10f)] public float mouseSensitivity = 1.2f;
        [Range(20f, 120f)] public float fieldOfView = 68f;

        [Header("Combat")]
        [Tooltip("D1a hit-location damage multipliers. Empty = the code defaults (head 1.5, body 1, arms 0.8, hands 0.7, legs 0.6, feet 0.5).")]
        public Emberdelve.Data.HitLocationTable hitLocations;
        [Tooltip("D1b combat-flow tunables (guard break reduction scale, guard stagger length and its move slow). A config saved before D1b reads these defaults.")]
        public Emberdelve.Data.CombatFlowTuning flow = new Emberdelve.Data.CombatFlowTuning();

        [Header("Layers (must match ProjectSettings/TagManager)")]
        public LayerMask levelMask;
        public LayerMask playerMask;
        public LayerMask enemyMask;
        public LayerMask interactMask;
        public LayerMask hitMask;
        public LayerMask projectileMask;

        public const string LevelLayerName = "Emv_Level";
        public const string PlayerLayerName = "Emv_Player";
        public const string EnemyLayerName = "Emv_Enemy";
        public const string LootLayerName = "Emv_Loot";
        public const string InteractLayerName = "Emv_Interact";
        public const string ProjectileLayerName = "Emv_Projectile";

        public void EnsureValid()
        {
            if (interactionRange <= 0f) interactionRange = 2.4f;
            if (inventoryCapacity <= 0) inventoryCapacity = 8;
            if (inventoryGridWidth <= 0) inventoryGridWidth = 10;
            if (inventoryGridHeight <= 0) inventoryGridHeight = 5;
            if (extractionHoldDuration <= 0f) extractionHoldDuration = 3f;
            raidDurationSeconds = Emberdelve.Rules.RaidCountdownRules.NormalizeDurationSeconds(raidDurationSeconds);
            if (interactionAuthorityRange <= 0f) interactionAuthorityRange = 3f;
            maxPartySize = Mathf.Clamp(maxPartySize, 1, 3);
            if (hitMask.value == 0) hitMask = LayerMask.GetMask(PlayerLayerName, EnemyLayerName);
            if (interactMask.value == 0) interactMask = LayerMask.GetMask(LootLayerName, InteractLayerName);
            if (levelMask.value == 0) levelMask = LayerMask.GetMask(LevelLayerName);
            if (projectileMask.value == 0) projectileMask = LayerMask.GetMask(ProjectileLayerName);
        }
    }
}
