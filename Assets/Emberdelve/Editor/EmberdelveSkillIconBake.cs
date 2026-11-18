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
    /// </summary>
    public static class EmberdelveSkillIconBake
    {
        private const string AtlasPath = "Assets/Emberdelve/Resources/Emberdelve/SkillIcons/Abilities.png";
        private const int Columns = 4;
        private const int OriginalRows = 2;
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
            int tileW = width / Columns;
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
            public readonly float[] Raised;   // relief that stands out of the tile
            public readonly float[] Groove;   // engraved lines
            public Mask(int n) { N = n; Raised = new float[n * n]; Groove = new float[n * n]; }

            // Shapes use 0..1 coordinates, y up.
            public void Poly(Vector2[] points, bool groove = false)
            {
                float[] target = groove ? Groove : Raised;
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

            public void Arc(Vector2 c, float r, float fromDeg, float toDeg, float thickness)
            {
                int steps = Mathf.Max(6, Mathf.CeilToInt(Mathf.Abs(toDeg - fromDeg) / 6f));
                var pts = new Vector2[steps + 1];
                for (int i = 0; i <= steps; i++) { float a = Mathf.Lerp(fromDeg, toDeg, i / (float)steps) * Mathf.Deg2Rad; pts[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r; }
                Polyline(pts, thickness);
            }

            private void Capsule(Vector2 a, Vector2 b, float radius, bool groove)
            {
                float[] target = groove ? Groove : Raised;
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

            // Everything is drawn inside the rim; the glyph area is the tile minus a margin.
            float[] height = Blur(mask.Raised, n, 2, 2);
            float[] soft = Blur(mask.Raised, n, 7, 2);
            float[] groove = Blur(mask.Groove, n, 2, 2);
            float[] shadowSource = Blur(Shift(mask.Raised, n, 5, -6), n, 6, 2);
            var h = new float[n * n];
            for (int i = 0; i < h.Length; i++) h[i] = height[i] * 1.0f + soft[i] * 0.9f - groove[i] * 0.55f;

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
                    float cracks = crack < .008f ? .55f : 1f;
                    float baseValue = (.045f + g * .06f) * cracks;
                    Color stone = new Color(baseValue * 1.05f, baseValue, baseValue * .93f, 1f);
                    // Edge falloff toward the rim (a recessed plate).
                    float edge = Mathf.Min(Mathf.Min(x - inset, n - inset - 1 - x), Mathf.Min(y - inset, n - inset - 1 - y));
                    stone *= Mathf.Lerp(.55f, 1f, Mathf.Clamp01(edge / 36f));
                    stone *= 1f - Mathf.Clamp01(shadowSource[i]) * .75f * (1f - Mathf.Clamp01(mask.Raised[i] * 4f));
                    stone.a = 1f;

                    if (mask.Raised[i] < .02f && height[i] < .03f && soft[i] < .02f) { result[i] = stone; continue; }

                    // Surface normal from the relief height.
                    float dx = H(h, n, x + 1, y) - H(h, n, x - 1, y);
                    float dy = H(h, n, x, y + 1) - H(h, n, x, y - 1);
                    Vector3 normal = new Vector3(-dx * 5.5f, -dy * 5.5f, 1f).normalized;
                    Vector3 light = new Vector3(-.5f, .62f, .6f).normalized;
                    float diffuse = Mathf.Clamp01(Vector3.Dot(normal, light));
                    float spec = Mathf.Pow(Mathf.Clamp01(Vector3.Dot(normal, new Vector3(-.28f, .34f, .9f).normalized)), 22f);
                    float grain = .86f + Noise(x * .6f, y * .6f, seed + 5) * .28f + (Noise(x * .09f, y * .09f, seed + 13) - .5f) * .3f;
                    Color pewter = new Color(.50f, .47f, .43f);
                    Color metal = pewter * (.18f + diffuse * 1.05f) * grain + Color.white * spec * .35f;
                    // Dark outline where the relief meets the plate.
                    float coverage = Mathf.Clamp01(mask.Raised[i] * 1.4f);
                    float rimDark = Mathf.Clamp01(1f - Mathf.Abs(height[i] - .5f) * 4.2f) * .35f;
                    metal *= 1f - rimDark;
                    metal.a = 1f;
                    result[i] = Color.Lerp(stone, metal, Mathf.Clamp01(Mathf.Max(coverage, Mathf.Clamp01(soft[i] * 1.2f) * .9f)));
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

        private static void Earthshaker(Mask m)
        {
            // Ground slab and the cracks that run out of the impact.
            m.Poly(new[] { new Vector2(.08f, .20f), new Vector2(.14f, .27f), new Vector2(.30f, .25f), new Vector2(.42f, .29f), new Vector2(.58f, .29f), new Vector2(.70f, .25f), new Vector2(.86f, .27f), new Vector2(.92f, .20f), new Vector2(.92f, .12f), new Vector2(.08f, .12f) });
            m.Polyline(new[] { new Vector2(.5f, .28f), new Vector2(.40f, .22f), new Vector2(.30f, .23f), new Vector2(.20f, .17f) }, .014f, true);
            m.Polyline(new[] { new Vector2(.5f, .28f), new Vector2(.60f, .21f), new Vector2(.70f, .23f), new Vector2(.80f, .16f) }, .014f, true);
            m.Polyline(new[] { new Vector2(.5f, .28f), new Vector2(.47f, .18f), new Vector2(.52f, .12f) }, .014f, true);
            // Flung rubble.
            m.Poly(new[] { new Vector2(.20f, .36f), new Vector2(.26f, .43f), new Vector2(.30f, .35f) });
            m.Poly(new[] { new Vector2(.72f, .38f), new Vector2(.78f, .46f), new Vector2(.83f, .37f) });
            m.Poly(new[] { new Vector2(.14f, .50f), new Vector2(.18f, .55f), new Vector2(.22f, .49f) });
            m.Poly(new[] { new Vector2(.80f, .56f), new Vector2(.85f, .61f), new Vector2(.88f, .54f) });
            // The hammer: shaft with grip rings, a broad head and a pommel.
            m.Poly(new[] { new Vector2(.47f, .93f), new Vector2(.53f, .93f), new Vector2(.53f, .62f), new Vector2(.47f, .62f) });
            for (int i = 0; i < 4; i++) m.Line(new Vector2(.47f, .85f - i * .06f), new Vector2(.53f, .85f - i * .06f), .012f, true);
            m.Poly(new[] { new Vector2(.24f, .62f), new Vector2(.76f, .62f), new Vector2(.80f, .57f), new Vector2(.80f, .43f), new Vector2(.76f, .37f), new Vector2(.24f, .37f), new Vector2(.20f, .43f), new Vector2(.20f, .57f) });
            m.Poly(new[] { new Vector2(.80f, .56f), new Vector2(.93f, .50f), new Vector2(.80f, .44f) });
            m.Line(new Vector2(.26f, .50f), new Vector2(.74f, .50f), .012f, true);
            m.Line(new Vector2(.5f, .62f), new Vector2(.5f, .37f), .012f, true);
            m.Disc(new Vector2(.5f, .95f), .035f);
            // Shock rings on both sides of the head.
            m.Arc(new Vector2(.5f, .45f), .34f, -28f, 28f, .022f);
            m.Arc(new Vector2(.5f, .45f), .34f, 152f, 208f, .022f);
            m.Arc(new Vector2(.5f, .45f), .42f, -24f, 24f, .018f);
            m.Arc(new Vector2(.5f, .45f), .42f, 156f, 204f, .018f);
        }

        private static void AdrenalineRush(Mask m)
        {
            // A heart (parametric) with a lightning bolt running through it, and speed ticks around it.
            var heart = new List<Vector2>();
            for (int i = 0; i < 72; i++)
            {
                float t = i / 72f * Mathf.PI * 2f;
                float x = 16f * Mathf.Pow(Mathf.Sin(t), 3f);
                float y = 13f * Mathf.Cos(t) - 5f * Mathf.Cos(2 * t) - 2f * Mathf.Cos(3 * t) - Mathf.Cos(4 * t);
                heart.Add(new Vector2(.5f + x * .0215f, .52f + y * .0215f));
            }
            // The heart is an outline; the bolt stands solid inside it.
            var outline = new List<Vector2>(heart);
            outline.Add(heart[0]);
            m.Polyline(outline.ToArray(), .040f);
            var inner = new List<Vector2>();
            foreach (Vector2 p in heart) inner.Add(new Vector2(.5f + (p.x - .5f) * .8f, .52f + (p.y - .52f) * .8f - .004f));
            inner.Add(inner[0]);
            m.Polyline(inner.ToArray(), .008f, true);
            m.Poly(new[] { new Vector2(.58f, .80f), new Vector2(.38f, .50f), new Vector2(.49f, .50f), new Vector2(.40f, .24f), new Vector2(.64f, .56f), new Vector2(.52f, .56f), new Vector2(.62f, .80f) });
            for (int i = 0; i < 3; i++)
            {
                float y = .30f + i * .2f;
                m.Line(new Vector2(.04f, y), new Vector2(.14f + i * .02f, y), .016f);
                m.Line(new Vector2(.96f, y + .06f), new Vector2(.86f - i * .02f, y + .06f), .016f);
            }
        }

        private static void IronConstitution(Mask m)
        {
            // A breastplate: shoulders, tapered waist, a central ridge and riveted trim.
            m.Poly(new[]
            {
                new Vector2(.20f, .80f), new Vector2(.34f, .88f), new Vector2(.44f, .84f), new Vector2(.5f, .78f), new Vector2(.56f, .84f), new Vector2(.66f, .88f), new Vector2(.80f, .80f),
                new Vector2(.78f, .60f), new Vector2(.72f, .40f), new Vector2(.62f, .27f), new Vector2(.5f, .22f), new Vector2(.38f, .27f), new Vector2(.28f, .40f), new Vector2(.22f, .60f)
            });
            m.Line(new Vector2(.5f, .76f), new Vector2(.5f, .27f), .014f, true);
            m.Polyline(new[] { new Vector2(.30f, .58f), new Vector2(.5f, .52f), new Vector2(.70f, .58f) }, .012f, true);
            m.Polyline(new[] { new Vector2(.34f, .42f), new Vector2(.5f, .37f), new Vector2(.66f, .42f) }, .012f, true);
            m.Polyline(new[] { new Vector2(.24f, .78f), new Vector2(.34f, .84f), new Vector2(.44f, .80f) }, .010f, true);
            m.Polyline(new[] { new Vector2(.76f, .78f), new Vector2(.66f, .84f), new Vector2(.56f, .80f) }, .010f, true);
            foreach (Vector2 p in new[] { new Vector2(.30f, .72f), new Vector2(.70f, .72f), new Vector2(.34f, .64f), new Vector2(.66f, .64f), new Vector2(.40f, .34f), new Vector2(.60f, .34f) })
                m.Disc(p, .016f);
            // Pauldron plates peeking out on each side.
            m.Poly(new[] { new Vector2(.10f, .82f), new Vector2(.22f, .90f), new Vector2(.24f, .76f), new Vector2(.12f, .70f) });
            m.Poly(new[] { new Vector2(.90f, .82f), new Vector2(.78f, .90f), new Vector2(.76f, .76f), new Vector2(.88f, .70f) });
        }

        private static void ArcaneReservoir(Mask m)
        {
            // A round flask of mana with a stopper, the liquid line engraved, and a swirl of magic above it.
            var body = new List<Vector2>();
            for (int i = 0; i < 40; i++) { float a = i / 40f * Mathf.PI * 2f; body.Add(new Vector2(.5f + Mathf.Cos(a) * .23f, .38f + Mathf.Sin(a) * .21f)); }
            m.Poly(body.ToArray());
            m.Poly(new[] { new Vector2(.43f, .56f), new Vector2(.57f, .56f), new Vector2(.555f, .70f), new Vector2(.445f, .70f) });
            m.Poly(new[] { new Vector2(.38f, .70f), new Vector2(.62f, .70f), new Vector2(.60f, .75f), new Vector2(.40f, .75f) });
            m.Poly(new[] { new Vector2(.45f, .75f), new Vector2(.55f, .75f), new Vector2(.53f, .84f), new Vector2(.47f, .84f) });
            m.Polyline(new[] { new Vector2(.30f, .40f), new Vector2(.38f, .43f), new Vector2(.46f, .39f), new Vector2(.54f, .43f), new Vector2(.62f, .39f), new Vector2(.70f, .42f) }, .014f, true);
            m.Ring(new Vector2(.50f, .38f), .17f, .010f, true);
            m.Disc(new Vector2(.42f, .30f), .022f, true);
            m.Disc(new Vector2(.58f, .27f), .016f, true);
            // Swirl and sparks.
            m.Arc(new Vector2(.5f, .78f), .30f, 150f, 215f, .018f);
            m.Arc(new Vector2(.5f, .78f), .30f, -35f, 30f, .018f);
            Spark(m, new Vector2(.20f, .74f), .06f);
            Spark(m, new Vector2(.80f, .80f), .05f);
            Spark(m, new Vector2(.84f, .50f), .04f);
        }

        private static void Spark(Mask m, Vector2 c, float r)
        {
            m.Poly(new[] { new Vector2(c.x, c.y + r), new Vector2(c.x + r * .22f, c.y + r * .22f), new Vector2(c.x + r, c.y), new Vector2(c.x + r * .22f, c.y - r * .22f), new Vector2(c.x, c.y - r), new Vector2(c.x - r * .22f, c.y - r * .22f), new Vector2(c.x - r, c.y), new Vector2(c.x - r * .22f, c.y + r * .22f) });
        }
    }
}
