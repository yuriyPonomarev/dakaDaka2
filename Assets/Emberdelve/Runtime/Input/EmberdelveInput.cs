using UnityEngine;
using UnityEngine.InputSystem;

namespace Emberdelve.InputLayer
{
    /// <summary>
    /// The only component that touches input devices. Everything else consumes plain properties, so a
    /// future networked build can swap this for a remote-input source without movement, combat or
    /// interaction code changing.
    ///
    /// Bindings live in Assets/Emberdelve/Data/EmberdelveInputActions.inputactions and are editable in
    /// the Input Actions editor. That asset is generated from EmberdelveInputFallback, which is also the
    /// runtime fallback, so the two can never drift.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EmberdelveInput : MonoBehaviour
    {
        [Header("Input asset (leave empty to use built-in fallback bindings)")]
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private string playerMapName = EmberdelveInputFallback.PlayerMapName;
        [SerializeField] private string systemMapName = EmberdelveInputFallback.SystemMapName;

        private InputActionMap _playerMap;
        private InputActionMap _systemMap;
        private InputAction _move;
        private InputAction _look;
        private InputAction _jump;
        private InputAction _sprint;
        private InputAction _crouch;
        private InputAction _primary;
        private InputAction _secondary;
        private InputAction _interact;
        private InputAction _reload;
        private InputAction _swap;
        private InputAction _skill1;
        private InputAction _skill2;
        private InputAction _inventory;
        private InputAction _pause;
        private InputAction _inventorySystem;
        private InputAction _pauseSystem;
        private readonly InputAction[] _slots = new InputAction[4];
        private bool _ownsActions;

        /// <summary>Test seams (D1b, same idea as the primary overrides): replace the real move / sprint / guard readings while the player map is live. Never set in game code.</summary>
        internal Vector2? MoveOverride { get; set; }
        internal bool? SprintHeldOverride { get; set; }
        internal bool? SecondaryHeldOverride { get; set; }

        public Vector2 Move => MoveOverride.HasValue ? (_playerMap != null && _playerMap.enabled ? MoveOverride.Value : Vector2.zero)
            : _move != null ? _move.ReadValue<Vector2>() : Vector2.zero;
        public Vector2 Look => _look != null ? _look.ReadValue<Vector2>() : Vector2.zero;
        public bool JumpPressed => _jump != null && _jump.WasPressedThisFrame();
        public bool SprintHeld => SprintHeldOverride.HasValue ? SprintHeldOverride.Value && _playerMap != null && _playerMap.enabled
            : _sprint != null && _sprint.IsPressed();
        public bool CrouchPressed => _crouch != null && _crouch.WasPressedThisFrame();
        /// <summary>
        /// Test seam for batch-mode harnesses that have no pointer device (the same idea as
        /// <see cref="InteractHeldOverride"/>): when set, each replaces the real primary-attack reading for the
        /// edge or level it names, so the REAL MeleeCombat/RangedCombat Update path runs from a button press
        /// instead of a hand-built intent. They still respect disabled gameplay input. Null (the default)
        /// means the real action is read. A harness holds one for exactly one frame to produce an edge.
        /// Never set in game code.
        /// </summary>
        internal bool? PrimaryPressedOverride { get; set; }
        internal bool? PrimaryHeldOverride { get; set; }
        internal bool? PrimaryReleasedOverride { get; set; }

        private bool OverrideLive => _playerMap != null && _playerMap.enabled;

        public bool PrimaryPressed => PrimaryPressedOverride.HasValue
            ? PrimaryPressedOverride.Value && OverrideLive
            : _primary != null && _primary.WasPressedThisFrame();
        public bool PrimaryHeld => PrimaryHeldOverride.HasValue
            ? PrimaryHeldOverride.Value && OverrideLive
            : _primary != null && _primary.IsPressed();
        public bool PrimaryReleased => PrimaryReleasedOverride.HasValue
            ? PrimaryReleasedOverride.Value && OverrideLive
            : _primary != null && _primary.WasReleasedThisFrame();
        public bool SecondaryHeld => SecondaryHeldOverride.HasValue ? SecondaryHeldOverride.Value && _playerMap != null && _playerMap.enabled
            : _secondary != null && _secondary.IsPressed();
        public bool SecondaryPressed => _secondary != null && _secondary.WasPressedThisFrame();
        public bool InteractPressed => _interact != null && _interact.WasPressedThisFrame();
        /// <summary>
        /// Test seam for batch-mode harnesses that have no input device: when set, it replaces the real
        /// interact-held reading, but still respects disabled gameplay input. Null (the default) means the real action is read. Never set in game code.
        /// </summary>
        internal bool? InteractHeldOverride { get; set; }

        public bool InteractHeld => InteractHeldOverride.HasValue
            ? InteractHeldOverride.Value && _playerMap != null && _playerMap.enabled
            : _interact != null && _interact.IsPressed();
        public bool ReloadPressed => _reload != null && _reload.WasPressedThisFrame();
        public bool SwapPressed => _swap != null && _swap.WasPressedThisFrame();
        public bool Skill1Pressed => _skill1 != null && _skill1.WasPressedThisFrame();
        public bool Skill2Pressed => _skill2 != null && _skill2.WasPressedThisFrame();

        /// <summary>
        /// What the player actually has to press for a skill slot, read from the bindings themselves so the
        /// HUD label can never disagree with the asset.
        /// </summary>
        public string SkillKeyLabel(int slot)
        {
            InputAction action = slot == 0 ? _skill1 : slot == 1 ? _skill2 : null;
            if (action == null || action.bindings.Count == 0) return slot == 0 ? "F" : "G";

            for (int i = 0; i < action.bindings.Count; i++)
            {
                InputBinding binding = action.bindings[i];
                if (binding.groups != null && binding.groups.Contains("Keyboard")) return action.GetBindingDisplayString(i);
            }

            return action.GetBindingDisplayString(0);
        }

        public bool InventoryPressed => (_inventorySystem != null && _inventorySystem.WasPressedThisFrame())
                                        || (_inventory != null && _inventory.WasPressedThisFrame());
        public bool PausePressed => (_pauseSystem != null && _pauseSystem.WasPressedThisFrame())
                                    || (_pause != null && _pause.WasPressedThisFrame());

        public bool SlotPressed(int index)
        {
            if (index < 0 || index >= _slots.Length) return false;
            InputAction action = _slots[index];
            return action != null && action.WasPressedThisFrame();
        }

        private void Awake()
        {
            if (actions == null)
            {
                Debug.LogWarning("[EmberdelveInput] No InputActionAsset assigned; using built-in fallback bindings.", this);
                actions = EmberdelveInputFallback.Build();
            }
            else
            {
                // R7: an InputActionAsset holds its action maps, and Enable/Disable act on the ASSET, so every rig that shared the
                // project asset shared one enabled state: one player turning its gameplay input off would have switched every
                // other player's off. Each rig now owns its own instance (the fallback above is already a fresh one).
                actions = Instantiate(actions);
                _ownsActions = true;
            }

            _playerMap = actions.FindActionMap(playerMapName);
            if (_playerMap == null)
            {
                Debug.LogError($"[EmberdelveInput] Action map '{playerMapName}' was not found in the assigned asset.", this);
                return;
            }

            _systemMap = actions.FindActionMap(systemMapName);

            _move = _playerMap.FindAction("Move");
            _look = _playerMap.FindAction("Look");
            _jump = _playerMap.FindAction("Jump");
            _sprint = _playerMap.FindAction("Sprint");
            _crouch = _playerMap.FindAction("Crouch");
            _primary = _playerMap.FindAction("PrimaryAttack");
            _secondary = _playerMap.FindAction("SecondaryAction");
            _interact = _playerMap.FindAction("Interact");
            _reload = _playerMap.FindAction("Reload");
            _swap = _playerMap.FindAction("SwapWeapon");
            _skill1 = _playerMap.FindAction("Skill1");
            _skill2 = _playerMap.FindAction("Skill2");
            _inventory = _playerMap.FindAction("Inventory");
            _pause = _playerMap.FindAction("Pause");

            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i] = _playerMap.FindAction($"UseItem{i + 1}");
            }

            if (_systemMap != null)
            {
                _pauseSystem = _systemMap.FindAction("Pause");
                _inventorySystem = _systemMap.FindAction("Inventory");
            }
        }

        private void OnEnable()
        {
            SetGameplayInputEnabled(true);
            if (_systemMap != null) _systemMap.Enable();
        }

        private void OnDisable() => SetGameplayInputEnabled(false);
        private void OnDestroy()
        {
            if (actions != null) actions.Disable();
            if (_ownsActions && actions != null) Destroy(actions);
        }

        public void SetGameplayInputEnabled(bool enabled)
        {
            if (_playerMap == null) return;
            if (enabled) _playerMap.Enable();
            else _playerMap.Disable();
        }
    }
}
