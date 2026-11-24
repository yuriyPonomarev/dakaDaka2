using UnityEngine;
using Emberdelve.Core;

namespace Emberdelve.Data
{
    /// <summary>Enemy stats and AI tuning. Consumed by EnemyBrain (pure logic) and EnemyAgent (scene object).</summary>
    [CreateAssetMenu(menuName = "Emberdelve/06 Enemy", fileName = "EnemyData")]
    public class EnemyData : ScriptableObject
    {
        public const string CinderWardenId = "cinder_warden";
        public const string CinderWardenVisual = "CinderWarden";
        public const float CinderWardenCenterHeight = 1.65f;

        [Header("Identity")]
        public string enemyId = "enemy";
        public string displayName = "Hollow Servitor";
        public bool isElite = false;
        public bool IsCinderWarden => enemyId == CinderWardenId;
        public const string ArcherVisual = "Archer";
        /// <summary>A ranged undead: its attack weapon is a projectile weapon (a bow), so it keeps its distance and shoots instead of swinging.</summary>
        public bool IsArcher => attackWeapon != null && attackWeapon.IsRanged;
        public float BodyScale => IsCinderWarden ? 1.50f : isElite ? 1.25f : 1f;

        /// <summary>
        /// The authored visual variant a monster of <paramref name="enemy"/> wears at spawn record <paramref name="spawnIndex"/>: the Cinder Warden's
        /// own model, the Archer for a bowman, the Warrior for elites, and Minion / Rogue alternating for the rest so a room does not look cloned.
        /// One source for the scene builder, the modular assembler and the floor content spawner.
        /// </summary>
        public static string VisualVariantFor(EnemyData enemy, int spawnIndex)
        {
            if (enemy != null && enemy.IsCinderWarden) return CinderWardenVisual;
            if (enemy != null && enemy.IsArcher) return ArcherVisual;
            if (enemy != null && enemy.isElite) return "Warrior";
            return (spawnIndex & 1) == 0 ? "Minion" : "Rogue";
        }

        [Header("Vitals")]
        public int maxHealth = 70;

        [Header("Combat")]
        [Tooltip("All damage numbers come from a WeaponData asset so players, enemies and future projectiles share one damage pipeline.")]
        public WeaponData attackWeapon;
        public float attackRange = 2.1f;
        public float attackWindup = 0.65f;
        public float attackCooldown = 1.9f;
        public float attackTelegraphHeight = 0.55f;
        [Tooltip("Total angle of the warned wedge. D1c4: this is also the yaw range of the attack weapon's swing pattern (the data build authors the pattern from it), so the telegraph is the ground projection of the swept swing that really lands. The pattern's timing (windup, active, recovery), reach and sweep radius are data on the pattern and the weapon, not here.")]
        [Range(40f, 200f)] public float attackTelegraphCone = 120f;
        public Color telegraphColor = new Color(1f, 0.32f, 0.18f, 1f);

        [Header("Ranged (archers only)")]
        [Tooltip("Preferred distance band (m). Closer than min the archer backs off or strafes; farther than max it advances.")]
        public float rangedMinDistance = 6f;
        public float rangedMaxDistance = 14f;
        [Tooltip("Closer than this (m) the archer flees instead of drawing, unless it is cornered (then it shoots anyway).")]
        public float rangedFleeDistance = 3.5f;

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
