using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Emberdelve.Core;
using Emberdelve.Combat;
using Emberdelve.Data;
using Emberdelve.Flow;
using Emberdelve.InputLayer;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Skills;

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
        private Text _bagSummaryLabel;

        private Text _classLabel;
        private Text _weaponLabel;
        private Text _objectiveLabel;
        private Text _statusLabel;
        private Text _healthLabel;
        private Image _healthFill;
        private RectTransform _healthFillRect;
        private Text _ammoLabel;
        private Image _ammoFill;
        private RectTransform _ammoFillRect;
        private Text _manaLabel;
        private Image _manaFill;
        private RectTransform _manaFillRect;
        private Text _staminaLabel;
        private Image _staminaFill;
        private RectTransform _staminaFillRect;
        private int _staminaShownCurrent = -1;
        private int _staminaShownMax = -1;
        private Text _kitLabel;
        private readonly SkillSlotWidget[] _skillSlots = new SkillSlotWidget[Skills.SkillCaster.ActiveSlotCapacity];
        private Text _promptLabel;
        private Image _holdFill;
        private Image _holdBack;
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
        private InventoryScreenController _inventoryScreen;

        /// <summary>Widgets of one skill slot. Built once, then updated from SkillSnapshot values.</summary>
        private sealed class SkillSlotWidget
        {
            public Image Background;
            public Image CooldownFill;
            public Text KeyLabel;
            public Text NameLabel;
            public Text CostLabel;
            public Text CooldownLabel;
            public Text ReasonLabel;
            public float RejectTimer;
        }

        private const float BarWidth = 340f;
        private const float BarHeight = 20f;
        private const float ManaBarHeight = 14f;
        private const float SkillRejectFlash = 1.6f;

        /// <summary>
        /// DEPRECATED (D1b, user decision): stamina no longer gates or drains block or sprint, so its bar is not built.
        /// The pool, its derived stats and the bar code below stay (dormant) so existing data and gates keep their
        /// meaning; set this true only to bring the bar back.
        /// </summary>
        public const bool ShowStaminaBar = false;

        // The stamina bar is the mana bar's shape in the free band to the right of the mana readout, so the
        // three pools read as one stack of bars. Measured in the same 1280x720 image coordinates the hotbar
        // comment below uses: mana bar x 24..364 y 695..709, mana label x 378..598 y 695..715, hotbar chips
        // x 430..850 y 648..692, ammo fill x 1036..1256 y 667..677, ammo label x 932..1252 y 632..660.
        // Stamina bar x 610..830 y 695..709 and label x 844..1034 y 695..715 sit in the empty stretch of that
        // bottom row: 12px right of the mana label, 3px below the hotbar chips, 22px left of the ammo fill,
        // and every one of them is corner-anchored with a fixed inset, so the same clearances hold at 1920x1080
        // (bar x 930..1150, label x 1164..1354, against chips that start at x 1250 and an ammo fill at x 1676).
        private const float StaminaBarWidth = 220f;
        private const float StaminaBarLeft = 610f;
        private const float StaminaLabelWidth = 190f;

        // The raid hotbar. HotbarController.Update loops i = 0..3 and calls Inventory.TryUseSlot(i), so 1-4
        // address the first four stacks the bag currently holds, never four capacity cells and never a
        // filtered subset of them. The strip therefore draws exactly those four stacks.
        private const int HotbarCapacity = 4;
        private const float HotbarChipWidth = 96f;
        private const float HotbarChipHeight = 44f;
        private const float HotbarChipGap = 12f;

        // Offsets of each widget's pivot above the window's bottom edge, which is what their bottom-centre
        // anchors are relative to. See RebuildInventory for the neighbour rectangles they were measured against.
        private const float HotbarChipCentre = 50f;
        private const float BagSummaryCentre = 90f;
        private const float BagSummaryWidth = 520f;
        private const float BagSummaryHeight = 22f;

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
            GameEvents.PlayerManaChanged += OnPlayerMana;
            GameEvents.SkillStateChanged += OnSkillState;
            GameEvents.SkillCastRejected += OnSkillRejected;
            GameEvents.InteractionPromptChanged += OnPrompt;
            GameEvents.InteractionHoldChanged += OnHold;
            GameEvents.ObjectiveChanged += OnObjective;
            GameEvents.StatusLineChanged += OnStatus;
            GameEvents.ActiveWeaponChanged += OnActiveWeapon;
            GameEvents.ToastRaised += OnToast;
            GameEvents.RunStateChanged += OnRunState;
            GameEvents.PlayerRunStateChanged += OnPlayerRunState;
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
            GameEvents.PlayerManaChanged -= OnPlayerMana;
            GameEvents.SkillStateChanged -= OnSkillState;
            GameEvents.SkillCastRejected -= OnSkillRejected;
            GameEvents.InteractionPromptChanged -= OnPrompt;
            GameEvents.InteractionHoldChanged -= OnHold;
            GameEvents.ObjectiveChanged -= OnObjective;
            GameEvents.StatusLineChanged -= OnStatus;
            GameEvents.ActiveWeaponChanged -= OnActiveWeapon;
            GameEvents.ToastRaised -= OnToast;
            GameEvents.RunStateChanged -= OnRunState;
            GameEvents.PlayerRunStateChanged -= OnPlayerRunState;
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

            for (int i = 0; i < _skillSlots.Length; i++)
            {
                SkillSlotWidget widget = _skillSlots[i];
                if (widget == null || widget.RejectTimer <= 0f) continue;

                widget.RejectTimer -= Time.unscaledDeltaTime;
                if (widget.ReasonLabel != null)
                {
                    widget.ReasonLabel.gameObject.SetActive(widget.RejectTimer > 0f);
                    Color color = widget.ReasonLabel.color;
                    color.a = Mathf.Clamp01(widget.RejectTimer / 0.5f);
                    widget.ReasonLabel.color = color;
                }
            }

            UpdateInventoryScreen();
            UpdateStamina();
        }

        private void UpdateInventoryScreen()
        {
            if (_inventoryScreen == null) return;

            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            PlayerRig rig = _director != null ? _director.Player : null;
            EmberdelveInput input = rig != null ? rig.Input : null;
            bool running = _director == null || (_director.State == RunState.Running && _director.LocalPlayerState == RunState.Running);

            if (input != null && running && input.InventoryPressed)
            {
                _inventoryScreen.Toggle();

                if (_director != null) _director.SetInventoryOpen(_inventoryScreen.IsOpen);
            }

            _inventoryScreen.HandlePointer();
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
                new Vector2(420f, 34f), new Vector2(24f, -22f), new Vector2(0f, 1f));

            _objectiveLabel = UiFactory.CreateText(_root, "ObjectiveLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_objectiveLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 60f), new Vector2(24f, -58f), new Vector2(0f, 1f));

            _statusLabel = UiFactory.CreateText(_root, "StatusLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_statusLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 26f), new Vector2(24f, -118f), new Vector2(0f, 1f));

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

            _weaponLabel = UiFactory.CreateText(_root, "WeaponLabel", "", 18, dim, TextAnchor.MiddleLeft);
            UiFactory.SetRect(_weaponLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(BarWidth, 26f), new Vector2(24f + BarWidth * 0.5f, 104f));

            _ammoLabel = UiFactory.CreateText(_root, "AmmoLabel", "", 20, ink, TextAnchor.MiddleRight);
            UiFactory.SetRect(_ammoLabel.rectTransform, new Vector2(1f, 0f), new Vector2(1f, 0f),
                new Vector2(320f, 28f), new Vector2(-28f, 74f), new Vector2(1f, 0.5f));

            _ammoFill = UiFactory.CreateImage(_root, "AmmoFill", new Color(0.85f, 0.72f, 0.3f, 1f),
                new Vector2(220f, 10f), new Vector2(-24f - 220f, 48f), new Vector2(1f, 0f), new Vector2(1f, 0f));
            _ammoFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _ammoFillRect = _ammoFill.rectTransform;

            UiFactory.CreateImage(_root, "ManaBack", new Color(0.07f, 0.07f, 0.09f, 0.85f),
                new Vector2(BarWidth, ManaBarHeight), new Vector2(24f + BarWidth * 0.5f, 18f),
                new Vector2(0f, 0f), new Vector2(0f, 0f));

            _manaFill = UiFactory.CreateImage(_root, "ManaFill", new Color(0.24f, 0.45f, 0.78f, 1f),
                new Vector2(BarWidth, ManaBarHeight), new Vector2(24f, 18f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            _manaFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _manaFillRect = _manaFill.rectTransform;

            _manaLabel = UiFactory.CreateText(_root, "ManaLabel", "0 / 0", 14, dim, TextAnchor.MiddleLeft);
            UiFactory.SetRect(_manaLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(220f, 20f), new Vector2(24f + BarWidth + 14f, 15f), new Vector2(0f, 0.5f));

            // Stamina, drawn the way health and mana are drawn: a dark back, a filled bar that shrinks from
            // the left, and a current / maximum line beside it. The colour is the one the kit already reads
            // as "endurance" - a green against the red health bar and the blue mana bar - and the numbers
            // come from the live pool, not from a ceiling the HUD invented.
            if (ShowStaminaBar)
            {
                UiFactory.CreateImage(_root, "StaminaBack", new Color(0.06f, 0.085f, 0.07f, 0.85f),
                    new Vector2(StaminaBarWidth, ManaBarHeight), new Vector2(StaminaBarLeft + StaminaBarWidth * 0.5f, 18f),
                    new Vector2(0f, 0f), new Vector2(0f, 0f));

                _staminaFill = UiFactory.CreateImage(_root, "StaminaFill", new Color(0.34f, 0.62f, 0.36f, 1f),
                    new Vector2(StaminaBarWidth, ManaBarHeight), new Vector2(StaminaBarLeft, 18f),
                    new Vector2(0f, 0f), new Vector2(0f, 0f));
                _staminaFill.rectTransform.pivot = new Vector2(0f, 0.5f);
                _staminaFillRect = _staminaFill.rectTransform;

                _staminaLabel = UiFactory.CreateText(_root, "StaminaLabel", "", 14, dim, TextAnchor.MiddleLeft);
                UiFactory.SetRect(_staminaLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(StaminaLabelWidth, 20f), new Vector2(StaminaBarLeft + StaminaBarWidth + 14f, 15f),
                    new Vector2(0f, 0.5f));
            }

            BuildSkillBar(ink, dim);
            BuildPassiveReadout(dim);

            _promptLabel = UiFactory.CreateText(_root, "PromptLabel", "", 20, ink, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_promptLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(760f, 32f), new Vector2(0f, 150f));

            _holdBack = UiFactory.CreateImage(_root, "HoldBack", new Color(0.08f, 0.07f, 0.06f, 0.85f),
                new Vector2(300f, 12f), new Vector2(0f, 128f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

            _holdFill = UiFactory.CreateImage(_root, "HoldFill", new Color(0.95f, 0.78f, 0.35f, 1f),
                new Vector2(300f, 12f), new Vector2(-150f, 128f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
            _holdFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _holdFillRect = _holdFill.rectTransform;
            // Nothing is being held when the HUD is built: the bar must not sit on screen full until the first hold.
            _holdFill.gameObject.SetActive(false);
            _holdBack.gameObject.SetActive(false);

            _toastLabel = UiFactory.CreateText(_root, "ToastLabel", "", 20, new Color(1f, 0.92f, 0.7f, 0f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(_toastLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                new Vector2(900f, 34f), new Vector2(0f, -140f));

            _pauseLabel = UiFactory.CreateText(_root, "PauseLabel", "PAUSED   -   Esc to resume", 26, ink, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_pauseLabel.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Vector2(700f, 40f), new Vector2(0f, 120f));
            _pauseLabel.gameObject.SetActive(false);

            BuildCrosshair(ink);
            BuildHotbar();

            _vignette = UiFactory.CreateImage(_root, "DamageVignette", new Color(0.65f, 0.05f, 0.05f, 0f),
                new Vector2(2400f, 1400f), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

            BuildInventoryScreen();
        }

        /// <summary>
        /// The satchel and worn-kit screen, opened with I or Tab. It lives beside the HUD rather than inside it
        /// because it takes over the pointer, and the run layer decides whether the player keeps control while
        /// it is open.
        /// </summary>
        private void BuildInventoryScreen()
        {
            GameObject screenObject = new GameObject("Emberdelve_InventoryScreen");
            screenObject.transform.SetParent(_canvas.transform, false);

            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            _inventoryScreen = screenObject.AddComponent<InventoryScreenController>();
            _inventoryScreen.Configure(_director, _director != null ? _director.Player : null);

            // The host stays active so the controller keeps its bag/gear subscriptions and its per-frame
            // pointer handling; only the screen's own visual root is switched off until Open() is called.
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

        /// <summary>
        /// Two skill slots, one per active skill. Everything on them arrives through GameEvents: the key the
        /// binding actually uses, the name, the mana cost, and a cooldown panel that shrinks as the skill
        /// recharges. A slot the player cannot afford is visibly different from one still on cooldown.
        /// </summary>
        private void BuildSkillBar(Color ink, Color dim)
        {
            const float chipWidth = 178f;
            const float chipHeight = 78f;
            const float gap = 14f;

            RectTransform bar = UiFactory.CreateGroup(_root, "SkillBar");
            UiFactory.SetRect(bar, new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(-28f, 128f));

            for (int i = 0; i < _skillSlots.Length; i++)
            {
                float x = -(chipWidth + gap) * (_skillSlots.Length - 1 - i) - chipWidth * 0.5f;

                Image background = UiFactory.CreateImage(bar, "SkillSlot" + i, new Color(0.075f, 0.07f, 0.06f, 0.9f),
                    new Vector2(chipWidth, chipHeight), new Vector2(x, chipHeight * 0.5f),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));

                SkillSlotWidget widget = new SkillSlotWidget { Background = background };

                UiFactory.CreateImage(background.transform, "Accent", new Color(0.5f, 0.45f, 0.38f, 1f),
                    new Vector2(chipWidth, 4f), new Vector2(0f, chipHeight * 0.5f - 2f),
                    new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));

                Image cooldown = UiFactory.CreateImage(background.transform, "CooldownFill", new Color(0.02f, 0.02f, 0.02f, 0.72f),
                    new Vector2(chipWidth, 0f), Vector2.zero, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
                cooldown.rectTransform.pivot = new Vector2(0.5f, 0f);
                widget.CooldownFill = cooldown;

                widget.KeyLabel = UiFactory.CreateText(background.transform, "KeyLabel", "?", 15,
                    new Color(1f, 0.9f, 0.72f, 1f), TextAnchor.MiddleCenter, FontStyle.Bold);
                UiFactory.SetRect(widget.KeyLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(46f, 22f), new Vector2(10f, -8f), new Vector2(0f, 1f));

                // Point anchors with an explicit width. The previous anchors stretched across the chip while
                // sizeDelta still carried a full chip width, which asked for a rectangle twice as wide as the
                // chip and pushed the title out of the slot on both sides.
                widget.NameLabel = UiFactory.CreateText(background.transform, "NameLabel", "", 16, ink, TextAnchor.UpperLeft);
                UiFactory.SetRect(widget.NameLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                    new Vector2(chipWidth - 66f, 24f), new Vector2(60f, -8f), new Vector2(0f, 1f));

                widget.CostLabel = UiFactory.CreateText(background.transform, "CostLabel", "", 13, dim, TextAnchor.MiddleLeft);
                UiFactory.SetRect(widget.CostLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(chipWidth - 20f, 20f), new Vector2(10f, 10f), new Vector2(0f, 0f));

                // Stretch anchors keep the cooldown readout glued to the chip, so sizeDelta is now the inset
                // rather than a second full size on top of the chip's own.
                widget.CooldownLabel = UiFactory.CreateText(background.transform, "CooldownLabel", "", 20,
                    new Color(1f, 0.85f, 0.6f, 1f), TextAnchor.MiddleRight);
                UiFactory.SetRect(widget.CooldownLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f),
                    new Vector2(-20f, -24f), Vector2.zero);

                widget.ReasonLabel = UiFactory.CreateText(background.transform, "RejectLabel", "", 12,
                    new Color(1f, 0.55f, 0.4f, 0f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(widget.ReasonLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(chipWidth - 16f, 34f), new Vector2(8f, 40f), new Vector2(0f, 0f));
                widget.ReasonLabel.gameObject.SetActive(false);

                _skillSlots[i] = widget;
            }
        }

        /// <summary>Passives are always-on, so they are read out as text rather than as buttons.</summary>
        private void BuildPassiveReadout(Color dim)
        {
            _kitLabel = UiFactory.CreateText(_root, "KitLabel", "", 13, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_kitLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(430f, 92f), new Vector2(24f, 132f), new Vector2(0f, 0f));
        }

        /// <summary>
        /// The raid hotbar: the four stacks the 1-4 keys actually reach, plus one compact line saying what the
        /// rest of the bag is doing. The full 10x4 satchel is not squeezed into the HUD; it is the screen
        /// opened with I or Tab, and this strip only names the stacks HotbarController consumes.
        /// </summary>
        private void BuildHotbar()
        {
            _inventoryRow = UiFactory.CreateGroup(_root, "InventoryRow");
            UiFactory.SetRect(_inventoryRow, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, Vector2.zero);

            _bagSummaryLabel = UiFactory.CreateText(_root, "BagSummary", "", 15,
                new Color(0.84f, 0.79f, 0.7f, 0.95f), TextAnchor.MiddleCenter);
            UiFactory.SetRect(_bagSummaryLabel.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f),
                new Vector2(BagSummaryWidth, BagSummaryHeight), new Vector2(0f, BagSummaryCentre));

            RebuildInventory();
        }

        /// <summary>
        /// Rebuilds the strip from the live bag. The chips are named Slot1..Slot4 and stand for stack indices
        /// 0..3 of Inventory.Slots, the same indices HotbarController hands to Inventory.TryUseSlot, so a
        /// stack that is consumed, removed, merged or shifted is re-read from the bag on the next
        /// InventoryChanged notification instead of being remembered here.
        /// </summary>
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

            // Geometry, measured against the neighbours that already live at the bottom of the HUD in the
            // tightest supported window (1280x720, image coords with y down from the top edge):
            //   health bar x 24..364 y 666..686, health label x 24..364 y 635..657, weapon label x 24..364
            //   y 603..629, mana bar x 24..364 y 695..709, mana label x 378..598 y 695..715,
            //   kit label x 24..454 y 496..588, ammo label x 932..1252 y 632..660, ammo fill x 1036..1256
            //   y 667..677, skill chips x 882..1252 y 514..592, prompt x 260..1020 y 554..586,
            //   hold bar x 490..790 y 586..598.
            // Four 96px chips on a 12px pitch, pivots 50px above the bottom edge, span x 430..850 and y
            // 648..692: 3px clear of the mana label's bottom edge, 50px below the hold bar, and clear of both
            // bars and the ammo readout in x. The 520px summary line, pivots 90px above the bottom edge, spans
            // x 380..900 and y 619..641: 7px above the chips, 21px below the hold bar, 16px right of the weapon
            // label and 32px left of the ammo label. Every one of those neighbours is corner-anchored with a
            // fixed pixel inset while this strip is bottom-centred, so the same offsets keep the same clearances
            // at 1920x1080 (chips x 750..1170 y 1008..1052, summary x 700..1220 y 979..1001, against a skill bar
            // that starts at x 1522, an ammo label that starts at x 1572 and a mana label that ends at x 598).
            float total = HotbarCapacity * HotbarChipWidth + (HotbarCapacity - 1) * HotbarChipGap;

            for (int i = 0; i < HotbarCapacity; i++)
            {
                float x = -total * 0.5f + (HotbarChipWidth + HotbarChipGap) * i + HotbarChipWidth * 0.5f;

                bool filled = i < inventory.Slots.Count;
                InventorySlot slot = filled ? inventory.Slots[i] : default;
                ItemData item = filled ? slot.Item : null;

                Image chip = UiFactory.CreateImage(_inventoryRow, "Slot" + (i + 1),
                    new Color(0.07f, 0.06f, 0.055f, item != null ? 0.93f : 0.6f),
                    new Vector2(HotbarChipWidth, HotbarChipHeight), new Vector2(x, HotbarChipCentre),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
                chip.raycastTarget = false;

                // The key glyph is the hint, and it is the key HotbarController reads for this stack index.
                Text key = UiFactory.CreateText(chip.transform, "Key", (i + 1).ToString(), 18,
                    item != null ? new Color(1f, 0.9f, 0.72f, 1f) : new Color(0.55f, 0.52f, 0.46f, 0.85f),
                    TextAnchor.MiddleCenter, FontStyle.Bold);
                UiFactory.SetRect(key.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                    new Vector2(24f, 22f), new Vector2(12f, HotbarChipHeight * 0.5f), new Vector2(0.5f, 0.5f));
                key.raycastTarget = false;

                UiFactory.CreateImage(chip.transform, "KeyDivider", new Color(0.45f, 0.41f, 0.35f, 0.8f),
                    new Vector2(1f, HotbarChipHeight - 12f), new Vector2(26f, HotbarChipHeight * 0.5f),
                    new Vector2(0f, 0f), new Vector2(0f, 0f)).raycastTarget = false;

                if (item == null)
                {
                    Text empty = UiFactory.CreateText(chip.transform, "Empty", "empty", 12,
                        new Color(0.5f, 0.47f, 0.42f, 0.8f), TextAnchor.MiddleCenter);
                    UiFactory.SetRect(empty.rectTransform, Vector2.zero, Vector2.one,
                        new Vector2(-34f, -14f), new Vector2(10f, 0f));
                    empty.raycastTarget = false;
                    continue;
                }

                // The chip's own accent strip carries the item's icon colour, so the name keeps the whole
                // remaining width instead of trading it for a swatch.
                Color accent = item.iconColor;
                accent.a = 1f;
                UiFactory.CreateImage(chip.transform, "Accent", accent, new Vector2(HotbarChipWidth, 4f),
                    new Vector2(HotbarChipWidth * 0.5f, HotbarChipHeight - 2f),
                    new Vector2(0f, 0f), new Vector2(0f, 0f)).raycastTarget = false;

                // Long names wrap inside the chip rather than being cut to fourteen characters; the full name,
                // footprint and summary stay in the satchel screen.
                Text label = UiFactory.CreateText(chip.transform, "Label", HotbarLabel(item, slot.Count), 12,
                    new Color(0.93f, 0.89f, 0.81f, 1f), TextAnchor.MiddleCenter);
                UiFactory.SetRect(label.rectTransform, Vector2.zero, Vector2.one,
                    new Vector2(-30f, -8f), new Vector2(12f, 0f));
                // Best fit shrinks the text to the chip instead of letting a long word break in the middle.
                label.resizeTextForBestFit = true;
                label.resizeTextMinSize = 8;
                label.resizeTextMaxSize = 12;
                label.horizontalOverflow = HorizontalWrapMode.Wrap;
                label.verticalOverflow = VerticalWrapMode.Truncate;
                label.raycastTarget = false;
            }

            UpdateBagSummary(inventory);
        }

        /// <summary>Stack name plus count, hard-cut only when a name is long enough to overflow the chip.</summary>
        private static string HotbarLabel(ItemData item, int count)
        {
            string name = item.displayName ?? string.Empty;
            if (name.Length > 22) name = name.Substring(0, 22).TrimEnd() + "...";

            return count > 1 ? name + " x" + count : name;
        }

        /// <summary>
        /// One readable line for the bag the four chips do not show: how full the satchel is, what it is worth,
        /// how much is worn, and the key that opens the whole 10x4 grid.
        /// </summary>
        private void UpdateBagSummary(Inventory inventory)
        {
            if (_bagSummaryLabel == null || inventory == null) return;

            Equipment equipment = _director != null && _director.Player != null ? _director.Player.Equipment : null;
            int worn = equipment != null ? equipment.FilledCount : 0;

            _bagSummaryLabel.text = $"1-4 use these stacks   bag {inventory.UsedCells}/{inventory.Capacity} cells   " +
                                    $"value {inventory.CarriedValue}   worn {worn}   [I] satchel";
        }

        private void OnPlayerHealth(int playerId, float current, float max)
        {
            if (!PlayerView.Shows(playerId)) return;
            ApplyPlayerHealth(current, max);
        }

        private void ApplyPlayerHealth(float current, float max)
        {
            if (_healthFill == null) return;

            float fraction = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            _healthFillRect.sizeDelta = new Vector2(BarWidth * fraction, BarHeight);

            if (_healthLabel != null) _healthLabel.text = Mathf.CeilToInt(current) + " / " + Mathf.CeilToInt(max);
        }

        private void OnPlayerDamaged(int playerId, DamageInfo info)
        {
            if (!PlayerView.Shows(playerId)) return;
            _vignetteTimer = VignetteDuration;

            // D1b: a broken guard says so (the block was engaged but the hit's impact power reached its resist).
            string label = info.GuardBroken ? "GUARD BROKEN" : info.Blocked ? "Blocked" : "Hit";
            GameEvents.RaiseToast(playerId, $"{label}  -{Mathf.RoundToInt(info.Amount)} HP");
        }

        private void OnDamageDealt(int playerId, HitFeedback feedback)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (_hitMarker != null)
            {
                _hitMarker.gameObject.SetActive(true);
                _hitMarkerTimer = HitMarkerDuration;
            }
        }

        private void OnItemCollected(int playerId, ItemData item, int totalCollected)
        {
            if (!PlayerView.Shows(playerId)) return;
            GameEvents.RaiseToast(playerId, $"Took {item.displayName}  (+{item.value} value)");
        }

        private void OnInventoryChanged(int playerId)
        {
            if (!PlayerView.Shows(playerId)) return;
            RebuildInventory();
        }

        private void OnAmmo(int playerId, int current, int reserve)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (_ammoLabel == null) return;

            _ammoLabel.gameObject.SetActive(true);
            _ammoFill.gameObject.SetActive(true);

            int magazineCapacity = 1;
            string ammoName = "Ammo";

            if (_director != null && _director.Player != null)
            {
                RangedCombat ranged = _director.Player.GetComponent<RangedCombat>();
                if (ranged != null && ranged.Weapon != null)
                {
                    magazineCapacity = Mathf.Max(1, ranged.Weapon.magazineSize);
                    if (!string.IsNullOrEmpty(ranged.Weapon.ammoDisplayName)) ammoName = ranged.Weapon.ammoDisplayName;
                }
            }

            _ammoLabel.text = $"{ammoName}  {current} / {reserve}";

            _ammoFillRect.sizeDelta = new Vector2(220f * Mathf.Clamp01(current / (float)magazineCapacity), 10f);
        }

        private void OnPlayerMana(int playerId, float current, float max)
        {
            if (!PlayerView.Shows(playerId)) return;
            ApplyPlayerMana(current, max);
        }

        private void ApplyPlayerMana(float current, float max)
        {
            if (_manaFill == null) return;

            float fraction = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            _manaFillRect.sizeDelta = new Vector2(BarWidth * fraction, ManaBarHeight);

            if (_manaLabel != null) _manaLabel.text = $"Mana  {Mathf.RoundToInt(current)} / {Mathf.RoundToInt(max)}";
        }

        /// <summary>
        /// The stamina read-out, re-read from the live pool. Stamina regenerates on its own clock and its
        /// ceiling moves with gear, so the bar is refreshed from the pool rather than remembered from an
        /// event; the label is only rewritten when a number the player can actually see has changed. This
        /// reads the pool - it never spends stamina, never changes a ceiling and never refills anything.
        /// </summary>
        private void UpdateStamina()
        {
            if (_staminaFill == null || _staminaFillRect == null) return;

            PlayerRig rig = _director != null ? _director.Player : null;
            StaminaComponent stamina = rig != null ? rig.Stamina : null;

            if (stamina == null)
            {
                _staminaFill.gameObject.SetActive(false);
                if (_staminaLabel != null) _staminaLabel.gameObject.SetActive(false);
                _staminaShownCurrent = -1;
                _staminaShownMax = -1;
                return;
            }

            _staminaFill.gameObject.SetActive(true);
            if (_staminaLabel != null) _staminaLabel.gameObject.SetActive(true);

            float current = stamina.CurrentStamina;
            float max = stamina.MaxStamina;
            int shownCurrent = Mathf.RoundToInt(current);
            int shownMax = Mathf.RoundToInt(max);

            if (shownCurrent == _staminaShownCurrent && shownMax == _staminaShownMax) return;

            _staminaShownCurrent = shownCurrent;
            _staminaShownMax = shownMax;

            float fraction = max > 0f ? Mathf.Clamp01(current / max) : 0f;
            _staminaFillRect.sizeDelta = new Vector2(StaminaBarWidth * fraction, ManaBarHeight);

            if (_staminaLabel != null) _staminaLabel.text = $"Stamina  {shownCurrent} / {shownMax}";
        }

        private void OnSkillState(int playerId, SkillSnapshot snapshot)
        {
            if (!PlayerView.Shows(playerId)) return;
            ApplySkillState(snapshot);
        }

        private void ApplySkillState(SkillSnapshot snapshot)
        {
            if (snapshot.Slot < 0 || snapshot.Slot >= _skillSlots.Length) return;

            SkillSlotWidget widget = _skillSlots[snapshot.Slot];
            if (widget == null || widget.Background == null) return;

            const float chipWidth = 178f;
            const float chipHeight = 78f;

            if (!snapshot.HasSkill)
            {
                widget.Background.color = new Color(0.05f, 0.05f, 0.05f, 0.7f);
                if (widget.KeyLabel != null) widget.KeyLabel.text = string.Empty;
                if (widget.NameLabel != null) widget.NameLabel.text = "no skill";
                if (widget.CostLabel != null) widget.CostLabel.text = string.Empty;
                if (widget.CooldownLabel != null) widget.CooldownLabel.text = string.Empty;
                if (widget.CooldownFill != null) widget.CooldownFill.rectTransform.sizeDelta = new Vector2(chipWidth, 0f);
                return;
            }

            Color accent = snapshot.AccentColor;
            widget.Background.color = snapshot.Castable
                ? new Color(Mathf.Lerp(0.075f, accent.r, 0.35f), Mathf.Lerp(0.07f, accent.g, 0.3f), Mathf.Lerp(0.06f, accent.b, 0.22f), 0.94f)
                : new Color(0.06f, 0.06f, 0.065f, 0.9f);

            Transform accentTransform = widget.Background.transform.Find("Accent");
            if (accentTransform != null)
            {
                Image accentImage = accentTransform.GetComponent<Image>();
                if (accentImage != null)
                {
                    accentImage.color = snapshot.Ready
                        ? accent
                        : new Color(accent.r * 0.4f, accent.g * 0.4f, accent.b * 0.4f, 1f);
                }
            }

            if (widget.KeyLabel != null) widget.KeyLabel.text = snapshot.KeyLabel;
            if (widget.NameLabel != null) widget.NameLabel.text = snapshot.DisplayName;
            // Summary already reads "24 MP  |  7s cooldown"; prefixing the cost again printed it twice.
            if (widget.CostLabel != null) widget.CostLabel.text = snapshot.Summary;

            float fraction = snapshot.CooldownFraction;
            if (widget.CooldownFill != null)
            {
                widget.CooldownFill.rectTransform.sizeDelta = new Vector2(chipWidth, chipHeight * fraction);
                widget.CooldownFill.gameObject.SetActive(fraction > 0.001f);
            }

            if (widget.CooldownLabel != null)
            {
                widget.CooldownLabel.text = fraction > 0.001f ? Mathf.CeilToInt(snapshot.CooldownRemaining) + "s" : string.Empty;
            }
        }

        private void OnSkillRejected(int playerId, SkillSnapshot snapshot, string reason)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (snapshot.Slot < 0 || snapshot.Slot >= _skillSlots.Length) return;

            SkillSlotWidget widget = _skillSlots[snapshot.Slot];
            if (widget == null || widget.ReasonLabel == null) return;

            Color color = widget.ReasonLabel.color;
            color.a = 1f;
            widget.ReasonLabel.color = color;
            widget.ReasonLabel.text = reason ?? string.Empty;
            widget.ReasonLabel.gameObject.SetActive(true);
            widget.RejectTimer = SkillRejectFlash;
        }

        private void OnPrompt(int playerId, string prompt)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (_promptLabel == null) return;
            _promptLabel.text = prompt ?? string.Empty;
        }

        private void OnHold(int playerId, float progress)
        {
            if (!PlayerView.Shows(playerId)) return;
            _holdProgress = Mathf.Clamp01(progress);
            if (_holdFill == null) return;

            bool active = _holdProgress > 0.001f;
            if (_holdBack != null) _holdBack.gameObject.SetActive(active);
            _holdFill.gameObject.SetActive(active);
            _holdFillRect.sizeDelta = new Vector2(300f * _holdProgress, 12f);
        }

        private void OnObjective(string objective)
        {
            if (_objectiveLabel != null) _objectiveLabel.text = objective ?? string.Empty;
        }

        private void OnStatus(int playerId, string status)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (_statusLabel != null) _statusLabel.text = status ?? string.Empty;
        }

        private void OnToast(int playerId, string message)
        {
            if (!PlayerView.Shows(playerId)) return;
            if (_toastLabel == null) return;
            _toastLabel.text = message ?? string.Empty;

            Color color = _toastLabel.color;
            color.a = 1f;
            _toastLabel.color = color;

            _toastTimer = ToastDuration;
        }

        private void OnActiveWeapon(int playerId, WeaponData weapon)
        {
            if (!PlayerView.Shows(playerId)) return;
            ApplyActiveWeapon(weapon);
        }

        private void ApplyActiveWeapon(WeaponData weapon)
        {
            if (_weaponLabel == null) return;
            _weaponLabel.text = weapon != null ? weapon.displayName : "Unarmed";
        }

        // R7: the HUD follows the run-wide state AND the local player's own run state (the local player dying hides it even
        // while a teammate or another party keeps the run going).
        private void OnPlayerRunState(int playerId, RunState state)
        {
            if (!PlayerView.Shows(playerId) || playerId == GameEvents.NoPlayer) return;
            OnRunState(state);
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

                    // Seed the bar from live rig state as well as from the event stream: class setup can
                    // raise the very first health change before this HUD is listening.
                    ApplyPlayerHealth(_director.Player.CurrentHealth, _director.Player.MaxHealth);
                    ApplyActiveWeapon(_director.Player.Loadout != null ? _director.Player.Loadout.ActiveWeapon : null);

                    ApplyPlayerMana(_director.Player.Mana != null ? _director.Player.Mana.CurrentMana : 0f,
                        _director.Player.Mana != null ? _director.Player.Mana.MaxMana : 0f);

                    // Stamina has no GameEvents channel yet (the pool only raises its own Changed), and the
                    // run layer is not the place to invent one for a bar. Seeding from the live pool here is
                    // the same seed the health and mana bars get, and Update keeps it current from there.
                    _staminaShownCurrent = -1;
                    _staminaShownMax = -1;
                    UpdateStamina();

                    SkillCaster caster = _director.Player.Skills;
                    if (caster != null)
                    {
                        for (int i = 0; i < _skillSlots.Length; i++) ApplySkillState(caster.Snapshot(i));
                        OnKitChanged(caster);
                    }

                    bool ranged = _director.Player.GetComponent<RangedCombat>() != null
                                  && _director.Player.GetComponent<RangedCombat>().enabled;
                    if (!ranged && _ammoLabel != null) _ammoLabel.gameObject.SetActive(false);
                    if (!ranged && _ammoFill != null) _ammoFill.gameObject.SetActive(false);
                }

                // Seed the hotbar from the live bag here too. RunDirector assigns its Inventory only after
                // ApplyClass has already handed out the starting kit, so the InventoryChanged raised by that
                // kit reaches RebuildInventory while RunDirector.Inventory is still null and is dropped, and a
                // class that starts with an empty bag never raises one at all. Running is the first state in
                // which the director's bag exists, so this is the point where the strip is built - from the
                // bag itself, not from an invented event. RebuildInventory keeps its own destroy/rebuild path
                // and its own director/bag null guards, so an empty bag still yields the four empty chips.
                RebuildInventory();
            }
        }

        /// <summary>Names and short effect lines for the passives, plus the actives, so the kit is readable.</summary>
        private void OnKitChanged(SkillCaster caster)
        {
            if (_kitLabel == null || caster == null) return;

            System.Text.StringBuilder builder = new System.Text.StringBuilder();
            builder.Append("PASSIVES\n");

            IReadOnlyList<SkillData> passives = caster.PassiveSkills;
            for (int i = 0; i < passives.Count; i++)
            {
                SkillData passive = passives[i];
                if (passive == null) continue;
                builder.Append(passive.displayName).Append(" - ").Append(passive.ShortSummary).Append('\n');
            }

            _kitLabel.text = builder.ToString().TrimEnd();
        }

        /// <summary>
        /// The pause label, and the one moment the run layer is told again that the satchel still holds the
        /// pointer. RunDirector.TogglePause re-enables gameplay input before it raises this notification and
        /// re-locks the cursor after it, so a resume out of pause while the satchel is open would otherwise
        /// leave the player looking at an open screen whose next click is also an attack. Re-running the
        /// run layer's own SetInventoryOpen contract from here puts the input gate back the way Open() set
        /// it; the cursor itself belongs to the screen that is open and is re-asserted there.
        /// </summary>
        private void OnPause(bool paused)
        {
            _paused = paused;
            if (_pauseLabel != null) _pauseLabel.gameObject.SetActive(paused);

            if (_inventoryScreen == null) return;
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            // Closed, this is the same call the toggle already made: the run layer keeps control and keeps
            // the cursor locked. Open, it is the difference between a tab that can be clicked and a swing.
            if (_director != null) _director.SetInventoryOpen(_inventoryScreen.IsOpen);
        }
    }
}
