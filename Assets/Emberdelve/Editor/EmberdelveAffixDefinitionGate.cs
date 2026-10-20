using System;
using UnityEditor;
using UnityEngine;
using Emberdelve.Data;
namespace Emberdelve.EditorTools {
public static class EmberdelveAffixDefinitionGate {
static int checks, failures;
static void Check(bool ok,string text){checks++;if(!ok){failures++;Debug.LogError("[AffixDefinition] mismatch "+text);}}
public static void Run(){
checks=failures=0;AffixDefinition a=null,b=null;
try {
float[] low={1,1,1,1,1,1,1,5,5,.03f,1,1,.1f},high={3,3,3,3,3,3,6,15,15,.12f,4,4,.4f},steps={1,1,1,1,1,1,1,1,1,.01f,1,1,.1f};
a=ScriptableObject.CreateInstance<AffixDefinition>();a.id="test_affix";a.allowedCategories=AffixCategory.Boots;
string reason;
for(int i=0;i<13;i++){
a.statKey=(AffixStatKey)i;a.minValue=low[i];a.maxValue=high[i];a.step=steps[i];
string before=EditorJsonUtility.ToJson(a);
Check(a.TryValidate(out reason),"valid definition "+i+" "+reason);
Check(a.TryValidateRolledValue(low[i],AffixCategory.Boots,out reason),"low endpoint "+i);
Check(a.TryValidateRolledValue(high[i],out reason),"high endpoint "+i);
Check(!a.TryValidateRolledValue(low[i]+steps[i]*.5f,out reason),"offstep rejected "+i);
Check(!a.TryValidateRolledValue(float.NaN,out reason),"NaN roll "+i);
Check(!a.TryValidateRolledValue(float.PositiveInfinity,out reason),"infinite roll "+i);
Check(!a.TryValidateRolledValue(high[i]+steps[i],out reason),"overflow "+i);
Check(!a.TryValidateRolledValue(low[i],AffixCategory.Cloak,out reason),"category rejected "+i);
Check(!a.TryValidateRolledValue(low[i],AffixCategory.Boots|AffixCategory.Cloak,out reason),"multicategory claim "+i);
Check(before==EditorJsonUtility.ToJson(a),"validation immutable "+i);
}
a.statKey=AffixStatKey.Strength;a.minValue=1;a.maxValue=3;a.step=1;
a.statKey=(AffixStatKey)99;Check(!a.TryValidate(out reason),"unknown enum");a.statKey=AffixStatKey.Strength;
foreach(string id in new[]{""," ","Upper","1bad","bad-key"}){a.id=id;Check(!a.TryValidate(out reason),"invalid id "+id);}a.id="test_affix";
a.displayName=" ";Check(!a.TryValidate(out reason),"blank label");a.displayName="Test";
foreach(float weight in new[]{0f,-1f,float.NaN,float.PositiveInfinity}){a.weight=weight;Check(!a.TryValidate(out reason),"bad weight");}a.weight=1;
foreach(AffixCategory category in new[]{AffixCategory.None,(AffixCategory)16,(AffixCategory)17}){a.allowedCategories=category;Check(!a.TryValidate(out reason),"bad flags");}a.allowedCategories=AffixCategory.Boots;
foreach(float step in new[]{0f,-1f,.5f,float.NaN,float.PositiveInfinity}){a.step=step;Check(!a.TryValidate(out reason),"bad attribute step");}a.step=1;
a.minValue=3;a.maxValue=1;Check(!a.TryValidate(out reason),"reversed");a.minValue=1;a.maxValue=4;Check(!a.TryValidate(out reason),"band outside");a.maxValue=3;
a.step=3;Check(!a.TryValidate(out reason),"unaligned top");a.step=1;
b=ScriptableObject.CreateInstance<AffixDefinition>();b.id="second";b.allowedCategories=AffixCategory.Boots;
Check(!AffixCatalog.TryValidate(new[]{a,b},out reason),"duplicate stat key");b.statKey=AffixStatKey.MaxHealth;b.minValue=5;b.maxValue=15;b.step=1;
Check(AffixCatalog.TryValidate(new[]{a,b},out reason),"distinct catalog");b.id=a.id;Check(!AffixCatalog.TryValidate(new[]{a,b},out reason),"duplicate id");
Check(!AffixCatalog.TryValidate(new AffixDefinition[]{a,null},out reason),"null catalog entry");
for(int i=0;i<3;i++){int count;Check(AffixRarities.TryExpectedAffixCount(i,out count)&&count==i,"rarity count "+i);}
foreach(int i in new[]{-1,3,99}){int count;Check(!AffixRarities.TryExpectedAffixCount(i,out count)&&count==-1,"unknown rarity");}
Check(!AffixBandRules.TryValidateValue(AffixStatKey.MoveSpeed,.03,.12,.0000001,.08000005,out reason),"fine-step half-step rejected at large grid index");
Check(AffixBandRules.TryValidateValue(AffixStatKey.MoveSpeed,.03,.12,.0000001,.08,out reason),"fine-grid valid point");
Check(!AffixBandRules.TryValidateBand(AffixStatKey.MoveSpeed,.03,.12000005,.0000001,out reason),"fine-grid half-step band endpoint rejected");
Check(!AffixBandRules.TryValidateBand(AffixStatKey.MoveSpeed,.03f,.12f,.00001f,out reason),"unresolvable float grid rejected");
Check(!AffixBandRules.IsIntegral(1000000000.5),"large fractional double not integral");
Check(AffixBandRules.TryValidateValue(AffixStatKey.MoveSpeed,.03f,.12f,.01f,.08f,out reason),"float-authored legal grid preserved");
Check(!AffixBandRules.TryValidateValue(AffixStatKey.MoveSpeed,.03,.12,.0000001,.080000025,out reason),"fine-grid quarter-step rejected");
} catch(Exception e){failures++;Debug.LogException(e);} finally{if(a!=null)UnityEngine.Object.DestroyImmediate(a);if(b!=null)UnityEngine.Object.DestroyImmediate(b);}
Debug.Log("[Emberdelve][AffixDefinition] "+(failures==0?"PASS":"FAIL")+" checks="+checks+" failures="+failures);EditorApplication.Exit(failures==0?0:1);
}
}}
