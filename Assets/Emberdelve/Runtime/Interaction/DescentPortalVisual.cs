using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.Interaction
{
    /// <summary>One-time shared presentation for authority and replicated descent slots.
    /// Meshes stay inside the original unit trigger; the caller owns the interaction identity.</summary>
    public sealed class DescentPortalVisual : MonoBehaviour
    {
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>(3);

        public static GameObject Create(Transform parent, string name, Material material, int layer)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.localScale = new Vector3(1.1f, 2.1f, .18f);
            if (layer >= 0) root.layer = layer;
            root.AddComponent<BoxCollider>().isTrigger = true;
            var visual = root.AddComponent<DescentPortalVisual>();
            visual.Build(material);
            return root;
        }

        private void Build(Material material)
        {
            var outer = new[] { new Vector2(-.5f,-.5f), new Vector2(.5f,-.5f), new Vector2(.5f,.29f),
                new Vector2(.3f,.43f), new Vector2(0,.5f), new Vector2(-.3f,.43f), new Vector2(-.5f,.29f) };
            var inner = new[] { new Vector2(-.38f,-.44f), new Vector2(.38f,-.44f), new Vector2(.38f,.26f),
                new Vector2(.23f,.36f), new Vector2(0,.415f), new Vector2(-.23f,.36f), new Vector2(-.38f,.26f) };
            var vertices = new List<Vector3>(); var triangles = new List<int>();
            for (int i = 0; i < outer.Length; i++)
            {
                int next = (i + 1) % outer.Length;
                Quad(vertices, triangles, At(outer[i], -.5f), At(outer[next], -.5f), At(inner[next], -.5f), At(inner[i], -.5f));
                Quad(vertices, triangles, At(outer[i], .5f), At(inner[i], .5f), At(inner[next], .5f), At(outer[next], .5f));
                Quad(vertices, triangles, At(outer[i], -.5f), At(outer[i], .5f), At(outer[next], .5f), At(outer[next], -.5f));
                Quad(vertices, triangles, At(inner[i], -.5f), At(inner[next], -.5f), At(inner[next], .5f), At(inner[i], .5f));
            }
            // The loop is counterclockwise; reverse the ring faces to point out of the solid rim.
            for (int i = 0; i < triangles.Count; i += 3)
            {
                int swap = triangles[i + 1]; triangles[i + 1] = triangles[i + 2]; triangles[i + 2] = swap;
            }
            View(gameObject, "Descent_Arch", vertices, triangles, material,
                new Color(.3f, .018f, .008f), new Color(1.1f, .025f, .008f));
            vertices.Clear(); triangles.Clear();
            for (int i = 0; i < inner.Length; i++)
            {
                int offset = vertices.Count;
                vertices.Add(Vector3.zero); vertices.Add(At(inner[i], 0)); vertices.Add(At(inner[(i + 1) % inner.Length], 0));
                triangles.Add(offset); triangles.Add(offset + 1); triangles.Add(offset + 2);
                // Opposite faces need independent vertices so normal accumulation cannot cancel.
                offset = vertices.Count;
                vertices.Add(Vector3.zero); vertices.Add(At(inner[(i + 1) % inner.Length], 0)); vertices.Add(At(inner[i], 0));
                triangles.Add(offset); triangles.Add(offset + 1); triangles.Add(offset + 2);
            }
            var veil = new GameObject("Descent_EmberVeil"); veil.transform.SetParent(transform, false);
            View(veil, "Descent_EmberVeil", vertices, triangles, material,
                new Color(.075f, .002f, .001f), new Color(.3f, .003f, .001f));
            vertices.Clear(); triangles.Clear();
            foreach (float y in new[] { .14f, -.10f })
                foreach (float z in new[] { -.48f, .48f })
                {
                    Quad(vertices, triangles, new Vector3(-.24f,y,z), new Vector3(0,y-.12f,z),
                        new Vector3(0,y-.18f,z), new Vector3(-.24f,y-.06f,z), true);
                    Quad(vertices, triangles, new Vector3(0,y-.12f,z), new Vector3(.24f,y,z),
                        new Vector3(.24f,y-.06f,z), new Vector3(0,y-.18f,z), true);
                }
            var glyph = new GameObject("Descent_DownwardSigil"); glyph.transform.SetParent(transform, false);
            View(glyph, "Descent_DownwardSigil", vertices, triangles, material,
                new Color(1f, .12f, .025f), new Color(3f, .2f, .025f));
        }

        private static Vector3 At(Vector2 point, float z) => new Vector3(point.x, point.y, z);
        private static void Quad(List<Vector3> vertices, List<int> triangles, Vector3 a, Vector3 b,
            Vector3 c, Vector3 d, bool doubleSided = false)
        {
            int i = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            triangles.Add(i); triangles.Add(i + 1); triangles.Add(i + 2);
            triangles.Add(i); triangles.Add(i + 2); triangles.Add(i + 3);
            if (!doubleSided) return;
            i = vertices.Count; vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            triangles.Add(i + 2); triangles.Add(i + 1); triangles.Add(i);
            triangles.Add(i + 3); triangles.Add(i + 2); triangles.Add(i);
        }

        private void View(GameObject target, string meshName, List<Vector3> vertices, List<int> triangles,
            Material material, Color color, Color emission)
        {
            var mesh = new Mesh { name = meshName };
            mesh.SetVertices(vertices); mesh.SetTriangles(triangles, 0); mesh.RecalculateNormals(); mesh.RecalculateBounds();
            _ownedMeshes.Add(mesh);
            target.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = target.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
            var tint = new MaterialPropertyBlock(); tint.SetColor("_BaseColor", color); tint.SetColor("_Color", color);
            tint.SetColor("_EmissionColor", emission); renderer.SetPropertyBlock(tint);
        }

        private void OnDestroy()
        {
            foreach (var mesh in _ownedMeshes)
                if (mesh != null) { if (Application.isPlaying) Destroy(mesh); else DestroyImmediate(mesh); }
            _ownedMeshes.Clear();
        }
    }
}
