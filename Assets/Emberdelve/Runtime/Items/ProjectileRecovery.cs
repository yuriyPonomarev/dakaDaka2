using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Flow;
namespace Emberdelve.Items
{
    public static class ProjectileRecovery
    {
        // Reserve room for ordinary loot in the existing 256-entry floor snapshot.
        public const int MaxLivePickups=224;
        public static bool TrySpawn(ItemData item,Vector3 terminal,int levelMask)
        {
            if(NetRole.IsClientOnly || item==null || !item.IsRecoveredAmmo) return false;
            var director=Object.FindFirstObjectByType<RunDirector>();
            if(director==null || director.State!=RunState.Running) return false;
            if(Object.FindObjectsByType<LootPickup>(FindObjectsInactive.Exclude,FindObjectsSortMode.None).Length>=MaxLivePickups) return false;
            if(!TryGround(terminal,levelMask,out var position)) return false;
            director.SpawnLoot(item,1,position);
            Debug.Log("[ProjectileRecovery] spawned item="+item.itemId+" quantity=1 at="+position);
            return true;
        }
        public static bool TryGround(Vector3 terminal,int levelMask,out Vector3 position)
        {
            position=default;
            if(float.IsNaN(terminal.sqrMagnitude)||float.IsInfinity(terminal.sqrMagnitude)) return false;
            if(!Physics.Raycast(terminal+Vector3.up*.15f,Vector3.down,out var hit,100f,levelMask,QueryTriggerInteraction.Ignore)) return false;
            if(hit.normal.y<.45f) return false;
            position=hit.point+Vector3.up*.055f; return true;
        }
        public static void DressPickup(GameObject root,ItemData item)
        {
            if(item==null || !item.IsRecoveredAmmo || root.transform.Find("RecoveredShaft")!=null)return;
            var catalog = ItemVisualCatalog.Load();
            if (catalog != null && catalog.TryGet(item.itemId, out var authored) && authored.worldPrefab != null) return;
            var cube=root.GetComponent<Renderer>();if(cube!=null)cube.enabled=false;
            // Relative to the generic loot proxy dimensions, on server and every client.
            Part(root,"RecoveredShaft",new Vector3(0,0,0),new Vector3(.12f,.12f,.9f),item.worldMaterial);
            Part(root,"RecoveredTip",new Vector3(0,0,.46f),new Vector3(.25f,.14f,.08f),item.worldMaterial);
            Part(root,"RecoveredFletching",new Vector3(0,0,-.32f),new Vector3(.48f,.10f,.16f),item.worldMaterial);
        }
        static void Part(GameObject root,string name,Vector3 position,Vector3 scale,Material material)
        {
            var part=GameObject.CreatePrimitive(PrimitiveType.Cube);part.name=name;part.layer=root.layer;
            part.transform.SetParent(root.transform,false);part.transform.localPosition=position;part.transform.localScale=scale;
            var collider=part.GetComponent<Collider>();if(collider!=null){collider.enabled=false;Object.Destroy(collider);}
            if(material!=null)part.GetComponent<Renderer>().sharedMaterial=material;
        }
    }
}
