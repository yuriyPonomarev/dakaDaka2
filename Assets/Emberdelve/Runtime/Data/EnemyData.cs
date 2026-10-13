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
        public ItemData[] lootTable;
        [Range(0, 4)] public int lootRolls = 2;
        [Range(0f, 1f)] public float lootDropChance = 0.85f;

        [Header("Blockout visuals")]
        public Material bodyMaterial;
        public Color eliteTint = new Color(1f, 0.45f, 0.35f);
    }
}
