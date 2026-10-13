using UnityEngine;

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
        public const string DefaultDungeonSceneName = "Emberdelve_Level01";

        [Header("Scenes")]
        public string lobbySceneName = DefaultLobbySceneName;
        public string dungeonSceneName = DefaultDungeonSceneName;

        [Header("Objective")]
        public string levelTitle = "The Ashen Halls";
        public string objectiveText = "Recover at least one relic, then hold the escape at the Ember Gate.";
        public bool requireLootToExtract = true;
        public float extractionHoldDuration = 3f;

        [Header("Interaction")]
        public float interactionRange = 2.4f;
        public float interactSphereRadius = 0.45f;

        [Header("Inventory")]
        public int inventoryCapacity = 8;

        [Header("Look")]
        [Range(0.1f, 10f)] public float mouseSensitivity = 1.2f;
        [Range(20f, 120f)] public float fieldOfView = 68f;

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
            if (extractionHoldDuration <= 0f) extractionHoldDuration = 3f;
            if (hitMask.value == 0) hitMask = LayerMask.GetMask(PlayerLayerName, EnemyLayerName);
            if (interactMask.value == 0) interactMask = LayerMask.GetMask(LootLayerName, InteractLayerName);
            if (levelMask.value == 0) levelMask = LayerMask.GetMask(LevelLayerName);
            if (projectileMask.value == 0) projectileMask = LayerMask.GetMask(ProjectileLayerName);
        }
    }
}
