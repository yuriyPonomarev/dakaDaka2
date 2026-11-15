using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Tests.Level
{
    public sealed class WorldLightInteractableTests
    {
        private GameObject _root;
        private Light _light;
        private Renderer _flame;
        private WorldLightInteractable _switch;
        private sealed class Actor : IInteractionActor
        {
            public int PlayerId => 0;
            public int PartyId => 0;
            public bool IsAlive { get; set; } = true;
            public Vector3 InteractionOrigin { get; set; } = Vector3.back;
            public Emberdelve.Items.Inventory Inventory => null;
        }
        [SetUp] public void SetUp()
        {
            NetRole.ResetToOffline();
            _root = new GameObject("crypt_0_0");
            _root.AddComponent<DungeonModuleDefinition>();
            var source = new GameObject("Brazier_Light");
            source.transform.SetParent(_root.transform, false);
            _light = source.AddComponent<Light>(); _light.type = LightType.Point;
            var flame = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            flame.name = "Brazier_Flame"; flame.transform.SetParent(_root.transform, false);
            Object.DestroyImmediate(flame.GetComponent<Collider>());
            _flame = flame.GetComponent<Renderer>();
            WorldLightInteractable.Install(_root.transform);
            _switch = source.GetComponentInChildren<WorldLightInteractable>();
            Physics.SyncTransforms();
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_root); NetRole.ResetToOffline(); }

        [Test] public void ExtinguishAndRelightKeepInteractionAvailableAndToggleGlow()
        {
            var actor = new Actor();
            Assert.IsTrue(_switch.TrySetAuthoritatively(actor, false, out _));
            Assert.IsFalse(_light.enabled); Assert.IsFalse(_flame.enabled);
            foreach (var renderer in _light.GetComponentsInChildren<Renderer>()) Assert.IsFalse(renderer.enabled);
            var trigger = _switch.GetComponent<Collider>();
            Assert.IsTrue(trigger.enabled); Assert.IsTrue(trigger.isTrigger);
            Assert.AreSame(_switch, trigger.GetComponentInParent<MonoBehaviour>());
            Assert.AreEqual(LayerMask.NameToLayer(GameConfig.InteractLayerName), trigger.gameObject.layer);
            Assert.IsTrue(_switch.TrySetAuthoritatively(actor, true, out _));
            Assert.IsTrue(_light.enabled); Assert.IsTrue(_flame.enabled);
        }
        [Test] public void AuthorityRejectsDeadDistantInvalidAndOccludedActors()
        {
            var actor = new Actor { IsAlive = false };
            Assert.IsFalse(_switch.TrySetAuthoritatively(actor, false, out _));
            actor.IsAlive = true; actor.InteractionOrigin = Vector3.back * 8;
            Assert.IsFalse(_switch.TrySetAuthoritatively(actor, false, out _));
            actor.InteractionOrigin = new Vector3(float.NaN, 0, 0);
            Assert.IsFalse(_switch.TrySetAuthoritatively(actor, false, out _));
            actor.InteractionOrigin = Vector3.back * 2;
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.transform.SetParent(_root.transform); wall.transform.position = Vector3.back;
            wall.transform.localScale = new Vector3(2, 2, .2f);
            wall.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            Physics.SyncTransforms();
            Assert.IsFalse(_switch.TrySetAuthoritatively(actor, false, out string reason));
            Assert.AreEqual("No line of sight", reason); Assert.IsTrue(_light.enabled);
        }
        [TestCase(LightShadows.None)] [TestCase(LightShadows.Soft)]
        public void InstallationAndRelightingPreserveAuthoredLightBudget(LightShadows shadows)
        {
            var source = new GameObject("Authored_Light");
            source.transform.SetParent(_root.transform, false);
            source.transform.localPosition = Vector3.right * 2f;
            var light = source.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 12f; light.range = 14f;
            light.shadows = shadows; light.shadowStrength = .63f;
            light.shadowBias = .06f; light.shadowNormalBias = .25f; light.shadowNearPlane = .2f;
            int mask = 1 << LayerMask.NameToLayer(GameConfig.LevelLayerName);
            light.cullingMask = mask;
            source.AddComponent<Emberdelve.Feedback.TorchFlicker>();
            WorldLightInteractable.Install(_root.transform);
            WorldLightInteractable.Install(_root.transform);
            var interaction = source.GetComponentInChildren<WorldLightInteractable>();
            Assert.That(interaction, Is.Not.Null);
            var actor = new Actor { InteractionOrigin = source.transform.position + Vector3.back };
            Physics.SyncTransforms();
            Assert.That(interaction.TrySetAuthoritatively(actor, false, out var why), Is.True, why);
            Assert.That(light.enabled, Is.False);
            Assert.That(interaction.TrySetAuthoritatively(actor, true, out why), Is.True, why);
            Assert.That(light.enabled, Is.True);
            Assert.That(light.shadows, Is.EqualTo(shadows));
            Assert.That(light.shadowStrength, Is.EqualTo(.63f).Within(.0001f));
            Assert.That(light.shadowBias, Is.EqualTo(.06f).Within(.0001f));
            Assert.That(light.shadowNormalBias, Is.EqualTo(.25f).Within(.0001f));
            Assert.That(light.shadowNearPlane, Is.EqualTo(.2f).Within(.0001f));
            Assert.That(light.cullingMask, Is.EqualTo(mask));
            Assert.That(light.intensity, Is.EqualTo(12f));
            Assert.That(light.range, Is.EqualTo(14f));
        }
        [Test] public void ClientCannotMutateAuthorityAndReplicatedStateIsIdempotent()
        {
            NetRole.Set(NetRoleKind.Client);
            Assert.IsFalse(_switch.TrySetAuthoritatively(new Actor(), false, out _));
            Assert.IsTrue(_light.enabled);
            _switch.ApplyReplicatedState(false); _switch.ApplyReplicatedState(false);
            Assert.IsFalse(_light.enabled); Assert.IsFalse(_flame.enabled);
            _switch.ApplyReplicatedState(true); Assert.IsTrue(_light.enabled);
        }
        [Test] public void InstallerExcludesMoonAndIdentityDistinguishesDuplicateFixtures()
        {
            var second = new GameObject("Brazier_Light"); second.transform.SetParent(_root.transform, false);
            second.AddComponent<Light>().type = LightType.Point;
            var moon = new GameObject("Moon"); moon.transform.SetParent(_root.transform, false);
            moon.AddComponent<Light>().type = LightType.Directional;
            WorldLightInteractable.Install(_root.transform); WorldLightInteractable.Install(_root.transform);
            Assert.AreEqual(2, _root.GetComponentsInChildren<WorldLightInteractable>().Length);
            Assert.IsNull(moon.GetComponentInChildren<WorldLightInteractable>());
            Assert.AreNotEqual(_switch.StableId, second.GetComponentInChildren<WorldLightInteractable>().StableId);
            var outer = new GameObject("DifferentSceneContainer");
            try
            {
                _root.transform.SetParent(outer.transform);
                Assert.AreEqual(_switch.StableId, WorldLightInteractable.Identity(_light.transform));
            }
            finally { _root.transform.SetParent(null); Object.DestroyImmediate(outer); }
        }
    }
}
