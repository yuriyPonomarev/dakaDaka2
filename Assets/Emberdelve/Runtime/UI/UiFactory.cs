using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace Emberdelve.UI
{
    /// <summary>
    /// Builds the prototype HUD from uGUI primitives in code. Keeping the UI code-built means the
    /// scenes stay tiny and every panel can later be replaced by an authored prefab: only the
    /// references inside HudController change, the event subscriptions do not.
    /// Legacy Text is used on purpose so the HUD needs no font asset to be present.
    /// </summary>
    public static class UiFactory
    {
        private static Font _font;
        private static readonly Dictionary<Color, Texture2D> _solidTextures = new Dictionary<Color, Texture2D>();

        public static Font Font
        {
            get
            {
                if (_font == null)
                {
                    _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                    if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                }

                return _font;
            }
        }

        public static Canvas EnsureCanvas(string name, int sortingOrder)
        {
            Canvas canvas = null;
            foreach (Canvas candidate in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
                if (candidate.name == name && candidate.renderMode == RenderMode.ScreenSpaceOverlay)
                { canvas = candidate; break; }

            if (canvas == null)
            {
                GameObject root = new GameObject(name);
                canvas = root.AddComponent<Canvas>();
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            }

            canvas.name = name;
            canvas.sortingOrder = sortingOrder;

            if (canvas.GetComponent<GraphicRaycaster>() == null) canvas.gameObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            return canvas;
        }

        public static void EnsureEventSystem()
        {
            if (Object.FindFirstObjectByType<EventSystem>() != null) return;

            GameObject es = new GameObject("EventSystem");
            es.AddComponent<EventSystem>();
            es.AddComponent<InputSystemUIInputModule>();
        }

        public static RectTransform CreatePanel(Transform parent, string name, Color color)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);

            Image image = root.AddComponent<Image>();
            image.color = color;
            image.sprite = null;

            RectTransform rect = image.rectTransform;
            Stretch(rect);
            return rect;
        }

        public static RectTransform CreateGroup(Transform parent, string name)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);
            RectTransform rect = root.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = Vector2.zero;
            return rect;
        }

        public static Text CreateText(Transform parent, string name, string content, int fontSize, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter, FontStyle style = FontStyle.Normal)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);

            Text text = root.AddComponent<Text>();
            text.font = Font;
            text.fontSize = Mathf.Max(8, fontSize);
            text.color = color;
            text.alignment = anchor;
            text.fontStyle = style;
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.text = content;
            return text;
        }

        public static Image CreateImage(Transform parent, string name, Color color, Vector2 size, Vector2 anchoredPosition,
            Vector2 anchorMin, Vector2 anchorMax, Image.Type type = Image.Type.Simple, float fillAmount = 1f)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);

            Image image = root.AddComponent<Image>();
            image.color = color;
            image.sprite = null;
            image.type = type;
            image.fillAmount = Mathf.Clamp01(fillAmount);

            RectTransform rect = image.rectTransform;
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;

            return image;
        }

        public static Button CreateButton(Transform parent, string name, string label, UnityEngine.Events.UnityAction onClick,
            Vector2 size, Color background, Color textColor, int fontSize = 20)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(parent, false);

            Image image = root.AddComponent<Image>();
            image.color = background;

            RectTransform rect = image.rectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;

            Button button = root.AddComponent<Button>();
            ColorBlock colors = button.colors;
            colors.highlightedColor = new Color(1.28f, 1.24f, 1.15f, 1f);
            colors.pressedColor = new Color(.72f, .68f, .58f, 1f);
            colors.selectedColor = Color.white;
            button.colors = colors;
            button.targetGraphic = image;
            DungeonUiStyle.Surface(image);

            Text text = CreateText(root.transform, "Label", label, fontSize, textColor);
            Stretch(text.rectTransform);
            text.raycastTarget = false;
            if (fontSize >= 17) DungeonUiStyle.Heading(text);

            if (onClick != null) button.onClick.AddListener(onClick);

            return button;
        }

        public static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        /// <summary>
        /// The pivot every SetRect call used before per-widget pivots existed. Kept as the default so every
        /// existing caller is laid out exactly as before: a centred widget is still a centred widget.
        /// </summary>
        public static readonly Vector2 DefaultPivot = new Vector2(0.5f, 0.5f);

        public static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta, Vector2 anchoredPosition)
        {
            SetRect(rect, anchorMin, anchorMax, sizeDelta, anchoredPosition, DefaultPivot);
        }

        /// <summary>
        /// The pivot a widget needs for its existing corner offset to describe the corner it is named for.
        /// </summary>
        public static Vector2 AnchorPivot(Vector2 anchorMin, Vector2 anchorMax)
        {
            return new Vector2((anchorMin.x + anchorMax.x) * 0.5f, (anchorMin.y + anchorMax.y) * 0.5f);
        }

        /// <summary>
        /// Places a rectangle. anchoredPosition is the position of the pivot, so the pivot decides which corner
        /// of the rectangle the offset is measured from: a top-left anchored label whose offset is meant to be
        /// its top-left inset needs pivot (0,1), and a bottom-right anchored readout whose offset is meant to
        /// be its bottom-right inset needs pivot (1,0). With the centre pivot those offsets put the rectangle's
        /// centre at the corner inset instead, and a half-width of the widget lands outside the screen.
        ///
        /// sizeDelta is always relative to the anchor region, exactly as RectTransform defines it: an explicit
        /// size for point anchors, an inset for stretch anchors. Callers that pass a full size together with a
        /// stretched anchor are asking for a rectangle wider than their parent, and are corrected at the caller
        /// rather than silently resized here.
        /// </summary>
        public static void SetRect(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta,
            Vector2 anchoredPosition, Vector2 pivot)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = pivot;
            rect.sizeDelta = sizeDelta;
            rect.anchoredPosition = anchoredPosition;
        }

        public static Texture2D Solid(Color color)
        {
            if (_solidTextures.TryGetValue(color, out Texture2D existing) && existing != null) return existing;

            Texture2D texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply();
            texture.hideFlags = HideFlags.HideAndDontSave;
            _solidTextures[color] = texture;
            return texture;
        }

        public static void SetAlphaMask(Image image, float alpha)
        {
            if (image == null) return;
            Color color = image.color;
            color.a = Mathf.Clamp01(alpha);
            image.color = color;
        }
    }
}
