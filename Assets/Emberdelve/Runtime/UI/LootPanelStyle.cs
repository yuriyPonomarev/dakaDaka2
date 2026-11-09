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
                case EquipmentSlot.Head: return new Vector2(0, 237);
                case EquipmentSlot.Chest: return new Vector2(0, 159);
                case EquipmentSlot.Legs: return new Vector2(0, 67);
                case EquipmentSlot.Hands: return new Vector2(-88, 126);
                case EquipmentSlot.Back: return new Vector2(88, 126);
                case EquipmentSlot.Feet: return new Vector2(88, 54);
                case EquipmentSlot.Necklace: return new Vector2(79, 219);
                case EquipmentSlot.RingLeft: return new Vector2(-88, 65);
                case EquipmentSlot.RingRight: return new Vector2(88, 185);
                case EquipmentSlot.Weapon: return new Vector2(-215, 206);
                case EquipmentSlot.OffHand: return new Vector2(-161, 206);
                case EquipmentSlot.AltWeapon: return new Vector2(161, 206);
                default: return new Vector2(215, 206);
            }
        }

        public static Vector2 SlotSize(EquipmentSlot slot)
        {
            switch (slot)
            {
                case EquipmentSlot.Weapon: case EquipmentSlot.OffHand:
                case EquipmentSlot.AltWeapon: case EquipmentSlot.AltOffHand: return new Vector2(50, 128);
                case EquipmentSlot.Chest: return new Vector2(80, 92);
                case EquipmentSlot.Legs: return new Vector2(72, 82);
                case EquipmentSlot.Necklace: case EquipmentSlot.RingLeft: case EquipmentSlot.RingRight: return new Vector2(34, 34);
                default: return new Vector2(60, 60);
            }
        }

        // Fixed decorative children stay behind the interactive content. No extra canvases or input targets.
        public static void Panel(Image panel)
        {
            panel.color = new Color(.065f, .066f, .063f);
            Frame(panel, new Color(.48f, .47f, .42f));
            for (int i = 0; i < 4; i++)
            {
                float y = i < 2 ? Height / 2 - 5 : -Height / 2 + 5;
                float x = (i % 2 == 0 ? -1 : 1) * (Width / 2 - 5);
                var corner = UiFactory.CreateImage(panel.transform, "SteelCorner", new Color(.55f,.54f,.49f),
                    new Vector2(9,9), new Vector2(x,y), Vector2.one*.5f, Vector2.one*.5f);
                corner.raycastTarget = false; corner.rectTransform.localRotation = Quaternion.Euler(0,0,45);
            }
            foreach (float y in new[] { 282f, 17f, -192f })
            {
                var line = UiFactory.CreateImage(panel.transform, "EngravedDivider", new Color(.26f,.25f,.21f),
                    new Vector2(Width-30,1), new Vector2(0,y), Vector2.one*.5f, Vector2.one*.5f);
                line.raycastTarget = false;
            }
            for (int i = -5; i <= 5; i++)
            {
                var line = UiFactory.CreateImage(panel.transform,"EtchedRays",new Color(.12f,.12f,.105f,.45f),
                    new Vector2(1,230),new Vector2(i*19,150),Vector2.one*.5f,Vector2.one*.5f);
                line.rectTransform.localRotation=Quaternion.Euler(0,0,i*-8);
                line.raycastTarget=false;
            }
        }

        public static void EmptySlot(Image image, EquipmentSlot slot)
        {
            image.color = new Color(.035f,.036f,.034f,.95f);
            var ghost = UiFactory.CreateImage(image.transform,"SlotEtching",new Color(.25f,.25f,.22f,.4f),
                SlotSize(slot)*.6f,Vector2.zero,Vector2.one*.5f,Vector2.one*.5f);
            ghost.sprite = IconForKey(slot == EquipmentSlot.AltWeapon ? "Weapon" : slot == EquipmentSlot.AltOffHand ? "OffHand" : slot.ToString());
            ghost.preserveAspect=true; ghost.raycastTarget=false;
        }

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
            icon.sprite = Icon(item); icon.preserveAspect = showName || image.rectTransform.sizeDelta.y < 100f; icon.raycastTarget = false;
            icon.rectTransform.anchorMin = Vector2.zero; icon.rectTransform.anchorMax = Vector2.one;
            icon.rectTransform.offsetMin = new Vector2(5, 5); icon.rectTransform.offsetMax = new Vector2(-7, -5);
            // Item names belong in the tooltip; tiny labels obscure small inventory footprints.

        }

        // Original small silhouette icons. Cached by equipment family; no per-frame textures or external artwork.
        private static Sprite Icon(ItemData item)
        {
            string key = item.IsEquippable ? item.equipSlot.ToString() : item.kind.ToString();
            if (item.grantedWeapon != null && item.grantedWeapon.IsRanged) key = "Crossbow";
            return IconForKey(key);
        }

        private static Sprite IconForKey(string key)
        {
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
                if (fill)
                {
                    float grain = ((x*17+y*29)%13)/130f;
                    float light = Mathf.Clamp01(.36f + .48f*(1f-a) + .16f*v + grain);
                    if (key == "Weapon" || key == "Crossbow") light *= u < 0 ? .75f : 1.1f;
                    pixels[y*64+x] = new Color(light, light*.97f, light*.88f, 1);
                }
            }
            texture.SetPixels(pixels); texture.Apply(false, true);
            Sprite sprite = Sprite.Create(texture, new Rect(0,0,64,96), new Vector2(.5f,.5f), 100);
            Icons[key] = sprite; return sprite;
        }
    }
}
