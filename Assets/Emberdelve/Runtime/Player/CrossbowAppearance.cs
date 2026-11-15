using System.Collections.Generic;
using Emberdelve.Data;
using UnityEngine;

namespace Emberdelve.Player
{
    /// <summary>Project-authored hard-surface dressing within the existing solid-part envelopes.
    /// Does not move the authoritative guard parts, grips, string or muzzle.</summary>
    public sealed class CrossbowAppearance : MonoBehaviour
    {
        private readonly List<Mesh> _meshes = new List<Mesh>();

        public static void Dress(Transform model, WeaponData weapon)
        {
            var appearance = model.gameObject.AddComponent<CrossbowAppearance>();
            for (int i = 0; i < CrossbowGuardPose.PartCount; i++)
            {
                var part = CrossbowGuardPose.Part(i);
                var child = model.Find(part.Name);
                if (child == null) continue;
                bool stock = part.Name == "Crossbow_Stock";
                bool limb = part.Name == "Crossbow_Limb";
                var mesh = Sculpt(part.Name, stock, limb);
                appearance._meshes.Add(mesh);
                child.GetComponent<MeshFilter>().sharedMesh = mesh;
                // A forged steel prod reads separately from the wooden tiller.
                if (limb) child.GetComponent<MeshRenderer>().sharedMaterial = weapon.edgeMaterial;
            }

            // Two narrow rails leave a visible bolt channel. Small fittings are cosmetic,
            // inside the stock/central mounting envelopes rather than new guard surfaces.
            Detail(model, "BoltRailLeft", new Vector3(-.020f, .017f, .15f),
                new Vector3(.006f, .005f, .31f), weapon.edgeMaterial);
            Detail(model, "BoltRailRight", new Vector3(.020f, .017f, .15f),
                new Vector3(.006f, .005f, .31f), weapon.edgeMaterial);
            Detail(model, "LatchPlate", new Vector3(0f, .017f, -.055f),
                new Vector3(.046f, .006f, .048f), weapon.gripMaterial);
            for (int side = -1; side <= 1; side += 2)
                for (int pin = 0; pin < 2; pin++)
                {
                    var rivet = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    rivet.name = "TillerPin";
                    rivet.transform.SetParent(model, false);
                    rivet.transform.localPosition = new Vector3(side * .034f, -.025f, .005f + pin * .19f);
                    rivet.transform.localRotation = Quaternion.Euler(0f, 0f, 90f);
                    rivet.transform.localScale = new Vector3(.010f, .001f, .010f);
                    Finish(rivet, model.gameObject.layer, weapon.edgeMaterial);
                }
        }

        private static void Detail(Transform model, string name, Vector3 position, Vector3 size, Material material)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(model, false);
            go.transform.localPosition = position;
            go.transform.localScale = size;
            Finish(go, model.gameObject.layer, material);
        }

        private static void Finish(GameObject go, int layer, Material material)
        {
            go.layer = layer;
            var collider = go.GetComponent<Collider>();
            if (collider != null)
            {
                collider.enabled = false;
                if (Application.isPlaying) Destroy(collider); else DestroyImmediate(collider);
            }
            go.GetComponent<Renderer>().sharedMaterial = material;
        }

        private static Mesh Sculpt(string name, bool stock, bool limb)
        {
            // Octagonal sections give broad planar faces and separate chamfer highlights.
            // Duplicate face vertices keep the chamfers crisp, avoiding inflated smooth normals.
            var section = new[] {
                new Vector2(-.34f,-.5f), new Vector2(.34f,-.5f),
                new Vector2(.5f,-.34f), new Vector2(.5f,.34f),
                new Vector2(.34f,.5f), new Vector2(-.34f,.5f),
                new Vector2(-.5f,.34f), new Vector2(-.5f,-.34f)
            };
            float[] stations = { -.5f, -.40f, .32f, .5f };
            float[] widths = stock ? new[] { .78f, 1f, 1f, .82f }
                : limb ? new[] { .76f, 1f, 1f, .76f } : new[] { .88f, 1f, 1f, .88f };
            float[] heights = stock ? new[] { .82f, 1f, .94f, .78f }
                : new[] { .86f, 1f, 1f, .86f };
            var vertices = new List<Vector3>();
            var uv = new List<Vector2>();
            var indices = new List<int>();
            Vector3 Point(int ring, int corner)
            {
                Vector2 p = section[corner];
                return limb
                    ? new Vector3(stations[ring], p.y * heights[ring], -p.x * widths[ring])
                    : new Vector3(p.x * widths[ring], p.y * heights[ring], stations[ring]);
            }
            void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float u, float v0, float v1)
            {
                int first = vertices.Count;
                vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
                uv.Add(new Vector2(u, v0)); uv.Add(new Vector2(u + .125f, v0));
                uv.Add(new Vector2(u + .125f, v1)); uv.Add(new Vector2(u, v1));
                indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                indices.Add(first); indices.Add(first + 2); indices.Add(first + 3);
            }
            for (int ring = 0; ring < stations.Length - 1; ring++)
                for (int side = 0; side < 8; side++)
                {
                    int next = (side + 1) % 8;
                    Quad(Point(ring, side), Point(ring, next), Point(ring + 1, next), Point(ring + 1, side),
                        side / 8f, stations[ring] + .5f, stations[ring + 1] + .5f);
                }
            for (int end = 0; end < 2; end++)
            {
                int ring = end == 0 ? 0 : stations.Length - 1;
                Vector3 center = limb ? new Vector3(stations[ring], 0f, 0f) : new Vector3(0f, 0f, stations[ring]);
                for (int side = 0; side < 8; side++)
                {
                    int first = vertices.Count;
                    vertices.Add(center);
                    vertices.Add(Point(ring, end == 0 ? (side + 1) % 8 : side));
                    vertices.Add(Point(ring, end == 0 ? side : (side + 1) % 8));
                    uv.Add(new Vector2(.5f, .5f));
                    uv.Add(section[end == 0 ? (side + 1) % 8 : side] + Vector2.one * .5f);
                    uv.Add(section[end == 0 ? side : (side + 1) % 8] + Vector2.one * .5f);
                    indices.Add(first); indices.Add(first + 1); indices.Add(first + 2);
                }
            }
            var mesh = new Mesh { name = name + "_Sculpted", hideFlags = HideFlags.DontSave };
            mesh.SetVertices(vertices); mesh.SetUVs(0, uv); mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals(); mesh.RecalculateBounds();
            return mesh;
        }

        private void OnDestroy()
        {
            foreach (Mesh mesh in _meshes)
                if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
        }
    }
}
