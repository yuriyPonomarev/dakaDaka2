using System;
using System.Collections;
using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Items;
using Emberdelve.Player;
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
                // target distance (which is capped at 18 m), while leaving three metres to each 24 m room boundary.
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
                DungeonModulePlanSession.ClearCurrent();
                ulong raidSeed = RunContext.ResolveRaidSeed(out _);
                RunContext.SetNextRaidSeed(raidSeed);
                int seed = unchecked((int)(raidSeed ^ (raidSeed >> 32)));
                if (DungeonModulePlanner.TryCreate(seed, catalog.Modules, out DungeonModulePlan plan, out string reason))
                    DungeonModulePlanSession.Install(plan);
                else Debug.LogError("[DungeonModuleSceneAssembler] Could not create offline map: " + reason, this);
            }
            else if (DungeonModulePlanSession.Current != null)
            {
                OnPlanChanged(DungeonModulePlanSession.Current);
            }
        }

        private void OnDestroy()
        {
            DungeonModulePlanSession.Changed -= OnPlanChanged;
            if (_planRetryRoutine != null) StopCoroutine(_planRetryRoutine);
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
            if (plan == null || !plan.Validate(out reason)) return false;
            if (catalog == null || catalog.Modules == null || catalog.Modules.Length == 0)
            { reason = "authored module catalog is missing"; return false; }
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
            DisableLegacyGeometry();
            if (!PlaceExistingGameplayActors(out reason)) { ClearGeneratedModules(); return false; }
            if (!ValidateExtractionPlacement(out reason)) { ClearGeneratedModules(); return false; }
            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            _appliedHash = plan.LayoutHash;
            if (director == null || director.Layout != _runtimeLayout || !director.CompleteRuntimeLayoutInstallation())
            {
                _appliedHash = string.Empty;
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
            RunDirector director = FindFirstObjectByType<RunDirector>(FindObjectsInactive.Include);
            if (director == null || director.Layout != _runtimeLayout || !director.RuntimeLayoutInstallationComplete)
            { reason = "RunDirector has not committed the runtime modular layout"; return false; }
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
            _runtimeLayout.playerStartCell = _runtimeLayout.CellFromWorld(ModuleCenter(0, 0, _runtimeLayout.CellSize));
            _runtimeLayout.extractionCell = _runtimeLayout.CellFromWorld(ModuleCenter(DungeonModulePlan.Width - 1, DungeonModulePlan.Height - 1, _runtimeLayout.CellSize));
            if (!RemapContent(_runtimeLayout, plan, out reason)) return false;

            List<string> problems = _runtimeLayout.Validate();
            if (problems.Count > 0) { reason = "generated gameplay layout is invalid: " + string.Join("; ", problems); return false; }

            _runtimeNav = ScriptableObject.CreateInstance<NavGridData>();
            _runtimeNav.name = "Runtime_Modular_Nav_" + plan.LayoutHash;
            _runtimeNav.hideFlags = HideFlags.DontSave;
            _runtimeNav.width = _runtimeLayout.gridWidth;
            _runtimeNav.height = _runtimeLayout.gridHeight;
            _runtimeNav.cellSize = _runtimeLayout.CellSize;
            _runtimeNav.origin = _runtimeLayout.origin;
            _runtimeNav.mask = _runtimeLayout.BuildNavMask();
            if (!ValidateNavConnectivity(_runtimeLayout, out reason)) return false;

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
            int maxDistance = Mathf.Max(Mathf.Abs(anchor.x - min.x), Mathf.Abs(anchor.x - max.x)) +
                              Mathf.Max(Mathf.Abs(anchor.y - min.y), Mathf.Abs(anchor.y - max.y));
            for (int distance = 0; distance <= maxDistance; distance++)
                for (int y = min.y; y <= max.y; y++)
                    for (int x = min.x; x <= max.x; x++)
                    {
                        if (Mathf.Abs(x - anchor.x) + Mathf.Abs(y - anchor.y) != distance) continue;
                        Vector2Int cell = new Vector2Int(x, y);
                        Vector3 root = layout.CellCenterWorld(cell) + Vector3.up * 0.15f;
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
                    _runtimeLayout.corridors.Add(new CorridorSpec { corridorName = "E_" + i, fromCell = new Vector2Int(x0 + 10, centerY), toCell = new Vector2Int(x0 + 13, centerY), width = 2 });
                if (p.Y + 1 < DungeonModulePlan.Height && DungeonModulePlan.Has(p.OpenSockets, DungeonModuleSockets.North))
                    _runtimeLayout.corridors.Add(new CorridorSpec { corridorName = "N_" + i, fromCell = new Vector2Int(centerX, y0 + 10), toCell = new Vector2Int(centerX, y0 + 13), width = 2 });
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

        private bool ValidateNavConnectivity(DungeonLayoutData layout, out string reason)
        {
            reason = string.Empty;
            if (layout.playerSpawns == null || layout.playerSpawns.Count != DungeonModulePlan.Width * DungeonModulePlan.Height)
            { reason = "modular layout must expose exactly nine player spawn cells"; return false; }

            bool[,] walkable = layout.BuildWalkableGrid();
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
            if (rooms.Count == 0) { reason = "no combat room for content"; return false; }
            if (layout.chestSpawns != null)
                for (int i = 0; i < layout.chestSpawns.Count; i++)
                {
                    var spec = layout.chestSpawns[i];
                    if (spec == null) continue;
                    if (!TryContentCell(layout, plan, rooms, i, 0, ChestSpawner.SizeFor(spec.variant), out spec.cell))
                    { reason = "no safe chest location for content " + i; return false; }
                }
            if (layout.lootSpawns != null)
                for (int i = 0; i < layout.lootSpawns.Count; i++)
                {
                    var spec = layout.lootSpawns[i];
                    if (spec == null) continue;
                    if (!TryContentCell(layout, plan, rooms, i, 2, Vector3.one * 0.3f, out spec.cell))
                    { reason = "no safe loot location for content " + i; return false; }
                }
            if (layout.enemySpawns != null)
                for (int i = 0; i < layout.enemySpawns.Count; i++)
                {
                    var spec = layout.enemySpawns[i];
                    if (spec == null) continue;
                    if (!TryContentCell(layout, plan, rooms, i, 4, new Vector3(0.8f, 1.8f, 0.8f), out spec.cell))
                    { reason = "no safe enemy location for content " + i; return false; }
                }
            return true;
        }

        private bool TryContentCell(DungeonLayoutData layout, DungeonModulePlan plan, List<int> rooms,
            int ordinal, int offset, Vector3 size, out Vector2Int result)
        {
            int moduleIndex = rooms[ordinal % rooms.Count];
            DungeonModulePlacement p = plan.Placements[moduleIndex];
            Vector2Int min = new Vector2Int(p.X * DungeonModulePlan.ModuleWidthCells + 1, p.Y * DungeonModulePlan.ModuleHeightCells + 1);
            Vector2Int max = new Vector2Int((p.X + 1) * DungeonModulePlan.ModuleWidthCells - 2, (p.Y + 1) * DungeonModulePlan.ModuleHeightCells - 2);
            return TryFindContentCell(layout, _moduleRoots[moduleIndex], ContentCell(plan, rooms, ordinal, offset), min, max, size, out result);
        }

        /// <summary>Finds a deterministic supported content cell clear of module geometry and every player spawn.</summary>
        public static bool TryFindContentCell(DungeonLayoutData layout, Transform moduleRoot, Vector2Int preferred,
            Vector2Int min, Vector2Int max, Vector3 size, out Vector2Int result)
        {
            result = default;
            if (layout == null || moduleRoot == null || size.x <= 0f || size.y <= 0f || size.z <= 0f) return false;
            int limit = Mathf.Max(max.x - min.x, max.y - min.y);
            for (int ring = 0; ring <= limit; ring++)
                for (int z = -ring; z <= ring; z++)
                    for (int x = -ring; x <= ring; x++)
                    {
                        if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) != ring) continue;
                        Vector2Int cell = preferred + new Vector2Int(x, z);
                        if (cell.x < min.x || cell.x > max.x || cell.y < min.y || cell.y > max.y) continue;
                        Vector3 ground = layout.CellCenterWorld(cell);
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
            int x = p.X * DungeonModulePlan.ModuleWidthCells + 6 + ((ordinal + offset) % 3) - 1;
            int y = p.Y * DungeonModulePlan.ModuleHeightCells + 6 + ((ordinal + offset * 2) % 3) - 1;
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
                    Destroy(_moduleRoots[i].gameObject);
                }
                _moduleRoots[i] = null;
                _spawnAnchors[i] = null;
            }
            if (_runtimeLayout != null) Destroy(_runtimeLayout);
            if (_runtimeNav != null) Destroy(_runtimeNav);
            _runtimeLayout = null;
            _runtimeNav = null;
            _appliedHash = string.Empty;
        }

        private Vector3 ModuleCenter(int x, int y, int cellSize)
        {
            float extentX = (x * DungeonModulePlan.ModuleWidthCells + DungeonModulePlan.ModuleWidthCells / 2f) * cellSize;
            float extentZ = (y * DungeonModulePlan.ModuleHeightCells + DungeonModulePlan.ModuleHeightCells / 2f) * cellSize;
            return mapOrigin + new Vector3(extentX, 0f, extentZ);
        }
    }
}
