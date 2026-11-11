using System;
using System.Linq;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Emberdelve.EditorTools
{
    /// <summary>Idempotent, bounded content authoring; no scene or other weapon/material rebuild.</summary>
    public static class EmberdelveTorchContent
    {
        public const string DataRoot = "Assets/Emberdelve/Data/";
        public const string PrefabPath = "Assets/Emberdelve/Prefabs/Weapons/Viewmodel_PitchTorch.prefab";
        public const string ItemPath = DataRoot + "Item_PitchTorch.asset";
        public const string WeaponPath = DataRoot + "Weapon_PitchTorch.asset";
        public const float Length = .70f;

        [MenuItem("Emberdelve/Visual/Build Inventory Torch")]
        public static void Ensure()
        {
            var wood = Material("Torch_Wood", new Color(.14f,.075f,.035f));
            var cloth = Material("Torch_PitchCloth", new Color(.11f,.08f,.055f));
            var iron = Material("Torch_Iron", new Color(.12f,.11f,.09f));
            var fire = Material("Torch_Flame", new Color(1f,.27f,.025f), true);
            var core = Material("Torch_FlameCore", new Color(1f,.72f,.20f), true);
            var root = new GameObject("PitchTorch");
            try
            {
                Part(root.transform,"Ash handle",PrimitiveType.Cylinder,new Vector3(0,.22f,0),new Vector3(.038f,.31f,.038f),wood);
                Part(root.transform,"Pitch cloth",PrimitiveType.Capsule,new Vector3(0,.48f,0),new Vector3(.075f,.075f,.075f),cloth);
                for(int i=0;i<3;i++) Part(root.transform,"Iron binding",PrimitiveType.Cylinder,new Vector3(0,.43f+i*.055f,0),new Vector3(.079f,.007f,.079f),iron);
                var flame = new GameObject("Visible flame"); flame.transform.SetParent(root.transform,false);
                flame.transform.localPosition = new Vector3(0,.60f,0);
                Part(flame.transform,"Amber flame",PrimitiveType.Sphere,new Vector3(0,.05f,0),new Vector3(.065f,.18f,.065f),fire);
                Part(flame.transform,"Hot core",PrimitiveType.Sphere,new Vector3(0,.01f,.012f),new Vector3(.045f,.10f,.045f),core);
                var lightObject = new GameObject("Flame light"); lightObject.transform.SetParent(root.transform,false);
                lightObject.transform.localPosition = new Vector3(0,.67f,0);
                var light = lightObject.AddComponent<Light>(); light.type = LightType.Point;
                light.color = new Color(1f,.57f,.24f); light.intensity=1.5f; light.range=8f;
                light.shadows=LightShadows.Soft; light.shadowBias=.035f; light.shadowNormalBias=.12f; light.shadowNearPlane=.08f;
                var visual=root.AddComponent<HandheldTorchVisual>(); visual.source=light; visual.flame=flame.transform;
                Folder("Assets/Emberdelve/Prefabs/Weapons");
                PrefabUtility.SaveAsPrefabAsset(root,PrefabPath);
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }

            var pattern=Asset<SwingPatternData>(DataRoot+"Swing_PitchTorch.asset");
            pattern.patternId="pitch_torch_swing"; pattern.kind=SwingKind.Overhead;
            pattern.windupTime=.3f; pattern.activeTime=.22f; pattern.recoveryTime=.4f;
            pattern.maxTargets=1; pattern.sweepRadius=.08f; pattern.reachInset=.3f;
            pattern.restBase=new Vector3(.26f,-.2f,.3f); pattern.restPitch=-55f;
            pattern.keys=new[] { new SwingKey(0,new Vector3(.27f,.1f,.3f),20,-100), new SwingKey(.5f,new Vector3(.2f,-.1f,.4f),10,-10), new SwingKey(1,new Vector3(.1f,-.3f,.35f),0,45) };
            EditorUtility.SetDirty(pattern);
            var weapon=Asset<WeaponData>(WeaponPath);
            weapon.weaponId=HandheldTorchVisual.WeaponId; weapon.displayName="Pitch Torch";
            weapon.kind=WeaponKind.Melee; weapon.baseDamage=8; weapon.reach=.98f; weapon.hitSphereRadius=.1f;
            weapon.windupTime=.3f; weapon.activeWindow=.22f; weapon.recoveryTime=.4f; weapon.cooldown=.92f;
            weapon.swingPatterns=new[]{pattern}; weapon.comboSteps=Array.Empty<ComboStep>();
            weapon.capabilities=DefenseCapability.None; weapon.canRiposte=false;
            weapon.riposteWithOffhandShield=false; weapon.twoHanded=false; weapon.isUnarmed=false;
            weapon.impactPower=4; weapon.heldMoveSpeedPenalty=5;
            weapon.shaftMaterial=wood; weapon.gripMaterial=wood; weapon.edgeMaterial=cloth;
            EditorUtility.SetDirty(weapon);
            var item=Asset<ItemData>(ItemPath);
            item.itemId="pitch_torch"; item.displayName="Pitch Torch";
            item.description="A wrapped pitch torch. Equip in a weapon slot to light your path; stow it to go dark.";
            item.kind=ItemKind.Weapon; item.equipSlot=EquipmentSlot.Weapon; item.grantedWeapon=weapon;
            item.maxStack=1; item.gridFootprint=new Vector2Int(1,2); item.value=3;
            item.iconColor=new Color(.85f,.48f,.12f); item.worldMaterial=wood; item.worldSize=new Vector3(.08f,.65f,.08f);
            EditorUtility.SetDirty(item);
            foreach(string className in new[]{"Warrior","Archer"})
            {
                var data=AssetDatabase.LoadAssetAtPath<CharacterClassData>(DataRoot+"Class_"+className+".asset");
                if(data==null) throw new InvalidOperationException("Missing class "+className);
                data.startingItems=WithStarterTorch(data.startingItems); EditorUtility.SetDirty(data);
            }
            var catalog=AssetDatabase.LoadAssetAtPath<ItemCatalog>(DataRoot+"EmberdelveItemCatalog.asset");
            if(catalog==null) throw new InvalidOperationException("Missing item catalog");
            catalog.EditorSetContents(catalog.Items.Concat(new[]{item}).Distinct().OrderBy(x=>x.itemId,StringComparer.Ordinal).ToArray(),catalog.Affixes.ToArray());
            EditorUtility.SetDirty(catalog);
            var set=AssetDatabase.LoadAssetAtPath<WeaponViewModelSet>("Assets/Emberdelve/Resources/Emberdelve/WeaponViewModelSet.asset");
            if(set==null) throw new InvalidOperationException("Missing weapon model set");
            set.entries=set.entries.Where(e=>e.weaponId!=HandheldTorchVisual.WeaponId).Concat(new[]{Entry()}).ToArray();
            EditorUtility.SetDirty(set); AssetDatabase.SaveAssets();
            Debug.Log("[Emberdelve] Inventory torch authored: catalog, starter bags, weapon and held model.");
        }
        public static ItemData[] WithStarterTorch(ItemData[] items)
        {
            var torch=AssetDatabase.LoadAssetAtPath<ItemData>(ItemPath);
            return torch==null ? items : (items??Array.Empty<ItemData>()).Concat(new[]{torch}).Distinct().ToArray();
        }
        public static WeaponViewModelSet.Entry Entry()
        {
            var prefab=AssetDatabase.LoadAssetAtPath<GameObject>(PrefabPath);
            return prefab==null ? null : new WeaponViewModelSet.Entry {weaponId=HandheldTorchVisual.WeaponId,prefab=prefab,length=Length,sourceId="original_pitch_torch"};
        }
        private static T Asset<T>(string path) where T:ScriptableObject
        {
            var asset=AssetDatabase.LoadAssetAtPath<T>(path);
            if(asset!=null) return asset;
            asset=ScriptableObject.CreateInstance<T>(); AssetDatabase.CreateAsset(asset,path); return asset;
        }
        private static void Folder(string path)
        {
            if(AssetDatabase.IsValidFolder(path)) return;
            int slash=path.LastIndexOf('/'); Folder(path.Substring(0,slash)); AssetDatabase.CreateFolder(path.Substring(0,slash),path.Substring(slash+1));
        }
        private static Material Material(string name, Color color, bool emissive=false)
        {
            const string folder="Assets/Emberdelve/Art/Materials/HandheldTorch"; Folder(folder);
            string path=folder+"/"+name+".mat";
            var material=AssetDatabase.LoadAssetAtPath<Material>(path);
            if(material==null) { material=new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(material,path); }
            material.SetColor("_BaseColor",color); material.SetFloat("_Smoothness",.15f);
            if(emissive) { material.EnableKeyword("_EMISSION"); material.SetColor("_EmissionColor",color*2f); }
            EditorUtility.SetDirty(material); return material;
        }
        private static void Part(Transform parent,string name,PrimitiveType type,Vector3 pos,Vector3 scale,Material mat)
        {
            var part=GameObject.CreatePrimitive(type); part.name=name; part.transform.SetParent(parent,false);
            part.transform.localPosition=pos; part.transform.localScale=scale;
            UnityEngine.Object.DestroyImmediate(part.GetComponent<Collider>());
            var renderer=part.GetComponent<Renderer>(); renderer.sharedMaterial=mat;
            renderer.shadowCastingMode=ShadowCastingMode.Off; renderer.receiveShadows=true;
        }
    }
}
