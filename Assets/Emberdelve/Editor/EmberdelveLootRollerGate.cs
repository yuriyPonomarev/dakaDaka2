using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Data;
using Emberdelve.Items;
namespace Emberdelve.EditorTools {
public static class EmberdelveLootRollerGate {
static int n,fail;static List<UnityEngine.Object> clean=new List<UnityEngine.Object>();
static void Check(bool x,string t){n++;if(!x){fail++;Debug.LogError("[LootRoller] mismatch "+t);}}
static T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();clean.Add(x);return x;}
// R4 fixture rewrite: the roller now takes the project ILootRng instead of System.Random, so the scripted generator implements ILootRng. Only NextDouble (weight ticket) and Next(max) (grid index) are used by the roller; same scripted values as before.
sealed class FixedRandom:ILootRng {readonly double ticket;readonly bool high;public int Calls;public FixedRandom(double t,bool h=false){ticket=t;high=h;}public double NextDouble(){Calls++;return ticket;}public int Next(int max){Calls++;return high?max-1:0;}public int Next(int a,int b){Calls++;return a;}public ulong NextUInt64(){Calls++;return 0;}public uint NextUInt32(){Calls++;return 0;}}
static bool Same(AffixRecord[] a,IReadOnlyList<AffixRecord> b){if(a.Length!=b.Count)return false;for(int i=0;i<a.Length;i++)if(!a[i].Equals(b[i]))return false;return true;}
public static void Run(){n=fail=0;try{
var boots=Make<ItemData>();boots.itemId="boots";boots.kind=ItemKind.Armor;boots.equipSlot=EquipmentSlot.Legs;
var coins=Make<ItemData>();coins.itemId="coins";coins.kind=ItemKind.Currency;coins.maxStack=80;
var dex=Make<AffixDefinition>();dex.id="agile";dex.statKey=AffixStatKey.Dexterity;dex.allowedCategories=AffixCategory.Boots;dex.weight=1;
var con=Make<AffixDefinition>();con.id="hearty";con.statKey=AffixStatKey.Constitution;con.allowedCategories=AffixCategory.Boots;con.weight=3;
var str=Make<AffixDefinition>();str.id="strong";str.statKey=AffixStatKey.Strength;str.allowedCategories=AffixCategory.Weapon;
ItemInstanceAuthority auth,reordered;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots,coins},new[]{dex,con,str},out auth,out reason),"catalog");
Check(ItemInstanceAuthority.TryCreate(new[]{coins,boots},new[]{str,con,dex},out reordered,out reason),"reordered catalog");
var issuer=ItemIdentityIssuer.CreateLocalTrusted();ItemInstance item;var random=new FixedRandom(.1);
Check(LootRoller.TryRoll(coins,8,AffixRarity.Common,auth,random,issuer,out item,out reason)&&item.AffixCount==0&&issuer.MintedCount==1&&random.Calls==0,"Common no random consumption");
string before=EditorJsonUtility.ToJson(boots)+EditorJsonUtility.ToJson(dex)+EditorJsonUtility.ToJson(con)+EditorJsonUtility.ToJson(str);
var variants=new HashSet<string>();
for(int seed=0;seed<32;seed++){
AffixRecord[] a,b,c;bool ok=LootRoller.TryRollAffixes(boots,AffixRarity.Rare,auth,DeterministicRng.FromInt(seed),out a,out reason);
Check(ok,"seeded Rare succeeds "+seed);if(!ok)continue;
Check(LootRoller.TryRollAffixes(boots,AffixRarity.Rare,reordered,DeterministicRng.FromInt(seed),out b,out reason)&&Same(a,b),"seed repeat order-independent "+seed);
Check(a.Length==2&&a[0].Id!=a[1].Id&&a[0].StatKey!=a[1].StatKey,"Rare count uniqueness "+seed);
Check(LootRoller.TryRoll(boots,1,AffixRarity.Rare,auth,DeterministicRng.FromInt(seed),issuer,out item,out reason)&&Same(a,item.Affixes),"mint/core same rolls "+seed);
Check(ItemInstance.TryRestore(item.ToSnapshot(),auth,out var copy,out reason)&&copy.Uid==item.Uid&&Same(a,copy.Affixes),"restore no reroll "+seed);
foreach(var r in a)Check(r.Id!="strong"&&r.Value>=1&&r.Value<=3&&r.Value==Mathf.Floor(r.Value),"category/grid valid");
variants.Add(a[0].Id+":"+a[0].Value+":"+a[1].Value);
}
Check(variants.Count>1,"different seeds vary rolls");
AffixRecord[] rolls;
Check(LootRoller.TryRollAffixes(boots,AffixRarity.Uncommon,auth,new FixedRandom(.249),out rolls,out reason)&&rolls.Length==1&&rolls[0].Id=="agile"&&rolls[0].Value==1,"weight interval below one quarter/min endpoint");
Check(LootRoller.TryRollAffixes(boots,AffixRarity.Uncommon,auth,new FixedRandom(.251,true),out rolls,out reason)&&rolls[0].Id=="hearty"&&rolls[0].Value==3,"weight interval above one quarter/max endpoint");
int minted=issuer.MintedCount;
Check(!LootRoller.TryRoll(boots,2,AffixRarity.Rare,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null&&issuer.MintedCount==minted,"quantity rejection no mint");
Check(!LootRoller.TryRoll(boots,1,(AffixRarity)99,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null,"unknown rarity");
Check(!LootRoller.TryRoll(coins,1,AffixRarity.Uncommon,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null,"rolled currency rejection");
Check(!LootRoller.TryRoll(boots,1,AffixRarity.Rare,auth,null,issuer,out item,out reason)&&item==null,"null random");
con.allowedCategories=AffixCategory.Weapon;
Check(!LootRoller.TryRoll(boots,1,AffixRarity.Rare,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null,"insufficient eligible distinct keys");con.allowedCategories=AffixCategory.Boots;
foreach(float w in new[]{0f,-1f,float.NaN,float.PositiveInfinity}){dex.weight=w;Check(!LootRoller.TryRoll(boots,1,AffixRarity.Rare,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null,"changed invalid weight");}dex.weight=1;
dex.step=.5f;Check(!LootRoller.TryRoll(boots,1,AffixRarity.Rare,auth,new FixedRandom(.1),issuer,out item,out reason)&&item==null,"changed invalid grid");dex.step=1;
Check(issuer.MintedCount==minted,"all refusals no mint");
Check(before==EditorJsonUtility.ToJson(boots)+EditorJsonUtility.ToJson(dex)+EditorJsonUtility.ToJson(con)+EditorJsonUtility.ToJson(str),"definitions unchanged");
}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in clean)if(x!=null)UnityEngine.Object.DestroyImmediate(x);clean.Clear();}
Debug.Log("[Emberdelve][LootRoller] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);
}
}}
