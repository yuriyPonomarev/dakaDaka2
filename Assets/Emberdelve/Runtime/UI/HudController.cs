using System;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.Items;

namespace Emberdelve.UI
{
    /// <summary>
    /// Presentation only. Subscribes to GameEvents, never reads combat state directly, and builds its
    /// widgets in code so the dungeon scene stays small.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HudController : MonoBehaviour
    {
        private const float ToastDuration = 2.6f;
        private const float HitMarkerDuration = 0.28f;
        private const float VignetteDuration = 0.55f;

        private Canvas _canvas;
        private RectTransform _root;
        private RectTransform _inventoryRow;

        private Text _classLabel;
        private Text _objectiveLabel;
        private Text _statusLabel;
        private Text _healthLabel;
        private Image _healthFill;
        private RectTransform _healthFillRect;
        private Text _ammoLabel;
        private Image _ammoFill;
        private RectTransform _ammoFillRect;
        private Text _promptLabel;
        private Image _holdFill;
        private RectTransform _holdFillRect;
        private Text _toastLabel;
        private Text _pauseLabel;
        private Image _vignette;
        private RectTransform _hitMarker;
        private RectTransform _crosshair;

        private float _toastTimer;
        private float _hitMarkerTimer;
        private float _vignetteTimer;
        private float _holdProgress;
        private bool _paused;
        private RunDirector _director;

        private const float BarWidth = 340f;
        private const float BarHeight = 20f;

        private void Awake()
        {
            BuildHud();
        }

        private void OnEnable()
        {
            GameEvents.PlayerHealthChanged += OnPlayerHealth;
            GameEvents.PlayerDamaged += OnPlayerDamaged;
            GameEvents.DamageDealt += OnDamageDealt;
            GameEvents.ItemCollected += OnItemCollected;
            GameEvents.InventoryChanged += OnInventoryChanged;
            GameEvents.AmmoChanged += OnAmmo;
            GameEvents.InteractionPromptChanged += OnPrompt;
            GameEvents.InteractionHoldChanged += OnHold;
            GameEvents.ObjectiveChanged += OnObjective;
            GameEvents.StatusLineChanged += OnStatus;
            GameEvents.ToastRaised += OnToast;
            GameEvents.RunStateChanged += OnRunState;
            GameEvents.PauseChanged += OnPause;
        }

        private void OnDisable()
        {
            GameEvents.PlayerHealthChanged -= OnPlayerHealth;
            GameEvents.PlayerDamaged -= OnPlayerDamaged;
            GameEvents.DamageDealt -= OnDamageDealt;
            GameEvents.ItemCollected -= OnItemCollected;
            GameEvents.InventoryChanged -= OnInventoryChanged;
            GameEvents.AmmoChanged -= OnAmmo;
            GameEvents.InteractionPromptChanged -= OnPrompt;
            GameEvents.InteractionHoldChanged -= OnHold;
            GameEvents.ObjectiveChanged -= OnObjective;
            GameEvents.StatusLineChanged -= OnStatus;
            GameEvents.ToastRaised -= OnToast;
            GameEvents.RunStateChanged -= OnRunState;
            GameEvents.PauseChanged -= OnPause;
        }

        private void Update()
        {
            if (_toastTimer > 0f)
            {
                _toastTimer -= Time.unscaledDeltaTime;

                if (_toastLabel != null)
                {
                    Color color = _toastLabel.color;
                    color.a = Mathf.Clamp01(_toastTimer / 0.6f);
                    _toastLabel.color = color;
                }
            }

            if (_hitMarkerTimer > 0f)
            {
                _hitMarkerTimer -= Time.unscaledDeltaTime;
                if (_hitMarker != null) _hitMarker.gameObject.SetActive(_hitMarkerTimer > 0f);
            }

            if (_vignetteTimer > 0f)
            {
                _vignetteTimer -= Time.unscaledDeltaTime;
                if (_vignette != null) UiFactory.SetAlphaMask(_vignette, 0.42f * Mathf.Clamp01(_vignetteTimer / VignetteDuration));
            }
        }

        private void BuildHud()
        {
            _canvas = UiFactory.EnsureCanvas("Emberdelve_Hud", 10);
            _root = UiFactory.CreateGroup(_canvas.transform, "HudRoot");
            UiFactory.Stretch(_root);

            Color ink = new Color(0.92f, 0.88f, 0.8f, 1f);
            Color dim = new Color(0.75f, 0.7f, 0.62f, 0.75f);

            _classLabel = UiFactory.CreateText(_root, "ClassLabel", "Class", 26, ink, TextAnchor.UpperLeft);
            UiFactory.SetRect(_classLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(420f, 34f), new Vector2(24f, -22f));

            _objectiveLabel = UiFactory.CreateText(_root, "ObjectiveLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_objectiveLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 60f), new Vector2(24f, -58f));

            _statusLabel = UiFactory.CreateText(_root, "StatusLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_statusLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 26f), new Vector2(24f, -118f));

            UiFactory.CreateImage(_root, "HealthBack", new Color(0.08f, 0.07f, 0.06f, 0.85f),
                new Vector2(BarWidth, BarHeight), new Vector2(24f + BarWidth * 0.5f, 44f),
                new Vector2(0f, 0f), new Vector2(0f, 0f));

            _healthFill = UiFactory.CreateImage(_root, "HealthFill", new Color(0.72f, 0.16f, 0.14f, 1f),
                new Vector2(BarWidth, BarHeight), new Vector2(24f, 44f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            _healthFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _healthFillRect = _healthFill.rectTransform;

            _healthLabel = UiFactory.CreateText(_root, "HealthLabel", "100 / 100", 15, ink, TextAnchor.MiddleLeft);
            UiFactory.SetRect(_healthLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(BarWidth, 22f), new Vector2(24f + BarWidth * 0.5f, 74f));

            _ammoLabel = UiFactory.CreateText(_root, "AmmoLabel", "", 20, ink, TextAnchor.MiddleRight);
            UiFactory.SetRect(_ammoLabel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(320f, 28f), new Vector2(-28f, 74f));

            _ammoFill = UiFactory.CreateImage(_root, "AmmoFill", new Color(0.85f, 0.72f, 0.3f, 1f),
                new Vector2(220f, 10f), new Vector2(-24f - 220f, 48f), new Vector2(1f, 0f), new Vector2(1f, 0f));
            _ammoFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _ammoFillRect = _ammoFill.rectTransform;

            _promptLabel = UiFactory.CreateText(_root, "PromptLabel", "", 20, ink, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_promptLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(760f, 32f), new Vector2(0f, 150f));

            UiFactory.CreateImage(_root, "HoldBack", new Color(0.08f, 0.07f, 0.06f, 0.85f),
                new Vector2(300f, 12f), new Vector2(0f, 128f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

            _holdFill = UiFactory.CreateImage(_root, "HoldFill", new Color(0.95f, 0.78f, 0.35f, 1f),
                new Vector2(300f, 12f), new Vector2(-150f, 128f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            _holdFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _holdFillRect = _holdFill.rectTransform;

            _toastLabel = UiFactory.CreateText(_root, "ToastLabel", "", 20, new Color(1f, 0.92f, 0.7f, 0f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(_toastLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(900f, 34f), new Vector2(0f, -140f));

            _pauseLabel = UiFactory.CreateText(_root, "PauseLabel", "PAUSED   -   Esc to resume", 26, ink, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_pauseLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(700f, 40f), new Vector2(0f, 120f));
            _pauseLabel.gameObject.SetActive(false);

            BuildCrosshair(ink);
            BuildInventoryRow();

            _vignette = UiFactory.CreateImage(_root, "DamageVignette", new Color(0.65f, 0.05f, 0.05f, 0f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        }

        private void BuildCrosshair(Color ink)
        {
            _crosshair = UiFactory.CreateGroup(_root, "Crosshair");
            UiFactory.SetRect(_crosshair, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Vector2.zero);

            Color crossColor = new Color(ink.r, ink.g, ink.b, 0.85f);

            UiFactory.CreateImage(_crosshair, "Dot", crossColor, new Vector2(3f, 3f), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            UiFactory.CreateImage(_crosshair, "Top", crossColor, new Vector2(2f, 9f), new Vector2(0f, 12f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            UiFactory.CreateImage(_crosshair, "Bottom", crossColor, new Vector2(2f, 9f), new Vector2(0f, -12f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            UiFactory.CreateImage(_crosshair, "Left", crossColor, new Vector2(9f, 2f), new Vector2(-12f, 0f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            UiFactory.CreateImage(_crosshair, "Right", crossColor, new Vector2(9f, 2f), new Vector2(12f, 0f),
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            _hitMarker = UiFactory.CreateGroup(_crosshair, "HitMarker");
            UiFactory.CreateImage(_hitMarker, "SlashA", new Color(1f, 0.35f, 0.3f, 1f), new Vector2(2f, 18f), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)).rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            UiFactory.CreateImage(_hitMarker, "SlashB", new Color(1f, 0.35f, 0.3f, 1f), new Vector2(2f, 18f), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f)).rectTransform.localEulerAngles = new Vector3(0f, 0f, -45f);
            _hitMarker.gameObject.SetActive(false);
        }

        private void BuildInventoryRow()
        {
            _inventoryRow = UiFactory.CreateGroup(_root, "InventoryRow");
            UiFactory.SetRect(_inventoryRow, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 40f));
            RebuildInventory();
        }

        private void RebuildInventory()
        {
            if (_inventoryRow == null) return;

            for (int i = _inventoryRow.childCount - 1; i >= 0; i--)
            {
                UnityEngine.Object.Destroy(_inventoryRow.GetChild(i).gameObject);
            }

            if (_director == null && !GameServices.TryGet<RunDirector>(out _director)) return;

            Inventory inventory = _director != null ? _director.Inventory : null;
            if (inventory == null) return;

            int capacity = Mathf.Max(1, inventory.Capacity);
            const float chipWidth = 96f;
            const float chipHeight = 44f;
            float total = capacity * chipWidth;

            for (int i = 0; i < capacity; i++)
            {
                float x = -total * 0.5f + chipWidth * (i + 0.5f);

                bool filled = i < inventory.Slots.Count;
                InventorySlot slot = filled ? inventory.Slots[i] : default;

                Color chipColor = new Color(0.07f, 0.06f, 0.055f, filled ? 0.9f : 0.55f);
                Image chip = UiFactory.CreateImage(_inventoryRow, "Slot" + (i + 1), chipColor,
                    new Vector2(chipWidth - 6f, chipHeight), new Vector2(x, 0f),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

                if (filled && slot.Item != null)
                {
                    Color accent = slot.Item.iconColor;
                    UiFactory.CreateImage(chip.transform, "Swatch", accent, new Vector2(10f, 10f),
                        new Vector2(-36f, 8f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                    string label = slot.Item.displayName;
                    if (label.Length > 14) label = label.Substring(0, 14);
                    if (slot.Count > 1) label += " x" + slot.Count;

                    Text text = UiFactory.CreateText(chip.transform, "Label", label, 12,
                        new Color(0.92f, 0.88f, 0.8f, 1f), TextAnchor.MiddleCenter);
                    UiFactory.SetRect(text.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                        new Vector2(chipWidth - 12f, 20f), new Vector2(4f, 6f));

                    string keyHint = i < 4 ? "[" + (i + 1) + "]" : "used by";
                    Text hint = UiFactory.CreateText(chip.transform, "Key", keyHint, 11,
                        new Color(0.7f, 0.66f, 0.58f, 0.9f), TextAnchor.MiddleCenter);
                    UiFactory.SetRect(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                        new Vector2(chipWidth - 12f, 16f), new Vector2(4f, 12f));
                }
                else
                {
                    Text empty = UiFactory.CreateText(chip.transform, "Empty", i < 4 ? "[" + (i + 1) + "]" : "-", 12,
                        new Color(0.5f, 0.47f, 0.42f, 0.8f), TextAnchor.MiddleCenter);
                    UiFactory.Stretch(empty.rectTransform);
                }
            }
        }

        private void OnPlayerHealth(float current, float max)
        {
            if (_healthFill == null) return;

            float fraction = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            _healthFillRect.sizeDelta = new Vector2(BarWidth * fraction, BarHeight);

            if (_healthLabel != null) _healthLabel.text = Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(max);
        }

        private void OnPlayerDamaged(DamageInfo info)
        {
            _vignetteTimer = VignetteDuration;

            string label = info.Blocked ? "Blocked" : "Hit";
            GameEvents.RaiseToast($"{label}  -{Mathf.RoundToInt(info.Amount)} HP");
        }

        private void OnDamageDealt(HitFeedback feedback)
        {
            if (_hitMarker != null)
            {
                _hitMarker.gameObject.SetActive(true);
                _hitMarkerTimer = HitMarkerDuration;
            }
        }

        private void OnItemCollected(ItemData item, int totalCollected)
        {
            GameEvents.RaiseToast($"Took {item.displayName}  (+{item.value} value)");
        }

        private void OnInventoryChanged()
        {
            RebuildInventory();
        }

        private void OnAmmo(int current, int reserve)
        {
            if (_ammoLabel == null) return;

            _ammoLabel.gameObject.SetActive(true);
            _ammoFill.gameObject.SetActive(true);
            _ammoLabel.text = $"Arrows  {current} / {reserve}";

            int magazineCapacity = 1;
            if (_director != null && _director.Player != null)
            {
                RangedCombat ranged = _director.Player.GetComponent<RangedCombat>();
                if (ranged != null && ranged.Weapon != null) magazineCapacity = Mathf.Max(1, ranged.Weapon.magazineSize);
            }

            _ammoFillRect.sizeDelta = new Vector2(220f * Mathf.Clamp01(current / (float)magazineCapacity), 10f);
        }

        private void OnPrompt(string prompt)
        {
            if (_promptLabel == null) return;
            _promptLabel.text = prompt ?? string.Empty;
        }

        private void OnHold(float progress)
        {
            _holdProgress = Mathf.Clamp01(progress);
            if (_holdFill == null) return;

            bool active = _holdProgress > 0.001f;
            _holdFill.gameObject.SetActive(active);
            _holdFillRect.sizeDelta = new Vector2(300f * _holdProgress, 12f);
        }

        private void OnObjective(string objective)
        {
            if (_objectiveLabel != null) _objectiveLabel.text = objective ?? string.Empty;
        }

        private void OnStatus(string status)
        {
            if (_statusLabel != null) _statusLabel.text = status ?? string.Empty;
        }

        private void OnToast(string message)
        {
            if (_toastLabel == null) return;
            _toastLabel.text = message ?? string.Empty;

            Color color = _toastLabel.color;
            color.a = 1f;
            _toastLabel.color = color;

            _toastTimer = ToastDuration;
        }

        private void OnRunState(RunState state)
        {
            if (_root == null) return;

            bool gameplay = state == RunState.Running || state == RunState.Paused || state == RunState.Briefing;
            _root.gameObject.SetActive(gameplay);

            if (state == RunState.Running)
            {
                if (_director == null) GameServices.TryGet<RunDirector>(out _director);
                if (_director != null && _director.Player != null)
                {
                    CharacterClassData classData = _director.Player.Class;
                    if (_classLabel != null && classData != null) _classLabel.text = classData.displayName;

                    bool ranged = _director.Player.GetComponent<RangedCombat>() != null
                                  && _director.Player.GetComponent<RangedCombat>().enabled;
                    if (!ranged && _ammoLabel != null) _ammoLabel.gameObject.SetActive(false);
                    if (!ranged && _ammoFill != null) _ammoFill.gameObject.SetActive(false);
                }
            }
        }

        private void OnPause(bool paused)
        {
            _paused = paused;
            if (_pauseLabel != null) _pauseLabel.gameObject.SetActive(paused);
        }
    }
}
