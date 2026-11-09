using System.Collections.Generic;
using Emberdelve.Data;
using Emberdelve.Items;
using UnityEngine;
using UnityEngine.UI;

namespace Emberdelve.UI
{
    /// <summary>Shared geometry and item artwork for the living and fallen adventurer.</summary>
    public static class LootPanelStyle
    {
        public const float Width = 520f, Height = 650f, Step = 48f, Cell = 46f;
        public static readonly Color Gold = new Color(.61f, .49f, .29f);
        public static readonly Color Ink = new Color(.045f, .047f, .049f);
        private static readonly Dictionary<string, Sprite> Icons = new Dictionary<string, Sprite>();

        public static Vector2 SlotPosition(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.Head: return new Vector2(0, 222);
                case EquipmentSlot.Chest: return new Vector2(0, 144);
                case EquipmentSlot.Legs: return new Vector2(0, 66);
                case EquipmentSlot.Hands: return new Vector2(-83, 144);
                case EquipmentSlot.Back: return new Vector2(83, 144);
                case EquipmentSlot.Feet: return new Vector2(83, 66);
                case EquipmentSlot.Necklace: return new Vector2(83, 222);
                case EquipmentSlot.RingLeft: return new Vector2(-83, 222);
                case EquipmentSlot.RingRight: return new Vector2(-83, 66);
                case EquipmentSlot.Weapon: return new Vector2(-182, 205);
                case EquipmentSlot.OffHand: return new Vector2(-182, 112);
                case EquipmentSlot.AltWeapon: return new Vector2(182, 205);
                default: return new Vector2(182, 112);
            }
        }

        public static Vector2 SlotSize(EquipmentSlot slot) =>
            slot == EquipmentSlot.Weapon || slot == EquipmentSlot.OffHand || slot == EquipmentSlot.AltWeapon || slot == EquipmentSlot.AltOffHand
                ? new Vector2(62, 76) : new Vector2(60, 60);

        public static void Frame(Image image, Color color)
        {
            var outline = image.gameObject.AddComponent<Outline>();
            outline.effectColor = color; outline.effectDistance = new Vector2(1, -1);
            outline.useGraphicAlpha = false;
        }

        public static void Decorate(Image image, ItemData item, ItemInstance instance = null, bool showName = true)
        {
            if (item == null) { Frame(image, new Color(.24f, .24f, .22f)); return; }
            Color rarity = instance != null ? ItemTooltip.RarityColor(instance.Rarity) : Gold;
            image.color = Color.Lerp(Ink, rarity, .12f);
            Frame(image, Color.Lerp(Ink, rarity, .65f));
            Image icon = UiFactory.CreateImage(image.transform, "ItemIcon", Color.Lerp(item.iconColor, Color.white, .38f),
                Vector2.zero, Vector2.zero, new Vector2(.5f, .5f), new Vector2(.5f, .5f));
            icon.sprite = Icon(item); icon.preserveAspect = true; icon.raycastTarget = false;
            icon.rectTransform.anchorMin = Vector2.zero; icon.rectTransform.anchorMax = Vector2.one;
            icon.rectTransform.offsetMin = new Vector2(7, 12); icon.rectTransform.offsetMax = new Vector2(-7, -5);
            if (!showName) return;
            Text name = UiFactory.CreateText(image.transform, "ItemName", item.displayName, 9, new Color(.88f,.85f,.77f), TextAnchor.LowerCenter);
            name.raycastTarget = false;
            name.rectTransform.anchorMin = Vector2.zero; name.rectTransform.anchorMax = Vector2.one;
            name.rectTransform.offsetMin = new Vector2(2, 2); name.rectTransform.offsetMax = new Vector2(-2, -2);
            name.horizontalOverflow = HorizontalWrapMode.Wrap; name.verticalOverflow = VerticalWrapMode.Truncate;
        }

        // Original small silhouette icons. Cached by equipment family; no per-frame textures or external artwork.
        private static Sprite Icon(ItemData item)
        {
            string key = item.IsEquippable ? item.equipSlot.ToString() : item.kind.ToString();
            if (item.grantedWeapon != null && item.grantedWeapon.IsRanged) key = "Crossbow";
            if (Icons.TryGetValue(key, out Sprite cached) && cached != null) return cached;
            var texture = new Texture2D(64, 96, TextureFormat.RGBA32, false) { name = "LootIcon_" + key, filterMode = FilterMode.Bilinear };
            var pixels = new Color[64 * 96];
            for (int y = 0; y < 96; y++) for (int x = 0; x < 64; x++)
            {
                float u = (x - 31.5f) / 32f, v = (y - 47.5f) / 48f;
                float a = Mathf.Abs(u);
                bool fill;
                switch (key)
                {
                    case "Crossbow": fill = (a < .1f && v > -.8f && v < .75f) || (a < .85f && Mathf.Abs(v - (.22f-.35f*u*u)) < .07f) || (a < .7f && Mathf.Abs(v-.02f)<.025f); break;
                    case "Weapon": fill = (a < .12f && v > -.45f && v < .82f - a) || (a < .46f && v > -.5f && v < -.38f) || (a < .085f && v > -.82f && v < -.5f); break;
                    case "OffHand": fill = a < .74f && v < .67f && v > -.83f + a * .62f; break;
                    case "Head": fill = u*u + (v-.12f)*(v-.12f) < .52f && v > -.42f && !(a < .39f && v > -.27f && v < -.1f); break;
                    case "Chest": fill = (a < .48f && v > -.65f && v < .5f) || (a > .3f && a < .83f && v > .12f && v < .65f - a*.22f); fill &= !(a < .18f && v > .34f); break;
                    case "Legs": fill = a < .5f && v < .64f && v > -.72f && (v > .2f || a > .09f); break;
                    case "Feet": fill = (a > .12f && a < .42f && v > -.4f && v < .55f) || (a > .12f && a < .7f && v > -.66f && v < -.3f); break;
                    case "Hands": fill = a < .48f && v > -.58f && v < .55f && (v < .25f || ((x/6)%2 == 0)); break;
                    case "Back": fill = a < .25f + (.7f-v)*.32f && v > -.7f && v < .65f; break;
                    case "RingLeft": case "RingRight": case "Necklace": float d = u*u + v*v; fill = d > .21f && d < .37f || (a < .2f && v > .45f && v < .72f); break;
                    case "Consumable": fill = (u*u/.46f + (v+.18f)*(v+.18f)/.38f < 1f) || (a < .24f && v > .2f && v < .68f); break;
                    case "Currency": fill = u*u/.55f + v*v/.32f < 1f; break;
                    case "AmmoPack": fill = (Mathf.Abs(u-.3f)<.055f || a<.055f || Mathf.Abs(u+.3f)<.055f) && v > -.75f && v < .7f; break;
                    default: fill = a + Mathf.Abs(v) < .85f; break;
                }
                if (fill) pixels[y*64+x] = new Color(.65f + .35f*(1f-a), .65f + .35f*(1f-a), .65f + .35f*(1f-a), 1);
            }
            texture.SetPixels(pixels); texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0,0,64,96), new Vector2(.5f,.5f), 100);
            Icons[key] = sprite; return sprite;
        }
    }
}
