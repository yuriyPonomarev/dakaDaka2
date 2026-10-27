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
[InitializeOnLoad] public static class EmberdelveNumericAffixGate {
const string Pending="Emberdelve.NumericAffix.Pending";
static EmberdelveNumericAffixGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("NumericAffix_Gate").AddComponent<NumericAffixHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class NumericAffixHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[NumericAffix] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][NumericAffix] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();Check(rig!=null&&!rig.IsDead,"living scene rig");if(rig==null)return;var bag=rig.Inventory;var gear=rig.Equipment;gear.Clear();bag.Clear();bag.ConfigureGrid(10,4);
var boots=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_FootpadBoots.asset");
var keys=new[]{AffixStatKey.MaxHealth,AffixStatKey.MaxMana,AffixStatKey.MoveSpeed,AffixStatKey.ManaRegen,AffixStatKey.Constitution};
var defs=new AffixDefinition[5];float[] lows={5,5,.03f,.1f,1},highs={15,15,.12f,.4f,3},steps={1,1,.01f,.1f,1};
for(int i=0;i<5;i++){defs[i]=Make<AffixDefinition>();defs[i].id="numeric_"+i;defs[i].statKey=keys[i];defs[i].minValue=lows[i];defs[i].maxValue=highs[i];defs[i].step=steps[i];defs[i].allowedCategories=AffixCategory.Boots;}
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots},defs,out auth,out reason),"catalog");
// R3 fixture: the gate rolls with its own ad-hoc affix definitions, which the production catalog does not own and the bag now validates every instance against, so the bag carries an explicit TEST catalog (real boots + exactly those affixes).
var testCatalog=ItemCatalog.CreateForTest(new[]{boots},defs);cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);string before=EditorJsonUtility.ToJson(boots);
ItemInstance pools;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Rare,new[]{new AffixRecord("numeric_0",AffixStatKey.MaxHealth,10),new AffixRecord("numeric_1",AffixStatKey.MaxMana,10)},auth,out pools,out reason),"pool gear");
EmberdelveKitFixtures.SpendMana(rig.Mana, 20);float hp=rig.CurrentHealth,mp=rig.Mana.CurrentMana,delay=rig.Mana.RegenDelayRemaining;
Check(bag.TryAddInstance(pools,out reason),"add pool gear");
for(int cycle=0;cycle<3;cycle++){
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(pools.Uid),out reason),"equip pool gear");
Check(rig.MaxHealth==130&&rig.Mana.MaxMana==80,"real HP and mana flat caps");
Check(rig.CurrentHealth==hp&&rig.Mana.CurrentMana==mp,"larger caps do not refill");
Check(Mathf.Abs(rig.Mana.RegenDelayRemaining-delay)<.0001f,"regen clock retained");
gear.RefreshStats();gear.RefreshStats();Check(rig.MaxHealth==130&&rig.Mana.MaxMana==80,"refresh no accumulation");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason)&&rig.MaxHealth==120&&rig.Mana.MaxMana==70&&rig.Mana.CurrentMana==mp,"unequip restores exact caps");}
ItemInstance motion;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Rare,new[]{new AffixRecord("numeric_2",AffixStatKey.MoveSpeed,.1f),new AffixRecord("numeric_3",AffixStatKey.ManaRegen,.2f)},auth,out motion,out reason),"motion and regen gear");
Check(bag.TryAddInstance(motion,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(motion.Uid),out reason),"equip motion gear");
Check(Mathf.Abs(rig.Movement.ResolveWalkSpeed()-5.174f)<.0001f,"fixed boots multiplier then flat speed");
Check(Mathf.Abs(rig.Mana.RegenPerSecond-3.2f)<.0001f,"WIS-derived regen plus flat");
Check(Mathf.Abs(rig.Movement.ResolveGroundSpeed(true,false)-5.824f*(1f-18f/300f))<.0001f,"walk flat does not alter sprint (x the D1b held factor: sword 10 + shield 8 points of 300)");
Check(Mathf.Abs(rig.Movement.ResolveGroundSpeed(false,true)-1.7f*(1f-18f/300f))<.0001f,"walk flat does not alter crouch (x the D1b held factor)");
Check(rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Dexterity)==11,"fixed DEX remains once");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason),"remove motion gear");
ItemInstance combined;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Rare,new[]{new AffixRecord("numeric_4",AffixStatKey.Constitution,3),new AffixRecord("numeric_0",AffixStatKey.MaxHealth,10)},auth,out combined,out reason),"CON plus HP gear");
Check(bag.TryAddInstance(combined,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(combined.Uid),out reason),"equip combined gear");Check(rig.MaxHealth==140&&rig.CurrentHealth==hp,"derived CON cap plus flatHP once/no heal");Check(gear.TryUnequip(EquipmentSlot.Legs,out reason),"remove combined gear");
ItemInstance slow=null,fast=null;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("numeric_2",AffixStatKey.MoveSpeed,.03f)},auth,out slow,out reason)&&ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("numeric_2",AffixStatKey.MoveSpeed,.12f)},auth,out fast,out reason),"same-base distinct speed rolls");
Check(bag.TryAddInstance(slow,out reason)&&bag.TryAddInstance(fast,out reason),"carry both speed rolls");
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(slow.Uid),out reason)&&Mathf.Abs(rig.Movement.ResolveWalkSpeed()-5.104f)<.0001f,"selected slower UID");
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(fast.Uid),out reason)&&Mathf.Abs(rig.Movement.ResolveWalkSpeed()-5.194f)<.0001f,"selected faster UID swap");
Check(gear.EquippedInstance(EquipmentSlot.Legs).Uid==fast.Uid&&bag.HoldsInstanceUid(slow.Uid),"swap preserves metadata ownership");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason)&&Mathf.Abs(rig.Movement.ResolveWalkSpeed()-4.3f)<.0001f&&Mathf.Abs(rig.Mana.RegenPerSecond-3f)<.0001f,"all numeric effects removed");
Check(before==EditorJsonUtility.ToJson(boots),"fixed definition unchanged");gear.Clear();bag.Clear();

}
}}
