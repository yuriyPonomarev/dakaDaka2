using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    /// <summary>
    /// Draws the four lobby-kit skill icons in the style of the existing ability atlas (dark cracked tile, riveted rim, embossed pewter relief)
    /// and writes the 4x3 atlas Resources/Emberdelve/SkillIcons/Abilities.png: the original eight tiles are copied untouched and the new
    /// ones are rendered below them. Run from the menu or `unity command script eval_file`. Re-running keeps the original eight.
    /// Each glyph is two relief layers (a base plate and detail standing on it) plus engraved lines; the shading adds surface pitting, scratches,
    /// cavity darkening and edge wear so the metal reads as worn pewter rather than flat shapes.
    /// </summary>
    public static class EmberdelveSkillIconBake
    {
        private const string AtlasPath = "Assets/Emberdelve/Resources/Emberdelve/SkillIcons/Abilities.png";
        private const int Columns = 4;
        private const int OriginalHeight = 887;
        private const int TileSize = 444;

        [MenuItem("Emberdelve/Visual/Bake Skill Icons")]
        public static void Bake()
        {
            string full = Path.GetFullPath(AtlasPath);
            var old = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!old.LoadImage(File.ReadAllBytes(full))) { Debug.LogError("[SkillIcons] cannot read the atlas"); return; }

            int width = old.width;
            int newHeight = OriginalHeight + TileSize;
            var atlas = new Texture2D(width, newHeight, TextureFormat.RGBA32, false);
            var fill = new Color32[width * newHeight];
            for (int i = 0; i < fill.Length; i++) fill[i] = new Color32(0, 0, 0, 255);
            atlas.SetPixels32(fill);

            // The original eight tiles stay exactly as they were, in the top two rows.
            int sourceBottom = old.height - OriginalHeight;
            atlas.SetPixels(0, newHeight - OriginalHeight, width, OriginalHeight, old.GetPixels(0, sourceBottom, width, OriginalHeight));

            // The old tile 0 supplies the rim; the interior is repainted for every new tile.
            Color[] template = old.GetPixels(0, old.height - TileSize, TileSize, TileSize);
            Action<Mask>[] glyphs = { Earthshaker, AdrenalineRush, IronConstitution, ArcaneReservoir };
            for (int k = 0; k < glyphs.Length; k++)
            {
                Color[] tile = RenderTile(template, glyphs[k], 17 + k * 31);
                int x = Mathf.Min(Mathf.RoundToInt(k * (width / (float)Columns)), width - TileSize);
                atlas.SetPixels(x, 0, TileSize, TileSize, tile);
            }
            atlas.Apply();
            File.WriteAllBytes(full, atlas.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(old);
            UnityEngine.Object.DestroyImmediate(atlas);
            AssetDatabase.ImportAsset(AtlasPath, ImportAssetOptions.ForceUpdate);
            Debug.Log("[SkillIcons] atlas written: " + width + "x" + newHeight + " with 4 new icons");
        }

        // ---------------------------------------------------------------------------------------------- tile

        private sealed class Mask
        {
            public readonly int N;
            public readonly float[] Base;     // the relief plate
            public readonly float[] Top;      // detail standing on the plate
            public readonly float[] Groove;   // engraved lines
            public int Level;                 // 0 = Base, 1 = Top
            public Mask(int n) { N = n; Base = new float[n * n]; Top = new float[n * n]; Groove = new float[n * n]; }

            private float[] Target(bool groove) => groove ? Groove : (Level == 0 ? Base : Top);

            // Shapes use 0..1 coordinates, y up.
            public void Poly(Vector2[] points, bool groove = false)
            {
                float[] target = Target(groove);
                float minX = 1, maxX = 0, minY = 1, maxY = 0;
                foreach (Vector2 p in points) { minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y); }
                int x0 = Mathf.Max(0, Mathf.FloorToInt(minX * N)), x1 = Mathf.Min(N - 1, Mathf.CeilToInt(maxX * N));
                int y0 = Mathf.Max(0, Mathf.FloorToInt(minY * N)), y1 = Mathf.Min(N - 1, Mathf.CeilToInt(maxY * N));
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        int hits = 0;
                        for (int sy = 0; sy < 2; sy++)
                            for (int sx = 0; sx < 2; sx++)
                                if (Inside(points, (x + .25f + .5f * sx) / N, (y + .25f + .5f * sy) / N)) hits++;
                        if (hits > 0) target[y * N + x] = Mathf.Max(target[y * N + x], hits / 4f);
                    }
            }

            public void Line(Vector2 a, Vector2 b, float thickness, bool groove = false) => Capsule(a, b, thickness * .5f, groove);

            public void Polyline(Vector2[] pts, float thickness, bool groove = false)
            {
                for (int i = 0; i + 1 < pts.Length; i++) Line(pts[i], pts[i + 1], thickness, groove);
            }

            public void Disc(Vector2 c, float r, bool groove = false) => Capsule(c, c, r, groove);

            public void Ring(Vector2 c, float r, float thickness, bool groove = false)
            {
                var pts = new Vector2[49];
                for (int i = 0; i < pts.Length; i++) { float a = i / 48f * Mathf.PI * 2f; pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r; }
                Polyline(pts, thickness, groove);
            }

            public void Arc(Vector2 c, float r, float fromDeg, float toDeg, float thickness, bool groove = false)
            {
                int steps = Mathf.Max(6, Mathf.CeilToInt(Mathf.Abs(toDeg - fromDeg) / 6f));
                var pts = new Vector2[steps + 1];
                for (int i = 0; i <= steps; i++) { float a = Mathf.Lerp(fromDeg, toDeg, i / (float)steps) * Mathf.Deg2Rad; pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r; }
                Polyline(pts, thickness, groove);
            }

            /// <summary>A rivet: a raised stud on the detail layer with a dark seat ring.</summary>
            public void Rivet(Vector2 c, float r)
            {
                int saved = Level;
                Ring(c, r * 1.5f, r * .5f, true);
                Level = 1; Disc(c, r); Level = saved;
            }

            /// <summary>Raised rim along an outline, one layer above the plate (rolled edges, borders).</summary>
            public void Rim(Vector2[] closed, float thickness)
            {
                int saved = Level; Level = 1;
                var pts = new List<Vector2>(closed) { closed[0] };
                Polyline(pts.ToArray(), thickness);
                Level = saved;
            }

            private void Capsule(Vector2 a, Vector2 b, float radius, bool groove)
            {
                float[] target = Target(groove);
                float minX = Mathf.Min(a.x, b.x) - radius, maxX = Mathf.Max(a.x, b.x) + radius;
                float minY = Mathf.Min(a.y, b.y) - radius, maxY = Mathf.Max(a.y, b.y) + radius;
                int x0 = Mathf.Max(0, Mathf.FloorToInt(minX * N) - 1), x1 = Mathf.Min(N - 1, Mathf.CeilToInt(maxX * N) + 1);
                int y0 = Mathf.Max(0, Mathf.FloorToInt(minY * N) - 1), y1 = Mathf.Min(N - 1, Mathf.CeilToInt(maxY * N) + 1);
                Vector2 ab = b - a;
                float len2 = ab.sqrMagnitude;
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 p = new Vector2((x + .5f) / N, (y + .5f) / N);
                        float t = len2 < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(p - a, ab) / len2);
                        float d = (p - (a + ab * t)).magnitude;
                        float cover = Mathf.Clamp01((radius - d) * N + .5f);
                        if (cover > 0f) target[y * N + x] = Mathf.Max(target[y * N + x], cover);
                    }
            }

            private static bool Inside(Vector2[] poly, float px, float py)
            {
                bool inside = false;
                for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
                    if ((poly[i].y > py) != (poly[j].y > py) &&
                        px < (poly[j].x - poly[i].x) * (py - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x) inside = !inside;
                return inside;
            }
        }

        private static Color[] RenderTile(Color[] rim, Action<Mask> draw, int seed)
        {
            int n = TileSize;
            var mask = new Mask(n);
            draw(mask);

            // Scratches across the relief: short engraved hairlines on the plate.
            var rng = new System.Random(seed);
            for (int i = 0; i < 22; i++)
            {
                float x = (float)rng.NextDouble(), y = (float)rng.NextDouble();
                int xi = Mathf.Clamp((int)(x * n), 0, n - 1), yi = Mathf.Clamp((int)(y * n), 0, n - 1);
                if (mask.Base[yi * n + xi] < .5f) continue;
                float a = (float)rng.NextDouble() * Mathf.PI, l = .02f + (float)rng.NextDouble() * .06f;
                mask.Line(new Vector2(x, y), new Vector2(x + Mathf.Cos(a) * l, y + Mathf.Sin(a) * l), .0025f, true);
            }

            float[] plate = Blur(mask.Base, n, 2, 2);
            float[] detail = Blur(mask.Top, n, 1, 2);
            float[] soft = Blur(mask.Base, n, 8, 2);
            float[] groove = Blur(mask.Groove, n, 1, 2);
            float[] shadowSource = Blur(Shift(mask.Base, n, 6, -7), n, 7, 2);
            var h = new float[n * n];
            for (int i = 0; i < h.Length; i++)
            {
                int x = i % n, y = i / n;
                float pitting = (Noise(x * .8f, y * .8f, seed + 41) - .5f) * .025f + (Noise(x * .22f, y * .22f, seed + 43) - .5f) * .035f;
                h[i] = plate[i] * 1.0f + detail[i] * .75f + soft[i] * .85f - groove[i] * .6f + pitting * Mathf.Clamp01(mask.Base[i] * 3f);
            }

            var result = new Color[n * n];
            const int inset = 27;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    bool interior = x >= inset && x < n - inset && y >= inset && y < n - inset;
                    if (!interior) { result[i] = rim[i]; continue; }

                    // Dark cracked stone.
                    float g = Noise(x * .035f, y * .035f, seed) * .6f + Noise(x * .11f, y * .11f, seed + 3) * .3f + Noise(x * .45f, y * .45f, seed + 9) * .12f;
                    float crack = Mathf.Abs(Noise(x * .018f, y * .018f, seed + 21) - .5f);
                    float crack2 = Mathf.Abs(Noise(x * .03f + 7f, y * .03f, seed + 27) - .5f);
                    float cracks = (crack < .007f || crack2 < .005f) ? .5f : 1f;
                    float baseValue = (.04f + g * .06f) * cracks;
                    Color stone = new Color(baseValue * 1.05f, baseValue, baseValue * .93f, 1f);
                    float edge = Mathf.Min(Mathf.Min(x - inset, n - inset - 1 - x), Mathf.Min(y - inset, n - inset - 1 - y));
                    stone *= Mathf.Lerp(.5f, 1f, Mathf.Clamp01(edge / 40f));
                    stone *= 1f - Mathf.Clamp01(shadowSource[i]) * .8f * (1f - Mathf.Clamp01(mask.Base[i] * 4f));
                    stone.a = 1f;

                    float cover = Mathf.Max(mask.Base[i], mask.Top[i]);
                    if (cover < .02f && plate[i] < .03f && soft[i] < .02f) { result[i] = stone; continue; }

                    // Surface normal from the relief height.
                    float dx = H(h, n, x + 1, y) - H(h, n, x - 1, y);
                    float dy = H(h, n, x, y + 1) - H(h, n, x, y - 1);
                    Vector3 normal = new Vector3(-dx * 9f, -dy * 9f, 1f).normalized;
                    Vector3 light = new Vector3(-.5f, .62f, .6f).normalized;
                    float diffuse = Mathf.Clamp01(Vector3.Dot(normal, light));
                    float spec = Mathf.Pow(Mathf.Clamp01(Vector3.Dot(normal, new Vector3(-.3f, .36f, .88f).normalized)), 26f);
                    float slope = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy) * 3.2f);

                    float grain = .84f + Noise(x * .6f, y * .6f, seed + 5) * .26f + (Noise(x * .09f, y * .09f, seed + 13) - .5f) * .34f;
                    // Patina: dark in the recesses and where the relief meets the plate, bright on worn edges that face the light.
                    float cavity = Mathf.Clamp01(groove[i] * 1.8f + (soft[i] - plate[i]) * .9f);
                    float wear = slope * Mathf.Clamp01(diffuse * 1.4f - .25f);
                    Color pewter = new Color(.52f, .485f, .44f);
                    Color metal = pewter * (.10f + diffuse * 1.3f) * grain;
                    metal *= 1f - cavity * .55f;
                    metal += new Color(.85f, .8f, .72f) * wear * .22f + Color.white * spec * .55f;
                    float rimDark = Mathf.Clamp01(1f - Mathf.Abs(plate[i] - .5f) * 3.2f) * .6f;
                    metal *= 1f - rimDark * (1f - Mathf.Clamp01(mask.Top[i] * 2f));
                    metal.a = 1f;
                    result[i] = Color.Lerp(stone, metal, Mathf.Clamp01(Mathf.Max(Mathf.Clamp01(cover * 1.4f), Mathf.Clamp01(soft[i] * 1.2f) * .9f)));
                }
            return result;
        }

        private static float H(float[] a, int n, int x, int y) => a[Mathf.Clamp(y, 0, n - 1) * n + Mathf.Clamp(x, 0, n - 1)];

        private static float[] Shift(float[] src, int n, int dx, int dy)
        {
            var o = new float[src.Length];
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int sx = x - dx, sy = y - dy;
                    if (sx >= 0 && sx < n && sy >= 0 && sy < n) o[y * n + x] = src[sy * n + sx];
                }
            return o;
        }

        private static float[] Blur(float[] src, int n, int radius, int passes)
        {
            float[] a = (float[])src.Clone(), b = new float[src.Length];
            for (int p = 0; p < passes; p++)
            {
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float sum = 0; int count = 0;
                        for (int k = -radius; k <= radius; k++) { int xx = x + k; if (xx >= 0 && xx < n) { sum += a[y * n + xx]; count++; } }
                        b[y * n + x] = sum / count;
                    }
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float sum = 0; int count = 0;
                        for (int k = -radius; k <= radius; k++) { int yy = y + k; if (yy >= 0 && yy < n) { sum += b[yy * n + x]; count++; } }
                        a[y * n + x] = sum / count;
                    }
            }
            return a;
        }

        private static float Noise(float x, float y, int seed)
        {
            int xi = Mathf.FloorToInt(x), yi = Mathf.FloorToInt(y);
            float fx = x - xi, fy = y - yi;
            float a = Hash(xi, yi, seed), b = Hash(xi + 1, yi, seed), c = Hash(xi, yi + 1, seed), d = Hash(xi + 1, yi + 1, seed);
            float u = fx * fx * (3 - 2 * fx), v = fy * fy * (3 - 2 * fy);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        private static float Hash(int x, int y, int seed)
        {
            uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1274126177);
            h = (h ^ (h >> 13)) * 1274126177u;
            return ((h ^ (h >> 16)) & 0xFFFF) / 65535f;
        }

        // ---------------------------------------------------------------------------------------------- glyphs

        private static Vector2 V(float x, float y) => new Vector2(x, y);

        private static void Earthshaker(Mask m)
        {
            // Broken ground: a thick slab with a jagged top edge, cracks running out of the impact, and flung rubble.
            m.Poly(new[] { V(.05f, .20f), V(.10f, .27f), V(.20f, .24f), V(.30f, .29f), V(.40f, .26f), V(.5f, .31f), V(.60f, .26f), V(.70f, .29f), V(.80f, .24f), V(.90f, .27f), V(.95f, .20f), V(.95f, .10f), V(.05f, .10f) });
            m.Level = 1;
            m.Polyline(new[] { V(.07f, .17f), V(.30f, .19f), V(.5f, .17f), V(.72f, .19f), V(.93f, .17f) }, .010f);
            m.Level = 0;
            m.Polyline(new[] { V(.5f, .30f), V(.42f, .24f), V(.35f, .25f), V(.27f, .19f), V(.20f, .20f) }, .013f, true);
            m.Polyline(new[] { V(.5f, .30f), V(.58f, .23f), V(.66f, .25f), V(.74f, .18f), V(.82f, .19f) }, .013f, true);
            m.Polyline(new[] { V(.5f, .30f), V(.47f, .20f), V(.52f, .14f), V(.49f, .11f) }, .013f, true);
            m.Polyline(new[] { V(.35f, .25f), V(.33f, .15f) }, .009f, true);
            m.Polyline(new[] { V(.66f, .25f), V(.69f, .14f) }, .009f, true);
            // Rubble and dust.
            foreach (var r in new[] { new[] { .17f, .36f, .24f }, new[] { .78f, .40f, .22f }, new[] { .11f, .52f, .15f }, new[] { .87f, .57f, .14f }, new[] { .25f, .62f, .10f }, new[] { .75f, .66f, .12f } })
                m.Poly(new[] { V(r[0], r[1]), V(r[0] + r[2] * .45f, r[1] + r[2] * .9f), V(r[0] + r[2] * .95f, r[1] + r[2] * .35f), V(r[0] + r[2] * .55f, r[1] - r[2] * .1f) });
            // Hammer: wrapped haft, steel collar, heavy head with a face on one side and a spike on the other.
            m.Poly(new[] { V(.465f, .95f), V(.535f, .95f), V(.535f, .60f), V(.465f, .60f) });
            m.Level = 1;
            for (int i = 0; i < 7; i++) m.Line(V(.462f, .88f - i * .036f), V(.538f, .84f - i * .036f), .012f);
            m.Disc(V(.5f, .965f), .04f);
            m.Level = 0;
            m.Poly(new[] { V(.20f, .60f), V(.74f, .60f), V(.77f, .56f), V(.77f, .42f), V(.74f, .38f), V(.20f, .38f), V(.16f, .42f), V(.16f, .56f) });
            m.Poly(new[] { V(.77f, .56f), V(.95f, .49f), V(.77f, .42f) });
            m.Level = 1;
            m.Poly(new[] { V(.44f, .66f), V(.56f, .66f), V(.57f, .60f), V(.43f, .60f) });
            m.Polyline(new[] { V(.20f, .59f), V(.74f, .59f), V(.76f, .55f), V(.76f, .43f), V(.74f, .39f), V(.20f, .39f), V(.17f, .43f), V(.17f, .55f), V(.20f, .59f) }, .014f);
            m.Level = 0;
            m.Line(V(.44f, .60f), V(.44f, .38f), .010f, true);
            m.Line(V(.56f, .60f), V(.56f, .38f), .010f, true);
            m.Line(V(.18f, .49f), V(.42f, .49f), .008f, true);
            m.Rivet(V(.26f, .54f), .014f); m.Rivet(V(.26f, .44f), .014f); m.Rivet(V(.68f, .54f), .014f); m.Rivet(V(.68f, .44f), .014f);
            // Shock rings.
            m.Arc(V(.5f, .49f), .35f, -26f, 26f, .022f);
            m.Arc(V(.5f, .49f), .35f, 154f, 206f, .022f);
            m.Arc(V(.5f, .49f), .43f, -22f, 22f, .018f);
            m.Arc(V(.5f, .49f), .43f, 158f, 202f, .018f);
        }

        private static void AdrenalineRush(Mask m)
        {
            // A heart: thick rolled outline, a chambered plate inside, and a lightning bolt, with jagged rays and speed streaks around it.
            var heart = new List<Vector2>();
            for (int i = 0; i < 72; i++)
            {
                float t = i / 72f * Mathf.PI * 2f;
                float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2 * t) - 2f * Mathf.Cos(3 * t) - Mathf.Cos(4 * t);
                heart.Add(V(.5f + x * .0225f, .52f + y * .0225f));
            }
            m.Poly(heart.ToArray());
            m.Rim(heart.ToArray(), .026f);
            var inner = new List<Vector2>();
            foreach (Vector2 p in heart) inner.Add(V(.5f + (p.x - .5f) * .78f, .52f + (p.y - .52f) * .78f - .004f));
            m.Rim(inner.ToArray(), .012f);
            // Chamber seams and veins.
            m.Polyline(new[] { V(.5f, .74f), V(.50f, .66f), V(.46f, .60f) }, .010f, true);
            m.Polyline(new[] { V(.34f, .66f), V(.38f, .60f), V(.36f, .54f) }, .008f, true);
            m.Polyline(new[] { V(.66f, .66f), V(.62f, .60f), V(.64f, .54f) }, .008f, true);
            m.Polyline(new[] { V(.40f, .38f), V(.44f, .32f) }, .008f, true);
            // The bolt, raised with a bevel line down its middle.
            Vector2[] bolt = { V(.60f, .83f), V(.36f, .50f), V(.49f, .50f), V(.40f, .20f), V(.67f, .57f), V(.53f, .57f), V(.64f, .83f) };
            m.Level = 1; m.Poly(bolt);
            m.Level = 0; m.Polyline(new[] { V(.58f, .78f), V(.44f, .53f), V(.52f, .50f), V(.46f, .30f) }, .007f, true);
            // Rays and streaks.
            for (int i = 0; i < 9; i++)
            {
                float a = (i / 9f) * Mathf.PI * 2f + .2f;
                float l = (i % 2 == 0) ? .13f : .08f;
                Vector2 c = V(.5f, .52f);
                Vector2 dir = V(Mathf.Cos(a), Mathf.Sin(a));
                Vector2 start = c + new Vector2(dir.x * .46f, dir.y * .40f);
                if (start.x < .06f || start.x > .94f || start.y < .08f || start.y > .94f) continue;
                m.Poly(new[] { start + V(-dir.y, dir.x) * .012f, start + dir * l, start + V(dir.y, -dir.x) * .012f });
            }
            for (int i = 0; i < 3; i++)
            {
                float y = .28f + i * .22f;
                m.Line(V(.04f, y), V(.15f + i * .02f, y), .014f);
                m.Line(V(.96f, y + .05f), V(.85f - i * .02f, y + .05f), .014f);
            }
        }

        private static void IronConstitution(Mask m)
        {
            // A breastplate: collar, shoulder lames, a ridged chest, belted waist and tassets, with a rolled rim and rivets all the way round.
            Vector2[] chest =
            {
                V(.22f, .80f), V(.34f, .87f), V(.44f, .84f), V(.5f, .79f), V(.56f, .84f), V(.66f, .87f), V(.78f, .80f),
                V(.77f, .62f), V(.72f, .46f), V(.64f, .38f), V(.36f, .38f), V(.28f, .46f), V(.23f, .62f)
            };
            m.Poly(chest);
            m.Rim(chest, .018f);
            // Pauldrons: three overlapping lames each side.
            for (int s = 0; s < 2; s++)
            {
                float sg = s == 0 ? -1f : 1f;
                for (int l = 0; l < 3; l++)
                {
                    float y = .80f - l * .05f;
                    float x0 = .5f + sg * (.28f + l * .015f), x1 = .5f + sg * (.40f - l * .015f);
                    m.Poly(new[] { V(x0, y + .04f), V(x1, y + .02f), V(x1, y - .03f), V(x0, y - .01f) });
                    m.Level = 1; m.Line(V(x0, y - .008f), V(x1, y - .028f), .008f); m.Level = 0;
                }
            }
            // Gorget.
            m.Poly(new[] { V(.40f, .90f), V(.60f, .90f), V(.58f, .82f), V(.42f, .82f) });
            m.Line(V(.42f, .86f), V(.58f, .86f), .008f, true);
            // Ridge and muscle planes.
            m.Level = 1; m.Poly(new[] { V(.49f, .78f), V(.51f, .78f), V(.52f, .42f), V(.48f, .42f) }); m.Level = 0;
            m.Polyline(new[] { V(.29f, .64f), V(.44f, .60f), V(.48f, .50f) }, .010f, true);
            m.Polyline(new[] { V(.71f, .64f), V(.56f, .60f), V(.52f, .50f) }, .010f, true);
            m.Polyline(new[] { V(.30f, .50f), V(.46f, .46f) }, .008f, true);
            m.Polyline(new[] { V(.70f, .50f), V(.54f, .46f) }, .008f, true);
            // Belt and tassets.
            m.Poly(new[] { V(.36f, .38f), V(.64f, .38f), V(.64f, .33f), V(.36f, .33f) });
            m.Level = 1; m.Poly(new[] { V(.46f, .385f), V(.54f, .385f), V(.54f, .325f), V(.46f, .325f) }); m.Level = 0;
            for (int i = 0; i < 3; i++)
            {
                float y = .33f - i * .055f, w = .19f - i * .025f;
                m.Poly(new[] { V(.5f - w, y), V(.5f + w, y), V(.5f + w - .012f, y - .05f), V(.5f - w + .012f, y - .05f) });
                m.Line(V(.5f - w + .01f, y - .05f), V(.5f + w - .01f, y - .05f), .007f, true);
            }
            foreach (Vector2 p in new[] { V(.30f, .72f), V(.70f, .72f), V(.28f, .58f), V(.72f, .58f), V(.34f, .44f), V(.66f, .44f), V(.40f, .84f), V(.60f, .84f) })
                m.Rivet(p, .014f);
        }

        private static void ArcaneReservoir(Mask m)
        {
            // A round flask of mana: glass bulb, banded neck with studs, a stopper, a rune circle around it, liquid surface and bubbles.
            var body = new List<Vector2>();
            for (int i = 0; i < 48; i++) { float a = i / 48f * Mathf.PI * 2f; body.Add(V(.5f + Mathf.Cos(a) * .23f, .36f + Mathf.Sin(a) * .21f)); }
            m.Poly(body.ToArray());
            m.Rim(body.ToArray(), .016f);
            // Rune circle.
            m.Ring(V(.5f, .36f), .33f, .008f, true);
            for (int i = 0; i < 16; i++)
            {
                float a = i / 16f * Mathf.PI * 2f;
                Vector2 c = V(.5f, .36f) + V(Mathf.Cos(a), Mathf.Sin(a)) * .33f;
                if (c.y < .09f || c.x < .08f || c.x > .92f) continue;
                m.Disc(c, .009f, true);
                m.Line(c, c + V(Mathf.Cos(a), Mathf.Sin(a)) * (i % 2 == 0 ? .035f : .02f), .006f, true);
            }
            // Neck, band and lip.
            m.Poly(new[] { V(.43f, .55f), V(.57f, .55f), V(.555f, .70f), V(.445f, .70f) });
            m.Level = 1; m.Poly(new[] { V(.425f, .62f), V(.575f, .62f), V(.575f, .66f), V(.425f, .66f) }); m.Level = 0;
            m.Rivet(V(.46f, .64f), .008f); m.Rivet(V(.54f, .64f), .008f);
            m.Poly(new[] { V(.37f, .70f), V(.63f, .70f), V(.61f, .76f), V(.39f, .76f) });
            m.Level = 1; m.Line(V(.38f, .73f), V(.62f, .73f), .010f); m.Level = 0;
            m.Poly(new[] { V(.45f, .76f), V(.55f, .76f), V(.535f, .85f), V(.465f, .85f) });
            m.Line(V(.47f, .82f), V(.53f, .80f), .006f, true);
            // Liquid line, swirl and bubbles.
            m.Polyline(new[] { V(.29f, .38f), V(.36f, .42f), V(.43f, .37f), V(.50f, .42f), V(.57f, .37f), V(.64f, .42f), V(.71f, .38f) }, .013f, true);
            m.Arc(V(.5f, .33f), .11f, 200f, 340f, .010f, true);
            foreach (var b in new[] { new[] { .41f, .28f, .022f }, new[] { .57f, .26f, .016f }, new[] { .50f, .22f, .012f }, new[] { .62f, .31f, .010f } })
                m.Ring(V(b[0], b[1]), b[2], .006f, true);
            // Glass glints on the bulb.
            m.Level = 1; m.Arc(V(.5f, .36f), .18f, 120f, 170f, .014f); m.Level = 0;
            // Sparks.
            Spark(m, V(.17f, .76f), .06f);
            Spark(m, V(.83f, .82f), .05f);
            Spark(m, V(.86f, .52f), .04f);
            Spark(m, V(.14f, .50f), .035f);
            m.Arc(V(.5f, .80f), .28f, 150f, 210f, .016f);
            m.Arc(V(.5f, .80f), .28f, -30f, 30f, .016f);
        }

        private static void Spark(Mask m, Vector2 c, float r)
        {
            m.Poly(new[] { V(c.x, c.y + r), V(c.x + r * .22f, c.y + r * .22f), V(c.x + r, c.y), V(c.x + r * .22f, c.y - r * .22f), V(c.x, c.y - r), V(c.x - r * .22f, c.y - r * .22f), V(c.x - r, c.y), V(c.x - r * .22f, c.y + r * .22f) });
        }
    }
}
