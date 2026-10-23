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
[InitializeOnLoad] public static class EmberdelveChestInstanceGate {
const string Pending="Emberdelve.ChestInstance.Pending";
static EmberdelveChestInstanceGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("ChestInstance_Gate").AddComponent<ChestInstanceHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class ChestInstanceHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[ChestInstance] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][ChestInstance] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();var director=FindFirstObjectByType<Emberdelve.Flow.RunDirector>();Check(rig!=null&&director!=null&&!rig.IsDead,"actual living run host");if(rig==null||director==null)return;var bag=rig.Inventory;rig.Equipment.Clear();bag.Clear();bag.ConfigureGrid(1,1);
var boots=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_FootpadBoots.asset");var filler=Make<ItemData>();filler.itemId="chest_gate_filler";filler.maxStack=1;
string reason;Check(bag.TryAdd(filler,1,out reason),"full bag fixture");
var dex=Make<AffixDefinition>();dex.id="chest_dex";dex.statKey=AffixStatKey.Dexterity;dex.allowedCategories=AffixCategory.Boots;
var con=Make<AffixDefinition>();con.id="chest_con";con.statKey=AffixStatKey.Constitution;con.allowedCategories=AffixCategory.Boots;
// R3 fixture: this gate rolls with ad-hoc test affixes, which the production catalog (rightly) does not own, so the bag carries an explicit TEST catalog (real boots + the two test affixes) for the duration; the production rule is untouched.
var realCatalog=bag.Catalog;var testCatalog=ItemCatalog.CreateForTest(new[]{boots},new[]{dex,con});cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);
var table=Make<LootTable>();table.maxRolls=1;table.guaranteeFirstEntry=true;table.affixCatalog=new[]{dex,con};table.entries=new[]{new LootEntry{item=boots,minAmount=1,maxAmount=1,weight=1,maxCopies=1,affixRarity=AffixRarity.Rare}};
string before=EditorJsonUtility.ToJson(boots)+EditorJsonUtility.ToJson(dex)+EditorJsonUtility.ToJson(con);
var first=new List<LootDrop>();var repeat=new List<LootDrop>();Check(table.TryRoll(123,first,testCatalog,out reason)&&table.TryRoll(123,repeat,testCatalog,out reason),"seeded Rare table rolls");
Check(first.Count==1&&first[0].Instance!=null&&first[0].Instance.AffixCount==2,"Rare gear concrete instance");
Check(first[0].Instance.Uid!=repeat[0].Instance.Uid&&first[0].Instance.Affixes[0].Equals(repeat[0].Instance.Affixes[0])&&first[0].Instance.Affixes[1].Equals(repeat[0].Instance.Affixes[1]),"same seed same rolls distinct mintedUIDs");
var go=new GameObject("Rolled_Chest");cleanup.Add(go);go.transform.position=rig.transform.position+rig.transform.forward;/*R2 fixture: the chest checks reach and sight for its actor, so it stands next to the rig*/var chest=go.AddComponent<Emberdelve.Interaction.ChestInteractable>();chest.Configure(Emberdelve.Interaction.ChestVariant.Supply,table,123,director);
Check(chest.Open(rig)&&chest.Remaining.Count==1,"full bag leaves rolled gear");var instance=chest.Remaining[0].Instance;string snapshot=instance.ToSnapshot().ToJson();int minted=bag.MintedInstanceCount;
Check(chest.Open(rig)&&ReferenceEquals(chest.Remaining[0].Instance,instance)&&chest.Remaining[0].Instance.ToSnapshot().ToJson()==snapshot,"reopen retains exact UID and rolls");
bag.ConfigureGrid(10,4);Check(chest.Open(rig)&&chest.Remaining.Count==0&&bag.HoldsInstanceUid(instance.Uid),"freed space transfers owned instance");
Check(ReferenceEquals(bag.InstanceAt(bag.IndexOfInstance(instance.Uid)),instance)&&bag.MintedInstanceCount==minted,"transfer no copy or remint");
int recovered=bag.RecoveredValue;chest.Open(rig);Check(bag.RecoveredValue==recovered,"empty reopen no credit");
Check(rig.Equipment.TryEquipFromInventory(bag.IndexOfInstance(instance.Uid),out reason)&&ReferenceEquals(rig.Equipment.EquippedInstance(EquipmentSlot.Legs),instance),"actual chest gear equipped");
Check(rig.AttributeStats.GetAttribute(Emberdelve.Stats.SixAttribute.Dexterity)>11,"actual random DEX bonus applied");
Check(!director.TryCollectLootInstance(rig,instance,out reason),"duplicate worn UID refused");
Check(before==EditorJsonUtility.ToJson(boots)+EditorJsonUtility.ToJson(dex)+EditorJsonUtility.ToJson(con),"definitions unchanged");
var common=Make<LootTable>();common.maxRolls=1;common.guaranteeFirstEntry=true;common.entries=new[]{new LootEntry{item=boots,minAmount=1,maxAmount=1,weight=1,maxCopies=1,affixRarity=AffixRarity.Common}};var commonDrops=new List<LootDrop>();Check(common.TryRoll(123,commonDrops,testCatalog,out reason),"Common table valid");Check(commonDrops.Count==1&&commonDrops[0].Instance!=null&&commonDrops[0].Instance.Rarity==AffixRarity.Common&&commonDrops[0].Instance.AffixCount==0,"Common gear also owns UID before pickup");

var commonGo=new GameObject("Common_Chest");cleanup.Add(commonGo);commonGo.transform.position=rig.transform.position+rig.transform.forward;var commonChest=commonGo.AddComponent<Emberdelve.Interaction.ChestInteractable>();commonChest.Configure(Emberdelve.Interaction.ChestVariant.Supply,common,123,director);
bag.ConfigureGrid(1,1);Check(commonChest.Open(rig)&&commonChest.Remaining.Count==1,"Common fullbag retains owned gear");var commonInstance=commonChest.Remaining[0].Instance;string commonSnapshot=commonInstance.ToSnapshot().ToJson();
Check(commonChest.Open(rig)&&ReferenceEquals(commonChest.Remaining[0].Instance,commonInstance)&&commonChest.Remaining[0].Instance.ToSnapshot().ToJson()==commonSnapshot,"Common reopen no remint");
bag.ConfigureGrid(10,4);int commonMinted=bag.MintedInstanceCount;Check(commonChest.Open(rig)&&commonChest.Remaining.Count==0&&ReferenceEquals(bag.InstanceAt(bag.IndexOfInstance(commonInstance.Uid)),commonInstance)&&bag.MintedInstanceCount==commonMinted,"Common exact instance transfer");
common.entries[0].minAmount=2;common.entries[0].maxAmount=2;var commonPair=new List<LootDrop>();Check(common.TryRoll(123,commonPair,testCatalog,out reason)&&commonPair.Count==2&&commonPair[0].Instance!=null&&commonPair[1].Instance!=null&&commonPair[0].Instance.Uid!=commonPair[1].Instance.Uid&&commonPair[0].Amount==1&&commonPair[1].Amount==1,"two Common units distinct UID quantity1");
var issuer=ItemIdentityIssuer.CreateLocalTrusted();table.entries[0].affixRarity=(AffixRarity)99;var invalid=new List<LootDrop>();Check(!table.TryRoll(123,invalid,testCatalog,issuer,out reason)&&invalid.Count==0&&issuer.MintedCount==0,"invalid profile atomic refusal no mint");
rig.Equipment.Clear();bag.Clear();bag.SetCatalog(realCatalog);

}
}}
