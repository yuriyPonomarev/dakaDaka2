using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Core;
using Emberdelve.Level;
using Emberdelve.Player;
using Emberdelve.Combat;
using Emberdelve.Items;
using Emberdelve.Skills;
namespace Emberdelve.EditorTools {
public static class EmberdelveBuilderOwnershipGate {
static int checks, failures;
static void Check(bool ok,string text){checks++; if(!ok){failures++;Debug.LogError("[BuilderOwnership] mismatch "+text);}}
static void Owned<T>(PlayerRig rig,string field,T value) where T:Component {
Check(rig.GetComponents<T>().Length==1,typeof(T).Name+" unique");
Check(value!=null && value==rig.GetComponent<T>(),typeof(T).Name+" rig identity");
Check(new SerializedObject(rig).FindProperty(field).objectReferenceValue==value,field+" serialized identity");
}
public static void Run(){
checks=failures=0; DungeonLayoutData layout=null; GameConfig config=null; PlayerRig rig=null;
try {
EditorSceneManager.NewScene(NewSceneSetup.EmptyScene,NewSceneMode.Single);
layout=ScriptableObject.CreateInstance<DungeonLayoutData>(); config=ScriptableObject.CreateInstance<GameConfig>();
rig=(PlayerRig)typeof(EmberdelveLevelBuilder).GetMethod("BuildPlayer",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{layout,config});
Owned(rig,"mana",rig.Mana); Owned(rig,"status",rig.Status); Owned(rig,"skills",rig.Skills); Owned(rig,"equipment",rig.Equipment);
Check(rig.Mana==ManaComponent.Ensure(rig.gameObject),"mana Ensure preserves identity");
bool staminaFree=typeof(PlayerRig).GetFields(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).All(f=>f.Name.IndexOf("stamina",StringComparison.OrdinalIgnoreCase)<0)&&typeof(PlayerRig).GetProperties(BindingFlags.Instance|BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic).All(p=>p.Name.IndexOf("stamina",StringComparison.OrdinalIgnoreCase)<0)&&rig.GetComponents<Component>().All(c=>c==null||c.GetType().Name.IndexOf("Stamina",StringComparison.OrdinalIgnoreCase)<0);
Check(staminaFree,"D1b/B3: the built rig has no stamina component and PlayerRig has no stamina member");
Check(rig.Status==StatusComponent.Ensure(rig.gameObject),"status Ensure preserves identity");
Check(rig.Equipment==Equipment.Ensure(rig.gameObject),"equipment Ensure preserves identity");
Check(rig.GetComponent<Camera>()==null && rig.GetComponentInChildren<Camera>()!=null,"camera child retained");
} catch(Exception e){failures++; Debug.LogException(e);} finally {
if(rig!=null)UnityEngine.Object.DestroyImmediate(rig.gameObject);
if(layout!=null)UnityEngine.Object.DestroyImmediate(layout);if(config!=null)UnityEngine.Object.DestroyImmediate(config);
}
Debug.Log("[Emberdelve][BuilderOwnership] "+(failures==0?"PASS":"FAIL")+" checks="+checks+" failures="+failures);EmberdelveGateExit.Exit(failures==0?0:1);
}
}}
