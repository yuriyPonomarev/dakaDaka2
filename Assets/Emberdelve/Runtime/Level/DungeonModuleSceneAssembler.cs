using System;
using System.Collections;
using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Player;
using Emberdelve.Rules;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Emberdelve.Level
{
    /// <summary>
    /// Applies the shared plan using authored room prefabs, then replaces the scene's legacy blockout layout before RunDirector.Start.
    /// This component is attached to the dungeon scene by the level builder; it does not generate room geometry from primitives.
    /// </summary>
    [DefaultExecutionOrder(-11000)]
    [DisallowMultipleComponent]
    public sealed class DungeonModuleSceneAssembler : MonoBehaviour, IDungeonModulePlanConsumer
    {
        private const int ModulesPerAxis = DungeonModulePlan.Width;
        private const string CatalogResource = "Emberdelve/DungeonModuleCatalog";
        private static readonly string[] LegacyGeometryRoots =
        {
            "Level_Blockout", "Crypt_Architecture", "Crypt_Archways", "Crypt_Wall_Tombs", "Torches", "Props"
        };

        [SerializeField] private DungeonLayoutData sourceLayout;
        [SerializeField] private DungeonModuleCatalog catalog;
        [SerializeField] private Transform generatedRoot;
        [SerializeField] private Vector3 mapOrigin;
        [SerializeField] private bool enableOnOffline;

        private readonly Transform[] _moduleRoots = new Transform[DungeonModulePlan.Width * DungeonModulePlan.Height];
        private readonly Transform[] _spawnAnchors = new Transform[DungeonModulePlan.Width * DungeonModulePlan.Height];
        private DungeonLayoutData _runtimeLayout;
        private NavGridData _runtimeNav;
        private string _appliedHash = string.Empty;
        private Coroutine _planRetryRoutine;
        private bool _stagedFloor;
        public bool RuntimeContentRetired { get; private set; }
        private static readonly List<DungeonModuleSceneAssembler> RetiredMaps = new List<DungeonModuleSceneAssembler>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRetiredMaps() => RetiredMaps.Clear();

        /// <summary>Presentation query over retired live maps only; no scene search or actor mutation.</summary>
        public static bool IsPositionOnRetiredFloor(Vector3 position)
        {
            for (int i = RetiredMaps.Count - 1; i >= 0; i--)
            {
                var map = RetiredMaps[i];
                // EditMode/manual lifecycle teardown may not deliver OnDestroy.
                if (map == null) { RetiredMaps.RemoveAt(i); continue; }
                var layout = map.RuntimeLayout;
                if (layout == null) continue;
                var bounds = new Rect(layout.origin.x, layout.origin.z,
                    layout.gridWidth * layout.CellSize, layout.gridHeight * layout.CellSize);
                if (bounds.Contains(new Vector2(position.x, position.z))) return true;
            }
            return false;
        }
        private RoyalePartyMode? _royaleSpawnMode;
        private HashSet<Vector2Int> _reachableNav;
        private EnemyAgent[] _initialEnemies = Array.Empty<EnemyAgent>();
        private LootPickup[] _initialLoot = Array.Empty<LootPickup>();
        private DungeonModulePlan _appliedPlan;
        public RoyalePartyMode? AppliedRoyaleMode => _royaleSpawnMode;
        public DungeonModulePlan CopyAppliedPlan() => _appliedPlan?.WithRoyaleMode(_appliedPlan.RoyaleMode);

        public bool EnsureInitialContentReady(out string reason)
        {
            reason = "Initial content is not ready";
            if (RuntimeContentRetired || _stagedFloor || !RuntimeMapValidationComplete || _runtimeLayout.enemySpawns.Count != _initialEnemies.Length
                || _runtimeLayout.lootSpawns.Count != _initialLoot.Length) return false;
            foreach (var enemy in _initialEnemies)
                if (enemy == null || !enemy.EnsureRuntimeInitialized()) return false;
            foreach (var loot in _initialLoot) if (loot == null) return false;
            reason = null; return true;
        }
        public bool AcceptsSessionPlan => !_stagedFloor;

        /// <summary>Preserves layout/plan metadata used by subsequent floors. The initial assembler may
        /// share its GameObject with run services, so only its owned content is disabled.</summary>
        public void RetireRuntimeContent()
        {
            if (RuntimeContentRetired) return;
            RuntimeContentRetired = true;
            RetiredMaps.Add(this);
            if (_stagedFloor) gameObject.SetActive(false);
            else
            {
                if (generatedRoot != null) generatedRoot.gameObject.SetActive(false);
                foreach (var enemy in _initialEnemies) if (enemy != null) enemy.gameObject.SetActive(false);
                foreach (var loot in _initialLoot) if (loot != null) loot.gameObject.SetActive(false);
            }
        }

        /// <summary>Builds an isolated floor without installing a director layout or moving any live actor.
        /// The caller supplies a non-overlapping staging origin. Success returns an inactive map; activation
        /// and participant transfer belong to the authority's later commit barrier.</summary>
        public static bool TryStageFloor(DungeonLayoutData source, DungeonModuleCatalog modules,
            DungeonModulePlan plan, Vector3 stagingOrigin, out DungeonModuleSceneAssembler staged, out string reason,
            RoyalePartyMode? partyMode = null)
        {
            staged = null;
            reason = "Invalid staging origin";
            if (!float.IsFinite(stagingOrigin.x) || !float.IsFinite(stagingOrigin.y) || !float.IsFinite(stagingOrigin.z)) return false;
            if (partyMode.HasValue && (partyMode.Value < RoyalePartyMode.Solo || partyMode.Value > RoyalePartyMode.Trio))
            { reason = "Invalid party mode"; return false; }
            if (plan == null) { reason = "Missing floor plan"; return false; }
            if (!plan.Validate(out reason)) return false;
            if (partyMode.HasValue && partyMode != plan.RoyaleMode)
            { reason = "Floor party mode differs from the authoritative plan"; return false; }
            var root = new GameObject("Staged_Dungeon_Floor");
            root.SetActive(false);
            var candidate = root.AddComponent<DungeonModuleSceneAssembler>();
            candidate._stagedFloor = true;
            candidate._royaleSpawnMode = plan.RoyaleMode;
            candidate.Configure(source, modules);
            candidate.mapOrigin = stagingOrigin;
            root.SetActive(true); // Physics validation needs active colliders; Awake does not opt into the live run.
            try
            {
                if (!candidate.ApplyDungeonModulePlan(plan, out reason)) return false;
                root.SetActive(false);
                staged = candidate;
                return true;
            }
            finally
            {
                if (staged == null)
                {
                    candidate.DiscardStagedFloor();
                }
            }
        }

        public bool BindStagedFloorIdentity(int floor, out string reason)
        {
            reason = "Invalid staged floor identity";
            if (!_stagedFloor || floor < 2 || floor > 3) return false;
            foreach (var door in GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>(true))
                if (!door.BindStagedFloor(floor)) return false;
            foreach (var barrel in GetComponentsInChildren<Emberdelve.Interaction.BreakableBarrel>(true))
                if (!barrel.BindStagedFloor(floor)) return false;
            reason = null; return true;
        }

        private static void DestroyOwnedObject(UnityEngine.Object value)
        {
            if (value == null) return;
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }

        /// <summary>Explicit ownership cleanup, including EditMode where MonoBehaviour.OnDestroy may not run.</summary>
        public void DiscardStagedFloor()
        {
            if (!_stagedFloor) throw new InvalidOperationException("Only a staged floor can be discarded by this API");
            RetiredMaps.Remove(this);
            gameObject.SetActive(false);
            ReleaseStagedAssets();
            DestroyOwnedObject(gameObject);
        }

        private void ReleaseStagedAssets()
        {
            DestroyOwnedObject(_runtimeLayout);
            DestroyOwnedObject(_runtimeNav);
            _runtimeLayout = null;
            _runtimeNav = null;
        }

        public DungeonLayoutData RuntimeLayout => _runtimeLayout;
        public NavGridData RuntimeNavGrid => _runtimeNav;
        public string AppliedLayoutHash => _appliedHash;
        public bool RuntimeMapValidationComplete => !string.IsNullOrEmpty(_appliedHash) && _runtimeLayout != null && _runtimeNav != null;

        /// <summary>Development fixture positions for real combat network gates, kept inside the authored entry module.</summary>
        public bool TryGetCombatTestPair(out Vector3 first, out Vector3 second, out string reason)
        {
            first = default;
            second = default;
            reason = string.Empty;
            if (!Application.isEditor && !Debug.isDebugBuild)
            { reason = "development fixture requested by a non-development build"; return false; }
            if (!RuntimeMapValidationComplete || _moduleRoots[0] == null || _spawnAnchors[0] == null)
            { reason = "the modular entry room is not installed"; return false; }

            Vector3 center = _spawnAnchors[0].position;
            Vector3[] axes = { _moduleRoots[0].right.normalized, _moduleRoots[0].forward.normalized };
            for (int axis = 0; axis < axes.Length; axis++)
            {
                // Eighteen metres keeps the ranged gate inside the same authored room and preserves its existing data-derived
                // target distance (which is capped at 18 m), with clearance from the enlarged room boundaries.
                Vector3 offset = axes[axis] * 9f;
                Vector3 candidateA = center - offset + Vector3.up * 0.15f;
                Vector3 candidateB = center + offset + Vector3.up * 0.15f;
                Physics.SyncTransforms();
                if (!IsCombatFixtureCapsuleClear(candidateA) || !IsCombatFixtureCapsuleClear(candidateB)) continue;
                if (!HasCombatFixtureLineOfSight(candidateA, candidateB)) continue;
                first = candidateA;
                second = candidateB;
                return true;
            }

            reason = "entry module has no tested pair of clear, non-overlapping combat positions";
            return false;
        }

        private static bool IsCombatFixtureCapsuleClear(Vector3 rootPosition)
        {
            const float radius = 0.34f;
            const float height = 1.8f;
            Vector3 bottom = rootPosition + Vector3.up * (radius + 0.02f);
            Vector3 top = rootPosition + Vector3.up * (height - radius);
            Collider[] overlaps = Physics.OverlapCapsule(bottom, top, radius, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < overlaps.Length; i++)
            {
                Collider collider = overlaps[i];
                if (collider == null || collider.GetComponentInParent<PlayerRig>() != null) continue;
                return false;
            }
            return true;
        }

        private static bool HasCombatFixtureLineOfSight(Vector3 first, Vector3 second)
        {
            Vector3 from = first + Vector3.up * 1.25f;
            Vector3 to = second + Vector3.up * 1.25f;
            Vector3 delta = to - from;
            RaycastHit[] hits = Physics.RaycastAll(from, delta.normalized, delta.magnitude, Physics.AllLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits.Length; i++)
            {
                Collider collider = hits[i].collider;
                if (collider != null && collider.GetComponentInParent<PlayerRig>() == null) return false;
            }
            return true;
        }

        public static bool IsTransientInitializationFailure(string reason)
        {
            return string.Equals(reason, "no modular scene assembler is present", StringComparison.Ordinal) ||
                   string.Equals(reason, "RunDirector is missing", StringComparison.Ordinal) ||
                   string.Equals(reason, "modular scene has no active extraction interactable", StringComparison.Ordinal);
        }

        public void Configure(DungeonLayoutData layout, DungeonModuleCatalog moduleCatalog, bool enableOfflineModules = false)
        {
            sourceLayout = layout;
            catalog = moduleCatalog;
            enableOnOffline = enableOfflineModules;
        }

        private void Awake()
        {
            // This component is also authored by the shared level builder on legacy scenes. Only a
            // scene explicitly built as Modular Crypts opts into plan assembly or the RunDirector gate.
            if (!enableOnOffline) return;

            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            if (director == null)
            {
                Debug.LogError("[DungeonModuleSceneAssembler] Modular scene has no RunDirector; gameplay remains unavailable", this);
                enabled = false;
                return;
            }
            if (!director.RequireRuntimeLayoutBeforeStart())
            {
                Debug.LogError("[DungeonModuleSceneAssembler] Could not arm fail-closed RunDirector start gate before Start; disabling the director", this);
                director.enabled = false;
                enabled = false;
                return;
            }

            if (catalog == null) catalog = Resources.Load<DungeonModuleCatalog>(CatalogResource);
            if (sourceLayout == null)
            {
                if (director != null) sourceLayout = director.Layout;
            }

            // Existing scenes do not opt in merely because a catalog was added to Resources.
            if (catalog == null)
            {
                Debug.LogError("[DungeonModuleSceneAssembler] Modular scene is opted in but its authored Resources catalog is missing; RunDirector remains gated", this);
                return;
            }

            DungeonModulePlanSession.Changed += OnPlanChanged;
            if (!NetRole.IsNetworked)
            {
                RoyalePartyMode mode = RunContext.SelectedRoyaleMode ?? RoyalePartyMode.Solo;
                RunContext.SelectRoyaleMode(mode);
                DungeonModulePlanSession.ClearCurrent();
                ulong raidSeed = RunContext.ResolveRaidSeed(out _);
                RunContext.SetNextRaidSeed(raidSeed);
                int seed = unchecked((int)(raidSeed ^ (raidSeed >> 32)));
                if (DungeonModulePlanner.TryCreate(seed, catalog.Modules, out DungeonModulePlan plan, out string reason))
                    DungeonModulePlanSession.Install(plan.WithRoyaleMode(mode));
                else Debug.LogError("[DungeonModuleSceneAssembler] Could not create offline map: " + reason, this);
            }
            else if (DungeonModulePlanSession.Current != null)
            {
                OnPlanChanged(DungeonModulePlanSession.Current);
            }
        }

        private void OnDestroy()
        {
            RetiredMaps.Remove(this);
            DungeonModulePlanSession.Changed -= OnPlanChanged;
            if (_planRetryRoutine != null) StopCoroutine(_planRetryRoutine);
            if (_stagedFloor)
            {
                ReleaseStagedAssets();
            }
        }

        private void OnPlanChanged(DungeonModulePlan plan)
        {
            if (!gameObject.scene.IsValid()) return;
            if (ApplyDungeonModulePlan(plan, out string reason)) return;

            if (!IsTransientInitializationFailure(reason))
            {
                Debug.LogError("[DungeonModuleSceneAssembler] rejected authoritative modular plan seed=" +
                               (plan != null ? plan.Seed.ToString() : "<null>") + ": " + reason, this);
                return;
            }

            Debug.LogWarning("[DungeonModuleSceneAssembler] initial plan apply deferred seed=" +
                             (plan != null ? plan.Seed.ToString() : "<null>") + ": " + reason +
                             "; retrying briefly while scene actors finish initialization", this);
            if (_planRetryRoutine != null) StopCoroutine(_planRetryRoutine);
            _planRetryRoutine = StartCoroutine(RetryPlanApply(plan));
        }

        private IEnumerator RetryPlanApply(DungeonModulePlan plan)
        {
            const int maxAttempts = 64;
            const float retryInterval = 0.25f;
            string lastReason = "plan has not been applied";
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                yield return new WaitForSecondsRealtime(retryInterval);
                if (this == null || !gameObject.scene.IsValid() || DungeonModulePlanSession.Current != plan) break;
                if (ApplyDungeonModulePlan(plan, out lastReason))
                {
                    Debug.Log("[DungeonModuleSceneAssembler] modular plan applied after initialization retry attempt=" + attempt +
                              " seed=" + plan.Seed + " hash=" + plan.LayoutHash, this);
                    _planRetryRoutine = null;
                    yield break;
                }
                if (!IsTransientInitializationFailure(lastReason)) break;
            }

            if (this != null && DungeonModulePlanSession.Current == plan)
                Debug.LogError("[DungeonModuleSceneAssembler] bounded plan-apply retries exhausted; gameplay remains gated. seed=" +
                               plan.Seed + " reason=" + lastReason, this);
            _planRetryRoutine = null;
        }

        public bool ApplyDungeonModulePlan(DungeonModulePlan plan, out string reason)
        {
            reason = string.Empty;
            if (RuntimeContentRetired) { reason = "Floor content is retired"; return false; }
            if (plan == null || !plan.Validate(out reason)) return false;
            if (!string.Equals(plan.LayoutHash, DungeonModulePlanner.ComputeHashForWire(plan), StringComparison.OrdinalIgnoreCase))
            { reason = "plan contains a stale layout hash"; return false; }
            if (catalog == null || catalog.Modules == null || catalog.Modules.Length == 0)
            { reason = "authored module catalog is missing"; return false; }
            // Validate every reference before instantiation or destruction of any existing geometry.
            foreach (DungeonModuleDefinition definition in catalog.Modules)
                if (definition == null || !definition.HasValidSockets(out reason))
                { reason = "module catalog contains a missing or invalid definition: " + reason; return false; }
            if (plan.CatalogHash != DungeonModulePlanner.ComputeCatalogHash(catalog.Modules))
            { reason = "plan catalog does not match the floor catalog"; return false; }
            foreach (DungeonModulePlacement placement in plan.Placements)
                if (placement.ModuleIndex < 0 || placement.ModuleIndex >= catalog.Modules.Length)
                { reason = "module index is not available in the floor catalog"; return false; }
            DungeonModulePlacement centerPlacement = plan.At(1, 1);
            if (centerPlacement.ModuleIndex < 0 || centerPlacement.ModuleIndex >= catalog.Modules.Length ||
                catalog.Modules[centerPlacement.ModuleIndex] == null ||
                catalog.Modules[centerPlacement.ModuleIndex].Kind != DungeonModuleKind.Bridge)
            { reason = "the center cell must use the catalog's dedicated bridge module"; return false; }
            if (sourceLayout == null) { reason = "source layout is missing"; return false; }
            if (sourceLayout.CellSize != 2) { reason = "module prefabs require a two-metre cell size"; return false; }
            // A plan can be applied from this component's earlier Awake, before NetSceneRoles applies its role switches.
            // Revalidate the completed scene each time the network layer asks for readiness.
            if (plan.LayoutHash == _appliedHash) return ValidateAppliedRuntimeState(plan, out reason);
            if (!string.IsNullOrEmpty(_appliedHash))
            { reason = "an installed floor cannot be replaced in place; stage a separate floor"; return false; }

            if (!_stagedFloor && !NetRole.IsClientOnly)
            {
                var authority = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
                if (authority == null) { reason = "RunDirector is missing"; return false; }
                if (authority.RoyaleMode != plan.RoyaleMode
                    && (!plan.RoyaleMode.HasValue || !authority.ConfigureRoyale(plan.RoyaleMode.Value)))
                { reason = "Royale mode must match the authoritative plan before the run starts"; return false; }
            }
            _royaleSpawnMode = plan.RoyaleMode;
            ClearGeneratedModules();
            if (generatedRoot == null)
            {
                GameObject root = new GameObject("Dungeon_Modules_Runtime");
                root.transform.SetParent(transform, false);
                generatedRoot = root.transform;
            }

            DungeonModuleDefinition[] definitions = catalog.Modules;
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement placement = plan.Placements[i];
                if (placement.ModuleIndex < 0 || placement.ModuleIndex >= definitions.Length || definitions[placement.ModuleIndex] == null)
                { reason = "module index " + placement.ModuleIndex + " is not available on this process"; ClearGeneratedModules(); return false; }

                DungeonModuleDefinition definition = definitions[placement.ModuleIndex];
                if (!definition.HasValidSockets(out string moduleReason))
                { reason = "module " + definition.ModuleId + " is invalid: " + moduleReason; ClearGeneratedModules(); return false; }
                Vector3 position = ModuleCenter(placement.X, placement.Y, sourceLayout.CellSize);
                Quaternion rotation = Quaternion.Euler(0f, placement.QuarterTurnsClockwise * 90f, 0f);
                DungeonModuleDefinition instance = Instantiate(definition, position, rotation, generatedRoot);
                instance.name = definition.ModuleId + "_" + placement.X + "_" + placement.Y;
                ApplyDoorClosures(instance.transform, placement.OpenSockets, placement.QuarterTurnsClockwise);
                _moduleRoots[i] = instance.transform;
                _spawnAnchors[i] = instance.PlayerSpawnAnchor;
            }

            if (!ValidateSocketAlignment(plan, out reason)) { ClearGeneratedModules(); return false; }
            if (!DungeonDoorwaySpawner.TryBuild(plan, _moduleRoots, out List<Emberdelve.Interaction.DungeonDoorInteractable> doors, out reason))
            { ClearGeneratedModules(); return false; }
            if (!ValidateClosureState(plan, out reason)) { ClearGeneratedModules(); return false; }
            if (!PrepareGameplayLayout(plan, out reason)) { ClearGeneratedModules(); return false; }
            if (_stagedFloor)
            {
                _appliedPlan = plan.WithRoyaleMode(plan.RoyaleMode);
                _appliedHash = plan.LayoutHash;
                return true;
            }
            DisableLegacyGeometry();
            Emberdelve.Interaction.WorldLightInteractable.Install(generatedRoot);
            if (!PlaceExistingGameplayActors(out reason)) { ClearGeneratedModules(); return false; }
            if (!ValidateExtractionPlacement(out reason)) { ClearGeneratedModules(); return false; }
            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            _appliedPlan = plan.WithRoyaleMode(plan.RoyaleMode);
            _appliedHash = plan.LayoutHash;
            if (director == null || director.Layout != _runtimeLayout || !director.CompleteRuntimeLayoutInstallation())
            {
                _appliedHash = string.Empty;
            _appliedPlan = null;
                reason = "RunDirector did not accept completed modular layout before run start";
                ClearGeneratedModules();
                return false;
            }
            Debug.Log("[DungeonModuleSceneAssembler] applied 3x3 plan seed=" + plan.Seed + " hash=" + plan.LayoutHash, this);
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            Debug.Log("[modular-proof] scene-applied hash=" + plan.LayoutHash + " modules=9 doors=" + doors.Count +
                      " closuresConsistent=true playerSpawns=" + _runtimeLayout.playerSpawns.Count +
                      " extraction=active-at-cell:" + _runtimeLayout.extractionCell + " navConnected=true", this);
#endif
            return true;
        }

        private bool ValidateAppliedRuntimeState(DungeonModulePlan plan, out string reason)
        {
            if (_runtimeLayout == null || _runtimeNav == null)
            { reason = "modular runtime layout or navigation grid is missing"; return false; }
            if (!DungeonDoorwaySpawner.ValidateBuilt(plan, _moduleRoots, out reason)) return false;
            if (_stagedFloor)
                return ValidateClosureState(plan, out reason) && ValidateNavConnectivity(_runtimeLayout, out reason);
            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            if (director == null || director.Layout != _runtimeLayout || !director.RuntimeLayoutInstallationComplete)
            { reason = "RunDirector has not committed the runtime modular layout"; return false; }
            if (!NetRole.IsClientOnly && director.RoyaleMode != plan.RoyaleMode)
            { reason = "RunDirector mode differs from the installed map"; return false; }
            if (!ValidateClosureState(plan, out reason)) return false;
            if (!ValidateNavConnectivity(_runtimeLayout, out reason)) return false;

            if (!ValidateExtractionPlacement(out reason)) return false;
            return true;
        }

        private bool PrepareGameplayLayout(DungeonModulePlan plan, out string reason)
        {
            reason = string.Empty;
            _runtimeLayout = Instantiate(sourceLayout);
            _runtimeLayout.name = "Runtime_Modular_Dungeon_" + plan.LayoutHash;
            _runtimeLayout.hideFlags = HideFlags.DontSave;
            _runtimeLayout.gridWidth = DungeonModulePlan.Width * DungeonModulePlan.ModuleWidthCells;
            _runtimeLayout.gridHeight = DungeonModulePlan.Height * DungeonModulePlan.ModuleHeightCells;
            _runtimeLayout.cellSize = sourceLayout.CellSize;
            _runtimeLayout.origin = mapOrigin;
            _runtimeLayout.rooms.Clear();
            _runtimeLayout.corridors.Clear();
            _runtimeLayout.playerSpawns.Clear();
            Physics.SyncTransforms();

            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                int x0 = p.X * DungeonModulePlan.ModuleWidthCells;
                int y0 = p.Y * DungeonModulePlan.ModuleHeightCells;
                _runtimeLayout.rooms.Add(new RoomSpec
                {
                    roomName = catalog.Modules[p.ModuleIndex].ModuleId,
                    minCell = new Vector2Int(x0 + 1, y0 + 1),
                    maxCell = new Vector2Int(x0 + DungeonModulePlan.ModuleWidthCells - 2, y0 + DungeonModulePlan.ModuleHeightCells - 2)
                });
                if (_spawnAnchors[i] == null) { reason = "module " + catalog.Modules[p.ModuleIndex].ModuleId + " has no spawn anchor"; return false; }
                Vector2Int anchor = _runtimeLayout.CellFromWorld(_spawnAnchors[i].position);
                if (!TryFindClearSpawnCell(_runtimeLayout, _moduleRoots[i], anchor,
                        new Vector2Int(x0 + 1, y0 + 1),
                        new Vector2Int(x0 + DungeonModulePlan.ModuleWidthCells - 2, y0 + DungeonModulePlan.ModuleHeightCells - 2), out Vector2Int spawn))
                { reason = "module " + catalog.Modules[p.ModuleIndex].ModuleId + " has no physically clear player spawn"; return false; }
                _runtimeLayout.playerSpawns.Add(spawn);
            }

            AddOpenCorridors(plan);
            // The authored entry anchor has already been resolved against geometry. The geometric room
            // centre may contain a pillar/raised feature and is not a valid navigation origin.
            _runtimeLayout.playerStartCell = _runtimeLayout.playerSpawns[0];
            _runtimeLayout.extractionCell = _runtimeLayout.CellFromWorld(ModuleCenter(DungeonModulePlan.Width - 1, DungeonModulePlan.Height - 1, _runtimeLayout.CellSize));
            _runtimeNav = ScriptableObject.CreateInstance<NavGridData>();
            _runtimeNav.name = "Runtime_Modular_Nav_" + plan.LayoutHash;
            _runtimeNav.hideFlags = HideFlags.DontSave;
            _runtimeNav.width = _runtimeLayout.gridWidth;
            _runtimeNav.height = _runtimeLayout.gridHeight;
            _runtimeNav.cellSize = _runtimeLayout.CellSize;
            _runtimeNav.origin = _runtimeLayout.origin;
            _runtimeNav.mask = _runtimeLayout.BuildNavMask();
            NavGridSurface.Bake(_runtimeNav, generatedRoot, LayerMask.GetMask(GameConfig.LevelLayerName));
            _reachableNav = ReachableNavCells(_runtimeLayout.playerStartCell);
            if (_reachableNav.Count == 0)
            { reason = "resolved entry spawn is blocked in baked navigation: " + _runtimeLayout.playerStartCell; return false; }
            if (_royaleSpawnMode.HasValue && !TryPlaceRoyaleParties(out reason)) return false;
            if (!RemapContent(_runtimeLayout, plan, out reason)) return false;
            List<string> problems = _runtimeLayout.Validate();
            if (problems.Count > 0) { reason = "generated gameplay layout is invalid: " + string.Join("; ", problems); return false; }
            if (!ValidateNavConnectivity(_runtimeLayout, out reason)) return false;
            if (_stagedFloor) return true;

            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            if (director == null) { reason = "RunDirector is missing"; return false; }
            if (!director.SetRuntimeLayout(_runtimeLayout)) { reason = "RunDirector already started before module layout install"; return false; }
            director.SetRuntimeDungeonSceneName(SceneManager.GetActiveScene().name);
            return true;
        }

        /// <summary>Finds the nearest cell whose standing player capsule clears the authored module geometry.</summary>
        public static bool TryFindClearSpawnCell(DungeonLayoutData layout, Transform moduleRoot, Vector2Int anchor,
            Vector2Int min, Vector2Int max, out Vector2Int result)
        {
            result = default;
            if (layout == null || moduleRoot == null || min.x > max.x || min.y > max.y) return false;
            var secrets = moduleRoot.GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>(true);
            int maxDistance = Mathf.Max(Mathf.Abs(anchor.x - min.x), Mathf.Abs(anchor.x - max.x)) +
                              Mathf.Max(Mathf.Abs(anchor.y - min.y), Mathf.Abs(anchor.y - max.y));
            for (int distance = 0; distance <= maxDistance; distance++)
                for (int y = min.y; y <= max.y; y++)
                    for (int x = min.x; x <= max.x; x++)
                    {
                        if (Mathf.Abs(x - anchor.x) + Mathf.Abs(y - anchor.y) != distance) continue;
                        Vector2Int cell = new Vector2Int(x, y);
                        Vector3 root = layout.CellCenterWorld(cell) + Vector3.up * 0.15f;
                        if (IsInsideSecretRoom(secrets, root)) continue;
                        const float radius = 0.36f;
                        Vector3 bottom = root + Vector3.up * (radius + 0.02f);
                        Vector3 top = root + Vector3.up * (1.8f - radius);
                        Collider[] overlaps = Physics.OverlapCapsule(bottom, top, radius, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                        bool blocked = false;
                        for (int i = 0; i < overlaps.Length; i++)
                        {
                            Collider hit = overlaps[i];
                            if (hit != null && hit.transform.IsChildOf(moduleRoot)) { blocked = true; break; }
                        }
                        if (blocked) continue;
                        bool supported = false;
                        RaycastHit[] groundHits = Physics.RaycastAll(root + Vector3.up * 0.5f, Vector3.down, 0.85f,
                            Physics.AllLayers, QueryTriggerInteraction.Ignore);
                        for (int i = 0; i < groundHits.Length; i++)
                        {
                            RaycastHit hit = groundHits[i];
                            if (hit.collider != null && hit.collider.transform.IsChildOf(moduleRoot) &&
                                hit.normal.y >= 0.7f && Mathf.Abs(hit.point.y - layout.origin.y) <= 0.1f)
                            { supported = true; break; }
                        }
                        if (!supported) continue;
                        result = cell;
                        return true;
                    }
            return false;
        }

        private static bool IsInsideSecretRoom(Emberdelve.Interaction.DungeonDoorInteractable[] secrets, Vector3 point)
        {
            foreach (var secret in secrets) if (secret.ContainsSecretRoomPoint(point)) return true;
            return false;
        }

        // Ordered by party, then member. This runs before content remapping so chests avoid every arrival capsule.
        private bool TryPlaceRoyaleParties(out string reason)
        {
            reason = "Not enough separated, supported party spawn positions";
            var roster = new RoyaleRoster(_royaleSpawnMode.Value);
            var candidates = new List<Vector2Int>();
            var rooms = new List<int>();
            for (int room = 0; room < _runtimeLayout.rooms.Count; room++)
            {
                RoomSpec bounds = _runtimeLayout.rooms[room];
                for (int y = bounds.minCell.y; y <= bounds.maxCell.y; y++)
                    for (int x = bounds.minCell.x; x <= bounds.maxCell.x; x++)
                    {
                        var cell = new Vector2Int(x, y);
                        if (!IsReachableGroundCell(cell)) continue;
                        if (!TryFindClearSpawnCell(_runtimeLayout, _moduleRoots[room], cell, cell, cell, out _)) continue;
                        candidates.Add(cell); rooms.Add(room);
                    }
            }
            var selected = new List<Vector2Int>();
            var centers = new List<Vector2Int>();
            var usedRooms = new HashSet<int>();
            float cellSize = _runtimeLayout.CellSize;
            for (int party = 0; party < roster.PartyCapacity; party++)
            {
                List<Vector2Int> best = null;
                int bestRoom = -1;
                float bestDistance = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (roster.PartySize > 1 && usedRooms.Contains(rooms[i])) continue;
                    Vector2Int center = candidates[i];
                    float separation = float.MaxValue;
                    foreach (var prior in centers) separation = Mathf.Min(separation, (center - prior).sqrMagnitude * cellSize * cellSize);
                    if (separation < 64f || separation <= bestDistance) continue;
                    var group = new List<Vector2Int> { center };
                    var nearby = new List<Vector2Int>();
                    for (int j = 0; j < candidates.Count; j++)
                        if (rooms[j] == rooms[i] && candidates[j] != center
                            && (candidates[j] - center).sqrMagnitude * cellSize * cellSize <= 36f)
                            nearby.Add(candidates[j]);
                    nearby.Sort((a, b) =>
                    {
                        int distance = (a - center).sqrMagnitude.CompareTo((b - center).sqrMagnitude);
                        if (distance != 0) return distance;
                        int row = a.y.CompareTo(b.y);
                        return row != 0 ? row : a.x.CompareTo(b.x);
                    });
                    foreach (var cell in nearby)
                    {
                        if (group.Count == roster.PartySize) break;
                        bool spaced = true;
                        foreach (var member in group)
                            if ((cell - member).sqrMagnitude < 4) spaced = false;
                        if (!spaced) continue;
                        group.Add(cell);
                    }
                    if (group.Count != roster.PartySize) continue;
                    bool clear = true;
                    foreach (var cell in group)
                        foreach (var prior in selected)
                            if ((cell - prior).sqrMagnitude * cellSize * cellSize < 64f
                                || HasSpawnSightline(cell, prior)) clear = false;
                    if (!clear) continue;
                    best = group; bestRoom = rooms[i]; bestDistance = separation;
                }
                if (best == null) return false;
                selected.AddRange(best); centers.Add(best[0]); usedRooms.Add(bestRoom);
            }
            _runtimeLayout.playerSpawns.Clear();
            _runtimeLayout.playerSpawns.AddRange(selected);
            reason = string.Empty;
            return true;
        }

        private bool HasSpawnSightline(Vector2Int first, Vector2Int second)
        {
            Vector3 a = _runtimeLayout.CellCenterWorld(first), b = _runtimeLayout.CellCenterWorld(second);
            // Bots can acquire opponents within24m. Nearby opposing parties need real map cover,
            // not a damage grace period or a player-only exception. Check both torso and head height.
            if ((a - b).sqrMagnitude > 24f * 24f) return false;
            foreach (float height in new[] { 1.2f, 1.7f })
            {
                Vector3 start = a + Vector3.up * height, end = b + Vector3.up * height;
                Vector3 delta = end - start;
                bool covered = false;
                foreach (var hit in Physics.RaycastAll(start, delta.normalized, delta.magnitude,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    if (hit.transform.IsChildOf(generatedRoot)) { covered = true; break; }
                if (!covered) return true;
            }
            return false;
        }

        private void AddOpenCorridors(DungeonModulePlan plan)
        {
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                int x0 = p.X * DungeonModulePlan.ModuleWidthCells;
                int y0 = p.Y * DungeonModulePlan.ModuleHeightCells;
                int centerX = x0 + DungeonModulePlan.ModuleWidthCells / 2;
                int centerY = y0 + DungeonModulePlan.ModuleHeightCells / 2;
                if (p.X + 1 < DungeonModulePlan.Width && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.East))
                    _runtimeLayout.corridors.Add(new CorridorSpec { corridorName = "E_" + i,
                        fromCell = new Vector2Int(x0 + DungeonModulePlan.ModuleWidthCells - 2, centerY),
                        toCell = new Vector2Int(x0 + DungeonModulePlan.ModuleWidthCells + 1, centerY), width = 2 });
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North))
                    _runtimeLayout.corridors.Add(new CorridorSpec { corridorName = "N_" + i,
                        fromCell = new Vector2Int(centerX, y0 + DungeonModulePlan.ModuleHeightCells - 2),
                        toCell = new Vector2Int(centerX, y0 + DungeonModulePlan.ModuleHeightCells + 1), width = 2 });
            }
        }

        private bool ValidateSocketAlignment(DungeonModulePlan plan, out string reason)
        {
            const float tolerance = 0.1f;
            reason = string.Empty;
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement p = plan.Placements[i];
                DungeonModuleDefinition here = _moduleRoots[i].GetComponent<DungeonModuleDefinition>();
                if (p.X + 1 < DungeonModulePlan.Width && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.East))
                {
                    int eastIndex = i + 1;
                    DungeonModulePlacement eastPlacement = plan.Placements[eastIndex];
                    DungeonModuleDefinition east = _moduleRoots[eastIndex].GetComponent<DungeonModuleDefinition>();
                    Transform eastFacingSocket = here.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.East, p.QuarterTurnsClockwise));
                    Transform westFacingSocket = east.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.West, eastPlacement.QuarterTurnsClockwise));
                    if (eastFacingSocket == null || westFacingSocket == null || Vector3.Distance(eastFacingSocket.position, westFacingSocket.position) > tolerance)
                    { reason = "east/west sockets do not align at cell " + p.X + "," + p.Y; return false; }
                }
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North))
                {
                    int northIndex = i + DungeonModulePlan.Width;
                    DungeonModulePlacement northPlacement = plan.Placements[northIndex];
                    DungeonModuleDefinition north = _moduleRoots[northIndex].GetComponent<DungeonModuleDefinition>();
                    Transform northFacingSocket = here.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.North, p.QuarterTurnsClockwise));
                    Transform southFacingSocket = north.Socket(DungeonModulePlanner.ToLocalSocketDirection(DungeonModuleSockets.South, northPlacement.QuarterTurnsClockwise));
                    if (northFacingSocket == null || southFacingSocket == null || Vector3.Distance(northFacingSocket.position, southFacingSocket.position) > tolerance)
                    { reason = "north/south sockets do not align at cell " + p.X + "," + p.Y; return false; }
                }
            }
            return true;
        }

        private bool ValidateClosureState(DungeonModulePlan plan, out string reason)
        {
            reason = string.Empty;
            for (int i = 0; i < plan.Placements.Length; i++)
            {
                DungeonModulePlacement placement = plan.Placements[i];
                DungeonModuleDefinition definition = _moduleRoots[i].GetComponent<DungeonModuleDefinition>();
                for (int directionIndex = 0; directionIndex < 4; directionIndex++)
                {
                    DungeonModuleSockets localDirection = directionIndex == 0 ? DungeonModuleSockets.North : directionIndex == 1
                        ? DungeonModuleSockets.East : directionIndex == 2 ? DungeonModuleSockets.South : DungeonModuleSockets.West;
                    DungeonModuleSockets worldDirection = DungeonModulePlanner.Rotate(localDirection, placement.QuarterTurnsClockwise);
                    Transform closure = definition.Closure(localDirection);
                    bool shouldBeClosed = !DungeonModulePlan.Has(placement.OpenSockets, worldDirection);
                    if (closure == null || closure.gameObject.activeSelf != shouldBeClosed)
                    {
                        reason = "closure state does not match planned socket at module " + i + " direction=" + worldDirection;
                        return false;
                    }
                }
            }
            return true;
        }

        private bool IsReachableGroundCell(Vector2Int cell) => _reachableNav != null && _reachableNav.Contains(cell)
            && Mathf.Abs(_runtimeNav.CellCenter(cell.x, cell.y).y - _runtimeLayout.origin.y) <= .1f;

        private HashSet<Vector2Int> ReachableNavCells(Vector2Int start)
        {
            var reached = new HashSet<Vector2Int>();
            if (_runtimeNav == null || !_runtimeNav.IsWalkable(start.x, start.y)) return reached;
            var queue = new Queue<Vector2Int>();
            queue.Enqueue(start); reached.Add(start);
            Vector2Int[] offsets = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue();
                foreach (var offset in offsets)
                {
                    var next = cell + offset;
                    if (reached.Contains(next) || !_runtimeNav.CanTraverse(cell.x, cell.y, next.x, next.y)) continue;
                    reached.Add(next); queue.Enqueue(next);
                }
            }
            return reached;
        }

        private bool ValidateNavConnectivity(DungeonLayoutData layout, out string reason)
        {
            reason = string.Empty;
            int expectedSpawns = _royaleSpawnMode.HasValue
                ? new RoyaleRoster(_royaleSpawnMode.Value).Capacity : DungeonModulePlan.Width * DungeonModulePlan.Height;
            if (layout.playerSpawns == null || layout.playerSpawns.Count != expectedSpawns)
            { reason = "modular layout has an incorrect player spawn count"; return false; }

            if (_runtimeNav == null) { reason = "baked navigation is missing"; return false; }
            bool[,] walkable = new bool[_runtimeNav.width, _runtimeNav.height];
            for (int y = 0; y < _runtimeNav.height; y++)
                for (int x = 0; x < _runtimeNav.width; x++) walkable[x, y] = _runtimeNav.IsWalkable(x, y);
            Vector2Int start = layout.playerStartCell;
            if (!InsideAndWalkable(walkable, start)) { reason = "navigation start cell is blocked"; return false; }
            bool[,] reached = new bool[walkable.GetLength(0), walkable.GetLength(1)];
            Queue<Vector2Int> queue = new Queue<Vector2Int>();
            reached[start.x, start.y] = true;
            queue.Enqueue(start);
            Vector2Int[] offsets = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            while (queue.Count > 0)
            {
                Vector2Int cell = queue.Dequeue();
                for (int i = 0; i < offsets.Length; i++)
                {
                    Vector2Int next = cell + offsets[i];
                    if (!InsideAndWalkable(walkable, next) || reached[next.x, next.y]) continue;
                    if (!_runtimeNav.CanTraverse(cell.x, cell.y, next.x, next.y)) continue;
                    reached[next.x, next.y] = true;
                    queue.Enqueue(next);
                }
            }

            for (int i = 0; i < layout.playerSpawns.Count; i++)
            {
                Vector2Int spawn = layout.playerSpawns[i];
                if (!InsideAndWalkable(walkable, spawn) || !reached[spawn.x, spawn.y])
                { reason = "player spawn " + i + " is blocked or disconnected from navigation start"; return false; }
            }
            Vector2Int extraction = layout.extractionCell;
            if (!InsideAndWalkable(walkable, extraction) || !reached[extraction.x, extraction.y])
            { reason = "extraction cell is blocked or disconnected from navigation start"; return false; }
            var contentCells = new List<Vector2Int>();
            if (layout.enemySpawns != null) foreach (var spec in layout.enemySpawns) if (spec != null) contentCells.Add(spec.cell);
            if (layout.lootSpawns != null) foreach (var spec in layout.lootSpawns) if (spec != null) contentCells.Add(spec.cell);
            if (layout.chestSpawns != null) foreach (var spec in layout.chestSpawns) if (spec != null) contentCells.Add(spec.cell);
            foreach (var cell in contentCells)
                if (!InsideAndWalkable(walkable, cell) || !reached[cell.x, cell.y])
                { reason = "content cell " + cell + " is blocked or disconnected in baked navigation"; return false; }
            return true;
        }

        private bool ValidateExtractionPlacement(out string reason)
        {
            reason = string.Empty;
            ExtractionPoint point = FindFirstObjectByType<ExtractionPoint>(FindObjectsInactive.Include);
            if (point == null || !point.isActiveAndEnabled)
            { reason = "modular scene has no active extraction interactable"; return false; }
            Vector3 expected = ModuleCenter(DungeonModulePlan.Width - 1, DungeonModulePlan.Height - 1, _runtimeLayout.CellSize);
            Vector2 actualXZ = new Vector2(point.transform.position.x, point.transform.position.z);
            Vector2 expectedXZ = new Vector2(expected.x, expected.z);
            if (Vector2.Distance(actualXZ, expectedXZ) > 0.1f)
            { reason = "extraction interactable is not placed in the planned extraction module"; return false; }
            return true;
        }

        private static bool InsideAndWalkable(bool[,] walkable, Vector2Int cell) =>
            cell.x >= 0 && cell.y >= 0 && cell.x < walkable.GetLength(0) && cell.y < walkable.GetLength(1) && walkable[cell.x, cell.y];

        private bool RemapContent(DungeonLayoutData layout, DungeonModulePlan plan, out string reason)
        {
            reason = string.Empty;
            List<int> rooms = CombatRoomIndices(plan);
            var occupied = new List<Bounds>();
            if (rooms.Count == 0) { reason = "no combat room for content"; return false; }
            int ordinaryChestCount = layout.chestSpawns != null ? layout.chestSpawns.Count : 0;
            if (!AddSecretChestSpawns(layout, plan, occupied, out reason)) return false;
            if (layout.chestSpawns != null)
                for (int i = 0; i < ordinaryChestCount; i++)
                {
                    var spec = layout.chestSpawns[i];
                    if (spec == null) continue;
                    if (!TryContentCell(layout, plan, rooms, i, 0, ChestSpawner.SizeFor(spec.variant), occupied, out spec.cell))
                    { reason = ContentLocationFailure("chest", i, plan, rooms); return false; }
                }
            if (layout.lootSpawns != null)
                for (int i = 0; i < layout.lootSpawns.Count; i++)
                {
                    var spec = layout.lootSpawns[i];
                    if (spec == null) continue;
                    Vector3 size = spec.item != null ? Vector3.Max(Vector3.one * .12f, spec.item.worldSize) : Vector3.one * .3f;
                    if (!TryContentCell(layout, plan, rooms, i, 2, size, occupied, out spec.cell))
                    { reason = ContentLocationFailure("loot", i, plan, rooms); return false; }
                }
            if (layout.enemySpawns != null)
                for (int i = 0; i < layout.enemySpawns.Count; i++)
                {
                    var spec = layout.enemySpawns[i];
                    if (spec == null) continue;
                    Vector3 size = new Vector3(1.1f, 2.1f, 1.1f) * (spec.enemy != null ? spec.enemy.BodyScale : 1f);
                    if (!TryContentCell(layout, plan, rooms, i, 4, size, occupied, out spec.cell))
                    { reason = ContentLocationFailure("enemy", i, plan, rooms); return false; }
                }
            return true;
        }

        private bool AddSecretChestSpawns(DungeonLayoutData layout, DungeonModulePlan plan,
            List<Bounds> occupied, out string reason)
        {
            reason = null;
            ChestSpawnSpec template = layout.chestSpawns?.Find(spec => spec != null && spec.lootTable != null);
            for (int i = 0; i < _moduleRoots.Length; i++)
                foreach (var secret in _moduleRoots[i].GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>(true))
                {
                    if (!secret.IsSecret) continue;
                    Transform anchor = null;
                    foreach (Transform child in secret.GetComponentsInChildren<Transform>(true))
                    {
                        if (child.name != "SecretChestAnchor") continue;
                        if (anchor != null) { reason = "Secret lever has duplicate chest anchors: " + secret.DoorId; return false; }
                        anchor = child;
                    }
                    if (anchor == null || template == null)
                    { reason = "Secret cache requires a chest anchor and an authored loot table: " + secret.DoorId; return false; }
                    Vector2Int cell = layout.CellFromWorld(anchor.position);
                    Vector3 position = layout.CellCenterWorld(cell);
                    Vector3 size = ChestSpawner.SizeFor(template.variant);
                    if (Vector3.Distance(position, anchor.position) > .1f ||
                        !secret.ContainsSecretRoomPoint(position) ||
                        !TryFindContentCell(layout, _moduleRoots[i], cell, cell, cell, size, out _, occupied, IsReachableGroundCell, true))
                    { reason = "Secret chest anchor is unsupported, obstructed, disconnected or off-grid: " + secret.DoorId; return false; }
                    layout.chestSpawns.Add(new ChestSpawnSpec
                    {
                        cell = cell, variant = template.variant,
                        seed = unchecked(plan.Seed ^ (cell.x * 73856093) ^ (cell.y * 19349663)),
                        lootTable = template.lootTable, bodyMaterial = template.bodyMaterial, lidMaterial = template.lidMaterial
                    });
                    occupied.Add(new Bounds(position + Vector3.up * (size.y * .5f + .05f), size));
                }
            return true;
        }

        private string ContentLocationFailure(string kind, int ordinal, DungeonModulePlan plan, List<int> rooms)
        {
            var placement = plan.Placements[rooms[ordinal % rooms.Count]];
            int walkable = 0, reachable = 0, ground = 0;
            for (int y = placement.Y * DungeonModulePlan.ModuleHeightCells + 1; y < (placement.Y + 1) * DungeonModulePlan.ModuleHeightCells - 1; y++)
                for (int x = placement.X * DungeonModulePlan.ModuleWidthCells + 1; x < (placement.X + 1) * DungeonModulePlan.ModuleWidthCells - 1; x++)
                {
                    var cell = new Vector2Int(x, y);
                    if (_runtimeNav.IsWalkable(x, y)) walkable++;
                    if (_reachableNav.Contains(cell)) reachable++;
                    if (IsReachableGroundCell(cell)) ground++;
                }
            return "no safe " + kind + " location for content " + ordinal + " module=" + catalog.Modules[placement.ModuleIndex].ModuleId
                + " turn=" + placement.QuarterTurnsClockwise + " start=" + _runtimeLayout.playerStartCell
                + " reachableTotal=" + _reachableNav.Count + " roomWalkable/Reachable/Ground=" + walkable + "/" + reachable + "/" + ground;
        }

        private bool TryContentCell(DungeonLayoutData layout, DungeonModulePlan plan, List<int> rooms,
            int ordinal, int offset, Vector3 size, List<Bounds> occupied, out Vector2Int result)
        {
            int moduleIndex = rooms[ordinal % rooms.Count];
            DungeonModulePlacement p = plan.Placements[moduleIndex];
            Vector2Int min = new Vector2Int(p.X * DungeonModulePlan.ModuleWidthCells + 1, p.Y * DungeonModulePlan.ModuleHeightCells + 1);
            Vector2Int max = new Vector2Int((p.X + 1) * DungeonModulePlan.ModuleWidthCells - 2, (p.Y + 1) * DungeonModulePlan.ModuleHeightCells - 2);
            if (!TryFindContentCell(layout, _moduleRoots[moduleIndex], ContentCell(plan, rooms, ordinal, offset), min, max, size, out result, occupied, IsReachableGroundCell)) return false;
            occupied.Add(new Bounds(layout.CellCenterWorld(result) + Vector3.up * (size.y * .5f + .05f), size));
            return true;
        }

        /// <summary>Finds a deterministic supported content cell clear of module geometry and every player spawn.</summary>
        public static bool TryFindContentCell(DungeonLayoutData layout, Transform moduleRoot, Vector2Int preferred,
            Vector2Int min, Vector2Int max, Vector3 size, out Vector2Int result, IReadOnlyList<Bounds> occupied = null,
            Func<Vector2Int, bool> allowed = null, bool allowSecretRoom = false)
        {
            result = default;
            if (layout == null || moduleRoot == null || size.x <= 0f || size.y <= 0f || size.z <= 0f) return false;
            var secrets = moduleRoot.GetComponentsInChildren<Emberdelve.Interaction.DungeonDoorInteractable>(true);
            int limit = Mathf.Max(max.x - min.x, max.y - min.y);
            for (int ring = 0; ring <= limit; ring++)
                for (int z = -ring; z <= ring; z++)
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != ring) continue;
                        Vector2Int cell = preferred + new Vector2Int(x, z);
                        if (cell.x < min.x || cell.x > max.x || cell.y < min.y || cell.y > max.y) continue;
                        if (allowed != null && !allowed(cell)) continue;
                        Vector3 ground = layout.CellCenterWorld(cell);
                        if (!allowSecretRoom && IsInsideSecretRoom(secrets, ground)) continue;
                        bool nearSpawn = false;
                        foreach (Vector2Int spawn in layout.playerSpawns)
                        {
                            Vector3 player = layout.CellCenterWorld(spawn);
                            // Conservative axis-aligned envelope of the content and 0.36 m player capsule.
                            if (Mathf.Abs(player.x - ground.x) < size.x * .5f + .36f &&
                                Mathf.Abs(player.z - ground.z) < size.z * .5f + .36f) { nearSpawn = true; break; }
                        }
                        if (nearSpawn) continue;
                        Vector3 half = size * .5f;
                        Vector3 centre = ground + Vector3.up * (half.y + .05f);
                        bool reserved = false;
                        if (occupied != null)
                            foreach (var bounds in occupied)
                                if (bounds.Intersects(new Bounds(centre, size))) { reserved = true; break; }
                        if (reserved) continue;
                        Collider[] hits = Physics.OverlapBox(centre, half, Quaternion.identity, Physics.AllLayers, QueryTriggerInteraction.Ignore);
                        bool blocked = false;
                        foreach (Collider hit in hits)
                            if (hit != null && hit.transform.IsChildOf(moduleRoot)) { blocked = true; break; }
                        if (blocked) continue;
                        bool supported = false;
                        foreach (RaycastHit hit in Physics.RaycastAll(ground + Vector3.up * .5f, Vector3.down, .85f, Physics.AllLayers, QueryTriggerInteraction.Ignore))
                            if (hit.collider != null && hit.collider.transform.IsChildOf(moduleRoot) && hit.normal.y >= .7f &&
                                Mathf.Abs(hit.point.y - layout.origin.y) <= .1f) { supported = true; break; }
                        if (!supported) continue;
                        result = cell;
                        return true;
                    }
            return false;
        }

        private static Vector2Int ContentCell(DungeonModulePlan plan, List<int> rooms, int ordinal, int offset)
        {
            int module = rooms[ordinal % rooms.Count];
            DungeonModulePlacement p = plan.Placements[module];
            int x = p.X * DungeonModulePlan.ModuleWidthCells + DungeonModulePlan.ModuleWidthCells / 2 + ((ordinal + offset) % 3) - 1;
            int y = p.Y * DungeonModulePlan.ModuleHeightCells + DungeonModulePlan.ModuleHeightCells / 2 + ((ordinal + offset * 2) % 3) - 1;
            return new Vector2Int(x, y);
        }

        private static List<int> CombatRoomIndices(DungeonModulePlan plan)
        {
            List<int> rooms = new List<int>();
            for (int i = 0; i < plan.Placements.Length; i++) if (plan.Placements[i].Role == DungeonModuleRole.Combat) rooms.Add(i);
            return rooms;
        }

        private bool PlaceExistingGameplayActors(out string reason)
        {
            reason = string.Empty;
            EnemyAgent[] enemies = FindObjectsByType<EnemyAgent>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            LootPickup[] loot = FindObjectsByType<LootPickup>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if ((enemies.Length > 0 && (_runtimeLayout.enemySpawns == null || _runtimeLayout.enemySpawns.Count < enemies.Length)) ||
                (loot.Length > 0 && (_runtimeLayout.lootSpawns == null || _runtimeLayout.lootSpawns.Count < loot.Length)))
            { reason = "authored gameplay actors exceed their remapped placement records"; return false; }
            for (int i = 0; i < enemies.Length; i++) if (_runtimeLayout.enemySpawns[i] == null)
            { reason = "authored enemy has no placement record"; return false; }
            for (int i = 0; i < loot.Length; i++) if (_runtimeLayout.lootSpawns[i] == null)
            { reason = "authored loot has no placement record"; return false; }

            // The boss reservation uses its larger bounds. FindObjectsByType does not preserve spawn order.
            int bossActor = System.Array.FindIndex(enemies, enemy => enemy.UsesCinderClub);
            int bossSlot = _runtimeLayout.enemySpawns?.FindIndex(spec => spec != null && spec.enemy != null && spec.enemy.IsCinderWarden) ?? -1;
            if (bossActor >= 0 && bossSlot >= 0 && bossSlot < enemies.Length && bossActor != bossSlot)
                (enemies[bossSlot], enemies[bossActor]) = (enemies[bossActor], enemies[bossSlot]);
            PlayerRig sceneRig = FindFirstObjectByType<PlayerRig>(FindObjectsInactive.Include);
            if (sceneRig != null) sceneRig.transform.position = _runtimeLayout.PlayerSpawnWorld(0) + Vector3.up * 0.15f;

            for (int i = 0; i < enemies.Length; i++)
            {
                Vector2Int cell = _runtimeLayout.enemySpawns[i].cell;
                enemies[i].transform.position = _runtimeLayout.CellCenterWorld(cell);
                enemies[i].ConfigureNavigationGrid(_runtimeNav);
            }

            for (int i = 0; i < loot.Length; i++)
                loot[i].PlaceAt(_runtimeLayout.CellCenterWorld(_runtimeLayout.lootSpawns[i].cell) + Vector3.up * 0.45f);

            ExtractionPoint exit = FindFirstObjectByType<ExtractionPoint>(FindObjectsInactive.Include);
            GameObject gate = GameObject.Find("Ember_Gate");
            if (gate != null) gate.transform.position = ModuleCenter(DungeonModulePlan.Width - 1, DungeonModulePlan.Height - 1, _runtimeLayout.CellSize);
            else if (exit != null) exit.transform.position = ModuleCenter(DungeonModulePlan.Width - 1, DungeonModulePlan.Height - 1, _runtimeLayout.CellSize);
            _initialEnemies = enemies; _initialLoot = loot;
            return true;
        }

        private void ApplyDoorClosures(Transform moduleRoot, DungeonModuleSockets worldOpenSockets, byte quarterTurns)
        {
            Transform[] children = moduleRoot.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < 4; i++)
            {
                DungeonModuleSockets localDirection = i == 0 ? DungeonModuleSockets.North : i == 1 ? DungeonModuleSockets.East : i == 2 ? DungeonModuleSockets.South : DungeonModuleSockets.West;
                DungeonModuleSockets worldDirection = DungeonModulePlanner.Rotate(localDirection, quarterTurns);
                Transform closure = moduleRoot.GetComponent<DungeonModuleDefinition>().Closure(localDirection);
                if (closure != null) closure.gameObject.SetActive(!DungeonModulePlan.Has(worldOpenSockets, worldDirection));
            }
        }

        private void DisableLegacyGeometry()
        {
            GameObject[] all = FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i].transform.parent != null) continue;
                for (int k = 0; k < LegacyGeometryRoots.Length; k++)
                    if (all[i].name == LegacyGeometryRoots[k]) { all[i].SetActive(false); break; }
            }
        }

        private void ClearGeneratedModules()
        {
            for (int i = 0; i < _moduleRoots.Length; i++)
            {
                if (_moduleRoots[i] != null)
                {
                    _moduleRoots[i].gameObject.SetActive(false);
                    DestroyOwnedObject(_moduleRoots[i].gameObject);
                }
                _moduleRoots[i] = null;
                _spawnAnchors[i] = null;
            }
            DestroyOwnedObject(_runtimeLayout);
            DestroyOwnedObject(_runtimeNav);
            _runtimeLayout = null;
            _runtimeNav = null;
            _appliedHash = string.Empty;
            _appliedPlan = null;
        }

        private Vector3 ModuleCenter(int x, int y, int cellSize)
        {
            float extentX = (x * DungeonModulePlan.ModuleWidthCells + DungeonModulePlan.ModuleWidthCells / 2f) * cellSize;
            float extentZ = (y * DungeonModulePlan.ModuleHeightCells + DungeonModulePlan.ModuleHeightCells / 2f) * cellSize;
            return mapOrigin + new Vector3(extentX, 0f, extentZ);
        }
    }
}
