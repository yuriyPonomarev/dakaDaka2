using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// V3c: the Poly Haven weapon models (CC0, see EmberdelvePolyHavenImport.WeaponIds) turned into normalised prefabs the game can
    /// hold, and the WeaponData -> model mapping (the data asset <see cref="WeaponViewModelSet"/> at Resources/Emberdelve).
    ///
    /// Normalisation is MEASURED from the mesh (the FBX is CPU readable for exactly this), never hand-rotated:
    ///   * the long axis of the vertex cloud is the blade / head axis; blades (sword, dagger): the crossguard is the widest slice, the
    ///     tip is the end farther from it, the grip is halfway between the butt and the guard. Heads (hammers, mace, hatchet): the
    ///     head is the end with the larger average cross section, the grip sits at <see cref="Spec.GripFraction"/> of the length from the butt.
    ///     Shields: the thin axis is the face normal, the wider end is the top, the origin is the centre of the face.
    ///   * result convention (see <see cref="WeaponViewModelSet"/>): grip at the origin, tip / head along +Y, flat face along +Z, edge along X,
    ///     uniform scale so the grip-to-far-end length equals <see cref="Spec.Length"/> (metres).
    /// Prefab: WV_&lt;id&gt; / Model (holder with the measured rotation, scale and offset) / the unpacked FBX. No colliders.
    /// </summary>
    public static class EmberdelveWeaponModels
    {
        public const string PrefabFolder = "Assets/Emberdelve/Prefabs/Visual/Weapons";
        public const string SetFolder = "Assets/Emberdelve/Resources/Emberdelve";
        public const string SetPath = SetFolder + "/WeaponViewModelSet.asset";
        public const string RustyFolder = "Assets/Emberdelve/Art/Materials/Undead";

        public enum Shape { Blade, Head, Shield }

        public sealed class Spec
        {
            public string Id;
            public Shape Shape;
            public float Length;          // metres, grip to far end (shield: height)
            public float GripFraction;    // heads: fraction of the full length from the butt
            public string[] Exclude;      // child names containing one of these are removed (scabbard)
            public string Role;
            public Color BaseColor = Color.white;   // multiplied into the photo albedo (the 1.8 prop lift is far too bright for polished steel next to the torch)
            public Color? BodyColor;                // materials whose name contains "body" (painted wood of the shield)
        }

        public static readonly Spec[] Specs =
        {
            new Spec { Id = "antique_estoc", Shape = Shape.Blade, Length = 1.15f, Role = "sword / rapier", BaseColor = new Color(0.85f, 0.85f, 0.85f) },
            new Spec { Id = "sledgehammer_01", Shape = Shape.Head, Length = 1.0f, GripFraction = 0.12f, Role = "maul", BaseColor = new Color(0.5f, 0.44f, 0.4f) },
            new Spec { Id = "ornate_medieval_mace", Shape = Shape.Head, Length = 0.6f, GripFraction = 0.15f, Role = "mace", BaseColor = new Color(1.1f, 1.1f, 1.1f) },
            new Spec { Id = "ornate_war_hammer", Shape = Shape.Head, Length = 0.7f, GripFraction = 0.15f, Role = "war hammer", BaseColor = new Color(0.95f, 0.95f, 0.95f) },
            new Spec { Id = "ornate_medieval_dagger", Shape = Shape.Blade, Length = 0.35f, Exclude = new[] { "scabbard" }, Role = "dagger", BaseColor = new Color(0.9f, 0.9f, 0.9f) },
            new Spec { Id = "hatchet", Shape = Shape.Head, Length = 0.3f, GripFraction = 0.18f, Role = "axe", BaseColor = new Color(1.1f, 1.0f, 0.95f) },
            new Spec { Id = "kite_shield", Shape = Shape.Shield, Length = 0.8f, Role = "shield", BaseColor = new Color(1f, 1f, 1f), BodyColor = new Color(0.62f, 0.52f, 0.48f) },
        };

        /// <summary>WeaponData.weaponId -> Poly Haven model. Anything not here must be in <see cref="Unmapped"/> (the test checks every WeaponData asset).</summary>
        public static readonly string[][] Mapping =
        {
            new[] { "ashen_short_sword", "antique_estoc" },
            new[] { "ember_rapier", "antique_estoc" },
            new[] { "warden_maul", "sledgehammer_01" },
            new[] { "emberbulwark_shield", "kite_shield" },
        };

        public static readonly string[][] Unmapped =
        {
            new[] { "ember_crossbow", "ranged: needs draw / reload arm animation (not in the Quaternius libraries); keeps the blockout crossbow and shows no arms" },
            new[] { "hollow_bow", "ranged: needs draw arm animation (not in the Quaternius libraries); keeps the blockout bow and shows no arms" },
            new[] { "unarmed_fists", "bare hands: no model; the first-person arms show empty hands and the swing is the fist punch" },
            new[] { "hollow_claw", "enemy-only weapon, there is no claw model; keeps its current visual" },
        };

        /// <summary>Imported weapon models no WeaponData uses (kept for later weapons; the dagger and hatchet are held by enemies).</summary>
        public static readonly string[] Spare = { "ornate_medieval_mace", "ornate_war_hammer" };

        public static Spec Find(string id)
        {
            for (int i = 0; i < Specs.Length; i++) if (Specs[i].Id == id) return Specs[i];
            return null;
        }

        public static string PrefabPath(string id) { return PrefabFolder + "/WV_" + id + ".prefab"; }
        public static GameObject LoadPrefab(string id) { return AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath(id)); }

        // ---------------------------------------------------------------- build

        [MenuItem("Emberdelve/Visual/Build Weapon View Models")]
        private static void MenuBuild() { Debug.Log("[Emberdelve][WeaponModels]\n" + EnsureAll(true)); }

        /// <summary>Builds every normalised prefab (and the data set). Returns a report of the measurements.</summary>
        public static string EnsureAll(bool force)
        {
            StringBuilder report = new StringBuilder();
            EmberdelveVisualSetup.EnsureFolder(PrefabFolder);
            for (int i = 0; i < Specs.Length; i++)
            {
                if (!force && LoadPrefab(Specs[i].Id) != null) continue;
                string result = BuildOne(Specs[i]);
                report.AppendLine(result);
            }

            for (int i = 0; i < Specs.Length; i++) ApplyLook(Specs[i].Id);
            WriteSet();
            return report.ToString();
        }

        private static string BuildOne(Spec spec)
        {
            GameObject fbx = EmberdelvePolyHavenImport.LoadModel(spec.Id);
            if (fbx == null) return spec.Id + ": FBX missing";

            GameObject source = (GameObject)PrefabUtility.InstantiatePrefab(fbx);
            PrefabUtility.UnpackPrefabInstance(source, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            source.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);

            try
            {
                if (spec.Exclude != null)
                {
                    Transform[] all = source.GetComponentsInChildren<Transform>(true);
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null || all[i] == source.transform) continue;
                        string lower = all[i].name.ToLowerInvariant();
                        for (int e = 0; e < spec.Exclude.Length; e++)
                        {
                            if (lower.Contains(spec.Exclude[e])) { Object.DestroyImmediate(all[i].gameObject); break; }
                        }
                    }
                }

                List<Vector3> points = CollectPoints(source);
                if (points.Count < 16) return spec.Id + ": no readable vertices (the FBX must be CPU readable)";

                Measure(spec, points, out Quaternion toPrefab, out Vector3 gripPoint, out float natural, out string measured);
                float scale = spec.Length / Mathf.Max(0.001f, natural);

                GameObject root = new GameObject("WV_" + spec.Id);
                GameObject holder = new GameObject("Model");
                holder.transform.SetParent(root.transform, false);
                holder.transform.localRotation = toPrefab;
                holder.transform.localScale = Vector3.one * scale;
                holder.transform.localPosition = -(toPrefab * gripPoint) * scale;
                source.transform.SetParent(holder.transform, false);
                source.transform.localPosition = Vector3.zero;
                source.transform.localRotation = Quaternion.identity;

                Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++) Object.DestroyImmediate(colliders[i]);

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath(spec.Id));
                Object.DestroyImmediate(root);
                source = null;
                return spec.Id + ": " + measured + " scale=" + scale.ToString("0.000");
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
            }
        }

        private static List<Vector3> CollectPoints(GameObject source)
        {
            List<Vector3> points = new List<Vector3>();
            MeshFilter[] filters = source.GetComponentsInChildren<MeshFilter>(true);
            for (int f = 0; f < filters.Length; f++)
            {
                Mesh mesh = filters[f].sharedMesh;
                if (mesh == null || !mesh.isReadable) continue;
                Matrix4x4 matrix = filters[f].transform.localToWorldMatrix;
                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i < vertices.Length; i++) points.Add(matrix.MultiplyPoint3x4(vertices[i]));
            }

            return points;
        }

        private static float Axis(Vector3 v, int axis) { return axis == 0 ? v.x : (axis == 1 ? v.y : v.z); }

        private static Vector3 Unit(int axis) { return axis == 0 ? Vector3.right : (axis == 1 ? Vector3.up : Vector3.forward); }

        /// <summary>
        /// Finds the long axis, the direction of the far end, the face axis and the grip point of a vertex cloud (instance frame) and the
        /// rotation that takes the instance frame to the prefab frame. natural = metres from the grip to the far end (shield: height).
        /// </summary>
        public static void Measure(Spec spec, List<Vector3> points, out Quaternion toPrefab, out Vector3 gripPoint, out float natural, out string report)
        {
            Vector3 min = points[0], max = points[0];
            for (int i = 1; i < points.Count; i++) { min = Vector3.Min(min, points[i]); max = Vector3.Max(max, points[i]); }
            Vector3 size = max - min;

            int longAxis = 0;
            for (int a = 1; a < 3; a++) if (Axis(size, a) > Axis(size, longAxis)) longAxis = a;
            int otherA = (longAxis + 1) % 3, otherB = (longAxis + 2) % 3;
            float lo = Axis(min, longAxis), hi = Axis(max, longAxis), span = hi - lo;

            // Cross section per slice along the long axis: extent of the two other axes.
            const int Slices = 48;
            float[] minA = new float[Slices], maxA = new float[Slices], minB = new float[Slices], maxB = new float[Slices];
            for (int s = 0; s < Slices; s++) { minA[s] = minB[s] = float.MaxValue; maxA[s] = maxB[s] = float.MinValue; }
            for (int i = 0; i < points.Count; i++)
            {
                int s = Mathf.Clamp((int)((Axis(points[i], longAxis) - lo) / span * Slices), 0, Slices - 1);
                float a = Axis(points[i], otherA), b = Axis(points[i], otherB);
                if (a < minA[s]) minA[s] = a; if (a > maxA[s]) maxA[s] = a;
                if (b < minB[s]) minB[s] = b; if (b > maxB[s]) maxB[s] = b;
            }

            float[] width = new float[Slices];
            float[] area = new float[Slices];
            for (int s = 0; s < Slices; s++)
            {
                bool empty = maxA[s] < minA[s];
                width[s] = empty ? 0f : Mathf.Max(maxA[s] - minA[s], maxB[s] - minB[s]);
                area[s] = empty ? 0f : (maxA[s] - minA[s]) * (maxB[s] - minB[s]);
            }

            float sliceSize = span / Slices;
            float dir;            // +1 when the far end (tip / head / top) is at the high coordinate
            float gripCoord;
            if (spec.Shape == Shape.Blade)
            {
                int guard = 0;
                for (int s = 1; s < Slices; s++) if (width[s] > width[guard]) guard = s;
                float guardCoord = lo + (guard + 0.5f) * sliceSize;
                dir = (hi - guardCoord) > (guardCoord - lo) ? 1f : -1f;
                float butt = dir > 0f ? lo : hi;
                gripCoord = (butt + guardCoord) * 0.5f;
                natural = Mathf.Abs((dir > 0f ? hi : lo) - gripCoord);
            }
            else
            {
                int outer = Mathf.Max(2, Slices / 4);
                float lowArea = 0f, highArea = 0f;
                for (int s = 0; s < outer; s++) { lowArea += area[s]; highArea += area[Slices - 1 - s]; }
                dir = highArea >= lowArea ? 1f : -1f;
                float butt = dir > 0f ? lo : hi;
                float far = dir > 0f ? hi : lo;
                if (spec.Shape == Shape.Shield) gripCoord = (lo + hi) * 0.5f;
                else gripCoord = butt + (far - butt) * spec.GripFraction;
                natural = spec.Shape == Shape.Shield ? span : Mathf.Abs(far - gripCoord);
            }

            // Lateral centre of the handle (or of the whole shield): average of the vertices around the grip coordinate.
            float window = spec.Shape == Shape.Shield ? span : Mathf.Max(0.02f, sliceSize * 2f);
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < points.Count; i++)
            {
                if (Mathf.Abs(Axis(points[i], longAxis) - gripCoord) > window * 0.5f) continue;
                sum += points[i];
                count++;
            }

            Vector3 centre = count > 0 ? sum / count : (min + max) * 0.5f;
            gripPoint = centre;
            if (longAxis == 0) gripPoint.x = gripCoord; else if (longAxis == 1) gripPoint.y = gripCoord; else gripPoint.z = gripCoord;

            // Face axis: the thinner of the other two (a blade's flat side, a shield's thickness).
            float extentA = Axis(size, otherA), extentB = Axis(size, otherB);
            int faceAxis = extentA <= extentB ? otherA : otherB;
            float faceSign = 1f;
            if (spec.Shape == Shape.Shield)
            {
                // The front of a convex shield is where the vertices bulge: the average depth lies on the back side of the middle.
                float mid = (Axis(min, faceAxis) + Axis(max, faceAxis)) * 0.5f, depth = 0f;
                for (int i = 0; i < points.Count; i++) depth += Axis(points[i], faceAxis) - mid;
                faceSign = depth > 0f ? -1f : 1f;
            }

            Vector3 up = Unit(longAxis) * dir;
            Vector3 forward = Unit(faceAxis) * faceSign;
            toPrefab = Quaternion.Inverse(Quaternion.LookRotation(forward, up));
            report = "long=" + "XYZ"[longAxis] + (dir > 0 ? "+" : "-") + " face=" + "XYZ"[faceAxis] + (faceSign > 0 ? "+" : "-") +
                     " size(m)=(" + size.x.ToString("0.000") + "," + size.y.ToString("0.000") + "," + size.z.ToString("0.000") + ") grip->far=" + natural.ToString("0.000");
        }

        /// <summary>Sets the base colour of the model's project materials (Poly Haven's generic 1.8 lift made the steel glow and left the shield paint and the sledgehammer handle toy bright).</summary>
        public static void ApplyLook(string id)
        {
            Spec spec = Find(id);
            if (spec == null) return;
            string[] guids = AssetDatabase.FindAssets("t:Material", new[] { EmberdelvePolyHavenImport.MaterialsFolder(id) });
            for (int i = 0; i < guids.Length; i++)
            {
                Material material = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guids[i]));
                if (material == null) continue;
                Color color = spec.BodyColor.HasValue && material.name.ToLowerInvariant().Contains("body") ? spec.BodyColor.Value : spec.BaseColor;
                color.a = 1f;
                material.SetColor("_BaseColor", color);
                if (material.HasProperty("_Color")) material.SetColor("_Color", color);
                EditorUtility.SetDirty(material);
            }

            AssetDatabase.SaveAssets();
        }

        // ---------------------------------------------------------------- data set

        /// <summary>Writes (or refreshes) Resources/Emberdelve/WeaponViewModelSet: the mapping above plus the arms prefab and shield block clip (kept when present).</summary>
        public static WeaponViewModelSet WriteSet()
        {
            EmberdelveVisualSetup.EnsureFolder(SetFolder);
            WeaponViewModelSet set = AssetDatabase.LoadAssetAtPath<WeaponViewModelSet>(SetPath);
            bool create = set == null;
            if (create) set = ScriptableObject.CreateInstance<WeaponViewModelSet>();

            List<WeaponViewModelSet.Entry> entries = new List<WeaponViewModelSet.Entry>();
            for (int i = 0; i < Mapping.Length; i++)
            {
                Spec spec = Find(Mapping[i][1]);
                GameObject prefab = spec != null ? LoadPrefab(spec.Id) : null;
                if (prefab == null) continue;
                entries.Add(new WeaponViewModelSet.Entry { weaponId = Mapping[i][0], prefab = prefab, length = spec.Length, sourceId = spec.Id });
            }

            set.entries = entries.ToArray();
            List<WeaponViewModelSet.Unmapped> unmapped = new List<WeaponViewModelSet.Unmapped>();
            for (int i = 0; i < Unmapped.Length; i++) unmapped.Add(new WeaponViewModelSet.Unmapped { weaponId = Unmapped[i][0], reason = Unmapped[i][1] });
            set.unmapped = unmapped.ToArray();
            set.spareModels = (string[])Spare.Clone();

            if (create) AssetDatabase.CreateAsset(set, SetPath); else EditorUtility.SetDirty(set);
            AssetDatabase.SaveAssets();
            return set;
        }

        // ---------------------------------------------------------------- rusty copies for the enemies

        /// <summary>
        /// Enemy weapons: the same Poly Haven materials with a rust tint (brown-orange multiplied into the already lifted albedo, rougher),
        /// one project material per source material under Art/Materials/Undead. Returns the map source material name -> rusty material.
        /// </summary>
        public static Dictionary<string, Material> RustyMaterials(string id)
        {
            Dictionary<string, Material> map = new Dictionary<string, Material>();
            GameObject prefab = LoadPrefab(id);
            if (prefab == null) return map;
            EmberdelveVisualSetup.EnsureFolder(RustyFolder);

            Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Material[] materials = renderers[r].sharedMaterials;
                for (int m = 0; m < materials.Length; m++)
                {
                    Material source = materials[m];
                    if (source == null || map.ContainsKey(source.name)) continue;

                    string path = RustyFolder + "/Rusty_" + source.name + ".mat";
                    Material rusty = AssetDatabase.LoadAssetAtPath<Material>(path);
                    bool create = rusty == null;
                    if (create) rusty = new Material(source);
                    else rusty.CopyPropertiesFromMaterial(source);

                    Color tint = new Color(0.82f, 0.52f, 0.34f, 1f);
                    Color lifted = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : Color.white;
                    rusty.SetColor("_BaseColor", new Color(lifted.r * tint.r, lifted.g * tint.g, lifted.b * tint.b, 1f));
                    if (rusty.HasProperty("_Smoothness")) rusty.SetFloat("_Smoothness", Mathf.Min(rusty.GetFloat("_Smoothness"), 0.3f));
                    if (create) AssetDatabase.CreateAsset(rusty, path); else EditorUtility.SetDirty(rusty);
                    map[source.name] = rusty;
                }
            }

            AssetDatabase.SaveAssets();
            return map;
        }
    }
}
