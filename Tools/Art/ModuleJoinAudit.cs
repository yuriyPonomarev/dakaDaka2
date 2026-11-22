using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Emberdelve.Level;
public static class ModuleJoinAudit {
 public static void Run() {
  string dir="Logs/acceptance/module-joins-20261122"; Directory.CreateDirectory(dir);
  foreach(var name in new[]{"Art/Models/AI/Meshy/CommunityRuins/OvergrownArch/OvergrownArch.asset","Art/Models/AI/Meshy/DownloadedArchitecture/OvergrownArchCrown.asset","Art/Models/AI/Meshy/DownloadedArchitecture/OvergrownArchStonePier.asset","Art/Generated/CryptArch.asset"}) {
   var m=AssetDatabase.LoadAssetAtPath<Mesh>("Assets/Emberdelve/"+name); if(m==null)continue;
   Debug.Log("[JoinAudit] "+name+" bounds="+m.bounds+" triangles="+m.triangles.Length/3);
   using(var w=new StreamWriter(dir+"/"+Path.GetFileNameWithoutExtension(name)+".obj")) {
    foreach(var v in m.vertices) w.WriteLine(FormattableString.Invariant($"v {v.x} {v.y} {v.z}"));
    var t=m.triangles; for(int i=0;i<t.Length;i+=3)w.WriteLine($"f {t[i]+1} {t[i+1]+1} {t[i+2]+1}");
   }
  }
  foreach(var room in UnityEngine.Object.FindObjectsByType<DungeonModuleDefinition>(FindObjectsSortMode.None)) {
   Debug.Log("[JoinAudit] liveRoom="+room.name+" position="+room.transform.position);
  }
 }
}
