using System;
using System.IO;
using System.Linq;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEditor;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    public static class EmberdelveSteelCuirassReview
    {
        const string Evidence = "Logs/acceptance/steel-cuirass-20261113";
        public static void Build()
        {
            if (!Application.isBatchMode || !Application.dataPath.Replace('\\','/').Contains("DakaDaka_ci/"))
                throw new InvalidOperationException("Use graphics batch on DakaDaka_ci");
            Directory.CreateDirectory(Evidence);
            EmberdelveItemArmorModels.CreateCuirassAssets();
            var catalog = AssetDatabase.LoadAssetAtPath<ItemVisualCatalog>("Assets/Emberdelve/Resources/Emberdelve/ItemVisualCatalog.asset");
            var entry = catalog.entries.First(e => e.itemId == "boiled_cuirass");
            EmberdelveItemVisualBake.BakeIcon(entry);
            EditorUtility.SetDirty(catalog);
            var item = AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_BoiledCuirass.asset");
            item.displayName = "Steel Cuirass";
            item.description = "Fitted steel breastplate and backplate with overlapping waist lames. Right click to wear.";
            EditorUtility.SetDirty(item);
            AssetDatabase.SaveAssets();
            var angles = new [] { new Vector3(.65f,.25f,3), new Vector3(3,.15f,1.3f), new Vector3(-.8f,.2f,-3) };
            for (int i=0;i<angles.Length;i++)
                EmberdelveItemVisualBake.Render(entry.worldPrefab,Evidence+"/cuirass-"+i+".png",1200,false,null,angles[i]);
            foreach (string character in new [] { "Warrior", "Archer" })
            foreach (string pose in new [] { "Idle", "Walk", "Block" })
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/Emberdelve/Resources/Emberdelve/PlayerVisuals/"+character+".prefab");
                EmberdelveItemVisualBake.Render(prefab,Evidence+"/worn-"+character+"-"+pose+".png",1200,false,model =>
                {
                    var view = model.AddComponent<EquippedArmorView>();
                    view.BindVisualIds(null,"boiled_cuirass",null,null,null,null);
                    var animator = model.GetComponentInChildren<Animator>();
                    animator.Rebind(); animator.Play(pose,0,.25f); animator.Update(0);
                });
            }
            Debug.Log("[SteelCuirass] PASS targeted assets, icon and nine review frames");
        }
    }
}
