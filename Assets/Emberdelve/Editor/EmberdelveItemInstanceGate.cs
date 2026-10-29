using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Items;
namespace Emberdelve.EditorTools {
public static class EmberdelveItemInstanceGate {
static int n,fail;static List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
static void Check(bool ok,string name){n++;if(!ok){fail++;Debug.LogError("[ItemInstance] mismatch "+name);}}
static T Make<T>() where T:ScriptableObject {T a=ScriptableObject.CreateInstance<T>();cleanup.Add(a);return a;}
static AffixDefinition Affix(string id,AffixStatKey key){var a=Make<AffixDefinition>();a.id=id;a.statKey=key;a.allowedCategories=AffixCategory.Boots;return a;}
static void Reject(ItemSnapshot dto,ItemInstanceAuthority authority,string label){ItemInstance x;string reason;bool ok=ItemInstance.TryRestore(dto,authority,out x,out reason);Check(!ok&&x==null&&!string.IsNullOrEmpty(reason),label);}
public static void Run(){n=fail=0;try{
var boots=Make<ItemData>();boots.itemId="boots";boots.kind=ItemKind.Armor;boots.equipSlot=EquipmentSlot.Legs;boots.maxStack=1;
var coins=Make<ItemData>();coins.itemId="coins";coins.kind=ItemKind.Currency;coins.maxStack=80;
var dex=Affix("agile",AffixStatKey.Dexterity);var con=Affix("hearty",AffixStatKey.Constitution);
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots,coins},new[]{dex,con},out auth,out reason),"trusted catalog");
string baseBefore=EditorJsonUtility.ToJson(boots),affixBefore=EditorJsonUtility.ToJson(dex);
ItemInstance common,uncommon,rare;
Check(ItemInstance.TryCreateNew(coins,8,AffixRarity.Common,null,auth,out common,out reason),"common currency");
Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Common,null,auth,out uncommon,out reason),"common gear");
var records=new[]{new AffixRecord("agile",AffixStatKey.Dexterity,2)};
Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,records,auth,out uncommon,out reason),"uncommon gear");
records[0]=new AffixRecord("hearty",AffixStatKey.Constitution,3);Check(uncommon.Affixes[0].Id=="agile"&&uncommon.Affixes[0].Value==2,"input array independent");
Check(!(uncommon.Affixes is System.Collections.IList)&&!(uncommon.Affixes is AffixRecord[]),"immutable collection view");
Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Rare,new[]{new AffixRecord("agile",AffixStatKey.Dexterity,2),new AffixRecord("hearty",AffixStatKey.Constitution,3)},auth,out rare,out reason),"rare distinct affixes");
var dto=rare.ToSnapshot();dto.affixes[0].value=1;Check(rare.Affixes[0].Value==2,"snapshot alias independent");
ItemSnapshot decoded;Check(ItemSnapshot.TryFromJson(rare.ToSnapshot().ToJson(),out decoded,out reason),"JSON read");
ItemInstance restored;Check(ItemInstance.TryRestore(decoded,auth,out restored,out reason),"strict restore");
Check(restored.Uid==rare.Uid&&restored.Quantity==1&&restored.Affixes[0].Equals(rare.Affixes[0])&&restored.Affixes[1].Equals(rare.Affixes[1]),"exact UID and rolls roundtrip");
ItemInstance copy;Check(rare.TryCopy(auth,out copy,out reason)&&copy.Uid==rare.Uid&&copy.Affixes[0].Equals(rare.Affixes[0]),"copy retains UID and rolls");
Check(common.TryWithQuantity(4,out copy,out reason)&&copy.Uid==common.Uid&&copy.Quantity==4&&common.Quantity==8,"quantity update immutable and UID stable");
Check(!common.TryWithQuantity(81,out copy,out reason)&&copy==null,"overflow quantity atomically refused");
var valid=rare.ToSnapshot();
foreach(string uid in new[]{"",new string('0',32),Guid.NewGuid().ToString("D"),"not_a_uid"}){dto=valid.Clone();dto.uid=uid;Reject(dto,auth,"malformed UID");}
dto=valid.Clone();dto.itemId="unknown";Reject(dto,auth,"unknown item");
foreach(int quantity in new[]{0,2,-1}){dto=valid.Clone();dto.quantity=quantity;Reject(dto,auth,"gear quantity");}
// D3a: 3..6 are Epic/Legendary/Unique/Poor now; the first unknown stored int is 7.
foreach(int rarity in new[]{-1,7,99}){dto=valid.Clone();dto.rarity=rarity;Reject(dto,auth,"unknown rarity");}
dto=valid.Clone();dto.rarity=0;Reject(dto,auth,"count rarity mismatch");
dto=valid.Clone();dto.affixes[1]=dto.affixes[0].Clone();Reject(dto,auth,"duplicate affix ID/key");
dto=valid.Clone();dto.affixes[0].id="unknown";Reject(dto,auth,"unknown affix");
dto=valid.Clone();dto.affixes[0].key="constitution";Reject(dto,auth,"wrong ID/key pair");
foreach(string key in new[]{"unknown","Dexterity","dexterity "}){dto=valid.Clone();dto.affixes[0].key=key;Reject(dto,auth,"noncanonical key");}
foreach(float value in new[]{float.NaN,float.PositiveInfinity,-1f,4f,1.5f}){dto=valid.Clone();dto.affixes[0].value=value;Reject(dto,auth,"bad rolled amount");}
dto=valid.Clone();dto.affixes[0]=null;Reject(dto,auth,"null affix");
dto=valid.Clone();dto.itemId="coins";Reject(dto,auth,"rolled currency refused");
dex.allowedCategories=AffixCategory.Weapon;Reject(valid,auth,"category refused");dex.allowedCategories=AffixCategory.Boots;
Check(baseBefore==EditorJsonUtility.ToJson(boots)&&affixBefore==EditorJsonUtility.ToJson(dex),"definitions unchanged after checks");
var rogue=Make<ItemData>();rogue.itemId="unknown_item";rogue.kind=ItemKind.Currency;rogue.maxStack=80;
Check(!ItemInstance.TryCreateNew(rogue,1,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null,"creation rejects unregistered definition");
rogue.itemId="coins";rogue.maxStack=999;
Check(!ItemInstance.TryCreateNew(rogue,999,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null,"creation rejects shadow definition sharing catalog ID");
var issuer=ItemIdentityIssuer.CreateLocalTrusted();
Check(!issuer.TryCreateInstance(rogue,999,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null&&issuer.MintedCount==0,"issuer refuses shadow without mint");
Guid supplied=Guid.NewGuid();
Check(!ItemInstance.TryCreateWithUid(supplied,rogue,1,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null,"supplied UID cannot bypass shadow check");
rogue.itemId="unknown_item";
Check(!issuer.TryCreateInstance(rogue,1,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null&&issuer.MintedCount==0,"issuer refuses unknown without mint");
Check(issuer.TryCreateInstance(coins,8,AffixRarity.Common,null,auth,out copy,out reason)&&issuer.MintedCount==1,"issuer legitimate registered item");
Check(ItemInstance.TryCreateWithUid(supplied,coins,8,AffixRarity.Common,null,auth,out copy,out reason)&&copy.Uid==supplied,"supplied UID preserved for trusted item");
Check(!ItemInstance.TryCreateWithUid(Guid.Empty,coins,8,AffixRarity.Common,null,auth,out copy,out reason)&&copy==null,"empty supplied UID refused");
}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var a in cleanup)if(a!=null)UnityEngine.Object.DestroyImmediate(a);cleanup.Clear();}
Debug.Log("[Emberdelve][ItemInstance] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EmberdelveGateExit.Exit(fail==0?0:1);
}
}}
