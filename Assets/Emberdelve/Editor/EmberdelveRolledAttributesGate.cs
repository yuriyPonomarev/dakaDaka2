using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
using Emberdelve.Player;
namespace Emberdelve.EditorTools {
[InitializeOnLoad] public static class EmberdelveRolledAttributesGate {
const string Pending="Emberdelve.RolledAttributes.Pending";
static EmberdelveRolledAttributesGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("RolledAttributes_Gate").AddComponent<RolledAttributesHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class RolledAttributesHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[RolledAttributes] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][RolledAttributes] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();Check(rig!=null&&!rig.IsDead,"live scene rig");if(rig==null)return;
var bag=rig.Inventory;var gear=rig.Equipment;gear.Clear();bag.Clear();bag.ConfigureGrid(10,4);
var boots=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_FootpadBoots.asset");
var defs=new AffixDefinition[6];for(int i=0;i<6;i++){defs[i]=Make<AffixDefinition>();defs[i].id="stat_"+i;defs[i].statKey=(AffixStatKey)i;defs[i].allowedCategories=AffixCategory.Boots;}
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots},defs,out auth,out reason),"attribute affix catalog");
// R3 fixture: the gate rolls with its own ad-hoc affix definitions, which the production catalog does not own and the bag now validates every instance against, so the bag carries an explicit TEST catalog (real boots + exactly those affixes).
var testCatalog=ItemCatalog.CreateForTest(new[]{boots},defs);cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);
Check(rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Constitution)==14&&rig.MaxHealth==120&&rig.Mana.MaxMana==70,"bare warrior baseline");
string classBefore=EditorJsonUtility.ToJson(rig.Class),bootsBefore=EditorJsonUtility.ToJson(boots);
ItemInstance rare;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Rare,new[]{new AffixRecord("stat_2",AffixStatKey.Constitution,3),new AffixRecord("stat_3",AffixStatKey.Intelligence,3)},auth,out rare,out reason),"rare CON INT gear");
Check(bag.TryAddInstance(rare,out reason),"carry rare gear");rig.Mana.TrySpend(20);
float hp=rig.CurrentHealth,mp=rig.Mana.CurrentMana;int minted=bag.MintedInstanceCount;
for(int cycle=0;cycle<3;cycle++){
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(rare.Uid),out reason),"equip rare cycle"+cycle);
Check(rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Constitution)==17&&rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Intelligence)==11&&rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Dexterity)==11,"fixed and rolled scores once");
Check(rig.MaxHealth==130&&rig.Mana.MaxMana==75,"actual pools derive rolled scores");
Check(rig.CurrentHealth==hp&&rig.Mana.CurrentMana==mp,"larger ceilings do not refill");
rig.RefreshEquipmentStats();rig.RefreshEquipmentStats();Check(rig.MaxHealth==130&&rig.Mana.MaxMana==75,"refresh no accumulation");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason)&&rig.MaxHealth==120&&rig.Mana.MaxMana==70,"unequip exact restore");
}
for(int i=0;i<6;i++){
ItemInstance single;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("stat_"+i,(AffixStatKey)i,3)},auth,out single,out reason),"create attribute variant"+i);
Check(bag.TryAddInstance(single,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(single.Uid),out reason),"equip attribute variant"+i);
int[] expected={16,10,14,8,10,10};expected[1]++;expected[i]+=3;
for(int j=0;j<6;j++)Check(rig.AttributeStats.GetAttribute((Emberdelve.Stats.SixAttribute)j)==expected[j],"score exact variant"+i+" stat"+j);
if(i==0)Check(Mathf.Abs(rig.GetOutgoingDamageMultiplier(rig.MainHandWeapon)-1.392f)<.0001f,"rolled STR actual weapon scaling");
if(i==1)Check(Mathf.Abs(rig.Movement.ResolveWalkSpeed()-5.3277f)<.0001f,"rolled DEX actual walking plus fixed boots");
if(i==4)Check(Mathf.Abs(rig.Mana.RegenPerSecond-3.25f)<.0001f,"rolled WIS real mana regen rate");
if(i==5)Check(Mathf.Abs(rig.AttributeStats.GetAbilityEffectScale()-1.04f)<.0001f,"rolled CHA actual ability scale");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason),"unequip variant"+i);
}
Check(bag.MintedInstanceCount==minted,"rolled transfers mint no UIDs");
Check(classBefore==EditorJsonUtility.ToJson(rig.Class)&&bootsBefore==EditorJsonUtility.ToJson(boots),"class and fixed definition unchanged");
gear.Clear();bag.Clear();

}
}}
