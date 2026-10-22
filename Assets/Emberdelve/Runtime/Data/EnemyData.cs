using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Data
{
    /// <summary>Enemy stats and AI tuning. Consumed by EnemyBrain (pure logic) and EnemyAgent (scene object).</summary>
    [CreateAssetMenu(menuName = "Emberdelve/06 Enemy", fileName = "EnemyData")]
    public class EnemyData : ScriptableObject
    {
        [Header("Identity")]
        public string enemyId = "enemy";
        public string displayName = "Hollow Servitor";
        public bool isElite = false;

        [Header("Vitals")]
        public int maxHealth = 70;

        [Header("Combat")]
        [Tooltip("All damage numbers come from a WeaponData asset so players, enemies and future projectiles share one damage pipeline.")]
        public WeaponData attackWeapon;
        public float attackRange = 2.1f;
        public float attackWindup = 0.65f;
        public float attackCooldown = 1.9f;
        public float attackTelegraphHeight = 0.55f;
        [Tooltip("Total angle of the warned wedge. The attack is only submitted inside this wedge, so what the telegraph shows is what actually lands.")]
        [Range(40f, 200f)] public float attackTelegraphCone = 120f;
        public Color telegraphColor = new Color(1f, 0.32f, 0.18f, 1f);

        [Header("Movement")]
        public float patrolSpeed = 1.05f;
        public float chaseSpeed = 2.7f;
        public float patrolRadius = 7f;
        public float turnRate = 420f;

        [Header("Perception")]
        public float sightRange = 15f;
        [Range(40f, 360f)] public float sightCone = 130f;
        public float hearingRange = 9f;
        public float memoryDuration = 7f;
        public float investigateTime = 3.5f;

        [Header("Loot")]
        [Tooltip("R4: what this enemy drops when it dies. A normal loot table rolled with the raid's seed and issuer, " +
                 "so gear drops are concrete item instances. This is the only drop source the runtime reads.")]
        public LootTable dropTable;
        [Tooltip("Legacy flat item list and chance (pre-R4). No longer rolled at runtime; kept so authored assets and " +
                 "tools that read it keep working. The builder derives dropTable from it.")]
        public ItemData[] lootTable;
        [Range(0, 4)] public int lootRolls = 2;
        [Range(0f, 1f)] public float lootDropChance = 0.85f;

        [Header("Blockout visuals")]
        public Material bodyMaterial;
        public Color eliteTint = new Color(1f, 0.45f, 0.35f);
    }
}
