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
using Emberdelve.AI;
namespace Emberdelve.EditorTools {
[InitializeOnLoad] public static class EmberdelveFlatDamageAffixGate {
const string Pending="Emberdelve.FlatDamageAffix.Pending";
static EmberdelveFlatDamageAffixGate(){EditorApplication.playModeStateChanged+=state=>{if(state==PlayModeStateChange.EnteredPlayMode&&SessionState.GetBool(Pending,false)){SessionState.SetBool(Pending,false);new GameObject("FlatDamageAffix_Gate").AddComponent<FlatDamageAffixHarness>();}};}
public static void Run(){SessionState.SetBool(Pending,true);EditorSceneManager.OpenScene("Assets/Emberdelve/Scenes/Emberdelve_Level01.unity");EditorApplication.EnterPlaymode();}
}
public sealed class FlatDamageAffixHarness:MonoBehaviour {
int n,fail;readonly List<UnityEngine.Object> cleanup=new List<UnityEngine.Object>();readonly List<GameObject> suspendedSceneEnemies=new List<GameObject>();readonly List<bool> suspendedSceneEnemyActive=new List<bool>();
void Check(bool ok,string text){n++;if(!ok){fail++;Debug.LogError("[FlatDamageAffix] mismatch "+text);}}
T Make<T>() where T:ScriptableObject {var x=ScriptableObject.CreateInstance<T>();cleanup.Add(x);return x;}
IEnumerator Start(){yield return null;yield return null;try{Execute();}catch(Exception e){fail++;Debug.LogException(e);}finally{RestoreSceneEnemies();foreach(var x in cleanup)if(x!=null)Destroy(x);}
Debug.Log("[Emberdelve][FlatDamageAffix] "+(fail==0?"PASS":"FAIL")+" checks="+n+" failures="+fail);EmberdelveGateExit.Exit(fail==0?0:1);}
sealed class Target:ICombatEntity {public HealthComponent Health;public Vector3 Point;public DamageInfo Last;public int CombatId{get;set;}public EntityKind Kind=>EntityKind.Enemy;public int TeamId=>1;public Vector3 CombatCenter=>Point;public Collider Head;public Collider HeadCollider=>Head;public bool IsDead=>Health.IsDead;public float CurrentHealth=>Health.CurrentHealth;public float MaxHealth=>Health.MaxHealth;public void ApplyDamage(DamageInfo d){Last=d;Health.ApplyDamage(d);}}
float clock=100;Target target;LocalCombatAuthority authority;
void Hit(PlayerRig rig,WeaponData weapon,float distance,float charge,bool head,float expected,string label){
 target.Health.Configure(1000,0,0);
 target.Point=weapon.IsRanged?rig.GetMuzzlePosition()+rig.AimDirection.normalized*distance:rig.CombatCenter+Vector3.forward*distance;
 target.Head.transform.position=target.Point;target.Head.enabled=head;Physics.SyncTransforms();clock+=5;
 Vector3 launchOrigin=weapon.IsRanged?rig.GetMuzzlePosition():rig.CombatCenter;Vector3 launchAim=weapon.IsRanged?rig.AimDirection:Vector3.forward;
 var intent=new AttackIntent(rig.CombatId,rig.Kind,weapon,charge,launchOrigin,launchAim,distance+1,CombatRegistry.NextSequence(rig.CombatId),clock);
 float expectedContact=distance;
 if(weapon.IsRanged){float collisionRadius=LocalCombatAuthority.TargetBodyRadius+Mathf.Max(.01f,weapon.projectileRadius*.5f);expectedContact=Mathf.Max(0f,distance-collisionRadius);float boosted=weapon.baseDamage+rig.GetFlatWeaponDamageBonus(weapon);float t=weapon.falloffEndRange>weapon.falloffStartRange?Mathf.Clamp01((expectedContact-weapon.falloffStartRange)/(weapon.falloffEndRange-weapon.falloffStartRange)):0f;float falloff=expectedContact<=weapon.falloffStartRange?boosted:Mathf.Lerp(boosted,boosted*weapon.falloffMinScale,t);float independent=falloff*Mathf.Lerp(weapon.minChargeDamageScale,1f,charge)*rig.GetOutgoingDamageMultiplier(weapon);Check(Mathf.Abs(independent-expected)<.001f,label+" literal equals independently calculated muzzle/contact falloff");}
 if(weapon.IsRanged&&label.Contains("31.475m"))Check(!HasActiveRegisteredBodyOnLane(rig,weapon,launchOrigin,launchAim,distance),label+" no active registered nonfixture body intersects numerical lane");
 bool ok;
 if(weapon.IsRanged){ok=authority.LaunchProjectileAsOwner(intent);Check(ok,label+" real projectile launch accepted");if(ok)for(int frame=0;frame<300&&target.CurrentHealth>=1000f;frame++)authority.AdvanceProjectiles(1f/60f);}else{ok=authority.SubmitAttackAsOwner(intent,new HitCandidate(target.CombatId,target.Point,head));Check(ok,label+" accepted");}
 float actualHealthDelta=1000f-target.CurrentHealth;string actuals=$"actualHPDelta={actualHealthDelta:R} expectedHPDelta={expected:R} actualDamageAmount={target.Last.Amount:R} actualDamagePoint={target.Last.Point} actualHeadshot={target.Last.Headshot}";
 Check(Mathf.Abs(actualHealthDelta-expected)<.001f,label+" real health delta "+actuals);Check(Mathf.Abs(target.Last.Amount-expected)<.001f,label+" DamageInfo amount "+actuals);
}
bool HasActiveRegisteredBodyOnLane(PlayerRig shooter,WeaponData weapon,Vector3 origin,Vector3 aim,float range){Vector3 direction=aim.normalized;float radius=LocalCombatAuthority.TargetBodyRadius+Mathf.Max(.01f,weapon.projectileRadius*.5f);foreach(ICombatEntity entity in CombatRegistry.All){if(entity==null||object.ReferenceEquals(entity,shooter)||object.ReferenceEquals(entity,target)||entity.IsDead)continue;Component component=entity as Component;if(component!=null&&!component.gameObject.activeInHierarchy)continue;Vector3 offset=entity.CombatCenter-origin;float along=Vector3.Dot(offset,direction);float lateral=(offset-direction*along).magnitude;if(along>=0f&&along<=range+radius&&lateral<=radius)return true;}return false;}
void SuspendRegisteredSceneEnemies(){var registered=new List<ICombatEntity>(CombatRegistry.All);for(int i=0;i<registered.Count;i++){EnemyAgent enemy=registered[i] as EnemyAgent;if(enemy==null)continue;GameObject enemyObject=enemy.gameObject;if(enemyObject==null||!enemyObject.activeSelf||suspendedSceneEnemies.Contains(enemyObject))continue;suspendedSceneEnemies.Add(enemyObject);suspendedSceneEnemyActive.Add(enemyObject.activeSelf);enemyObject.SetActive(false);}}
void RestoreSceneEnemies(){for(int i=0;i<suspendedSceneEnemies.Count;i++){GameObject enemy=suspendedSceneEnemies[i];if(enemy!=null)enemy.SetActive(suspendedSceneEnemyActive[i]);}}
void Execute(){
var rig=FindFirstObjectByType<PlayerRig>();Check(rig!=null&&!rig.IsDead,"living rig");if(rig==null)return;SuspendRegisteredSceneEnemies();var bag=rig.Inventory;var gear=rig.Equipment;gear.Clear();bag.Clear();bag.ConfigureGrid(10,4);
var cfg=Make<GameConfig>();cfg.levelMask=0;var authorityGo=new GameObject("FlatBonus_Authority");cleanup.Add(authorityGo);authority=authorityGo.AddComponent<LocalCombatAuthority>();authority.SetClock(()=>clock);authority.SimulateBallistics=false;authority.AllowLegacyMeleeAttacks=true;typeof(LocalCombatAuthority).GetField("config",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).SetValue(authority,cfg);
var targetGo=new GameObject("FlatBonus_Target");cleanup.Add(targetGo);var headGo=new GameObject("FlatBonus_Head");cleanup.Add(headGo);var headCol=headGo.AddComponent<SphereCollider>();headCol.radius=.3f;target=new Target{Health=targetGo.AddComponent<HealthComponent>(),Head=headCol,CombatId=CombatRegistry.AllocateId()};CombatRegistry.Register(target.CombatId,target);
var boots=AssetDatabase.LoadAssetAtPath<ItemData>("Assets/Emberdelve/Data/Item_FootpadBoots.asset");var melee=Make<AffixDefinition>();melee.id="flat_melee";melee.statKey=AffixStatKey.MeleeDamage;melee.minValue=1;melee.maxValue=4;melee.step=1;melee.allowedCategories=AffixCategory.Boots;
var ranged=Make<AffixDefinition>();ranged.id="flat_ranged";ranged.statKey=AffixStatKey.ProjectileDamage;ranged.minValue=1;ranged.maxValue=4;ranged.step=1;ranged.allowedCategories=AffixCategory.Boots;
ItemInstanceAuthority auth;string reason;Check(ItemInstanceAuthority.TryCreate(new[]{boots},new[]{melee,ranged},out auth,out reason),"affix catalog");
// R3 fixture: the gate rolls with its own ad-hoc affix definitions, which the production catalog does not own and the bag now validates every instance against, so the bag carries an explicit TEST catalog (real boots + exactly those affixes).
var testCatalog=ItemCatalog.CreateForTest(new[]{boots},new[]{melee,ranged});cleanup.Add(testCatalog);bag.SetCatalog(testCatalog);
ItemInstance a,b;Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("flat_melee",AffixStatKey.MeleeDamage,3)},auth,out a,out reason),"melee gear");Check(ItemInstance.TryCreateNew(boots,1,AffixRarity.Uncommon,new[]{new AffixRecord("flat_ranged",AffixStatKey.ProjectileDamage,3)},auth,out b,out reason),"ranged gear");
var sword=rig.MainHandWeapon;string before=EditorJsonUtility.ToJson(sword);Hit(rig,sword,1.5f,1,false,34.944f,"zero bonus sword");
Check(bag.TryAddInstance(a,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(a.Uid),out reason),"equip melee3");Check(rig.GetFlatWeaponDamageBonus(sword)==3,"live melee provider");Hit(rig,sword,1.5f,1,false,38.976f,"boosted sword");Hit(rig,sword,1.5f,1,true,70.1568f,"boosted headshot");gear.RefreshStats();gear.RefreshStats();Check(rig.GetFlatWeaponDamageBonus(sword)==3,"no flat accumulation");
var cry=Make<SkillData>();cry.flatDamage=14;cry.skillId="gate_flat";var flatIntent=new SkillIntent(rig.CombatId,rig.Kind,cry,sword,rig.CombatCenter,Vector3.forward,3,1,100,default,1,1);
var hit=new HitCandidate(target.CombatId,rig.CombatCenter+Vector3.forward,false);var flatInfo=DamageResolver.ResolveSkill(flatIntent,hit,target,rig.GetOutgoingDamageMultiplier(sword),1,rig.GetFlatWeaponDamageBonus(sword));Check(Mathf.Abs(flatInfo.Amount-18.816f)<.0001f,"flat skill ignores weapon flat");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason),"remove melee gear");Hit(rig,sword,1.5f,1,false,34.944f,"unequip restores sword");
var archer=AssetDatabase.LoadAssetAtPath<CharacterClassData>("Assets/Emberdelve/Data/Class_Archer.asset");rig.ApplyClass(archer,cfg);gear.Clear();bag.Clear();var bow=rig.MainHandWeapon;
Check(bag.TryAddInstance(b,out reason)&&gear.TryEquipFromInventory(bag.IndexOfInstance(b.Uid),out reason),"equip projectile3");Check(rig.GetFlatWeaponDamageBonus(bow)==3&&rig.GetFlatWeaponDamageBonus(sword)==0,"channels distinct");
Hit(rig,bow,15,1,false,48.8992f,"boosted full bow");Hit(rig,bow,15,.5f,false,34.22944f,"charge scales full boosted base");Hit(rig,bow,32,1,false,35.95619f,"independent 31.475m swept-contact falloff scales full boosted base");
var piercing=Make<SkillData>();piercing.skillId="gate_pierce";piercing.flatDamage=-1;piercing.damageMultiplier=1.35f;
var skillIntent=new SkillIntent(rig.CombatId,rig.Kind,piercing,bow,rig.CombatCenter,Vector3.forward,30,2,110,default,2,.96f);hit=new HitCandidate(target.CombatId,rig.CombatCenter+Vector3.forward*15,false);
var skillInfo=DamageResolver.ResolveSkill(skillIntent,hit,target,rig.GetOutgoingDamageMultiplier(bow),.96f,rig.GetFlatWeaponDamageBonus(bow));Check(Mathf.Abs(skillInfo.Amount-63.3733632f)<.0001f,"weapon skill boostedbase skill CHA once");
foreach(float bad in new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,-1f})Check(bow.DamageAtRange(15,bad)==bow.DamageAtRange(15),"bad bonus sanitized");
Check(gear.TryUnequip(EquipmentSlot.Legs,out reason),"remove ranged gear");Hit(rig,bow,15,1,false,44.9344f,"unboosted archer restored");Check(before==EditorJsonUtility.ToJson(sword),"weapon definition unchanged");CombatRegistry.Unregister(target.CombatId,target);gear.Clear();bag.Clear();

}
}}
