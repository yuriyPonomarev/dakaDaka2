using System.Reflection;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Combat.Tests
{
    public sealed class InteractorPortalTargetTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private GameObject _actor, _portal;
        private GameConfig _config;
        private Interactor _interactor;

        [SetUp] public void Setup()
        {
            _actor = new GameObject("PortalTargetActor");
            _actor.transform.position = new Vector3(17000, 1000, 17000);
            var rig = _actor.AddComponent<PlayerRig>();
            _interactor = _actor.GetComponent<Interactor>();
            if (_interactor == null) _interactor = _actor.AddComponent<Interactor>();
            _config = ScriptableObject.CreateInstance<GameConfig>();
            _config.interactionRange = 3f; _config.interactSphereRadius = .45f; _config.interactMask = 1 << 31;
            typeof(Interactor).GetField("rig", Private).SetValue(_interactor, rig);
            typeof(Interactor).GetField("config", Private).SetValue(_interactor, _config);
        }

        [TearDown] public void Cleanup()
        {
            // The production visual owns these meshes; explicit destruction also handles manual
            // EditMode lifecycles that do not deliver the visual's OnDestroy callback.
            if (_portal != null)
            {
                foreach (var filter in _portal.GetComponentsInChildren<MeshFilter>(true))
                    if (filter.sharedMesh != null) Object.DestroyImmediate(filter.sharedMesh);
                Object.DestroyImmediate(_portal);
            }
            if (_actor != null) Object.DestroyImmediate(_actor);
            if (_config != null) Object.DestroyImmediate(_config);
            Physics.SyncTransforms();
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void RealOverlapFindsPortalInterfaceBehindVisual(bool replicated, bool childCollider)
        {
            // Use the real production factory: it installs DescentPortalVisual before the slot.
            _portal = DescentPortalVisual.Create(null, "PortalTarget", null, 31);
            _portal.transform.position = _actor.transform.position + Vector3.up + Vector3.forward * 1.5f;
            IInteractable target = replicated
                ? (IInteractable)_portal.AddComponent<ReplicatedDescentPortalSlot>()
                : _portal.AddComponent<DescentPortalSlot>();
            if (childCollider)
            {
                _portal.GetComponent<BoxCollider>().enabled = false;
                var trigger = new GameObject("NestedPortalTrigger");
                trigger.transform.SetParent(_portal.transform, false); trigger.layer = 31;
                trigger.AddComponent<DescentPortalVisual>(); // Cosmetic child must not mask the parent target.
                trigger.AddComponent<BoxCollider>().isTrigger = true;
            }
            Physics.SyncTransforms();
            var found = (IInteractable)typeof(Interactor).GetMethod("ResolveTarget", Private).Invoke(_interactor, null);
            Assert.That(found, Is.SameAs(target), "The actual physics probe must resolve the authority/replicated slot through its interface");
        }
    }
}
