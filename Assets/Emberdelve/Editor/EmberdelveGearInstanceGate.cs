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
[InitializeOnLoad] public static class EmberdelveGearInstanceGate {
const string Pending="Emberdelve.GearInstance.Pending";
static EmberdelveGearInstanceGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("GearInstance_Gate").AddComponent<GearInstanceHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class GearInstanceHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[GearInstance] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][GearInstance] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();Check(rig!=null&&!rig.IsDead,"actual living scene rig");if(rig==null)return;
var bag=rig.Inventory;var gear=rig.Equipment;gear.Clear();bag.Clear();bag.ConfigureGrid(10,4);
var boots=Make<ItemData>();boots.itemId="gate_boots";boots.kind=ItemKind.Armor;boots.equipSlot=EquipmentSlot.Legs;boots.gridFootprint=new Vector2Int(2,2);boots.value=23;
var big=Make<ItemData>();big.itemId="gate_big_boots";big.kind=ItemKind.Armor;big.equipSlot=EquipmentSlot.Legs;big.gridFootprint=new Vector2Int(4,3);big.value=41;
var filler=Make<ItemData>();filler.itemId="gate_filler";filler.kind=ItemKind.Relic;filler.gridFootprint=Vector2Int.one;filler.maxStack=1;
var dex=Make<AffixDefinition>();dex.id="agile";dex.statKey=AffixStatKey.Dexterity;dex.allowedCategories=AffixCategory.Boots;
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots,big},new[]{dex},out auth,out reason),"catalog");
// R3 fixture: the ad-hoc boots/big boots are not production catalog entries (and the legacy-mint check at the end needs a catalog that owns them), so the bag carries an explicit TEST catalog listing exactly what this gate's authority holds.
var testCatalog=ItemCatalog.CreateForTest(new[]{boots,big},new[]{dex});cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);
ItemInstance a,b,c;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("agile",AffixStatKey.Dexterity,1)},auth,out a,out reason),"first rolled boots");
Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("agile",AffixStatKey.Dexterity,3)},auth,out b,out reason),"same-base different rolled boots");
Check(ItemInstance.TryCreateNew(big,1,AffixRarity.Common,null,auth,out c,out reason),"large gear");
string sa=a.ToSnapshot().ToJson(),sb=b.ToSnapshot().ToJson();
Check(bag.TryAddInstanceAt(a,new Vector2Int(0,0),out reason)&&bag.TryAddInstanceAt(b,new Vector2Int(2,0),out reason),"add exact rolled objects");
Check(!bag.TryAddInstance(a,out reason)&&bag.TotalUnits==2,"duplicate bag UID refused");
Check(!bag.TryMergeStacks(bag.IndexOfInstance(a.Uid),bag.IndexOfInstance(b.Uid),out reason),"same-base gear never merges");
Check(bag.TryMove(bag.IndexOfInstance(a.Uid),new Vector2Int(0,2),out reason)&&ReferenceEquals(bag.InstanceAt(bag.IndexOfInstance(a.Uid)),a),"move preserves exact instance");
Check(bag.TrySwap(bag.IndexOfInstance(a.Uid),bag.IndexOfInstance(b.Uid),out reason),"swap equal footprints");
int minted=bag.MintedInstanceCount,recovered=bag.RecoveredValue,lifetime=bag.LifetimeCollected,collected=0,changes=0;
Action<int,ItemData,int> onCollected=(p,d,total)=>collected++;Action<int> onChanged=p=>changes++;
GameEvents.ItemCollected+=onCollected;GameEvents.InventoryChanged+=onChanged;
try{
float hp=rig.CurrentHealth;
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(b.Uid),out reason)&&ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Legs),b),"index equips requested UID not first definition");
Check(!bag.HoldsInstanceUid(b.Uid)&&bag.HoldsInstanceUid(a.Uid),"unique owner after equip");
Check(!bag.TryAddInstance(b,out reason),"duplicate worn UID refused");
Check(gear.TryEquipFromInventory(bag.IndexOfInstance(a.Uid),out reason)&&ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Legs),a)&&bag.HoldsInstanceUid(b.Uid),"replacement conserves both UIDs");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason)&&bag.HoldsInstanceUid(a.Uid)&&bag.HoldsInstanceUid(b.Uid),"unequip same instances");
Check(bag.MintedInstanceCount==minted&&bag.RecoveredValue==recovered&&bag.LifetimeCollected==lifetime&&collected==0,"transfers no mint or collection credit");
Check(rig.CurrentHealth==hp,"gear transfer no free healing");
Check(sa==a.ToSnapshot().ToJson()&&sb==b.ToSnapshot().ToJson(),"UID rarity rolls unchanged");
}finally{GameEvents.ItemCollected-=onCollected;GameEvents.InventoryChanged-=onChanged;}
bag.Clear();gear.Clear();Check(bag.TryAddInstance(c,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(c.Uid),out reason),"wear large gear");
Check(bag.TryAddInstanceAt(a,Vector2Int.zero,out reason),"small incoming in bag");
for(int y=0;y<4;y++)for(int x=0;x<10;x++)if(!bag.IsCellOccupied(new Vector2Int(x,y)))Check(bag.TryAddAt(filler,1,new Vector2Int(x,y),out reason),"fill free cell");
Check(bag.FreeCells==0,"bag actually full");int units=bag.TotalUnits,value=bag.CarriedValue;recovered=bag.RecoveredValue;minted=bag.MintedInstanceCount;
Check(!gear.TryEquipFromInventory(bag.IndexOfInstance(a.Uid),out reason),"impossible large outgoing swap refused");
Check(ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Legs),c)&&bag.HoldsInstanceUid(a.Uid)&&bag.TotalUnits==units&&bag.CarriedValue==value&&bag.RecoveredValue==recovered&&bag.MintedInstanceCount==minted,"failed swap conserves exact identity and counters");
Check(!gear.TryUnequip(EquipmentSlot.Legs,out reason)&&ReferenceEquals(gear.EquippedInstance(EquipmentSlot.Legs),c),"fullbag unequip refused without loss");
gear.Clear();bag.Clear();minted=bag.MintedInstanceCount;Check(bag.TryAdd(boots,2,out reason)&&bag.TotalUnits==2&&bag.MintedInstanceCount==minted+2,"legacy gear mints once per item");
Check(bag.InstanceAt(0)!=null&&bag.InstanceAt(1)!=null&&bag.InstanceAt(0).Uid!=bag.InstanceAt(1).Uid&&bag.InstanceAt(0).Rarity==AffixRarity.Common,"legacy common distinct UIDs");
bag.Clear();
}
}}
