using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Stats;
namespace Emberdelve.EditorTools {
public static class EmberdelveAttributeDataGate {
 static int n,fail;
 static void Check(bool value,string name){n++;if(!value){fail++;Debug.LogError("[AttributeData] mismatch "+name);}}
 static T Load<T>(string name) where T:UnityEngine.Object {var a=AssetDatabase.LoadAssetAtPath<T>("Assets/Emberdelve/Data/"+name+".asset");if(a==null)throw new Exception("Missing "+name);return a;}
 public static void Run(){
 n=fail=0;ItemData temp=null;CharacterClassData custom=null;
 try {
 var w=Load<CharacterClassData>("Class_Warrior");var a=Load<CharacterClassData>("Class_Archer");var b=Load<ItemData>("Item_FootpadBoots");var c=Load<ItemData>("Item_BoiledCuirass");
 int[] we={16,10,14,8,10,10}, ar={10,16,12,10,14,8};
 for(int i=0;i<6;i++){Check(w.BaseAttributes.Get((SixAttribute)i)==we[i],"warrior score "+i);Check(a.BaseAttributes.Get((SixAttribute)i)==ar[i],"archer score "+i);}
 // U2A3 corrected this fixture: ATTRIBUTES.md now authors warrior 100/75/3 and archer 80/90/3.5 as the
 // pristine BASES the six attributes derive from, and U2A3 migrated those numbers in the builder and in the
 // saved class assets. Asserting the pre-amendment 150/120 and 100/100 would now assert wrong data; the
 // coverage is preserved as exact authored values and extended with the new mana pair.
 Check(w.maxHealth==100&&w.maxMana==75&&Mathf.Approximately(w.manaRegenPerSecond,3f),"warrior amended base vitals");
 Check(a.maxHealth==80&&a.maxMana==90&&Mathf.Approximately(a.manaRegenPerSecond,3.5f),"archer amended base vitals");
 Check(b.AttributeBonusTotals.Total(SixAttribute.Dexterity)==1,"boots DEX1");Check(c.AttributeBonusTotals.Total(SixAttribute.Constitution)==1,"cuirass CON1");
 custom=ScriptableObject.CreateInstance<CharacterClassData>();custom.strength=-9;custom.charisma=99;Check(custom.BaseAttributes.Strength==1&&custom.BaseAttributes.Charisma==30,"programmatic score clamp");Check(custom.strength==-9&&custom.charisma==99,"snapshot does not mutate class");
 temp=ScriptableObject.CreateInstance<ItemData>();Check(temp.AttributeBonusTotals.IsEmpty,"null bonus");temp.attributeBonuses=new AttributeBonusEntry[0];Check(temp.AttributeBonusTotals.IsEmpty,"empty bonus");
 temp.attributeBonuses=new[]{new AttributeBonusEntry(SixAttribute.Dexterity,7),new AttributeBonusEntry(SixAttribute.Dexterity,8),new AttributeBonusEntry(SixAttribute.Strength,99),new AttributeBonusEntry(SixAttribute.Wisdom,-99),new AttributeBonusEntry((SixAttribute)99,5)};
 string before=EditorJsonUtility.ToJson(temp);var totals=temp.AttributeBonusTotals;Check(totals.Total(SixAttribute.Dexterity)==15,"sum before score clamp");Check(totals.Total(SixAttribute.Strength)==10&&totals.Total(SixAttribute.Wisdom)==-10,"amount bounds");Check(totals.Total(SixAttribute.Charisma)==0,"invalid enum ignored");Check(before==EditorJsonUtility.ToJson(temp),"item snapshot immutable");
 var assets=new UnityEngine.Object[]{w,a,b,c};string[] snapshots=new string[4];bool[] dirty=new bool[4];for(int i=0;i<4;i++){snapshots[i]=EditorJsonUtility.ToJson(assets[i]);dirty[i]=EditorUtility.IsDirty(assets[i]);}
 typeof(EmberdelveAssetLibrary).GetMethod("ApplyAttributeDefinitions",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
 for(int i=0;i<4;i++)Check(snapshots[i]==EditorJsonUtility.ToJson(assets[i])&&dirty[i]==EditorUtility.IsDirty(assets[i]),"builder idempotent "+i);
 }catch(Exception e){fail++;Debug.LogException(e);}finally{if(temp!=null)UnityEngine.Object.DestroyImmediate(temp);if(custom!=null)UnityEngine.Object.DestroyImmediate(custom);}
 Debug.Log("[Emberdelve][AttributeData] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);
 }
}}
