using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Data;

namespace Emberdelve.Items
{
    /// <summary>The run host exposes its raid session so containers can roll through it (chests, later a server).</summary>
    public interface IRaidSessionHost
    {
        RaidSession Session { get; }
    }

    /// <summary>
    /// R4/R5: everything one raid owns for loot and item identity, in one object.
    ///
    /// <b>Seed.</b> <see cref="RaidSeed"/> is a 64-bit value chosen when the raid starts (<see cref="RunContext.ResolveRaidSeed"/>:
    /// an explicit seed, else a high-entropy default). Every loot outcome of the raid is a pure function of that seed
    /// and a stable key, so (raidSeed, key) always yields the same contents and a different raidSeed yields different
    /// contents.
    ///
    /// <b>Derivation</b> (all through <see cref="RngMix.Combine"/>, FNV-1a for text; no generator state involved):
    /// <code>
    /// chestSeed(raid, chestId, salt)  = Combine(Combine(Combine(raid, H("chest")),      H(chestId)), uint(salt))
    /// enemyDropSeed(raid, dropKey)    = Combine(Combine(raid, H("enemy-drop")),          H(dropKey))
    /// uidStreamSeed(raid)             = Combine(raid, H("uid"))
    /// table streams                   = Combine(seed, 1) shape, Combine(seed, 2) affixes   (LootTable)
    /// </code>
    /// The authored per-chest seed on the layout stays what it was, a SALT: it separates two chests that share an id
    /// and lets level design re-roll one chest without touching the raid. The chest id is the chest's GameObject
    /// name, which the spawner derives from variant, cell and layout index, so it is stable per layout.
    ///
    /// <b>Identity.</b> <see cref="Issuer"/> is the one UID source of the raid. Chest rolls, enemy drops, the starting
    /// kit (the player's bag is pointed at this issuer by the run) and ground pickups of dropped instances all use
    /// it, so UIDs are unique within the raid across the bag, equipment and the ground. By default UIDs are
    /// UNIQUE BUT NOT REPRODUCIBLE (random 128-bit, each checked against the issuer's own ledger): contents and
    /// affix rolls are deterministic, identities are not, so replaying a raid seed twice into one profile cannot
    /// create duplicate items. A session built with <c>deterministicUids</c> draws UIDs from the seeded uid stream
    /// instead (tests and replay tooling); two such sessions with the same seed issue the same UIDs.
    ///
    /// A new session is created for every run (the run director builds one in Awake), so a retry after a result
    /// screen gets a fresh issuer and, by default, a fresh seed.
    /// </summary>
    public sealed class RaidSession
    {
        public const string ChestDomain = "chest";
        public const string EnemyDropDomain = "enemy-drop";
        public const string UidDomain = "uid";

        public const ulong ScatterStream = 3UL;

        private RaidSession(ulong raidSeed, string seedSource, ItemIdentityIssuer issuer)
        {
            RaidSeed = raidSeed;
            SeedSource = seedSource;
            Issuer = issuer;
        }

        /// <summary>Starts a raid with this seed. <paramref name="seedSource"/> is only a label for logs.</summary>
        public static RaidSession Begin(ulong raidSeed, string seedSource = "explicit", bool deterministicUids = false)
        {
            ItemIdentityIssuer issuer = deterministicUids
                ? ItemIdentityIssuer.CreateSeeded(new DeterministicRng(UidStreamSeed(raidSeed)))
                : ItemIdentityIssuer.CreateLocalTrusted();

            return new RaidSession(raidSeed, seedSource ?? "explicit", issuer);
        }

        public ulong RaidSeed { get; }
        public string SeedSource { get; }

        /// <summary>The raid's one UID source. Never create another issuer for loot of this raid.</summary>
        public ItemIdentityIssuer Issuer { get; }

        public static ulong UidStreamSeed(ulong raidSeed) =>
            RngMix.Combine(raidSeed, RngMix.HashString(UidDomain));

        public ulong ChestSeed(string chestId, int authoredSalt) => ChestSeed(RaidSeed, chestId, authoredSalt);

        public ulong EnemyDropSeed(string dropKey) => EnemyDropSeed(RaidSeed, dropKey);

        public static ulong ChestSeed(ulong raidSeed, string chestId, int authoredSalt)
        {
            ulong h = RngMix.Combine(raidSeed, RngMix.HashString(ChestDomain));
            h = RngMix.Combine(h, RngMix.HashString(chestId));
            return RngMix.Combine(h, unchecked((uint)authoredSalt));
        }

        public static ulong EnemyDropSeed(ulong raidSeed, string dropKey)
        {
            ulong h = RngMix.Combine(raidSeed, RngMix.HashString(EnemyDropDomain));
            return RngMix.Combine(h, RngMix.HashString(dropKey));
        }

        /// <summary>
        /// Rolls one chest's contents: (raid seed, chest id, authored salt) fully determine items, amounts and
        /// affixes; the UIDs come from the raid issuer.
        /// </summary>
        public bool TryRollChest(LootTable table, string chestId, int authoredSalt, ItemCatalog catalog,
            List<LootDrop> output, out string reason)
        {
            if (table == null)
            {
                reason = "no loot table";
                return false;
            }

            return table.TryRollSeeded(ChestSeed(chestId, authoredSalt), output, catalog, Issuer, out reason);
        }

        /// <summary>
        /// Rolls the drops of one enemy that died: the enemy's drop table seeded by (raid seed, drop key). An enemy
        /// without a drop table drops nothing (and reports no error).
        /// </summary>
        public bool TryRollEnemyDrops(EnemyData enemy, string dropKey, ItemCatalog catalog,
            List<LootDrop> output, out string reason)
        {
            if (output == null)
            {
                reason = "no output list";
                return false;
            }

            output.Clear();
            reason = null;

            if (enemy == null || enemy.dropTable == null) return true;

            return enemy.dropTable.TryRollSeeded(EnemyDropSeed(dropKey), output, catalog, Issuer, out reason);
        }

        /// <summary>The deterministic stream a dropped item's scatter position is drawn from (gameplay position, not identity).</summary>
        public DeterministicRng ScatterRng(string dropKey) =>
            new DeterministicRng(EnemyDropSeed(dropKey)).Fork(ScatterStream);

        public override string ToString() =>
            $"raid seed 0x{RaidSeed:X16} ({SeedSource}), uids {(Issuer.IsSeeded ? "seeded" : "random")}, issued {Issuer.MintedCount}";
    }
}
