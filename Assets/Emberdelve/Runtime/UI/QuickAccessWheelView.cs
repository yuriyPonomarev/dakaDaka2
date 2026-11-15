using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using UnityEngine;

namespace Emberdelve.UI
{
    /// <summary>One three-sector overlay, fed by the same controller on the host and owning client.</summary>
    public sealed class QuickAccessWheelView : MonoBehaviour
    {
        private RectTransform _wheel;
        private UnityEngine.UI.Text _title, _hint, _clientStrip;
        private readonly WheelSectorGraphic[] _sectors = new WheelSectorGraphic[3];
        private readonly UnityEngine.UI.Image[] _cards = new UnityEngine.UI.Image[3];
        private readonly UnityEngine.UI.Text[] _names = new UnityEngine.UI.Text[3];
        private readonly ItemData[] _items = new ItemData[3];
        private bool _wasOpen;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Object.FindFirstObjectByType<QuickAccessWheelView>() != null) return;
            var canvas = UiFactory.EnsureCanvas("QuickAccessWheelCanvas", 160);
            Object.DontDestroyOnLoad(canvas.gameObject);
            canvas.gameObject.AddComponent<QuickAccessWheelView>();
        }
        private void Awake()
        {
            var scaler = gameObject.GetComponent<UnityEngine.UI.CanvasScaler>();
            if (scaler == null) scaler = gameObject.AddComponent<UnityEngine.UI.CanvasScaler>();
            scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1280, 720); scaler.matchWidthOrHeight = .5f;
            _wheel = UiFactory.CreateGroup(transform, "UtilityWheel");
            UiFactory.SetRect(_wheel, Vector2.one * .5f, Vector2.one * .5f, new Vector2(460, 460), Vector2.zero);
            for (int i = 0; i < 3; i++)
            {
                var sector = new GameObject("Sector" + i, typeof(RectTransform), typeof(CanvasRenderer), typeof(WheelSectorGraphic));
                sector.transform.SetParent(_wheel, false);
                var graphic = sector.GetComponent<WheelSectorGraphic>(); graphic.Sector = i; graphic.raycastTarget = false;
                UiFactory.SetRect(graphic.rectTransform, Vector2.one * .5f, Vector2.one * .5f, new Vector2(440, 440), Vector2.zero);
                _sectors[i] = graphic;
            }
            for (int i = 0; i < 3; i++)
            {
                float angle = i * 120f * Mathf.Deg2Rad;
                Vector2 position = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle)) * 126f;
                _cards[i] = UiFactory.CreateImage(_wheel, "Item" + i, new Color(.055f,.05f,.045f),
                    new Vector2(60, 70), position + Vector2.up * 9f, Vector2.one * .5f, Vector2.one * .5f);
                _cards[i].raycastTarget = false;
                _names[i] = UiFactory.CreateText(_wheel, "ItemName" + i, "", 15, new Color(.93f,.87f,.74f));
                _names[i].raycastTarget = false;
                UiFactory.SetRect(_names[i].rectTransform, Vector2.one * .5f, Vector2.one * .5f, new Vector2(156, 42), position + Vector2.down * 49f);
            }
            _title = UiFactory.CreateText(_wheel, "WheelTitle", "", 20, new Color(1f,.87f,.59f), TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.SetRect(_title.rectTransform, Vector2.one * .5f, Vector2.one * .5f, new Vector2(140, 60), Vector2.zero);
            _title.raycastTarget = false;
            _hint = UiFactory.CreateText(_wheel, "WheelHint", "", 16, new Color(.94f,.89f,.79f));
            UiFactory.SetRect(_hint.rectTransform, Vector2.one * .5f, Vector2.one * .5f, new Vector2(650, 48), Vector2.down * 235f);
            _hint.raycastTarget = false;
            _clientStrip = UiFactory.CreateText(transform, "QuickWheelSummary", "", 14, new Color(.91f,.85f,.73f));
            UiFactory.SetRect(_clientStrip.rectTransform, new Vector2(.5f,0), new Vector2(.5f,0), new Vector2(540, 52), new Vector2(0,50));
            _clientStrip.raycastTarget = false;
            _wheel.gameObject.SetActive(false);
        }
        private void LateUpdate()
        {
            var controller = HotbarController.Local;
            var actor = controller != null ? controller.GetComponent<Emberdelve.Player.PlayerRig>() : null;
            bool available = controller != null && controller.isActiveAndEnabled && controller.Bag != null
                && actor != null && actor.IsAlive && actor.IsInPlay;
            bool open = available && controller.WheelOpen;
            _wheel.gameObject.SetActive(open);
            bool showStrip = NetRole.IsClientOnly && available && !InventoryInputFocus.OwnsEscape;
            _clientStrip.gameObject.SetActive(showStrip);
            if (showStrip)
            {
                var bag = controller.Bag;
                string held = bag.HeldItem != null ? bag.HeldItem.displayName : "weapon";
                _clientStrip.text = "1 / 2  Weapons     3  Quick wheel I     4  Quick wheel II\nIn hand: " + held + "   |   LMB: use held item";
            }
            if (!open) { _wasOpen = false; return; }
            _title.text = (controller.WheelGroup + 3) + "\nQUICK " + (controller.WheelGroup + 1);
            _hint.text = controller.Assigning
                ? "Choose one of three slots. Release " + (controller.WheelGroup + 3) + " to move into the belt. Esc cancels."
                : "Choose an item. Release " + (controller.WheelGroup + 3) + " to hold. LMB uses it; 1 / 2 return to weapons.";
            for (int i = 0; i < 3; i++)
            {
                int slot = controller.WheelGroup * 3 + i;
                bool found = controller.Bag.Hotbar.TryGetStack(slot, out var stack);
                ItemData item = found ? stack.Item : null;
                var color = controller.WheelSelection == i ? new Color(.30f,.23f,.12f,.97f) : new Color(.055f,.055f,.05f,.95f);
                if (_sectors[i].color != color) _sectors[i].color = color;
                if (!_wasOpen || _items[i] != item)
                {
                    for (int c = _cards[i].transform.childCount - 1; c >= 0; c--) Object.Destroy(_cards[i].transform.GetChild(c).gameObject);
                    var outline = _cards[i].GetComponent<UnityEngine.UI.Outline>(); if (outline != null) Object.Destroy(outline);
                    _items[i] = item;
                    LootPanelStyle.Decorate(_cards[i], item, found ? stack.Instance : null);
                }
                _names[i].text = (i + 1) + "  " + (item == null ? "Empty" : item.displayName + (item.healAmount > 0 ? " " + stack.Count + "/5" : " x" + stack.Count));
            }
            _wasOpen = true;
        }
    }

    public sealed class WheelSectorGraphic : UnityEngine.UI.MaskableGraphic
    {
        public int Sector;
        protected override void OnPopulateMesh(UnityEngine.UI.VertexHelper helper)
        {
            helper.Clear();
            const int steps = 32;
            float inner = 49f, outer = 220f;
            for (int i = 0; i <= steps; i++)
            {
                float angle = (Sector * 120f - 59f + i * (118f / steps)) * Mathf.Deg2Rad;
                Vector2 direction = new Vector2(Mathf.Sin(angle), Mathf.Cos(angle));
                helper.AddVert(direction * inner, color, Vector2.zero);
                helper.AddVert(direction * outer, color, Vector2.one);
                if (i == 0) continue;
                int index = i * 2;
                helper.AddTriangle(index - 2, index - 1, index);
                helper.AddTriangle(index, index - 1, index + 1);
            }
        }
    }
}
