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
using Emberdelve.Combat;
namespace Emberdelve.EditorTools {
[InitializeOnLoad] public static class EmberdelveArmorAffixGate {
const string Pending="Emberdelve.ArmorAffix.Pending";
static EmberdelveArmorAffixGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("ArmorAffix_Gate").AddComponent<ArmorAffixHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class ArmorAffixHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[ArmorAffix] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][ArmorAffix] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EditorApplication.Exit(fail==0?0:1);}
sealed class Actor:ICombatEntity,IArmorRatingProvider,IActiveWeaponProvider {public WeaponData Weapon;public WeaponData ActiveWeapon=>Weapon;public int CombatId{get;set;}public Vector3 Point;public EntityKind Kind=>EntityKind.Enemy;public int TeamId=>1;public Vector3 CombatCenter=>Point;public Collider HeadCollider=>null;public bool IsDead=>false;public float CurrentHealth=>1000;public float MaxHealth=>1000;public void ApplyDamage(DamageInfo d){}public float GetArmorRating()=>10000;}
Actor actor;LocalCombatAuthority authority;float clock=100;DamageInfo captured;
void Hit(PlayerRig rig,WeaponData weapon,bool guard,bool behind,float expected,string label){rig.Health.Configure(120,0,0);rig.transform.rotation=Quaternion.identity;rig.SetGuardHeld(guard);actor.Point=rig.CombatCenter+(behind?Vector3.back:Vector3.forward)*1.5f;clock+=5;var intent=new AttackIntent(actor.CombatId,actor.Kind,weapon,1,actor.Point,rig.CombatCenter-actor.Point,3,CombatRegistry.NextSequence(actor.CombatId),clock);Action<DamageInfo> receive=d=>captured=d;rig.Health.Damaged+=receive;bool ok;try{ok=authority.SubmitAttack(intent,new HitCandidate(rig.CombatId,rig.CombatCenter,false));}finally{rig.Health.Damaged-=receive;}Check(ok,label+" accepted");Check(Mathf.Abs(120-rig.CurrentHealth-expected)<.001f,label+" real health delta");Check(Mathf.Abs(captured.Amount-expected)<.001f,label+" DamageInfo amount");Check(captured.Blocked==(guard&&!behind),label+" Blocked flag only guard");}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();Check(rig!=null&&!rig.IsDead,"real living rig");if(rig==null)return;var bag=rig.Inventory;var gear=rig.Equipment;gear.Clear();bag.Clear();bag.ConfigureGrid(10,4);
var cfg=Make<GameConfig>();cfg.levelMask=0;var go=new GameObject("Armor_Authority");cleanup.Add(go);authority=go.AddComponent<LocalCombatAuthority>();authority.SetClock(()=>clock);typeof(LocalCombatAuthority).GetField("config",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(authority,cfg);
actor=new Actor{CombatId=CombatRegistry.AllocateId()};CombatRegistry.Register(actor.CombatId,actor);
var weapon=Make<WeaponData>();weapon.weaponId="armor_gate_claw";actor.Weapon=weapon;weapon.baseDamage=15;weapon.damageType=DamageType.Physical;
Check(rig.GetArmorRating()==0,"baseline zero armor");Hit(rig,weapon,false,false,15,"zero defender armor despite armored attacker");
var boots=Make<ItemData>();boots.itemId="armor_gate_boots";boots.kind=ItemKind.Armor;boots.equipSlot=EquipmentSlot.Legs;boots.statModifiers=new[]{new StatModifier(SkillStat.Armor,SkillModifierMode.Flat,4)};
var affix=Make<AffixDefinition>();affix.id="armor_bonus";affix.statKey=AffixStatKey.Armor;affix.minValue=1;affix.maxValue=6;affix.step=1;affix.allowedCategories=AffixCategory.Boots;
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots},new[]{affix},out auth,out reason),"armor catalog");
// R3 fixture: the ad-hoc boots and affix are not production catalog entries, so the bag carries an explicit TEST catalog listing exactly the definitions this gate's authority was built from.
var testCatalog=ItemCatalog.CreateForTest(new[]{boots},new[]{affix});cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);ItemInstance gearItem;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("armor_bonus",AffixStatKey.Armor,6)},auth,out gearItem,out reason),"armor gear");
string snapshot=gearItem.ToSnapshot().ToJson();Check(bag.TryAddInstance(gearItem,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(gearItem.Uid),out reason),"equip fixed4 rolled6");Check(rig.GetArmorRating()==10,"fixed plus rolled sum");gear.RefreshStats();gear.RefreshStats();Check(rig.GetArmorRating()==10,"armor does not accumulate");
Hit(rig,weapon,false,false,15f*100/110,"physical armor body");Hit(rig,weapon,true,false,15f*.18f*100/110,"guard then armor");Check(Mathf.Abs(captured.BlockReduction-.82f)<.001f,"guard reduction metadata unchanged");Hit(rig,weapon,true,true,15f*100/110,"rear armor without guard");Check(captured.BlockReduction==0,"rear has no guard reduction");Check(!rig.Defense.CanParry&&rig.Defense.HasShield,"shield still never parries");
weapon.damageType=DamageType.Holy;Hit(rig,weapon,false,false,15,"Holy unaffected");weapon.damageType=DamageType.Physical;
var raw=new DamageInfo(15,Vector3.one,Vector3.forward,Vector3.zero,actor.CombatId,EntityKind.Enemy,DamageType.Physical,false,true,0);
Check(Mathf.Abs(DamageResolver.ApplyArmorRating(raw,6).Amount-14.1509434f)<.0001f,"rating6 reference");Check(DamageResolver.ApplyArmorRating(raw,100).Amount==7.5f,"rating100 halves");Check(DamageResolver.ApplyArmorRating(raw,float.MaxValue).Amount==1,"huge finite stable minimum");
foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1f,0f}){var d=DamageResolver.ApplyArmorRating(raw,bad);Check(d.Amount==raw.Amount&&d.Headshot==raw.Headshot&&d.Blocked==raw.Blocked&&d.SourceCombatId==raw.SourceCombatId&&d.Type==raw.Type,"sanitization zero exact metadata");}
rig.SetGuardHeld(false);Check(gear.TryUnequip(EquipmentSlot.Legs,out reason)&&rig.GetArmorRating()==0,"unequip zero exact");Check(snapshot==gearItem.ToSnapshot().ToJson(),"UID rolls unchanged");Hit(rig,weapon,false,false,15,"old physical damage restored");CombatRegistry.Unregister(actor.CombatId,actor);gear.Clear();bag.Clear();

}
}}
