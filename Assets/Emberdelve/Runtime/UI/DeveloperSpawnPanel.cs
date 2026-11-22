#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Level;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Emberdelve.UI
{
    internal sealed class DeveloperSpawnPanel
    {
        private enum Tab { Enemies, Chests, Loot }

        private static readonly Color Paper = new Color(.94f, .89f, .8f);
        private static readonly Color ButtonColor = new Color(.22f, .18f, .13f);
        private readonly Transform _hud;
        private readonly UnityEngine.UI.Button[] _tabs = new UnityEngine.UI.Button[3];
        private RectTransform _window;
        private RectTransform _rows;
        private UnityEngine.UI.ScrollRect _scroll;
        private UnityEngine.UI.Text _status;
        private RunDirector _director;
        private DeveloperRaidSpawner _spawner;
        private Tab _tab;

        public DeveloperSpawnPanel(Transform hud) => _hud = hud;

        public bool IsOpen { get; private set; }

        public void Tick(RunDirector director)
        {
            var keyboard = Keyboard.current;
            bool toggle = keyboard != null && keyboard.f8Key.wasPressedThisFrame;
            if (IsOpen)
            {
                if (toggle || (keyboard != null && keyboard.escapeKey.wasPressedThisFrame)
                    || !DeveloperRaidSpawner.CanSpawn(director)) Close();
                return;
            }
            if (!toggle || !DeveloperRaidSpawner.CanSpawn(director) || director.InventoryOpen) return;

            if (_director != director)
            {
                _director = director;
                _spawner = new DeveloperRaidSpawner(director);
            }
            if (_window == null) Build();
            _window.SetAsLastSibling();
            _window.gameObject.SetActive(true);
            IsOpen = true;
            Show(_tab);
            _status.text = "Choose an entry to spawn it in front of you.";
            InventoryInputFocus.Open(this);
            director.SetInventoryOpen(true);
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        public void Close()
        {
            if (!IsOpen) return;
            IsOpen = false;
            if (_window != null) _window.gameObject.SetActive(false);
            InventoryInputFocus.Close(this);
            if (_director != null) _director.SetInventoryOpen(false);
        }

        private void Build()
        {
            _window = UiFactory.CreatePanel(_hud, "DeveloperSpawn", new Color(.045f, .033f, .025f, .97f));
            Rect(_window, 450f, 474f, 0f, 0f, .5f);
            var title = UiFactory.CreateText(_window, "Title", "Developer Spawn", 23, Paper, TextAnchor.MiddleLeft);
            Rect(title.rectTransform, 314f, 35f, -50f, -28f);
            DungeonUiStyle.Heading(title);
            var close = UiFactory.CreateButton(_window, "Close", "Close", Close, new Vector2(74f, 32f), ButtonColor, Paper, 14);
            Rect((RectTransform)close.transform, 74f, 32f, 166f, -28f);
            for (int i = 0; i < _tabs.Length; i++)
            {
                Tab tab = (Tab)i;
                var button = UiFactory.CreateButton(_window, tab + "Tab", tab.ToString(), () => Show(tab),
                    new Vector2(130f, 35f), ButtonColor, Paper, 15);
                Rect((RectTransform)button.transform, 130f, 35f, (i - 1) * 138f, -75f);
                _tabs[i] = button;
            }

            var scrollRoot = UiFactory.CreateGroup(_window, "SpawnList");
            Rect(scrollRoot, 406f, 318f, 0f, -99f, 1f, 1f);
            _scroll = scrollRoot.gameObject.AddComponent<UnityEngine.UI.ScrollRect>();
            var viewport = UiFactory.CreatePanel(scrollRoot, "Viewport", new Color(.09f, .064f, .046f, 1f));
            viewport.gameObject.AddComponent<RectMask2D>();
            _rows = UiFactory.CreateGroup(viewport, "Entries");
            UiFactory.SetRect(_rows, new Vector2(0f, 1f), Vector2.one, Vector2.zero, Vector2.zero, new Vector2(.5f, 1f));
            var column = _rows.gameObject.AddComponent<VerticalLayoutGroup>();
            column.childControlWidth = true;
            column.childControlHeight = true;
            column.childForceExpandWidth = true;
            column.childForceExpandHeight = false;
            column.spacing = 5f;
            column.padding = new RectOffset(4, 4, 4, 4);
            var fitter = _rows.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            _scroll.viewport = viewport;
            _scroll.content = _rows;
            _scroll.horizontal = false;
            _scroll.movementType = UnityEngine.UI.ScrollRect.MovementType.Clamped;
            _scroll.scrollSensitivity = 23f;

            _status = UiFactory.CreateText(_window, "Status", "", 14, Paper);
            _status.horizontalOverflow = HorizontalWrapMode.Wrap;
            Rect(_status.rectTransform, 402f, 43f, 0f, -442f);
        }

        private void Show(Tab tab)
        {
            _tab = tab;
            for (int i = 0; i < _tabs.Length; i++)
                _tabs[i].interactable = i != (int)tab;
            for (int i = _rows.childCount - 1; i >= 0; i--)
            {
                Transform child = _rows.GetChild(i);
                child.gameObject.SetActive(false);
                child.SetParent(null, false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
            var layout = _director.Layout;
            switch (tab)
            {
                case Tab.Enemies:
                    var seen = new HashSet<string>();
                    foreach (var spec in layout.enemySpawns)
                    {
                        var enemy = spec?.enemy;
                        if (enemy == null || !seen.Add(enemy.enemyId)) continue;
                        AddEntry(enemy.displayName, () => _spawner.SpawnEnemy(enemy, enemy.isElite ? "Warrior" : "Minion"));
                        if (!enemy.isElite)
                            AddEntry(enemy.displayName + " (Rogue)", () => _spawner.SpawnEnemy(enemy, "Rogue"));
                    }
                    break;
                case Tab.Chests:
                    var variants = new HashSet<ChestVariant>();
                    var visuals = ChestVisualSet.Load();
                    foreach (var spec in layout.chestSpawns)
                    {
                        if (spec?.lootTable == null || !variants.Add(spec.variant)) continue;
                        AddEntry(spec.variant + " chest", () => _spawner.SpawnChest(spec, false));
                        if (visuals != null && visuals.Find(spec.variant)?.alternateModel != null)
                            AddEntry(spec.variant + " chest (alternate)", () => _spawner.SpawnChest(spec, true));
                    }
                    break;
                case Tab.Loot:
                    var catalog = _director.Config.itemCatalog;
                    if (catalog != null)
                        foreach (var item in catalog.Items)
                            if (item != null) AddEntry(item.displayName, () => _spawner.SpawnLoot(item));
                    break;
            }
            LayoutRebuilder.ForceRebuildLayoutImmediate(_rows);
            _scroll.StopMovement();
            _scroll.verticalNormalizedPosition = 1f;
        }

        private void AddEntry(string label, Func<string> spawn)
        {
            var row = UiFactory.CreateButton(_rows, label, label, () => _status.text = spawn(),
                new Vector2(398f, 34f), ButtonColor, Paper, 15);
            row.gameObject.AddComponent<LayoutElement>().preferredHeight = 34f;
        }

        private static void Rect(RectTransform rect, float width, float height, float x, float y,
            float anchorY = 1f, float pivotY = .5f) =>
            UiFactory.SetRect(rect, new Vector2(.5f, anchorY), new Vector2(.5f, anchorY),
                new Vector2(width, height), new Vector2(x, y), new Vector2(.5f, pivotY));
    }
}
#endif
