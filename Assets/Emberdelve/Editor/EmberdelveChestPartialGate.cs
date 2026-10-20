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
[InitializeOnLoad] public static class EmberdelveChestPartialGate {
const string Pending="Emberdelve.ChestPartial.Pending";
static EmberdelveChestPartialGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("ChestPartial_Gate").AddComponent<ChestPartialHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class ChestPartialHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[ChestPartial] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
yield return null;Check(pendingPickup==null,"fully collected loose source destroyed");
Debug.Log("[Emberdelve][ChestPartial] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
LootPickup pendingPickup;
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();var director=FindFirstObjectByType<Emberdelve.Flow.RunDirector>();Check(rig!=null&&director!=null,"real scene host");if(rig==null||director==null)return;
rig.Equipment.Clear();var bag=rig.Inventory;bag.Clear();bag.ConfigureGrid(1,1);
var coin=Make<ItemData>();coin.itemId="partial_gate_coin";coin.kind=ItemKind.Currency;coin.maxStack=80;coin.value=1;
string reason;Check(bag.TryAdd(coin,79,out reason),"initial79coins in1cell");
var table=Make<LootTable>();table.guaranteeFirstEntry=true;table.maxRolls=1;table.entries=new[]{new LootEntry{item=coin,minAmount=5,maxAmount=5,weight=1,maxCopies=1}};
var go=new GameObject("Partial_Chest");cleanup.Add(go);go.transform.position=rig.transform.position+Vector3.forward;var chest=go.AddComponent<Emberdelve.Interaction.ChestInteractable>();chest.Configure(Emberdelve.Interaction.ChestVariant.Supply,table,42,director);
Check(chest.Open(),"chest opened");int remaining=0;foreach(var drop in chest.Remaining)remaining+=drop.Amount;
Debug.Log("[ChestPartial] observed bag="+bag.TotalUnits+" chest="+remaining+" sum="+(bag.TotalUnits+remaining)+" expected84");
Check(bag.TotalUnits==80,"exactly1coin accepted");Check(remaining==4,"unaccepted4coins stay in chest");Check(bag.TotalUnits+remaining==84,"total conserved after partial add");
Check(chest.TakenCount==1&&chest.TakenValue==1,"only actual accepted units credited");
chest.Open();Check(bag.TotalUnits==80&&chest.RemainingUnits==4&&chest.TakenCount==1,"full bag reopen unchanged");
bag.TryRemoveAt(0,1);chest.Open();Check(bag.TotalUnits==80&&chest.RemainingUnits==3&&chest.TakenCount==2,"reopen only actual remainder");
bag.TryRemoveAt(0,79);chest.Open();Check(bag.TotalUnits==4&&chest.RemainingUnits==0&&chest.TakenCount==5,"remaining exact total collected");
Check(bag.RecoveredValue==84&&bag.LifetimeCollected==84&&chest.TakenValue==5,"no duplicate recovered/lifetime/value credit");
bag.Clear();int pickupRecoveryBefore=bag.RecoveredValue,pickupLifetimeBefore=bag.LifetimeCollected;Check(bag.TryAdd(coin,79,out reason),"pickup initial79");
var pickupGo=new GameObject("Partial_Pickup");pendingPickup=pickupGo.AddComponent<LootPickup>();pendingPickup.Configure(coin,5,director);
pendingPickup.OnInteractInstant();Check(bag.TotalUnits==80&&pendingPickup.Amount==4,"loose pickup partial keeps4");
pendingPickup.OnInteractInstant();Check(bag.TotalUnits==80&&pendingPickup.Amount==4,"loose fullbag zero unchanged");
bag.TryRemoveAt(0,1);pendingPickup.OnInteractInstant();Check(bag.TotalUnits==80&&pendingPickup.Amount==3,"loose reopen subtracts1");
bag.TryRemoveAt(0,79);pendingPickup.OnInteractInstant();Check(bag.TotalUnits==4&&bag.RecoveredValue-pickupRecoveryBefore==84&&bag.LifetimeCollected-pickupLifetimeBefore==84,"loose eventual exact total and credit");

bag.Clear();

}
}}
