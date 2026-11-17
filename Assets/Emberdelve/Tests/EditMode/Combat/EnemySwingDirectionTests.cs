using Emberdelve.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Combat.Tests
{
    /// <summary>
    /// The shared enemy attack clip (Sword_Regular_A) moves the attacker's hand from his right to his left. The damage sweep of every
    /// enemy that plays it must run the same way, or the telegraphed blow and the real hit disagree.
    /// </summary>
    public sealed class EnemySwingDirectionTests
    {
        [TestCase("Assets/Emberdelve/Data/Pattern_ClawSlash.asset")]
        [TestCase("Assets/Emberdelve/Data/Pattern_MaulSweep.asset")]
        public void SwordClipEnemies_SweepFromTheAttackersRightToLeft(string path)
        {
            SwingPatternData pattern = AssetDatabase.LoadAssetAtPath<SwingPatternData>(path);
            Assert.That(pattern, Is.Not.Null, path);
            Assert.That(pattern.kind, Is.EqualTo(SwingKind.HorizontalRightToLeft));
            SwingKey first = pattern.keys[0];
            SwingKey last = pattern.keys[pattern.keys.Length - 1];
            Assert.That(first.basePos.x, Is.GreaterThan(last.basePos.x), "the hand travels right to left");
            Assert.That(first.bladeYaw, Is.GreaterThan(last.bladeYaw), "the blade yaw turns right to left");
        }
    }
}
