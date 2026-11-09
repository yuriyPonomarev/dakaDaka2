using System.Reflection;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.EditorTools;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.Tests.Visual
{
    public sealed class EnemyBloodFeedbackTests
    {
        [Test]
        public void DamageEmitsAtHitPoint_ZeroDamageDoesNot_AndInvalidPointFallsBack()
        {
            var parent = new GameObject("EnemyBloodTest");
            try
            {
                var agent = parent.AddComponent<EnemyAgent>();
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(EmberdelveEnemyVisualFactory.PrefabPath("Minion"));
                var visual = Object.Instantiate(prefab, parent.transform);
                var driver = visual.GetComponent<EnemyVisualDriver>();
                var particles = visual.GetComponentInChildren<ParticleSystem>(true);
                Assert.NotNull(particles);
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
                typeof(EnemyVisualDriver).GetField("_agent", flags).SetValue(driver, agent);
                var handle = typeof(EnemyVisualDriver).GetMethod("HandleDamaged", flags);
                DamageInfo Hit(float amount, Vector3 point) => new DamageInfo(amount, point, Vector3.forward,
                    Vector3.zero, 1, EntityKind.LocalPlayer, DamageType.Physical, false, false, 0f);

                handle.Invoke(driver, new object[] { Hit(0f, Vector3.one) });
                Assert.AreEqual(0, particles.particleCount);
                var point = new Vector3(0.2f, 1f, 0.1f);
                handle.Invoke(driver, new object[] { Hit(10f, point) });
                Assert.AreEqual(18, particles.particleCount);
                Assert.That(Vector3.Distance(point, particles.transform.position), Is.LessThan(0.001f));
                Assert.That(Vector3.Dot(particles.transform.forward, Vector3.back), Is.GreaterThan(0.999f),
                    "blood should spray away from the attacker-facing incoming direction");
                handle.Invoke(driver, new object[] { Hit(10f, new Vector3(float.NaN, 0f, 0f)) });
                Assert.AreEqual(36, particles.particleCount);
                Assert.That(Vector3.Distance(Vector3.up, particles.transform.position), Is.LessThan(0.001f));
            }
            finally { Object.DestroyImmediate(parent); }
        }
    }
}
