using UnityEngine;

namespace Emberdelve.UI
{
    /// <summary>Original shared presentation; decoration never participates in pointer hit tests.</summary>
    public static class DungeonUiStyle
    {
        public static readonly Color Brass = new Color(.43f, .36f, .24f);
        public static readonly Color Parchment = new Color(.86f, .81f, .69f);
        private static Sprite _surface;
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
                    float edge = Mathf.Min(x, y, size - 1 - x, size - 1 - y) / 20f;
                    float light = .86f + .08f * Mathf.Clamp01(edge) + .06f * (hash % 101) / 100f;
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

        private static void Edge(Transform parent, string name, Vector2 min, Vector2 max, Vector2 size, Vector2 at, Color color)
        {
            var line = UiFactory.CreateImage(parent, name, color, size, at, min, max);
            line.raycastTarget = false;
        }
    }
}
