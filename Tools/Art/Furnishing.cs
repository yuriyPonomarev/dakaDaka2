using System;
using System.Linq;
using UnityEditor;
using UnityEngine;
public static class Furnishing
{
 public static string Sources()
 {
  var result=new System.Text.StringBuilder();
  var assembly=typeof(Emberdelve.EditorTools.DungeonLayoutFamilies).Assembly;
  var type=assembly.GetType("Emberdelve.EditorTools.EmberdelvePolyHavenImport");
  foreach(string id in new[]{"WoodenTable_01","wooden_stool_01","wooden_bookshelf_worn","GothicCabinet_01","worn_metal_rack","wooden_crate_02","wooden_bucket_01","wine_barrel_01","wooden_candlestick","brass_candleholders"})
  {
   var source=(GameObject)type.GetMethod("LoadModel").Invoke(null,new object[]{id});
   if(source==null){result.AppendLine("MISSING "+id);continue;}
   var go=UnityEngine.Object.Instantiate(source);
   try {var rs=go.GetComponentsInChildren<Renderer>(); var b=rs[0].bounds;foreach(var r in rs)b.Encapsulate(r.bounds);result.AppendLine(id+" size="+b.size.ToString("F3"));}
   finally {UnityEngine.Object.DestroyImmediate(go);}
  }
 return result.ToString();
 }


 public static void Apply()=>Emberdelve.EditorTools.DungeonLayoutFurnishing.ApplySaved();
 public static void Validate()=>Emberdelve.EditorTools.DungeonLayoutFurnishing.ValidateSaved();
 public static void Doors()=>typeof(Emberdelve.EditorTools.DungeonLayoutFamilies).Assembly.GetType("Emberdelve.EditorTools.DungeonDoorAuthoring").GetMethod("RepairSavedVisuals").Invoke(null,null);
 public static string DoorUv()
 {
  const string path="Assets/Emberdelve/Art/Generated/ArchitecturalJoins/DoorCore_Rebated65mm_v1.asset";
  var mesh=AssetDatabase.LoadAssetAtPath<Mesh>(path);
  string Geometry()=>string.Join("|",mesh.vertices.Select(v=>v.ToString("R")))+string.Join("|",mesh.normals.Select(v=>v.ToString("R")))+string.Join(",",mesh.triangles)+mesh.bounds.ToString("R");
  string before=Geometry();var uv=mesh.uv;
  Doors();
  bool same=before==Geometry(),changed=!uv.SequenceEqual(mesh.uv);
  if(!same)throw new Exception("UV repair changed door geometry");
  string result="Door core vertices, normals, triangles, bounds identical; UV changed="+changed;
  System.IO.File.WriteAllText("Logs/acceptance/layout-furnishing-20261122/door-uv-equivalence.txt",result);
  return result;
 }
}
