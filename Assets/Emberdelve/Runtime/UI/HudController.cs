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
using Emberdelve.Level;
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

        // B2: combo pips and hit-marker variants. Names are used by the HUD gates.
        internal const int MaxComboPips = 5;
        private const float ComboPipSize = 10f;
        private const float ComboPipPitch = 16f;
        private const float ComboWindowQuantum = 32f;

        private Canvas _canvas;
        private RectTransform _root;
        private RectTransform _inventoryRow;
        private Text _bagSummaryLabel;

        private UnityEngine.UI.Text _roomLabel;
        private UnityEngine.UI.Image _roomBack;
        private float _nextWayfinding;
        private Text _classLabel;
        private Text _weaponLabel;
        private Text _objectiveLabel;
        private Text _raidCountdownLabel;
        private Image _raidCountdownBack;
        private Text _royaleZoneLabel;
        private Image _royaleZoneBack;
        private Text _statusLabel;
        private Text _spectatorLabel;
        private UnityEngine.UI.Image _impactGuardBuff;
        private UnityEngine.UI.Text _impactGuardLabel;
        private PlayerRig _impactGuardPlayer;
        private float _nextImpactGuardPlayerSearch;
        private Text _healthLabel;
        private Image _healthFill;
        private RectTransform _healthFillRect;
        private Text _ammoLabel;
        private Image _ammoFill;
        private RectTransform _ammoFillRect;
        private Text _manaLabel;
        private Image _manaFill;
        private RectTransform _manaFillRect;
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
        private Image _hitSlashA;
        private Image _hitSlashB;
        private HitLocation _lastHitLocation = HitLocation.Unspecified;

        // B2 combo pips: one pip per step of the held weapon's chain, under the crosshair, plus a thin window bar.
        private RectTransform _comboPips;
        private readonly Image[] _comboPipImages = new Image[MaxComboPips];
        private Image _comboWindowBar;
        private int _pipsStep = -1;
        private int _pipsTotal = -1;
        private int _pipsWindowQ = -1;
        private RectTransform _crosshair;

        private float _toastTimer;
        private float _hitMarkerTimer;
        private float _vignetteTimer;
        private float _holdProgress;
        private bool _paused;
        private RunDirector _director;
        private InventoryScreenController _inventoryScreen;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
        private DeveloperSpawnPanel _developerSpawn;
#endif

        /// <summary>Widgets of one skill slot. Built once, then updated from SkillSnapshot values.</summary>
        private sealed class SkillSlotWidget
        {
            public Image Background;
            public Image Icon;
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

        // The raid hotbar. HotbarController.Update uses the StackId bound to key N (Inventory.Hotbar, R6c2), so the
        // strip draws the live stack behind each of the six wheel bindings, and an empty chip for a slot with no
        // binding or whose stack has retired. It never shows bag list positions.
        private const int HotbarCapacity = 6;
        private const float HotbarChipWidth = 76f;
        private const float HotbarChipHeight = 44f;
        private const float HotbarChipGap = 8f;

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
            GameEvents.GuardContact += OnGuardContact;
            GameEvents.DamageDealt += OnDamageDealt;
            GameEvents.ItemCollected += OnItemCollected;
            GameEvents.InventoryChanged += OnInventoryChanged;
            GameEvents.HotbarChanged += OnHotbarChanged;
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
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _developerSpawn?.Close();
#endif
            GameEvents.PlayerHealthChanged -= OnPlayerHealth;
            GameEvents.PlayerDamaged -= OnPlayerDamaged;
            GameEvents.GuardContact -= OnGuardContact;
            GameEvents.DamageDealt -= OnDamageDealt;
            GameEvents.ItemCollected -= OnItemCollected;
            GameEvents.InventoryChanged -= OnInventoryChanged;
            GameEvents.HotbarChanged -= OnHotbarChanged;
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

            // The caster only raises an event when a slot changes state; the cooldown wedge unwinds every frame.
            SkillCaster liveCaster = _director != null && _director.Player != null ? _director.Player.Skills : null;
            if (liveCaster != null)
                for (int i = 0; i < _skillSlots.Length; i++) ApplySkillState(liveCaster.Snapshot(i));

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

#if UNITY_EDITOR || DEVELOPMENT_BUILD
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            _developerSpawn?.Tick(_director);
            if (_developerSpawn == null || !_developerSpawn.IsOpen)
#endif
                UpdateInventoryScreen();
            UpdateComboPips();
            UpdateRaidCountdown();
            UpdateWayfinding();
            UpdateImpactGuardBuff();
            UpdatePortalSpectator();
        }

        private void UpdatePortalSpectator()
        {
            if (_spectatorLabel == null) return;
            var local = _impactGuardPlayer;
            bool watching = local != null && local.IsPortalSpectating;
            if (_crosshair != null && _crosshair.gameObject.activeSelf == watching) _crosshair.gameObject.SetActive(!watching);
            if (_spectatorLabel.gameObject.activeSelf != watching) _spectatorLabel.gameObject.SetActive(watching);
            if (!watching) return;
            _spectatorLabel.text = DescribeSpectator(local.GetComponent<PortalSpectatorCamera>());
        }

        internal static string DescribeSpectator(PortalSpectatorCamera camera)
        {
            if (camera == null || camera.VisibleTarget == null) return "DESCENDING…";
            var target = camera.VisibleTarget;
            string name = target.Class != null ? target.Class.displayName : "Ally";
            return "SPECTATING  " + name + "  #" + target.PlayerId
                + (camera.TargetCount > 1 ? "   •   TAB  SWITCH ALLY" : "");
        }

        private void UpdateImpactGuardBuff()
        {
            if (_impactGuardBuff == null) return;
            int localId = PlayerView.LocalPlayerId;
            if (_impactGuardPlayer != null && (_impactGuardPlayer.PlayerId != localId || !_impactGuardPlayer.IsLocallyControlled))
                _impactGuardPlayer = null;
            if (_impactGuardPlayer == null && Time.unscaledTime >= _nextImpactGuardPlayerSearch)
            {
                _nextImpactGuardPlayerSearch = Time.unscaledTime + .5f;
                // Network owners are runtime PlayerRig views too; avoid depending on the Net assembly.
                foreach (PlayerRig candidate in FindObjectsByType<PlayerRig>(FindObjectsSortMode.None))
                    if (candidate.IsLocallyControlled && candidate.PlayerId == localId)
                    { _impactGuardPlayer = candidate; break; }
            }
            PlayerRig local = _impactGuardPlayer;
            float remaining = local != null && !local.IsDead && !local.IsExtracted ? local.ImpactGuardRemaining : 0f;
            bool visible = _root != null && _root.gameObject.activeInHierarchy && remaining > 0f && !float.IsInfinity(remaining);
            if (_impactGuardBuff.gameObject.activeSelf != visible) _impactGuardBuff.gameObject.SetActive(visible);
            if (visible) _impactGuardLabel.text = "IMPACT GUARD  " + remaining.ToString("0.0") + "s";
        }

        /// <summary>
        /// B2: reads the authority's chain state (read-only) for the held weapon and shows it as pips. Hidden for any
        /// weapon without a combo chain (ranged, shield, single pattern) and when no authority reports a chain.
        /// </summary>
        private void UpdateComboPips()
        {
            if (_comboPips == null) return;

            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            PlayerRig rig = _director != null ? _director.Player : null;
            WeaponData weapon = rig != null ? rig.ActiveWeapon : null;

            if (weapon != null && weapon.HasComboChain
                && GameServices.TryGet<ICombatAuthority>(out ICombatAuthority authority) && authority is IComboStateSource source
                && source.TryGetComboState(rig.CombatId, out int step, out int total, out float remaining))
            {
                // "step" is the NEXT step of the chain, so the open window belongs to the step before it
                float fraction = remaining <= 0f ? 0f : Mathf.Clamp01(remaining / weapon.ComboWindowAfter(step - 1));
                ApplyComboPips(step, total, fraction);
                return;
            }

            ApplyComboPips(0, 0, 0f);
        }

        /// <summary>
        /// B2: draws the pips. <paramref name="total"/> below 2 hides the whole group (no chain). Pips below
        /// <paramref name="step"/> are lit; the window bar shows <paramref name="windowFraction"/> of the combo window
        /// left while a continuation is still possible (step between 1 and total - 1). Only writes UI when something changed.
        /// </summary>
        internal void ApplyComboPips(int step, int total, float windowFraction)
        {
            if (_comboPips == null) return;

            total = Mathf.Clamp(total, 0, MaxComboPips);
            step = Mathf.Clamp(step, 0, total);
            bool show = total >= 2;
            bool windowOpen = show && step > 0 && step < total && windowFraction > 0f;
            int windowQ = windowOpen ? Mathf.Clamp(Mathf.CeilToInt(windowFraction * ComboWindowQuantum), 1, (int)ComboWindowQuantum) : 0;
            if (!show) { step = 0; total = 0; }

            if (step == _pipsStep && total == _pipsTotal && windowQ == _pipsWindowQ) return;
            _pipsStep = step;
            _pipsTotal = total;
            _pipsWindowQ = windowQ;

            if (_comboPips.gameObject.activeSelf != show) _comboPips.gameObject.SetActive(show);
            if (!show) return;

            Color unlit = new Color(0.25f, 0.23f, 0.2f, 0.55f);
            Color lit = windowOpen ? new Color(0.95f, 0.78f, 0.35f, 1f) : new Color(0.7f, 0.55f, 0.3f, 0.9f);
            for (int i = 0; i < _comboPipImages.Length; i++)
            {
                Image pip = _comboPipImages[i];
                if (pip == null) continue;

                bool exists = i < total;
                if (pip.gameObject.activeSelf != exists) pip.gameObject.SetActive(exists);
                if (!exists) continue;

                pip.rectTransform.anchoredPosition = new Vector2((i - (total - 1) * 0.5f) * ComboPipPitch, 0f);
                pip.color = i < step ? lit : unlit;
            }

            if (_comboWindowBar != null)
            {
                if (_comboWindowBar.gameObject.activeSelf != windowOpen) _comboWindowBar.gameObject.SetActive(windowOpen);
                if (windowOpen)
                {
                    float full = total * ComboPipPitch - (ComboPipPitch - ComboPipSize);
                    _comboWindowBar.rectTransform.sizeDelta = new Vector2(full * (windowQ / ComboWindowQuantum), 2f);
                }
            }
        }

        // B2 test surface (the HUD gates read these; the widgets are also found by name under Crosshair / ComboPips).
        internal bool ComboPipsVisible => _comboPips != null && _comboPips.gameObject.activeSelf;
        internal int ComboPipsShown => _pipsTotal;
        internal int ComboPipsLit => _pipsStep;
        internal bool ComboWindowOpen => _comboWindowBar != null && _comboWindowBar.gameObject.activeSelf;
        internal Color HitMarkerColor => _hitSlashA != null ? _hitSlashA.color : default;
        internal float HitMarkerScale => _hitMarker != null ? _hitMarker.localScale.x : 0f;
        internal bool HitMarkerVisible => _hitMarker != null && _hitMarker.gameObject.activeSelf;
        internal HitLocation LastHitMarkerLocation => _lastHitLocation;

        private void UpdateInventoryScreen()
        {
            if (_inventoryScreen == null || _inventoryScreen.IsLootCompanion) return;

            if (_director == null) GameServices.TryGet<RunDirector>(out _director);

            PlayerRig rig = _director != null ? _director.Player : null;
            EmberdelveInput input = rig != null ? rig.Input : null;
            bool running = _director == null || (_director.State == RunState.Running && _director.LocalPlayerState == RunState.Running);

            if (input != null && running && input.InventoryPressed && InventoryScreenController.LastCloseFrame != Time.frameCount)
            {
                _inventoryScreen.Toggle();

                if (_director != null) _director.SetInventoryOpen(_inventoryScreen.IsOpen);
            }

            _inventoryScreen.HandlePointer();
        }

        private void UpdateWayfinding()
        {
            if (_roomLabel == null || Time.unscaledTime < _nextWayfinding) return;
            _nextWayfinding = Time.unscaledTime + .12f;
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            PlayerRig player = _director != null ? _director.Player : null;
            var layout = _director == null ? null : _director.RoyaleMode.HasValue && _director.LocalSession != null
                ? _director.FloorCoordinator.ActiveLayoutFor(_director.LocalSession.Floor) : _director.Layout;
            bool visible = player != null && player.IsInPlay && !player.IsDead && layout != null &&
                _director.State == RunState.Running && _director.LocalPlayerState == RunState.Running &&
                !_paused && (_inventoryScreen == null || !_inventoryScreen.IsOpen);
            _roomLabel.gameObject.SetActive(visible);
            _roomBack.gameObject.SetActive(visible);
            if (!visible) return;
            Vector2 offset = new Vector2(0f, _root.rect.width >= 1280f ? -28f : -170f);
            _roomLabel.rectTransform.anchoredPosition = offset;
            _roomBack.rectTransform.anchoredPosition = offset;
            float yaw = player.Look != null ? player.Look.AimTransform.eulerAngles.y : player.transform.eulerAngles.y;
            string label = DungeonWayfinding.Heading(yaw) + "  |  " + DungeonWayfinding.RoomAt(layout, player.transform.position);
            if (_roomLabel.text != label) _roomLabel.text = label;
        }

        private void BuildHud()
        {
            _canvas = UiFactory.EnsureCanvas("Emberdelve_Hud", 10);
            _root = UiFactory.CreateGroup(_canvas.transform, "HudRoot");
            UiFactory.Stretch(_root);
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _developerSpawn = new DeveloperSpawnPanel(_root);
#endif

            Color ink = new Color(0.92f, 0.88f, 0.8f, 1f);
            Color dim = new Color(0.75f, 0.7f, 0.62f, 0.75f);

            _roomBack = UiFactory.CreateImage(_root, "RoomWayfindingBack", new Color(.055f, .05f, .04f, .8f),
                new Vector2(290f, 30f), new Vector2(0f, -28f), new Vector2(.5f, 1f), new Vector2(.5f, 1f));
            _roomLabel = UiFactory.CreateText(_root, "RoomWayfinding", "", 15, ink, TextAnchor.MiddleCenter);
            UiFactory.SetRect(_roomLabel.rectTransform, new Vector2(.5f, 1f), new Vector2(.5f, 1f),
                new Vector2(282f, 28f), new Vector2(0f, -28f), new Vector2(.5f, .5f));
            _roomLabel.raycastTarget = false;
            _roomBack.raycastTarget = false;
            DungeonUiStyle.BarBack(_roomBack.transform);
            _roomLabel.gameObject.SetActive(false);
            _roomBack.gameObject.SetActive(false);

            _classLabel = UiFactory.CreateText(_root, "ClassLabel", "Class", 26, ink, TextAnchor.UpperLeft);
            DungeonUiStyle.Heading(_classLabel);
            UiFactory.SetRect(_classLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(420f, 34f), new Vector2(24f, -22f), new Vector2(0f, 1f));

            _objectiveLabel = UiFactory.CreateText(_root, "ObjectiveLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_objectiveLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 60f), new Vector2(24f, -58f), new Vector2(0f, 1f));

            _raidCountdownBack = UiFactory.CreateImage(_root, "RaidCountdownBack", new Color(0.07f, 0.06f, 0.05f, 0.88f),
                new Vector2(236f, 48f), new Vector2(-24f, -24f), Vector2.one, Vector2.one);
            _raidCountdownBack.rectTransform.pivot = Vector2.one;
            DungeonUiStyle.Surface(_raidCountdownBack);
            _raidCountdownLabel = UiFactory.CreateText(_root, "RaidCountdownLabel", "RAID  10:00", 20, ink,
                TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.SetRect(_raidCountdownLabel.rectTransform, Vector2.one, Vector2.one,
                new Vector2(220f, 40f), new Vector2(-32f, -28f), Vector2.one);
            _raidCountdownBack.gameObject.SetActive(false);
            _raidCountdownLabel.gameObject.SetActive(false);

            _royaleZoneBack = UiFactory.CreateImage(_root, "RoyaleZoneBack", new Color(.07f, .06f, .05f, .88f),
                new Vector2(236f, 28f), new Vector2(-24f, -76f), Vector2.one, Vector2.one);
            _royaleZoneBack.rectTransform.pivot = Vector2.one;
            DungeonUiStyle.Surface(_royaleZoneBack);
            _royaleZoneLabel = UiFactory.CreateText(_royaleZoneBack.transform, "RoyaleZoneLabel", "", 14, ink,
                TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.Stretch(_royaleZoneLabel.rectTransform);
            _royaleZoneLabel.raycastTarget = false;
            _royaleZoneBack.raycastTarget = false;
            _royaleZoneBack.gameObject.SetActive(false);

            _statusLabel = UiFactory.CreateText(_root, "StatusLabel", "", 16, dim, TextAnchor.UpperLeft);
            UiFactory.SetRect(_statusLabel.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f),
                new Vector2(520f, 26f), new Vector2(24f, -118f), new Vector2(0f, 1f));

            _spectatorLabel = UiFactory.CreateText(_root, "PortalSpectatorLabel", "", 18, ink, TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.SetRect(_spectatorLabel.rectTransform, new Vector2(.5f, 0f), new Vector2(.5f, 0f),
                new Vector2(560f, 32f), new Vector2(0f, 126f));
            _spectatorLabel.raycastTarget = false;
            _spectatorLabel.gameObject.SetActive(false);

            UiFactory.CreateImage(_root, "HealthBack", new Color(0.08f, 0.07f, 0.06f, 0.85f),
                new Vector2(BarWidth, BarHeight), new Vector2(24f + BarWidth * 0.5f, 44f),
                new Vector2(0f, 0f), new Vector2(0f, 0f));

            _healthFill = UiFactory.CreateImage(_root, "HealthFill", new Color(0.52f, 0.09f, 0.075f, 1f),
                new Vector2(BarWidth, BarHeight), new Vector2(24f, 44f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            _healthFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _healthFillRect = _healthFill.rectTransform;
            DungeonUiStyle.Bar(_healthFill);
            DungeonUiStyle.BarBack(_root.Find("HealthBack"));

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
            DungeonUiStyle.Bar(_ammoFill);

            UiFactory.CreateImage(_root, "ManaBack", new Color(0.07f, 0.07f, 0.09f, 0.85f),
                new Vector2(BarWidth, ManaBarHeight), new Vector2(24f + BarWidth * 0.5f, 18f),
                new Vector2(0f, 0f), new Vector2(0f, 0f));

            _manaFill = UiFactory.CreateImage(_root, "ManaFill", new Color(0.24f, 0.45f, 0.78f, 1f),
                new Vector2(BarWidth, ManaBarHeight), new Vector2(24f, 18f), new Vector2(0f, 0f), new Vector2(0f, 0f));
            _manaFill.rectTransform.pivot = new Vector2(0f, 0.5f);
            _manaFillRect = _manaFill.rectTransform;
            DungeonUiStyle.Bar(_manaFill);
            DungeonUiStyle.BarBack(_root.Find("ManaBack"));

            _manaLabel = UiFactory.CreateText(_root, "ManaLabel", "0 / 0", 14, dim, TextAnchor.MiddleLeft);
            UiFactory.SetRect(_manaLabel.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f),
                new Vector2(220f, 20f), new Vector2(24f + BarWidth + 14f, 15f), new Vector2(0f, 0.5f));

            BuildSkillBar(ink, dim);
            BuildPassiveReadout(dim);

            // Temporary defensive buff above the existing skill row, away from aim and interaction prompts.
            _impactGuardBuff = UiFactory.CreateImage(_root, "ImpactGuardBuff", new Color(.06f, .13f, .17f, .95f),
                new Vector2(220f, 30f), new Vector2(-138f, 232f), Vector2.right, Vector2.right);
            _impactGuardBuff.raycastTarget = false;
            UnityEngine.UI.Image buffAccent = UiFactory.CreateImage(_impactGuardBuff.transform, "GuardAccent", new Color(.48f, .85f, 1f, 1f),
                new Vector2(4f, 22f), new Vector2(-101f, 0f), new Vector2(.5f, .5f), new Vector2(.5f, .5f));
            buffAccent.raycastTarget = false;
            _impactGuardLabel = UiFactory.CreateText(_impactGuardBuff.transform, "ImpactGuardLabel", "", 15,
                new Color(.85f, .96f, 1f, 1f), TextAnchor.MiddleCenter, FontStyle.Bold);
            UiFactory.SetRect(_impactGuardLabel.rectTransform, new Vector2(.5f, .5f), new Vector2(.5f, .5f),
                new Vector2(206f, 28f), new Vector2(4f, 0f));
            _impactGuardLabel.raycastTarget = false;
            _impactGuardBuff.gameObject.SetActive(false);

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

        private void UpdateRaidCountdown()
        {
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            bool visible = _director != null && _director.RaidCountdownEnabled;
            if (_raidCountdownBack != null && _raidCountdownBack.gameObject.activeSelf != visible)
                _raidCountdownBack.gameObject.SetActive(visible);
            if (_raidCountdownLabel != null && _raidCountdownLabel.gameObject.activeSelf != visible)
                _raidCountdownLabel.gameObject.SetActive(visible);
            if (!visible || _raidCountdownLabel == null || _raidCountdownBack == null)
            {
                if (_royaleZoneBack != null) _royaleZoneBack.gameObject.SetActive(false);
                return;
            }

            float remaining = _director.RaidSecondsRemaining;
            bool expired = _director.RaidExpired;
            bool royale = _director.RoyaleMode.HasValue;
            PlayerRaidClock clock = royale ? _director.ClockFor(_director.Player) : default;
            bool waiting = royale && clock.Waiting;
            bool descend = royale && clock.Floor < 3;
            if (royale && _objectiveLabel != null)
                _objectiveLabel.text = waiting ? "Preparing the next floor. Your carried loot stays with you."
                    : descend ? "Find a red portal and press [F] to descend.\n" + DescentObjective(clock.Floor)
                    : "Recover loot, then hold [F] at the final escape.\nStay inside the zone until you leave.";
            UpdateRoyaleZone(royale && !waiting ? clock.Floor : 0);
            bool urgent = !waiting && (expired || remaining <= 10f);
            bool warning = !waiting && remaining <= 60f;
            _raidCountdownLabel.text = waiting ? "PREPARING FLOOR " + clock.Floor : expired ? "RAID EXPIRED" :
                urgent ? "FINAL SECONDS  " + FormatRaidClock(remaining) :
                warning ? (descend ? "DESCEND  " : "EXTRACT  ") + FormatRaidClock(remaining) :
                (royale ? "FLOOR " + clock.Floor + "  " : "RAID  ") + FormatRaidClock(remaining);
            _raidCountdownLabel.color = urgent ? new Color(1f, 0.72f, 0.58f, 1f) :
                warning ? new Color(1f, 0.87f, 0.56f, 1f) : new Color(0.92f, 0.88f, 0.8f, 1f);
            _raidCountdownBack.color = urgent ? new Color(0.28f, 0.06f, 0.04f, 0.94f) :
                warning ? new Color(0.25f, 0.18f, 0.05f, 0.92f) : new Color(0.07f, 0.06f, 0.05f, 0.88f);
        }

        private string DescentObjective(int floor)
        {
            var encounter = _director.FloorCoordinator.EncounterFor(floor);
            if (encounter == null || !encounter.TryGetPortalStatus(out int open, out double nextWave)) return "";
            if (open > 0) return open == 1 ? "1 portal group open inside the safe zone." : open + " portal groups open inside the safe zone.";
            return nextWave > 0 ? "Next portal wave in " + FormatRaidClock((float)nextWave) + "."
                : "All portal slots on this floor have been used.";
        }

        private void UpdateRoyaleZone(int floor)
        {
            var actor = _director.Player;
            var encounter = floor > 0 ? _director.FloorCoordinator.EncounterFor(floor) : null;
            if (_royaleZoneBack == null) return;
            if (encounter == null || actor == null || !actor.IsInPlay || actor.IsDead || !encounter.TrySample(out var sample))
            {
                _royaleZoneBack.gameObject.SetActive(false);
                return;
            }
            _royaleZoneBack.gameObject.SetActive(true);

            Vector3 position = actor.transform.position;
            float distance = Vector2.Distance(new Vector2(position.x, position.z), new Vector2((float)sample.X, (float)sample.Z));
            float outside = distance - (float)sample.Radius;
            _royaleZoneLabel.text = outside > 0 ? "OUTSIDE ZONE  " + Mathf.CeilToInt(outside) + " m to safety"
                : sample.Completed ? "FINAL ZONE" : sample.Shrinking ? "ZONE SHRINKING"
                : "ZONE CLOSES IN " + Mathf.CeilToInt((float)sample.SecondsToBoundary) + " s";
            _royaleZoneLabel.color = outside > 0 ? new Color(1f, .65f, .45f) : new Color(.92f, .88f, .8f);
        }

        private static string FormatRaidClock(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
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
            _hitSlashA = UiFactory.CreateImage(_hitMarker, "SlashA", HitMarkerBody.Color, new Vector2(2f, 18f), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _hitSlashA.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            _hitSlashB = UiFactory.CreateImage(_hitMarker, "SlashB", HitMarkerBody.Color, new Vector2(2f, 18f), Vector2.zero,
                new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _hitSlashB.rectTransform.localEulerAngles = new Vector3(0f, 0f, -45f);
            _hitMarker.gameObject.SetActive(false);

            BuildComboPips();
        }

        /// <summary>B2: the combo pips under the crosshair (hidden until a chain weapon is held).</summary>
        private void BuildComboPips()
        {
            _comboPips = UiFactory.CreateGroup(_crosshair, "ComboPips");
            UiFactory.SetRect(_comboPips, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(0f, -40f));

            for (int i = 0; i < MaxComboPips; i++)
            {
                _comboPipImages[i] = UiFactory.CreateImage(_comboPips, "ComboPip" + i, new Color(0.25f, 0.23f, 0.2f, 0.55f),
                    new Vector2(ComboPipSize, ComboPipSize), new Vector2(i * ComboPipPitch, 0f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            }

            _comboWindowBar = UiFactory.CreateImage(_comboPips, "ComboWindow", new Color(0.95f, 0.78f, 0.35f, 0.9f),
                new Vector2(ComboPipPitch * 3f, 2f), new Vector2(0f, -10f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
            _comboWindowBar.gameObject.SetActive(false);
            _comboPips.gameObject.SetActive(false);
        }

        /// <summary>
        /// Two skill slots, one per active skill. Everything on them arrives through GameEvents: the key the
        /// binding actually uses, the name, the mana cost, and a cooldown panel that shrinks as the skill
        /// recharges. A slot the player cannot afford is visibly different from one still on cooldown.
        /// </summary>
        private const float SkillIconSize = 76f;
        private static Sprite _radialSprite;

        /// <summary>A white sprite: Image.Type.Filled needs one to draw its radial wedge.</summary>
        private static Sprite RadialSprite()
        {
            if (_radialSprite == null)
            {
                Texture2D white = Texture2D.whiteTexture;
                _radialSprite = Sprite.Create(white, new Rect(0f, 0f, white.width, white.height), new Vector2(0.5f, 0.5f), 100f);
                _radialSprite.name = "SkillCooldownRadial";
            }
            return _radialSprite;
        }

        /// <summary>
        /// Active skills as square icons (the same art as the character sheet), the key in the corner, the mana cost under it,
        /// and the cooldown as a dark clockwise wedge that unwinds over the icon with the seconds left on top.
        /// </summary>
        private void BuildSkillBar(Color ink, Color dim)
        {
            const float frame = SkillIconSize + 8f;
            const float gap = 12f;

            RectTransform bar = UiFactory.CreateGroup(_root, "SkillBar");
            // Centred above the quick belt: the bottom-right corner is where the first-person weapon hand sits.
            UiFactory.SetRect(bar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(0f, 132f));

            for (int i = 0; i < _skillSlots.Length; i++)
            {
                float x = (i - (_skillSlots.Length - 1) * 0.5f) * (frame + gap);

                Image background = UiFactory.CreateImage(bar, "SkillSlot" + i, new Color(0.075f, 0.07f, 0.06f, 0.92f),
                    new Vector2(frame, frame), new Vector2(x, frame * 0.5f),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
                DungeonUiStyle.Surface(background);
                SkillSlotWidget widget = new SkillSlotWidget { Background = background };

                widget.Icon = UiFactory.CreateImage(background.transform, "Icon", Color.white,
                    new Vector2(SkillIconSize, SkillIconSize), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                widget.Icon.preserveAspect = true;
                widget.Icon.raycastTarget = false;

                Image cooldown = UiFactory.CreateImage(background.transform, "CooldownRadial", new Color(0f, 0f, 0f, 0.72f),
                    new Vector2(SkillIconSize, SkillIconSize), Vector2.zero, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
                cooldown.sprite = RadialSprite();
                cooldown.type = Image.Type.Filled;
                cooldown.fillMethod = Image.FillMethod.Radial360;
                cooldown.fillOrigin = (int)Image.Origin360.Top;
                cooldown.fillClockwise = false;
                cooldown.fillAmount = 0f;
                cooldown.raycastTarget = false;
                widget.CooldownFill = cooldown;

                widget.CooldownLabel = UiFactory.CreateText(background.transform, "CooldownLabel", "", 24,
                    new Color(1f, 0.9f, 0.72f, 1f), TextAnchor.MiddleCenter, FontStyle.Bold);
                UiFactory.SetRect(widget.CooldownLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), Vector2.zero, Vector2.zero);

                // Key badge in the top-left corner and the mana cost along the bottom edge, both outside the wedge's centre.
                Image keyBack = UiFactory.CreateImage(background.transform, "KeyBadge", new Color(0.05f, 0.045f, 0.04f, 0.9f),
                    new Vector2(24f, 22f), new Vector2(4f, -4f), new Vector2(0f, 1f), new Vector2(0f, 1f));
                keyBack.rectTransform.pivot = new Vector2(0f, 1f);
                keyBack.raycastTarget = false;
                widget.KeyLabel = UiFactory.CreateText(keyBack.transform, "KeyLabel", "?", 15,
                    new Color(1f, 0.9f, 0.72f, 1f), TextAnchor.MiddleCenter, FontStyle.Bold);
                UiFactory.SetRect(widget.KeyLabel.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

                widget.CostLabel = UiFactory.CreateText(background.transform, "CostLabel", "", 13, new Color(0.62f, 0.78f, 1f, 1f),
                    TextAnchor.LowerRight, FontStyle.Bold);
                UiFactory.SetRect(widget.CostLabel.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(-10f, 18f), new Vector2(0f, 5f), new Vector2(0.5f, 0f));

                widget.ReasonLabel = UiFactory.CreateText(background.transform, "RejectLabel", "", 12,
                    new Color(1f, 0.55f, 0.4f, 0f), TextAnchor.LowerCenter);
                UiFactory.SetRect(widget.ReasonLabel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f),
                    new Vector2(220f, 34f), new Vector2(0f, 4f), new Vector2(0.5f, 0f));
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
        /// The raid hotbar: the six stacks the 3/4 quick wheels reach, plus one compact line saying what the
        /// rest of the bag is doing. The full 10x4 satchel is not squeezed into the HUD; it is the screen
        /// opened with I or Tab, and this strip only names the stacks the six wheel entries point at.
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
        /// Rebuilds the strip from the live bag. The chips are named Slot1..Slot4 and stand for hotbar slots 0..3 of
        /// Inventory.Hotbar: each shows the live stack behind its bound StackId (current count included), so a
        /// stack that is consumed, removed, merged or shifted is re-read from the bag on the next
        /// InventoryChanged (or HotbarChanged) notification instead of being remembered here.
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

                bool filled = inventory.Hotbar.TryGetStack(i, out InventorySlot slot);
                ItemData item = filled ? slot.Item : null;

                Image chip = UiFactory.CreateImage(_inventoryRow, "Slot" + (i + 1),
                    new Color(0.07f, 0.06f, 0.055f, item != null ? 0.93f : 0.6f),
                    new Vector2(HotbarChipWidth, HotbarChipHeight), new Vector2(x, HotbarChipCentre),
                    new Vector2(0.5f, 0f), new Vector2(0.5f, 0f));
                chip.raycastTarget = false;

                // The key glyph is the hint, and it is the key HotbarController reads for this hotbar slot.
                Text key = UiFactory.CreateText(chip.transform, "Key", (i / 3 + 3) + "." + (i % 3 + 1), 12,
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

            _bagSummaryLabel.text = $"1/2 weapons   hold 3/4 quick wheels   LMB use held item   [I] bag";
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
            bool tookDamage = float.IsFinite(info.Amount) && info.Amount > 0f;
            if (!tookDamage)
            {
                if (info.Blocked) GameEvents.RaiseToast(playerId, info.GuardBroken ? "GUARD BROKEN" : "Blocked");
                return;
            }

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
                // B2: the marker's look follows the authority's hit location of a hit the local player dealt.
                HitMarkerStyle style = HitMarkerStyleFor(feedback.Location, feedback.Headshot);
                _lastHitLocation = feedback.Location;
                if (_hitSlashA != null) _hitSlashA.color = style.Color;
                if (_hitSlashB != null) _hitSlashB.color = style.Color;
                _hitMarker.localScale = new Vector3(style.Scale, style.Scale, 1f);
                _hitMarker.gameObject.SetActive(true);
                _hitMarkerTimer = style.Duration;
            }
        }

        private void OnGuardContact(int playerId, Vector3 point, bool parried)
        {
            if (parried && playerId != GameEvents.NoPlayer && PlayerView.Shows(playerId)) GameEvents.RaiseToast(playerId, "Parry");
        }

        /// <summary>B2: look of the hit marker for one hit location. Body (and an unknown location) is the original marker.</summary>
        internal readonly struct HitMarkerStyle
        {
            public readonly Color Color;
            public readonly float Scale;
            public readonly float Duration;

            public HitMarkerStyle(Color color, float scale, float duration)
            {
                Color = color; Scale = scale; Duration = duration;
            }
        }

        private static readonly HitMarkerStyle HitMarkerBody = new HitMarkerStyle(new Color(1f, 0.35f, 0.3f, 1f), 1f, HitMarkerDuration);
        private static readonly HitMarkerStyle HitMarkerHead = new HitMarkerStyle(new Color(1f, 0.86f, 0.2f, 1f), 1.45f, 0.38f);
        private static readonly HitMarkerStyle HitMarkerArms = new HitMarkerStyle(new Color(1f, 0.5f, 0.42f, 0.8f), 0.85f, HitMarkerDuration);
        private static readonly HitMarkerStyle HitMarkerLegs = new HitMarkerStyle(new Color(0.95f, 0.58f, 0.5f, 0.7f), 0.75f, HitMarkerDuration);

        /// <summary>Head (or any hit flagged as a headshot) gets the distinct gold, larger marker; arms / hands and legs / feet a smaller, subtler one.</summary>
        internal static HitMarkerStyle HitMarkerStyleFor(HitLocation location, bool headshot)
        {
            if (headshot || location == HitLocation.Head) return HitMarkerHead;
            switch (location)
            {
                case HitLocation.Arms:
                case HitLocation.Hands:
                    return HitMarkerArms;
                case HitLocation.Legs:
                case HitLocation.Feet:
                    return HitMarkerLegs;
                default:
                    return HitMarkerBody;
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
            ApplyActiveWeapon(_director != null && _director.Player != null && _director.Player.Loadout != null ? _director.Player.Loadout.ActiveWeapon : null);
        }

        private void OnHotbarChanged(int playerId)
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

            if (!snapshot.HasSkill)
            {
                widget.Background.color = new Color(0.05f, 0.05f, 0.05f, 0.6f);
                if (widget.Icon != null) { widget.Icon.sprite = null; widget.Icon.enabled = false; }
                if (widget.KeyLabel != null) widget.KeyLabel.text = string.Empty;
                if (widget.CostLabel != null) widget.CostLabel.text = string.Empty;
                if (widget.CooldownLabel != null) widget.CooldownLabel.text = string.Empty;
                if (widget.CooldownFill != null) widget.CooldownFill.fillAmount = 0f;
                return;
            }

            Sprite icon = SkillIconAtlas.Get(snapshot.SkillId);
            if (widget.Icon != null)
            {
                if (widget.Icon.sprite != icon) widget.Icon.sprite = icon;
                widget.Icon.enabled = true;
                // No art yet: the skill's accent colour fills the square instead of an empty frame.
                Color baseColor = icon != null ? Color.white : snapshot.AccentColor;
                // Ready but short of mana: the icon greys out (a cooldown is shown by the wedge instead).
                widget.Icon.color = snapshot.Castable || !snapshot.Ready
                    ? baseColor
                    : new Color(baseColor.r * 0.45f, baseColor.g * 0.5f, baseColor.b * 0.7f, 1f);
            }
            widget.Background.color = snapshot.Ready && snapshot.Castable
                ? new Color(0.14f, 0.11f, 0.07f, 0.95f)
                : new Color(0.06f, 0.06f, 0.065f, 0.92f);

            if (widget.KeyLabel != null) widget.KeyLabel.text = snapshot.KeyLabel;
            if (widget.CostLabel != null) widget.CostLabel.text = snapshot.ManaCost > 0 ? snapshot.ManaCost.ToString() : string.Empty;

            float fraction = snapshot.CooldownFraction;
            if (widget.CooldownFill != null) widget.CooldownFill.fillAmount = fraction;
            if (widget.CooldownLabel != null)
            {
                float left = snapshot.CooldownRemaining;
                widget.CooldownLabel.text = fraction > 0.001f
                    ? (left < 1f ? left.ToString("0.0") : Mathf.CeilToInt(left).ToString())
                    : string.Empty;
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
            var held = _director != null && _director.Player != null && _director.Player.Inventory != null ? _director.Player.Inventory.HeldItem : null;
            if (held != null) { _weaponLabel.text = held.displayName + (held.healAmount > 0 || held.ammoGrant > 0 ? "  [LMB]" : ""); return; }
            // D3c: weapon set 2 is marked; set 1 reads exactly as before.
            string setTag = _director != null && _director.Player != null && _director.Player.Equipment != null &&
                            _director.Player.Equipment.ActiveWeaponSet == 1 ? "  (II)" : string.Empty;
            _weaponLabel.text = (weapon != null ? weapon.displayName : "Unarmed") + setTag;
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
            if (_director == null) GameServices.TryGet<RunDirector>(out _director);
            if (_director != null && (_director.LocalPlayerState == RunState.Dead || _director.LocalPlayerState == RunState.Extracted))
                gameplay = false;
            _root.gameObject.SetActive(gameplay);

            if (gameplay && state == RunState.Running)
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
            _kitLabel.gameObject.SetActive(false); // the passives are shown with their icons in the inventory window, not as a HUD list
            return;

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
