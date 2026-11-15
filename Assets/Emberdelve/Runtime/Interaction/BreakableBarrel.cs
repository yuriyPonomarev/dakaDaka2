using System.Collections.Generic;
using System.Text;
using Emberdelve.Core;
using Emberdelve.Level;
using UnityEngine;

namespace Emberdelve.Interaction
{
    /// <summary>Saved world prop. Only authoritative weapon contacts remove its collision.</summary>
    public sealed class BreakableBarrel : MonoBehaviour
    {
        [SerializeField] private GameObject intactVisual;
        [SerializeField] private GameObject brokenVisual;
        [SerializeField, Min(1f)] private float health = 24f;
        private float _remaining;
        private bool _initialized;
        private Transform _identityModule;
        private string _identityModuleName;
        private ulong _stableId;
        private int _boundFloor;
        public bool IsBroken { get; private set; }
        public float RemainingHealth => _initialized ? _remaining : health;
        public static readonly List<BreakableBarrel> Active = new List<BreakableBarrel>();

        public void Configure(GameObject intact, GameObject broken, float hitPoints = 24f)
        {
            intactVisual = intact; brokenVisual = broken;
            health = Mathf.Max(1f, hitPoints);
            if (brokenVisual != null) brokenVisual.SetActive(false);
        }

        public bool BindStagedFloor(int floor)
        {
            if (floor < 2 || floor > 3) return false;
            if (_boundFloor != 0) return _boundFloor == floor;
            _boundFloor = floor; _identityModule = null; return true;
        }

        // Computed after module assembly: Awake occurs before the assembler gives the module
        // its coordinate-qualified name. Stop at that module, ignoring host/client scene roots.
        public ulong StableId
        {
            get
            {
                if (_identityModule!=null && _identityModule.name==_identityModuleName) return _stableId;
                var path = new StringBuilder();
                for (Transform t=transform; t!=null; t=t.parent)
                {
                    path.Insert(0, "/"+t.name);
                    if (t.GetComponent<DungeonModuleDefinition>() != null)
                    {
                        _identityModule=t; _identityModuleName=t.name;
                        break;
                    }
                }
                if (_boundFloor != 0) path.Insert(0, "floor" + _boundFloor + ":");
                unchecked
                {
                    ulong hash=14695981039346656037UL;
                    foreach(char c in path.ToString()) { hash^=c; hash*=1099511628211UL; }
                    _stableId=hash;
                    return hash;
                }
            }
        }

        private void OnEnable() { if (!Active.Contains(this)) Active.Add(this); }
        private void OnDisable() { Active.Remove(this); }

        public bool ApplyWeaponDamage(float damage)
        {
            if (!NetRole.RunsSimulation || IsBroken || float.IsNaN(damage) || float.IsInfinity(damage) || damage<=0f) return false;
            if (!_initialized) { _remaining=health; _initialized=true; }
            _remaining=Mathf.Max(0f, _remaining-damage);
            if (_remaining<=0f) Break();
            return true;
        }

        /// <summary>Called only by the server-authenticated world-state receiver, never a damage intent.</summary>
        public void ApplyReplicatedBroken()
        {
            if (NetRole.IsClientOnly && !IsBroken) Break();
        }

        private void Break()
        {
            IsBroken=true; _remaining=0; _initialized=true;
            foreach(var collider in GetComponentsInChildren<Collider>(true)) collider.enabled=false;
            if (intactVisual != null) intactVisual.SetActive(false);
            if (brokenVisual != null) brokenVisual.SetActive(true);
            Debug.Log("[BreakableBarrel] broken id="+StableId+" at="+transform.position);
        }
    }
}
