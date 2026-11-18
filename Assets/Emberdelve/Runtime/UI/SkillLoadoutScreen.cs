using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Emberdelve.Data;
using Emberdelve.Skills;

namespace Emberdelve.UI
{
    /// <summary>
    /// Lobby screen for the skill kit: two active and two passive sockets, and the list of skills the class can take. A skill is dragged into
    /// a socket of its own type, whether the socket is empty or not; the skill that was there goes back to the list. A kit skill dragged onto
    /// another socket of the same type swaps places. The choice is saved per class and applied when the raid starts.
    /// </summary>
    public sealed class SkillLoadoutScreen : MonoBehaviour
    {
        private const float TileSize = 84f, SlotSize = 112f;
        private static readonly Color Ink = new Color(.045f, .04f, .036f, 1f);
        private static readonly Color TextColor = new Color(.91f, .87f, .79f);
        private static readonly Color Muted = new Color(.67f, .65f, .6f);
        private static readonly Color Gold = new Color(.89f, .77f, .51f);

        private sealed class Tile { public RectTransform Rect; public SkillData Skill; public int SlotIndex = -1; public SkillSlot Kind; }
        private sealed class Socket { public RectTransform Rect; public SkillSlot Kind; public int Index; }

        private RectTransform _root, _poolRoot, _slotRoot;
        private Image _ghost;
        private Text _title, _name, _summary, _description, _status;
        private CharacterClassData _class;
        private readonly SkillData[] _actives = new SkillData[SkillLoadoutStore.ActiveSlots];
        private readonly SkillData[] _passives = new SkillData[SkillLoadoutStore.PassiveSlots];
        private readonly List<Tile> _tiles = new List<Tile>();
        private readonly List<Socket> _sockets = new List<Socket>();
        private Action _onClosed;
        private Tile _dragging;
        private Vector2 _dragStart;
        private SkillData _shown;

        public bool IsOpen => _root != null && _root.gameObject.activeSelf;

        public void Open(CharacterClassData cls, Action onClosed)
        {
            if (cls == null) return;
            Build();
            _class = cls;
            _onClosed = onClosed;
            SkillLoadoutStore.Get(cls, out SkillData[] actives, out SkillData[] passives);
            Array.Copy(actives, _actives, _actives.Length);
            Array.Copy(passives, _passives, _passives.Length);
            _title.text = "PERKS & SKILLS  /  " + cls.displayName.ToUpperInvariant();
            _status.text = "Drag a skill into a socket. A skill that was there returns to the list.";
            _status.color = Muted;
            _dragging = null;
            _ghost.gameObject.SetActive(false);
            Show(null);
            Rebuild();
            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
        }

        public void Close()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _dragging = null;
            Action done = _onClosed;
            _onClosed = null;
            done?.Invoke();
        }

        private void Update()
        {
            if (!IsOpen) return;
            _root.localScale = Vector3.one * Mathf.Min(1f, Screen.width / 1440f, Screen.height / 800f);
            Mouse mouse = Mouse.current;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) { Close(); return; }
            if (mouse == null) return;
            Vector2 pointer = mouse.position.ReadValue();

            if (_dragging != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(_root, pointer, null, out Vector2 local))
                _ghost.rectTransform.anchoredPosition = local;

            if (_dragging == null)
            {
                Tile hover = TileAt(pointer);
                if (hover != null) Show(hover.Skill);
                if (mouse.leftButton.wasPressedThisFrame && hover != null)
                {
                    _dragging = hover;
                    _dragStart = pointer;
                    BeginGhost(hover.Skill);
                }
            }
            else if (mouse.leftButton.wasReleasedThisFrame)
            {
                Tile source = _dragging;
                _dragging = null;
                _ghost.gameObject.SetActive(false);
                if ((pointer - _dragStart).sqrMagnitude > 16f) Drop(source, pointer);
                else Show(source.Skill);
            }
        }

        private void Drop(Tile source, Vector2 pointer)
        {
            Socket target = null;
            for (int i = 0; i < _sockets.Count; i++)
                if (RectTransformUtility.RectangleContainsScreenPoint(_sockets[i].Rect, pointer, null)) { target = _sockets[i]; break; }
            if (target == null) return;

            if (target.Kind != source.Kind)
            {
                SetStatus((source.Kind == SkillSlot.Active ? "Active skills go into the active sockets." : "Passives go into the passive sockets."), true);
                return;
            }

            SkillData[] slots = source.Kind == SkillSlot.Active ? _actives : _passives;
            SkillData replaced = slots[target.Index];
            if (source.SlotIndex == target.Index) return;

            if (source.SlotIndex >= 0) slots[source.SlotIndex] = replaced; // socket to socket: swap
            slots[target.Index] = source.Skill;
            SetStatus(replaced != null && source.SlotIndex < 0
                ? source.Skill.displayName + " replaces " + replaced.displayName + "; " + replaced.displayName + " is back in the list."
                : source.Skill.displayName + " equipped.", false);
            SkillLoadoutStore.Save(_class, _actives, _passives);
            Rebuild();
        }

        private void Reset()
        {
            if (_class == null) return;
            SkillLoadoutStore.Reset(_class);
            SkillLoadoutStore.Get(_class, out SkillData[] actives, out SkillData[] passives);
            Array.Copy(actives, _actives, _actives.Length);
            Array.Copy(passives, _passives, _passives.Length);
            SetStatus("Back to the class kit.", false);
            Rebuild();
        }

        // ---- layout -------------------------------------------------------------------------------------

        private void Build()
        {
            if (_root != null) return;
            Canvas canvas = UiFactory.EnsureCanvas("Emberdelve_Skills", 36);
            _root = UiFactory.CreateGroup(canvas.transform, "SkillLoadout");
            _root.sizeDelta = new Vector2(1440, 800);
            UiFactory.CreateImage(_root, "Backdrop", new Color(.018f, .018f, .016f, 1f), new Vector2(6000, 4000), Vector2.zero, Vector2.one * .5f, Vector2.one * .5f);

            _title = Label(_root, "Title", "PERKS & SKILLS", 30, Gold, new Vector2(0, 348), new Vector2(1100, 46));
            DungeonUiStyle.Heading(_title);

            RectTransform left = Panel("AvailablePanel", new Vector2(-300, 20), new Vector2(660, 560));
            RectTransform right = Panel("SocketPanel", new Vector2(390, 20), new Vector2(430, 560));
            Label(left, "Heading", "AVAILABLE SKILLS", 20, Gold, new Vector2(0, 252), new Vector2(620, 32));
            Label(right, "Heading", "EQUIPPED", 20, Gold, new Vector2(0, 252), new Vector2(400, 32));
            _poolRoot = UiFactory.CreateGroup(left, "Pool");
            _slotRoot = UiFactory.CreateGroup(right, "Sockets");

            RectTransform detail = Panel("DetailPanel", new Vector2(0, -332), new Vector2(1320, 116));
            _name = Label(detail, "Name", "", 22, TextColor, new Vector2(-330, 32), new Vector2(620, 30));
            _name.alignment = TextAnchor.MiddleLeft;
            _summary = Label(detail, "Summary", "", 15, Gold, new Vector2(-330, 4), new Vector2(620, 24));
            _summary.alignment = TextAnchor.MiddleLeft;
            _description = Label(detail, "Description", "", 16, TextColor, new Vector2(-330, -30), new Vector2(620, 48));
            _description.alignment = TextAnchor.UpperLeft;
            _description.horizontalOverflow = HorizontalWrapMode.Wrap;
            _status = Label(detail, "Status", "", 15, Muted, new Vector2(330, 14), new Vector2(560, 60));
            _status.alignment = TextAnchor.MiddleLeft;
            _status.horizontalOverflow = HorizontalWrapMode.Wrap;
            Button(detail, "Reset", "RESET TO CLASS KIT", new Vector2(190, -34), new Vector2(250, 34), Reset);
            Button(detail, "Done", "DONE", new Vector2(480, -34), new Vector2(200, 34), Close);

            _ghost = UiFactory.CreateImage(_root, "DragGhost", new Color(1, 1, 1, .85f), new Vector2(TileSize, TileSize), Vector2.zero, Vector2.one * .5f, Vector2.one * .5f);
            _ghost.raycastTarget = false;
            _ghost.gameObject.SetActive(false);
            _root.gameObject.SetActive(false);
        }

        private void Rebuild()
        {
            foreach (Transform child in _poolRoot) Destroy(child.gameObject);
            foreach (Transform child in _slotRoot) Destroy(child.gameObject);
            _tiles.Clear();
            _sockets.Clear();

            BuildPool(SkillSlot.Active, "ACTIVE", 168f, _actives);
            BuildPool(SkillSlot.Passive, "PASSIVE", -52f, _passives);
            BuildSockets(SkillSlot.Active, "ACTIVE SKILLS", 150f, _actives, new[] { "Q", "E" });
            BuildSockets(SkillSlot.Passive, "PASSIVE SKILLS", -80f, _passives, new[] { "1", "2" });
            _ghost.transform.SetAsLastSibling();
        }

        private void BuildPool(SkillSlot kind, string label, float top, SkillData[] equipped)
        {
            var heading = Label(_poolRoot, label + "Heading", label, 15, Muted, new Vector2(-250, top + 26), new Vector2(120, 24));
            heading.alignment = TextAnchor.MiddleLeft;
            var available = new List<SkillData>();
            foreach (SkillData skill in SkillLoadoutStore.Pool(_class, kind))
                if (Array.IndexOf(equipped, skill) < 0) available.Add(skill);
            if (available.Count == 0)
            {
                var none = Label(_poolRoot, label + "None", "Nothing else available.", 14, Muted, new Vector2(0, top - 44), new Vector2(560, 28));
                none.alignment = TextAnchor.MiddleCenter;
            }
            const int perRow = 5;
            for (int i = 0; i < available.Count; i++)
            {
                float x = -265f + (i % perRow) * 130f;
                float y = top - 44f - (i / perRow) * 124f;
                RectTransform rect = Frame(_poolRoot, "Skill_" + available[i].skillId, available[i], new Vector2(x, y), TileSize);
                Label(rect, "Name", available[i].displayName, 13, TextColor, new Vector2(0, -TileSize * .5f - 14), new Vector2(124, 24));
                _tiles.Add(new Tile { Rect = rect, Skill = available[i], Kind = kind });
            }
        }

        private void BuildSockets(SkillSlot kind, string label, float top, SkillData[] slots, string[] keys)
        {
            var heading = Label(_slotRoot, label + "Heading", label, 15, Muted, new Vector2(0, top + 52), new Vector2(380, 24));
            for (int i = 0; i < slots.Length; i++)
            {
                float x = -86f + i * 172f;
                RectTransform rect = Frame(_slotRoot, "Socket_" + kind + i, slots[i], new Vector2(x, top - 34), SlotSize, true);
                Label(rect, "Key", keys[i], 15, Gold, new Vector2(-SlotSize * .5f + 14, SlotSize * .5f - 14), new Vector2(26, 24));
                if (slots[i] != null)
                    Label(rect, "Name", slots[i].displayName, 13, TextColor, new Vector2(0, -SlotSize * .5f - 14), new Vector2(166, 24));
                else
                    Label(rect, "Empty", "EMPTY", 14, Muted, Vector2.zero, new Vector2(100, 24));
                _sockets.Add(new Socket { Rect = rect, Kind = kind, Index = i });
                if (slots[i] != null) _tiles.Add(new Tile { Rect = rect, Skill = slots[i], Kind = kind, SlotIndex = i });
            }
        }

        private RectTransform Frame(RectTransform parent, string name, SkillData skill, Vector2 position, float size, bool socket = false)
        {
            Image frame = UiFactory.CreateImage(parent, name, socket ? new Color(.09f, .08f, .07f, 1f) : Ink, new Vector2(size, size), position, Vector2.one * .5f, Vector2.one * .5f);
            if (socket) DungeonUiStyle.Surface(frame);
            if (skill != null)
            {
                Image icon = UiFactory.CreateImage(frame.transform, "Icon", Color.white, new Vector2(size - 10, size - 10), Vector2.zero, Vector2.one * .5f, Vector2.one * .5f);
                icon.raycastTarget = false;
                Sprite sprite = SkillIconAtlas.Get(skill.skillId);
                if (sprite != null) icon.sprite = sprite;
                else icon.color = skill.iconColor;
            }
            return frame.rectTransform;
        }

        // ---- helpers --------------------------------------------------------------------------------------

        private Tile TileAt(Vector2 pointer)
        {
            for (int i = _tiles.Count - 1; i >= 0; i--)
                if (RectTransformUtility.RectangleContainsScreenPoint(_tiles[i].Rect, pointer, null)) return _tiles[i];
            return null;
        }

        private void BeginGhost(SkillData skill)
        {
            Sprite sprite = SkillIconAtlas.Get(skill.skillId);
            _ghost.sprite = sprite;
            _ghost.color = sprite != null ? new Color(1, 1, 1, .85f) : skill.iconColor;
            _ghost.gameObject.SetActive(true);
            _ghost.transform.SetAsLastSibling();
            Show(skill);
        }

        private void Show(SkillData skill)
        {
            if (skill == _shown && skill != null) return;
            _shown = skill;
            if (skill == null) { _name.text = ""; _summary.text = ""; _description.text = "Hover a skill to read it."; return; }
            _name.text = skill.displayName + (skill.IsPassive ? "   (passive)" : "   (active)");
            _summary.text = skill.ShortSummary;
            _description.text = skill.description;
        }

        private void SetStatus(string text, bool warning)
        {
            _status.text = text;
            _status.color = warning ? new Color(1f, .6f, .42f) : Muted;
        }

        private RectTransform Panel(string name, Vector2 position, Vector2 size)
        {
            Image panel = UiFactory.CreateImage(_root, name, new Color(.06f, .054f, .046f, .96f), size, position, Vector2.one * .5f, Vector2.one * .5f);
            DungeonUiStyle.Surface(panel);
            return panel.rectTransform;
        }

        private static Text Label(Transform parent, string name, string text, int size, Color color, Vector2 position, Vector2 box)
        {
            Text label = UiFactory.CreateText(parent, name, text, size, color, TextAnchor.MiddleCenter);
            label.rectTransform.anchoredPosition = position;
            label.rectTransform.sizeDelta = box;
            label.raycastTarget = false;
            return label;
        }

        private static UnityEngine.UI.Button Button(Transform parent, string name, string text, Vector2 position, Vector2 size, UnityEngine.Events.UnityAction action)
        {
            UnityEngine.UI.Button button = UiFactory.CreateButton(parent, name, text, action, size, new Color(.18f, .16f, .12f), TextColor, 15);
            ((RectTransform)button.transform).anchoredPosition = position;
            return button;
        }
    }
}
