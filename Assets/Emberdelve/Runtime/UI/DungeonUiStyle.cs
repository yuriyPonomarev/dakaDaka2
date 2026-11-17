using UnityEngine;

namespace Emberdelve.UI
{
    /// <summary>Original shared presentation; decoration never participates in pointer hit tests.</summary>
    public static class DungeonUiStyle
    {
        public static readonly Color Brass = new Color(.43f, .36f, .24f);
        public static readonly Color Parchment = new Color(.86f, .81f, .69f);
        private static Sprite _surface;
        private static Sprite _bar;
        private static Font _heading;
        public static Font HeadingFont => _heading != null ? _heading : (_heading = Resources.Load<Font>("UI/Fonts/Cinzel"));

        public static void Heading(UnityEngine.UI.Text text)
        {
            if (HeadingFont != null) text.font = HeadingFont;
            text.fontStyle = FontStyle.Normal;
            text.color = Parchment;
            text.raycastTarget = false;
        }

        public static void Surface(UnityEngine.UI.Image image, bool frame = true)
        {
            if (_surface == null)
            {
                const int size = 512;
                var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
                { name = "DakaDaka_UiGrain", filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var pixels = new Color[size * size];
                for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
                {
                    // Deterministic etched grain; no gameplay random-state changes.
                    uint hash = (uint)(x * 374761393 + y * 668265263);
                    hash = (hash ^ (hash >> 13)) * 1274126177;
                    // Recessed plate: darker towards the rim, lit slightly from above.
                    float edge = Mathf.SmoothStep(0f, 1f, Mathf.Min(x, y, size - 1 - x, size - 1 - y) / 56f);
                    float light = .68f + .18f * edge + .08f * y / (size - 1f) + .06f * (hash % 101) / 100f;
                    pixels[y * size + x] = new Color(light, light, light, 1);
                }
                texture.SetPixels(pixels); texture.Apply(false, true);
                _surface = Sprite.Create(texture, new Rect(0, 0, size, size), Vector2.one * .5f, 100);
            }
            image.sprite = _surface;
            if (!frame || image.transform.Find("MetalFrame") != null) return;
            var root = UiFactory.CreateGroup(image.transform, "MetalFrame");
            UiFactory.Stretch(root);
            Edge(root, "Top", new Vector2(0,1), Vector2.one, new Vector2(0,1), new Vector2(0,-1), Brass);
            Edge(root, "Bottom", Vector2.zero, new Vector2(1,0), new Vector2(0,1), new Vector2(0,1), Brass * .65f);
            Edge(root, "Left", Vector2.zero, new Vector2(0,1), new Vector2(1,0), new Vector2(1,0), Brass);
            Edge(root, "Right", new Vector2(1,0), Vector2.one, new Vector2(1,0), new Vector2(-1,0), Brass * .65f);
            foreach (var anchor in new[] { Vector2.zero, Vector2.one, new Vector2(0,1), new Vector2(1,0) })
            {
                var stud = UiFactory.CreateImage(root, "CornerPin", Brass, new Vector2(3,3),
                    new Vector2(anchor.x == 0 ? 5 : -5, anchor.y == 0 ? 5 : -5), anchor, anchor);
                stud.raycastTarget = false;
            }
        }

        /// <summary>Glossy fill for health, mana, ammo and hold bars; the image keeps its own tint and size.</summary>
        public static void Bar(UnityEngine.UI.Image image)
        {
            if (image == null) return;
            if (_bar == null)
            {
                const int height = 32;
                var texture = new Texture2D(4, height, TextureFormat.RGBA32, false)
                { name = "DakaDaka_UiBar", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
                var pixels = new Color[4 * height];
                for (int y = 0; y < height; y++)
                {
                    float v = y / (height - 1f);
                    float light = Mathf.Lerp(.5f, .86f, v) + (v > .58f && v < .9f ? .14f : 0f);
                    if (y == 0 || y == height - 1) light = .42f;
                    for (int x = 0; x < 4; x++) pixels[y * 4 + x] = new Color(light, light, light, 1);
                }
                texture.SetPixels(pixels); texture.Apply(false, true);
                _bar = Sprite.Create(texture, new Rect(0, 0, 4, height), Vector2.one * .5f, 100);
            }
            image.sprite = _bar;
            image.raycastTarget = false;
        }

        /// <summary>Etched trough with a thin brass rim behind a bar; adds no child objects.</summary>
        public static void BarBack(Transform back)
        {
            if (back == null) return;
            var image = back.GetComponent<UnityEngine.UI.Image>();
            if (image == null) return;
            Surface(image, false);
            image.raycastTarget = false;
            var rim = back.GetComponent<UnityEngine.UI.Outline>();
            if (rim == null) rim = back.gameObject.AddComponent<UnityEngine.UI.Outline>();
            rim.effectColor = new Color(Brass.r, Brass.g, Brass.b, .9f);
            rim.effectDistance = new Vector2(1.5f, 1.5f);
            rim.useGraphicAlpha = false;
        }

        private static void Edge(Transform parent, string name, Vector2 min, Vector2 max, Vector2 size, Vector2 at, Color color)
        {
            var line = UiFactory.CreateImage(parent, name, color, size, at, min, max);
            line.raycastTarget = false;
        }
    }
}
