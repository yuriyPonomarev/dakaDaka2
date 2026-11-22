using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>Authors circulation and furnishing into saved modules, using the existing crypt kit.</summary>
    internal static class DungeonRoomExpansion
    {
        private const float GalleryHeight = 3.9f;
        // Collision overlap remains continuous; StairSurfaceSeparation fits render meshes to dry masonry joints.
        private const float CollisionTreadDepth = .305f;
        internal static void BuildFloor(Transform root, Material paving, Material stone)
        {
            // Leave a genuine opening in the floor for the east undercroft. No invisible flat floor over stairs.
            Box(root, "Floor", new Vector3(-3, -.3f, 0), new Vector3(30, .6f, 36), paving);
            Box(root, "Floor_EastNorth", new Vector3(15, -.3f, 6.5f), new Vector3(6, .6f, 23), paving);
            Box(root, "Floor_EastSouth", new Vector3(15, -.3f, -16.5f), new Vector3(6, .6f, 3), paving);
            Box(root, "Floor_EastEdge", new Vector3(17.5f, -.3f, -10), new Vector3(1, .6f, 10), paving);
            Box(root, "Undercroft_Floor", new Vector3(14.5f, -1.8f, -10), new Vector3(5, .6f, 4), paving);
            Stairs(root, "Undercroft_SouthStairs", new Vector3(14.5f, -1.5f, -12), 180, 1.5f, 5, stone);
            Stairs(root, "Undercroft_NorthStairs", new Vector3(14.5f, -1.5f, -8), 0, 1.5f, 5, stone);
            // The ground slab supplies the upper 0.6 m of wall. StairSurfaceSeparation removes that
            // hidden portion from these render meshes while preserving the full-height collision backing.
            Box(root, "Undercroft_RetainingWest", new Vector3(11.8f, -.75f, -10), new Vector3(.4f, 1.5f, 10), stone);
            Box(root, "Undercroft_RetainingEast", new Vector3(17.2f, -.75f, -10), new Vector3(.4f, 1.5f, 10), stone);
        }

        internal static void Dress(Transform root, DungeonModuleKind kind, int variant, int destination, Material stone, Material paving)
        {
            Material metal = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Blade_Metal.mat");
            Material wood = AssetDatabase.LoadAssetAtPath<Material>("Assets/Emberdelve/Art/Materials/Bow_Wood.mat");
            Transform gallery = Group(root, "Western_Gallery", Vector3.zero);
            // Two upper galleries leave the west doorway axis open to the full room height.
            // Solid wings keep each stair route unambiguous for the existing single-surface navigation.
            foreach (int side in new[] { -1, 1 })
            {
                // One solid support has no buried horizontal collider face for the navigation baker to select.
                Box(gallery, "Gallery_Deck", new Vector3(-14.5f, GalleryHeight * .5f, side * 5.125f),
                    new Vector3(5, GalleryHeight, 5.75f), paving);
                Box(gallery, "Gallery_EndParapet", new Vector3(-14.5f, GalleryHeight + .55f, side * 2.35f),
                    new Vector3(5, 1.1f, .25f), stone);
            }
            // Keep two-metre navigation cell centres away from overlapping tread edges.
            Stairs(gallery, "Gallery_SouthStairs", new Vector3(-14.5f, 0, -15.75f), 0, GalleryHeight, 4.5f, stone);
            Stairs(gallery, "Gallery_NorthStairs", new Vector3(-14.5f, 0, 15.75f), 180, GalleryHeight, 4.5f, stone);
            foreach (int side in new[] { -1, 1 })
            {
                Box(gallery, "Gallery_Parapet", new Vector3(-12.1f, GalleryHeight + .55f, side * 5.125f), new Vector3(.28f, 1.1f, 5.75f), stone);
                Box(gallery, "Gallery_OuterParapet", new Vector3(-16.9f, GalleryHeight + .55f, side * 5.125f), new Vector3(.28f, 1.1f, 5.75f), stone);
                foreach (float z in new[] { 3.5f, 7.5f })
                    CryptRoomArchitecture.Column(gallery, new Vector3(-14.5f, 0, side * z), GalleryHeight - .3f, stone);
                Arch(root, new Vector3(-14.5f, GalleryHeight + 2.3f, side * 6), 2.35f, 1.35f, stone);
                foreach (float x in new[] { -16.95f, -12.05f })
                    CryptRoomArchitecture.Column(root, new Vector3(x, GalleryHeight, side * 6), 2.3f, stone);
            }
            // A low arch and side niches emphasize the descent, with clear headroom above the lower route.
            Arch(root, new Vector3(14.5f, 1.65f, -10), 2.05f, 1.5f, stone);
            foreach (float x in new[] { 12.25f, 16.75f })
                CryptRoomArchitecture.Column(root, new Vector3(x, -1.5f, -10), 3.15f, stone);
            CryptRoomArchitecture.Brazier(root, new Vector3(16.1f, -1.5f, -10), stone, metal, destination == 2);

            var furnishings = Group(root, "Authored_Room_Furnishings", Vector3.zero);
            if (kind == DungeonModuleKind.Ossuary || kind == DungeonModuleKind.Vault)
            {
                // Reuse the existing stone sarcophagus asset recipe and materials.
                foreach (float z in new[] { -5f, 5f })
                    CryptRoomArchitecture.Tomb(furnishings, new Vector3(-14.5f, GalleryHeight, z), 0, stone, paving);
                CryptRoomArchitecture.Tomb(furnishings, new Vector3(6, 0, 14.8f), 90, stone, paving);
            }
            else
            {
                DungeonRoomProps.Model(furnishings, "WoodenTable_01", new Vector3(-14.5f, GalleryHeight, variant % 2 == 0 ? 4 : -4), .85f, 90);
                DungeonRoomProps.Model(furnishings, "wooden_bucket_01", new Vector3(-15.5f, GalleryHeight, variant % 2 == 0 ? 6 : -6), .5f, 10);
            }
            // Different room uses: refectory, burial gallery, supply hall, shrine approach.
            if (kind == DungeonModuleKind.Hall || kind == DungeonModuleKind.Entry)
            {
                DungeonRoomProps.Model(furnishings, "WoodenTable_01", new Vector3(-6, 0, 14.5f), .9f, variant == 2 ? 25 : 90);
                DungeonRoomProps.Model(furnishings, "wine_barrel_01", new Vector3(-9, 0, 15), 1.1f, 12);
                DungeonRoomProps.Model(furnishings, "wooden_crate_02", new Vector3(-8, 0, 13.4f), .8f, -12);
            }
            else if (kind == DungeonModuleKind.Shrine || kind == DungeonModuleKind.Extraction)
            {
                Arch(root, new Vector3(7, 3, 14.5f), 3.2f, 2.1f, stone);
                foreach (float x in new[] { 3.6f, 10.4f })
                    CryptRoomArchitecture.Column(root, new Vector3(x, 0, 14.5f), 3, stone);
                CryptRoomArchitecture.Brazier(root, new Vector3(7, 0, 16.3f), stone, metal, destination == 2);
            }
            else
            {
                DungeonRoomProps.Model(furnishings, "wooden_crate_02", new Vector3(-6, 0, -15), 1.1f, 10);
                DungeonRoomProps.Model(furnishings, "wine_barrel_01", new Vector3(-8, 0, -14), 1.2f, -10);
            }
            // Break up the perimeter into supported bays instead of one uninterrupted rectangular wall.
            foreach (int side in new[] { -1, 1 })
                foreach (float x in new[] { -9f, -5f, 5f, 9f })
                    CryptRoomArchitecture.Column(root, new Vector3(x, 0, side * 16.7f), destination == 1 ? 3.8f : 7.9f, stone);

            if (root.Find("Hidden_Reliquary") == null && (kind == DungeonModuleKind.Ossuary || kind == DungeonModuleKind.Vault || kind == DungeonModuleKind.Hall && variant == 1))
                SecretChamber(root, stone, paving, metal, wood);
        }

        internal static void SecretChamber(Transform root, Material stone, Material paving, Material metal, Material wood)
        {
            Transform chamber = Group(root, "Hidden_Reliquary", Vector3.zero);
            Box(chamber, "Cache_WestWall", new Vector3(11.9f, 2.15f, 14.5f), new Vector3(.5f, 4.3f, 6.5f), stone);
            Box(chamber, "Cache_FrontLeft", new Vector3(12.7f, 2.15f, 11.3f), new Vector3(1.1f, 4.3f, .5f), stone);
            Box(chamber, "Cache_FrontRight", new Vector3(17, 2.15f, 11.3f), new Vector3(1.5f, 4.3f, .5f), stone);
            Box(chamber, "Cache_Lintel", new Vector3(14.75f, 3.8f, 11.3f), new Vector3(3, 1, .5f), stone);
            Box(chamber, "Cache_Roof", new Vector3(14.9f, 4.45f, 14.5f), new Vector3(6.2f, .3f, 6.5f), paving);
            CryptRoomArchitecture.Column(chamber, new Vector3(11, 0, 9.35f), 1.8f, stone);
            Transform lever = Group(chamber, "Secret_Lever", new Vector3(11, 1.25f, 9));
            Box(lever, "Lever_Backplate", Vector3.zero, new Vector3(.4f, .5f, .15f), metal);
            var handle = Box(lever, "Lever_Handle", new Vector3(0, .2f, -.16f), new Vector3(.12f, .65f, .12f), wood);
            handle.transform.localRotation = Quaternion.Euler(-35, 0, 0);
            var probeObject = new GameObject("LeverInteraction");
            probeObject.transform.SetParent(lever, false);
            probeObject.layer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
            var probe = probeObject.AddComponent<BoxCollider>(); probe.isTrigger = true;
            probe.center = new Vector3(0, .1f, -.12f); probe.size = new Vector3(.65f, 1, .7f);
            // Both moving masonry and lever are under the same stable network identity.
            var panel = Box(lever, "Concealed_Stone_Panel", new Vector3(3.75f, .4f, 2.3f), new Vector3(3, 3.3f, .5f), stone);
            var door = lever.gameObject.AddComponent<DungeonDoorInteractable>();
            door.ConfigureSecret("cache", panel.transform, panel.GetComponent<Collider>(), probe, Vector3.up * 3.4f);
            var volumeObject = new GameObject("SecretRoomVolume");
            volumeObject.transform.SetParent(lever, false); volumeObject.layer = 2;
            var volume = volumeObject.AddComponent<BoxCollider>(); volume.isTrigger = true;
            volume.center = new Vector3(3.9f, 0, 5.5f); volume.size = new Vector3(5.4f, 3, 5.8f);
            door.ConfigureSecretRoom(volume);
            Group(lever, "SecretChestAnchor", new Vector3(4, -1.25f, 6)); // room-local (15,0,15), exact grid centre
            DungeonRoomProps.Model(chamber, "wooden_crate_02", new Vector3(13, 0, 16.5f), .65f, 8);
            var lamp = Group(chamber, "Cache_Lamp", new Vector3(14, 2.8f, 15.5f)).gameObject.AddComponent<Light>();
            lamp.type = LightType.Point; lamp.color = new Color(1, .65f, .32f); lamp.intensity = 4; lamp.range = 6;
        }

        private static void Stairs(Transform root, string name, Vector3 basePosition, float yaw, float height, float width, Material material)
        {
            Transform stairs = Group(root, name, basePosition); stairs.localRotation = Quaternion.Euler(0, yaw, 0);
            int steps = Mathf.RoundToInt(height / .15f);
            for (int i = 0; i < steps; i++)
            {
                float rise = (i + 1) * .15f;
                Box(stairs, "Tread_" + i, new Vector3(0, rise * .5f, (i + .5f) * .3f), new Vector3(width, rise, CollisionTreadDepth), material);
            }
        }

        private static void Arch(Transform parent, Vector3 position, float radius, float rise, Material material)
        {
            var mesh = AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Emberdelve/Art/Generated/CryptArch.asset");
            if (mesh == null) throw new System.InvalidOperationException("Existing CryptArch asset is required");
            Transform arch = Group(parent, "Reused_Stone_Arch", position); arch.localScale = new Vector3(radius, rise, .65f);
            arch.gameObject.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            arch.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            arch.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            arch.gameObject.AddComponent<MeshCollider>().sharedMesh = mesh;
        }

        private static Transform Group(Transform parent, string name, Vector3 position)
        {
            var t = new GameObject(name).transform; t.SetParent(parent, false); t.localPosition = position; return t;
        }

        private static GameObject Box(Transform parent, string name, Vector3 position, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube); go.name = name;
            go.layer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.transform.SetParent(parent, false); go.transform.localPosition = position; go.transform.localScale = size;
            go.GetComponent<Renderer>().sharedMaterial = material; return go;
        }
    }
}
