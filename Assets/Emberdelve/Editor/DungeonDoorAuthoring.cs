using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Interaction;
using Emberdelve.Level;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Emberdelve.EditorTools
{
    internal static class DungeonDoorAuthoring
    {
        private const string Source = "Assets/Emberdelve/Art/Models/AI/Meshy/MapDressing/RustboundGate/";
        private const string PrefabPath = "Assets/Emberdelve/Resources/" + DungeonDoorwaySpawner.PrefabResource + ".prefab";
        private const float Width = 1.72f;
        private const float Height = 2.32f;
        private const float SpringLine = 1.60f;
        private const float Thickness = .21f;
        // The downloaded grille is a solid mesh with finished faces on both sides.
        // A rear-mounted opaque slab used to cover its +Z ironwork completely.
        // Keep the timber inside that grille, leaving both authored metal faces visible.
        private const float WoodCoreThickness = .020f;
        private const float WoodRimHalfDepth = .100f;
        private const float WoodRimWidth = .065f;
        // A module seam falls between two columns of navigation cells. Center the passage on one lane.
        private const float LaneOffset = .77f;
        private const float SocketHalf = 2.02f;
        private const float FrameTop = 3.86f;
        private const float FrameDepth = .65f;
        // Positive local yaw swings toward -Z. Mount the complete leaf on that face,
        // with its rear surface 5mm clear of the masonry throughout the 0..96 degree swing.
        private const float HingeFaceZ = -(FrameDepth + Thickness) * .5f - .005f;

        [MenuItem("Emberdelve/Visual/Build Dungeon Door Prefab")]
        public static void Build()
        {
            var model = Load<GameObject>(Source + "RustboundGate.fbx");
            var frame = Load<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
            var doorMaterial = DoorMaterial();
            var root = new GameObject("DungeonDoor");
            try
            {
                int levelLayer = LayerMask.NameToLayer(GameConfig.LevelLayerName);
                int interactLayer = LayerMask.NameToLayer(GameConfig.InteractLayerName);
                if (levelLayer < 0 || interactLayer < 0) throw new InvalidOperationException("Door layers are missing.");
                float left = LaneOffset - Width * .5f;
                float right = LaneOffset + Width * .5f;
                // Frame and leaf are siblings. Navigation treats only the leaf as a toggleable obstacle.
                var masonry = Child(root.transform, "StoneFrame", Vector3.zero);
                AddMasonry(masonry, "Infilled_Wall", -SocketHalf, left, 0f, FrameTop, frame, levelLayer);
                AddMasonry(masonry, "Jamb", right, SocketHalf, 0f, FrameTop, frame, levelLayer);
                var lintel = AddArchedPiece(masonry, "StoneArch", new Vector3(LaneOffset, 0, 0),
                    ArchMesh("StoneArch", Width, SpringLine, Height, FrameTop, FrameDepth, cutout: true), frame);
                lintel.gameObject.layer = levelLayer;
                lintel.gameObject.AddComponent<MeshCollider>().sharedMesh = lintel.GetComponent<MeshFilter>().sharedMesh;

                var doorRoot = Child(root.transform, "Door", new Vector3(LaneOffset, 0, 0));
                var hinge = Child(doorRoot, "Hinge", new Vector3(-Width * .5f, 0, HingeFaceZ));
                var panel = Child(hinge, "LeafCollider", new Vector3(Width * .5f, Height * .5f, 0));
                panel.gameObject.layer = levelLayer;
                var blocker = panel.gameObject.AddComponent<BoxCollider>();
                blocker.size = new Vector3(Width, Height, Thickness);

                var backing = AddArchedPiece(hinge, "DarkWoodBacking", new Vector3(Width * .5f, 0, 0),
                    FittedBackingMesh(),
                    Load<Material>("Assets/Emberdelve/Art/Materials/Wood_Planks.mat"));
                PlaceWoodCore(backing);

                var visual = Child(hinge, "RustboundLeaf", Vector3.zero);
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, visual);
                var renderers = instance.GetComponentsInChildren<MeshRenderer>();
                var bounds = ModelBounds(renderers);
                visual.localScale = new Vector3((Width - .015f) / bounds.size.x, (Height - .025f) / bounds.size.y, Thickness / bounds.size.z);
                bounds = ModelBounds(renderers);
                Vector3 baseCenter = new Vector3(bounds.center.x, bounds.min.y, bounds.center.z);
                visual.localPosition = new Vector3(Width * .5f, .012f, 0f) - hinge.InverseTransformPoint(baseCenter);
                foreach (var renderer in renderers)
                {
                    var slots = renderer.sharedMaterials;
                    Array.Fill(slots, doorMaterial);
                    renderer.sharedMaterials = slots;
                }

                var probe = Child(doorRoot, "InteractionVolume", Vector3.up * Height * .5f);
                probe.gameObject.layer = interactLayer;
                var trigger = probe.gameObject.AddComponent<BoxCollider>();
                trigger.isTrigger = true;
                trigger.size = new Vector3(Width, Height, 1.6f);
                var door = doorRoot.gameObject.AddComponent<DungeonDoorInteractable>();
                door.Configure("door_unbound", hinge, blocker, 96f);
                DownloadedCryptArchitecture.Apply(root);
                RepairVisuals(root);
                if (PrefabUtility.SaveAsPrefabAsset(root, PrefabPath) == null)
                    throw new InvalidOperationException("Could not save " + PrefabPath);
                AssetDatabase.SaveAssets();
                Debug.Log($"[DungeonDoorAuthoring] Saved {Width:F2} x {Height:F2} m doorway with {renderers.Length} Rustbound mesh renderers.");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [MenuItem("Emberdelve/Visual/Repair Saved Dungeon Door Visuals")]
        public static void RepairSavedVisuals()
        {
            const string primaryAssets = "C:/Users/yrapa/OneDrive/Desktop/Unity_DakaDaka/DakaDaka_clean_handoff/Assets";
            if (!string.Equals(Application.dataPath.Replace('\\', '/').TrimEnd('/'), primaryAssets,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Repair the saved door only in the primary DakaDaka_clean_handoff checkout.");
            // Isolated prefab contents only: never touch the user's open scene or Play state.
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try
            {
                RepairVisuals(root);
                if (PrefabUtility.SaveAsPrefabAsset(root, PrefabPath) == null)
                    throw new InvalidOperationException("Could not save " + PrefabPath);
                Debug.Log("[DungeonDoorAuthoring] Restored fitted masonry and face-mounted hinge at z=" + HingeFaceZ
                    + "; leaf and blocker move together; collider dimensions and interaction wiring preserved.");
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        internal static void RepairVisuals(GameObject root)
        {
            var hinge = root.transform.Find("Door/Hinge");
            var door = root.GetComponentInChildren<DungeonDoorInteractable>(true);
            if (hinge == null || door == null || door.BlockerCollider == null
                || !door.BlockerCollider.transform.IsChildOf(hinge))
                throw new InvalidOperationException("Door leaf and blocker must share the authored hinge.");
            var wiring = new SerializedObject(door);
            float yaw = wiring.FindProperty("openYaw").floatValue;
            if (wiring.FindProperty("hinge").objectReferenceValue != hinge || yaw <= 0f || yaw > 180f)
                throw new InvalidOperationException("Face mounting requires the authored positive-yaw door swing.");
            Vector3 hingePosition = hinge.localPosition;
            if (Mathf.Abs(hingePosition.x + Width * .5f) > .0001f || Mathf.Abs(hingePosition.y) > .0001f
                || (Mathf.Abs(hingePosition.z) > .0001f && Mathf.Abs(hingePosition.z - HingeFaceZ) > .0001f))
                throw new InvalidOperationException("Unexpected door hinge placement; refuse to overwrite it.");
            // Keep the closed blocker and visible leaf coincident. The interaction volume,
            // door root, network identity and all collider dimensions stay unchanged.
            hingePosition.z = HingeFaceZ;
            hinge.localPosition = hingePosition;
            var backing = hinge.Find("DarkWoodBacking");
            if (backing == null || !backing.TryGetComponent<MeshFilter>(out var backingFilter)
                || backing.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("The authored wood backing must be visual-only.");
            backingFilter.sharedMesh = FittedBackingMesh();
            PlaceWoodCore(backing);

            var arch = root.transform.Find("StoneFrame/StoneArch");
            if (arch == null || !arch.TryGetComponent<MeshFilter>(out var filter)
                || !arch.TryGetComponent<MeshRenderer>(out var renderer)
                || !arch.TryGetComponent<MeshCollider>(out var collider)
                || filter.sharedMesh == null || filter.sharedMesh != collider.sharedMesh)
                throw new InvalidOperationException("Door arch must retain its authored, matching render and collision mesh.");

            // This mesh is the solid infill ABOVE a precisely arched aperture, not an arch
            // trim. Fitting a downloaded crown to its rectangular bounds stretches the
            // crown from the spring line to the ceiling and leaves gaps beside the leaf.
            // Restore the actual opening surface so its jambs/reveal join the wall exactly.
            var replacement = arch.Find("Meshy_Architecture_Visual");
            if (replacement != null)
            {
                if (replacement.GetComponentsInChildren<Collider>(true).Length != 0)
                    throw new InvalidOperationException("Unexpected collision in the door's visual-only arch replacement.");
                Object.DestroyImmediate(replacement.gameObject);
            }
            renderer.sharedMaterial = Load<Material>("Assets/Emberdelve/Art/Generated/DungeonSurfaces/World_Stone_Wall.mat");
            renderer.enabled = true;

            // Saved doors still carry the old column-fragment wall tiles. Their closed
            // authored cubes remain underneath; restore those without rebuilding physics.
            foreach (string name in new[] { "Infilled_Wall", "Jamb" })
            {
                var wall = root.transform.Find("StoneFrame/" + name);
                if (wall == null || !wall.TryGetComponent<MeshFilter>(out var wallFilter)
                    || !wall.TryGetComponent<MeshRenderer>(out var wallRenderer)
                    || !wall.TryGetComponent<BoxCollider>(out var wallCollider)
                    || wallFilter.sharedMesh == null
                    || (wallFilter.sharedMesh.bounds.center - wallCollider.center).sqrMagnitude > .000001f
                    || (wallFilter.sharedMesh.bounds.size - wallCollider.size).sqrMagnitude > .000001f)
                    throw new InvalidOperationException("Door masonry must retain its closed authored collider-matched mesh: " + name);
                var tiles = wall.Find("Meshy_Architecture_Visual");
                if (tiles != null)
                {
                    if (tiles.GetComponentsInChildren<Collider>(true).Length != 0)
                        throw new InvalidOperationException("Unexpected collision in the door's visual-only wall replacement.");
                    Object.DestroyImmediate(tiles.gameObject);
                }
                wallRenderer.sharedMaterial = renderer.sharedMaterial;
                wallRenderer.enabled = true;
            }
            ValidateTwoSidedLeaf(root);
        }

        private static void PlaceWoodCore(Transform backing)
        {
            // Same fitted contour and UVs, entirely inside the existing 210mm leaf.
            // This is real geometric separation, not double-sided shader/depth bias.
            backing.localPosition = new Vector3(Width * .5f, 0, 0);
            backing.localRotation = Quaternion.identity;
            backing.localScale = Vector3.one;
        }

        [MenuItem("Emberdelve/Visual/Validate Saved Dungeon Door Faces")]
        public static void ValidateSavedVisuals()
        {
            var root = PrefabUtility.LoadPrefabContents(PrefabPath);
            try { ValidateTwoSidedLeaf(root); }
            finally { PrefabUtility.UnloadPrefabContents(root); }
        }

        private static void ValidateTwoSidedLeaf(GameObject root)
        {
            var hinge = root.transform.Find("Door/Hinge");
            var backing = hinge != null ? hinge.Find("DarkWoodBacking") : null;
            var visual = hinge != null ? hinge.Find("RustboundLeaf") : null;
            if (backing == null || visual == null || backing.GetComponentsInChildren<Collider>(true).Length != 0)
                throw new InvalidOperationException("Door needs its original grille and a visual-only timber core.");
            var core = backing.GetComponent<MeshFilter>();
            var coreBounds = core.sharedMesh.bounds;
            float coreNear = backing.localPosition.z + coreBounds.min.z * backing.localScale.z;
            float coreFar = backing.localPosition.z + coreBounds.max.z * backing.localScale.z;
            if (Mathf.Abs(coreNear + WoodRimHalfDepth) > .0001f
                || Mathf.Abs(coreFar - WoodRimHalfDepth) > .0001f
                || (backing.localScale - Vector3.one).sqrMagnitude > .000001f)
                throw new InvalidOperationException("The solid timber rim must fit inside the unchanged 210mm leaf.");
            var innerContour = BackingContour(.005f + WoodRimWidth);
            ValidateClosedCore(core.sharedMesh);
            coreNear = -WoodCoreThickness * .5f;
            coreFar = WoodCoreThickness * .5f;
            bool InsideCore(Vector3 point)
            {
                var p = new Vector2(point.x - Width * .5f, point.y);
                for (int i = 0; i < innerContour.Count; i++)
                {
                    Vector2 edge = innerContour[(i + 1) % innerContour.Count] - innerContour[i];
                    Vector2 delta = p - innerContour[i];
                    if (edge.x * delta.y - edge.y * delta.x < 0) return false;
                }
                return true;
            }

            float frontArea = 0, rearArea = 0;
            foreach (var filter in visual.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter.TryGetComponent<MeshRenderer>(out var renderer) || !renderer.enabled) continue;
                var toHinge = hinge.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                // Editor snapshot also supports imported meshes with Read/Write disabled;
                // validation must not change the original FBX importer just to inspect it.
                using var snapshot = MeshUtility.AcquireReadOnlyMeshData(filter.sharedMesh);
                var data = snapshot[0];
                using var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                data.GetVertices(vertices);
                for (int submesh = 0; submesh < data.subMeshCount; submesh++)
                {
                    var descriptor = data.GetSubMesh(submesh);
                    if (descriptor.topology != MeshTopology.Triangles) continue;
                    using var triangles = new NativeArray<int>(descriptor.indexCount, Allocator.Temp);
                    data.GetIndices(triangles, submesh);
                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        Vector3 a = toHinge.MultiplyPoint3x4(vertices[triangles[i]]);
                        Vector3 b = toHinge.MultiplyPoint3x4(vertices[triangles[i + 1]]);
                        Vector3 c = toHinge.MultiplyPoint3x4(vertices[triangles[i + 2]]);
                        // Count only faces over the thin central panel, clear of the
                        // bevelled perimeter rebate as well as clear in depth.
                        if (!InsideCore(a) || !InsideCore(b) || !InsideCore(c)) continue;
                        // Area projected onto the door plane, not a count of tiny edge faces.
                        float orientedArea = Vector3.Cross(b - a, c - a).z * .5f;
                        const float separation = .003f;
                        if (Mathf.Max(a.z, Mathf.Max(b.z, c.z)) < coreNear - separation && orientedArea < 0)
                            frontArea -= orientedArea;
                        if (Mathf.Min(a.z, Mathf.Min(b.z, c.z)) > coreFar + separation && orientedArea > 0)
                            rearArea += orientedArea;
                    }
                }
            }
            if (frontArea < .15f || rearArea < .15f)
                throw new InvalidOperationException($"Both door faces require exposed imported ironwork: front={frontArea:F3}, rear={rearArea:F3} m2.");
            Debug.Log($"[DungeonDoorAuthoring] Both original Meshy faces exposed: front={frontArea:F3}, rear={rearArea:F3} m2; centred timber={WoodCoreThickness:F3} m; unchanged leaf={Thickness:F3} m.");
        }

        public static void BuildWithPreview()
        {
            Build();
            var scene = EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Lobby.unity");
            foreach (var root in scene.GetRootGameObjects()) root.SetActive(false);
            foreach (bool open in new[] { false, true })
                EmberdelveViewmodelPreview.Capture("Logs/small-doors/" + (open ? "open" : "closed") + ".png",
                    1120, 730, 51, true, (root, camera) =>
                    {
                        var instance = Object.Instantiate(Load<GameObject>(PrefabPath), root);
                        var door = instance.GetComponentInChildren<DungeonDoorInteractable>();
                        door.ApplyReplicatedState(door.DoorId, open, immediate: true);
                        camera.transform.position = EmberdelveViewmodelPreview.Origin + new Vector3(2.7f, 1.73f, -4.4f);
                        camera.transform.LookAt(EmberdelveViewmodelPreview.Origin + new Vector3(.1f, 1.71f, 0));
                    });
        }

        private static void AddMasonry(Transform parent, string name, float left, float right, float bottom, float top,
            Material material, int layer)
        {
            var piece = GameObject.CreatePrimitive(PrimitiveType.Cube);
            piece.name = name;
            piece.layer = layer;
            piece.transform.SetParent(parent, false);
            piece.transform.localPosition = new Vector3((left + right) * .5f, (bottom + top) * .5f, 0);
            piece.transform.localScale = new Vector3(right - left, top - bottom, FrameDepth);
            piece.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Transform AddArchedPiece(Transform parent, string name, Vector3 position, Mesh mesh, Material material)
        {
            var piece = Child(parent, name, position);
            piece.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            piece.gameObject.AddComponent<MeshRenderer>().sharedMaterial = material;
            return piece;
        }

        private static Mesh ArchMesh(string name, float width, float spring, float peak, float straightEdge, float depth, bool cutout)
        {
            const int segments = 20;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            var uv = new List<Vector2>();
            float halfDepth = depth * .5f;
            for (int i = 0; i < segments; i++)
            {
                float u0 = 2f * i / segments - 1f;
                float u1 = 2f * (i + 1) / segments - 1f;
                float x0 = u0 * width * .5f;
                float x1 = u1 * width * .5f;
                float y0 = Mathf.Lerp(spring, peak, Mathf.Sqrt(1f - u0 * u0));
                float y1 = Mathf.Lerp(spring, peak, Mathf.Sqrt(1f - u1 * u1));
                Vector3 a = new Vector3(x0, cutout ? y0 : straightEdge, -halfDepth);
                Vector3 b = new Vector3(x1, cutout ? y1 : straightEdge, -halfDepth);
                Vector3 c = new Vector3(x1, cutout ? straightEdge : y1, -halfDepth);
                Vector3 d = new Vector3(x0, cutout ? straightEdge : y0, -halfDepth);
                Vector3 offset = Vector3.forward * depth;
                AddQuad(a, d, c, b, vertices, triangles, uv);
                AddQuad(a + offset, b + offset, c + offset, d + offset, vertices, triangles, uv);
                AddQuad(d, d + offset, c + offset, c, vertices, triangles, uv);
                AddQuad(a, b, b + offset, a + offset, vertices, triangles, uv);
                if (i == 0) AddQuad(a, a + offset, d + offset, d, vertices, triangles, uv);
                if (i == segments - 1) AddQuad(b, c, c + offset, b + offset, vertices, triangles, uv);
            }
            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetTriangles(triangles, 0);
            mesh.SetUVs(0, uv);
            mesh.RecalculateNormals();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            string path = Source + name + ".asset";
            var existing = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(mesh, path);
                return mesh;
            }
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing);
            Object.DestroyImmediate(mesh);
            return existing;
        }

        private static List<Vector2> BackingContour(float inset)
        {
            const int segments = 20;
            // Match the actual twenty-segment aperture with a true edge offset.
            var aperture = new List<Vector2> { new Vector2(-Width * .5f, 0), new Vector2(Width * .5f, 0) };
            for (int i = 0; i <= segments; i++)
            {
                float u = 1f - 2f * i / segments;
                aperture.Add(new Vector2(u * Width * .5f,
                    Mathf.Lerp(SpringLine, Height, Mathf.Sqrt(Mathf.Max(0, 1f - u * u)))));
            }
            var contour = new List<Vector2>();
            for (int i = 0; i < aperture.Count; i++)
            {
                Vector2 previous = (aperture[i] - aperture[(i + aperture.Count - 1) % aperture.Count]).normalized;
                Vector2 next = (aperture[(i + 1) % aperture.Count] - aperture[i]).normalized;
                Vector2 a = new Vector2(-previous.y, previous.x), b = new Vector2(-next.y, next.x);
                contour.Add(aperture[i] + (a + b) * (inset / (1f + Vector2.Dot(a, b))));
            }
            return contour;
        }

        private static Mesh FittedBackingMesh()
        {
            const string folder = "Assets/Emberdelve/Art/Generated/ArchitecturalJoins";
            const string path = folder + "/DoorCore_Rebated65mm_v1.asset";
            var saved = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (saved != null)
            {
                RepairOuterRimUvs(saved);
                return saved;
            }
            var outer = BackingContour(.005f);
            var inner = BackingContour(.005f + WoodRimWidth);
            var vertices = new List<Vector3>(); var triangles = new List<int>(); var uv = new List<Vector2>();
            void Face(Vector3 a, Vector3 b, Vector3 c)
            {
                int first = vertices.Count;
                foreach (var p in new[] { a, b, c })
                {
                    vertices.Add(p); uv.Add(new Vector2(p.x + .5f, p.y * .45f));
                }
                triangles.Add(first); triangles.Add(first + 1); triangles.Add(first + 2);
            }
            for (int i = 0; i < outer.Count; i++)
            {
                int j = (i + 1) % outer.Count;
                Vector3 frontA = new Vector3(outer[i].x, outer[i].y, -WoodRimHalfDepth);
                Vector3 frontB = new Vector3(outer[j].x, outer[j].y, -WoodRimHalfDepth);
                Vector3 backA = new Vector3(outer[i].x, outer[i].y, WoodRimHalfDepth);
                Vector3 backB = new Vector3(outer[j].x, outer[j].y, WoodRimHalfDepth);
                Vector3 innerFrontA = new Vector3(inner[i].x, inner[i].y, -WoodCoreThickness * .5f);
                Vector3 innerFrontB = new Vector3(inner[j].x, inner[j].y, -WoodCoreThickness * .5f);
                Vector3 innerBackA = new Vector3(inner[i].x, inner[i].y, WoodCoreThickness * .5f);
                Vector3 innerBackB = new Vector3(inner[j].x, inner[j].y, WoodCoreThickness * .5f);
                // One closed solid: outside edge, two bevelled rebates, two inset faces.
                // No old full slab or independent overlapping ring remains underneath.
                AddQuad(frontA, frontB, backB, backA, vertices, triangles, uv);
                AddQuad(frontA, innerFrontA, innerFrontB, frontB, vertices, triangles, uv);
                AddQuad(backA, backB, innerBackB, innerBackA, vertices, triangles, uv);
                Face(new Vector3(0, 1, -WoodCoreThickness * .5f), innerFrontB, innerFrontA);
                Face(new Vector3(0, 1, WoodCoreThickness * .5f), innerBackA, innerBackB);
            }
            var mesh = new Mesh { name = "Door timber core - closed 65mm perimeter rebate" };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.SetUVs(0, uv);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            ValidateClosedCore(mesh);
            RepairOuterRimUvs(mesh);
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder("Assets/Emberdelve/Art/Generated", "ArchitecturalJoins");
            AssetDatabase.CreateAsset(mesh, path);
            return mesh;
        }

        private static void RepairOuterRimUvs(Mesh mesh)
        {
            // XY projection collapses the side-wall UVs across Z, stretching one
            // texture column into bands. Unwrap only the outer wall by depth and
            // contour distance. Position/index data and the asset GUID stay intact.
            var contour = BackingContour(.005f);
            var points = mesh.vertices;
            var uv = mesh.uv;
            const int verticesPerSegment = 18;
            if (points.Length != contour.Count * verticesPerSegment || uv.Length != points.Length)
                throw new InvalidOperationException("Unexpected door rebate topology; refuse to replace its UVs.");
            float distance = 0;
            bool changed = false;
            for (int i = 0; i < contour.Count; i++)
            {
                float nextDistance = distance + Vector2.Distance(contour[i], contour[(i + 1) % contour.Count]);
                int first = i * verticesPerSegment;
                for (int j = 0; j < 4; j++)
                {
                    Vector3 point = points[first + j];
                    float expectedDepth = j < 2 ? -WoodRimHalfDepth : WoodRimHalfDepth;
                    if (Mathf.Abs(point.z - expectedDepth) > .00001f)
                        throw new InvalidOperationException("Unexpected door rim face layout.");
                    var value = new Vector2(point.z + .5f, (j == 1 || j == 2 ? nextDistance : distance) * .45f);
                    if ((uv[first + j] - value).sqrMagnitude <= 1e-12f) continue;
                    uv[first + j] = value;
                    changed = true;
                }
                distance = nextDistance;
            }
            if (!changed) return;
            mesh.uv = uv;
            mesh.RecalculateTangents();
            EditorUtility.SetDirty(mesh);
            if (AssetDatabase.Contains(mesh)) AssetDatabase.SaveAssetIfDirty(mesh);
        }

        private static void ValidateClosedCore(Mesh mesh)
        {
            var points = mesh.vertices;
            var indices = mesh.triangles;
            var welded = new Dictionary<Vector3, int>();
            var edges = new Dictionary<(int, int), int>();
            int Vertex(Vector3 point)
            {
                if (!welded.TryGetValue(point, out int id)) welded.Add(point, id = welded.Count);
                return id;
            }
            void Edge(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                edges.TryGetValue(key, out int count); edges[key] = count + 1;
            }
            for (int i = 0; i < indices.Length; i += 3)
            {
                Vector3 a = points[indices[i]], b = points[indices[i + 1]], c = points[indices[i + 2]];
                if (Vector3.Cross(b - a, c - a).sqrMagnitude < 1e-12f)
                    throw new InvalidOperationException("Door core contains a degenerate face.");
                int ia = Vertex(a), ib = Vertex(b), ic = Vertex(c);
                Edge(ia, ib); Edge(ib, ic); Edge(ic, ia);
            }
            foreach (var edge in edges)
                if (edge.Value != 2) throw new InvalidOperationException("Door core must be a single closed surface without duplicate internal faces.");
        }

        private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
            List<Vector3> vertices, List<int> triangles, List<Vector2> uv)
        {
            int first = vertices.Count;
            foreach (var point in new[] { a, b, c, d })
            {
                vertices.Add(point);
                uv.Add(new Vector2(point.x + .5f, point.y * .45f));
            }
            triangles.Add(first);
            triangles.Add(first + 1);
            triangles.Add(first + 2);
            triangles.Add(first);
            triangles.Add(first + 2);
            triangles.Add(first + 3);
        }

        private static Material DoorMaterial()
        {
            string path = Source + "RustboundGate.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Load<Material>("Assets/Emberdelve/Resources/Emberdelve/DoorPanel.mat"));
                AssetDatabase.CreateAsset(material, path);
            }
            material.SetTexture("_BaseMap", Load<Texture2D>(Source + "RustboundGate_Albedo.png"));
            material.SetColor("_BaseColor", new Color(.83f, .80f, .75f));
            material.SetFloat("_Smoothness", .22f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Bounds ModelBounds(MeshRenderer[] renderers)
        {
            if (renderers.Length == 0) throw new InvalidOperationException("Rustbound Gate has no mesh renderers.");
            Bounds result = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) result.Encapsulate(renderers[i].bounds);
            if (result.size.x <= .01f || result.size.y <= .01f || result.size.z <= .001f)
                throw new InvalidOperationException("Rustbound Gate has invalid bounds.");
            return result;
        }

        private static Transform Child(Transform parent, string name, Vector3 position)
        {
            var child = new GameObject(name).transform;
            child.SetParent(parent, false);
            child.localPosition = position;
            return child;
        }

        private static T Load<T>(string path) where T : Object
        {
            var loaded = AssetDatabase.LoadAssetAtPath<T>(path);
            if (loaded == null) throw new InvalidOperationException("Missing dungeon door input " + path);
            return loaded;
        }
    }
}
