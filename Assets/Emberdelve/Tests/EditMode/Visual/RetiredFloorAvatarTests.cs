using System.Collections.Generic;
using System.Reflection;
using Emberdelve.Level;
using Emberdelve.Player;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.Tests.Visual
{
    public sealed class RetiredFloorAvatarTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly List<Object> _owned = new List<Object>();
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, Private).SetValue(target, value);
        private DungeonModuleSceneAssembler Map(float x)
        {
            var root = new GameObject("AvatarRetirementMap"); _owned.Add(root);
            var layout = ScriptableObject.CreateInstance<DungeonLayoutData>(); _owned.Add(layout);
            layout.origin = new Vector3(x, 0, 0); layout.gridWidth = layout.gridHeight = 20; layout.cellSize = 1;
            var map = root.AddComponent<DungeonModuleSceneAssembler>(); Set(map, "_runtimeLayout", layout); return map;
        }
        private (PlayerAvatar avatar, Renderer renderer, PlayerRig rig) Avatar(float x)
        {
            var carrier = new GameObject("RetainedAvatarCarrier"); _owned.Add(carrier);
            carrier.transform.position = new Vector3(x, 0, 5);
            var rig = carrier.AddComponent<PlayerRig>(); rig.AssignParty(0, false);
            var avatar = carrier.AddComponent<PlayerAvatar>();
            var model = GameObject.CreatePrimitive(PrimitiveType.Cube); model.transform.SetParent(carrier.transform, false);
            var renderer = model.GetComponent<Renderer>();
            Set(avatar, "_rig", rig); Set(avatar, "_model", model); Set(avatar, "_renderers", new[] { renderer });
            return (avatar, renderer, rig);
        }
        private static void Frame(PlayerAvatar avatar) => typeof(PlayerAvatar).GetMethod("LateUpdate", Private).Invoke(avatar, null);
        [TearDown] public void Cleanup()
        {
            for (int i = _owned.Count - 1; i >= 0; i--) if (_owned[i] != null) Object.DestroyImmediate(_owned[i]);
            _owned.Clear();
            DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(Vector3.zero); // Prunes manual EditMode destruction.
        }
        [Test] public void SourceAvatarHidesWhileDestinationAndCarrierRemain()
        {
            var source = Map(0); Map(100);
            var oldActor = Avatar(5); var survivor = Avatar(105);
            Frame(oldActor.avatar); Frame(survivor.avatar);
            Assert.True(oldActor.renderer.enabled); Assert.True(survivor.renderer.enabled);
            source.RetireRuntimeContent(); Frame(oldActor.avatar); Frame(survivor.avatar);
            Assert.False(oldActor.renderer.enabled); Assert.True(survivor.renderer.enabled);
            Assert.True(oldActor.rig.gameObject.activeInHierarchy); Assert.False(oldActor.rig.IsExtracted);
            Assert.False(oldActor.rig.IsDead, "Presentation must not mutate retained actor state");
        }
        [Test] public void AvatarCreatedAfterRetirementAlsoHides()
        {
            Map(0).RetireRuntimeContent();
            var late = Avatar(5); Frame(late.avatar);
            Assert.False(late.renderer.enabled); Assert.True(late.rig.gameObject.activeInHierarchy);
        }
        [Test] public void DestroyedMapDoesNotHideNextRunAtSameCoordinates()
        {
            var oldMap = Map(0); oldMap.RetireRuntimeContent();
            Assert.True(DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(new Vector3(5, 0, 5)));
            Object.DestroyImmediate(oldMap.gameObject);
            Map(0); var nextRun = Avatar(5); Frame(nextRun.avatar);
            Assert.False(DungeonModuleSceneAssembler.IsPositionOnRetiredFloor(new Vector3(5, 0, 5)));
            Assert.True(nextRun.renderer.enabled);
        }
        [Test] public void TemporaryInactiveMapDoesNotHideItsAvatar()
        {
            var map = Map(0); map.gameObject.SetActive(false);
            var actor = Avatar(5); Frame(actor.avatar); Assert.True(actor.renderer.enabled);
        }
    }
}
