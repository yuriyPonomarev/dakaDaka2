using Emberdelve.Core;
using Emberdelve.Level;
using UnityEngine;

namespace Emberdelve.EditorTools
{
    // Editor-authored wall landmarks. Local X spans the wall; local Z is its shallow depth.
    internal static class RuinsRoomLandmarks
    {
        internal static void Build(Transform root, DungeonModuleKind kind, int variant, Material stone, Material floor)
        {
            string identity = kind == DungeonModuleKind.Hall
                ? new[] { "Monastery_Stores", "Broken_Cloister", "Fallen_Effigy" }[variant % 3]
                : kind == DungeonModuleKind.Ossuary ? (variant % 2 == 0 ? "Burial_Galleries" : "Wheel_Of_Remembrance")
                : kind + "_Landmark";
            Transform first = Group(root, identity, new Vector3(-10.8f,0,6),90);
            Transform second = Group(root, identity + "_Echo", new Vector3(10.8f,0,-6),-90);
            switch (kind)
            {
                case DungeonModuleKind.Entry:
                    Watchtower(first,stone,floor);
                    Arc(second,new Vector3(0,2.2f,0),1.7f,0,180,stone);
                    Pillar(second,-1.8f,2.2f,stone); Pillar(second,1.8f,1.5f,stone);
                    break;
                case DungeonModuleKind.Hall:
                    if (variant % 3 == 0) { Stores(first,stone,floor); Stores(second,stone,floor); }
                    else if (variant % 3 == 1) { Cloister(first,stone); Cloister(second,stone); }
                    else { Effigy(first,stone,floor); Arc(second,new Vector3(0,1.1f,0),1.6f,15,145,stone); }
                    break;
                case DungeonModuleKind.Ossuary:
                    if (variant % 2 == 0) { Niches(first,stone,floor); Niches(second,stone,floor); }
                    else
                    {
                        Arc(first,new Vector3(0,2.5f,0),1.7f,0,340,stone);
                        for (int i=0;i<6;i++) Spoke(first,2.5f,i*60,.95f,floor);
                        Box(first,"Memorial_Stem",new Vector3(0,.8f,0),new Vector3(.6f,1.6f,.6f),stone);
                        Niches(second,stone,floor);
                    }
                    break;
                case DungeonModuleKind.Shrine:
                    Arc(first,new Vector3(0,3.6f,0),1.9f,-25,245,stone);
                    Arc(first,new Vector3(0,3.6f,0),.75f,0,360,floor);
                    for(int i=0;i<6;i++) Spoke(first,3.6f,i*60,1.3f,stone);
                    Pillar(first,-2.1f,3.4f,stone); Pillar(first,2.1f,2.2f,stone);
                    Arc(second,new Vector3(0,2.3f,0),1.7f,0,150,stone);
                    break;
                case DungeonModuleKind.Vault:
                    Treasury(first,stone,floor); Treasury(second,stone,floor);
                    break;
                case DungeonModuleKind.Extraction:
                    Waymark(first,stone,floor);
                    Arc(second,new Vector3(0,2.6f,0),1.6f,0,180,stone);
                    Pillar(second,-1.7f,2.6f,stone); Pillar(second,1.7f,2.6f,stone);
                    break;
                case DungeonModuleKind.Bridge:
                    Pier(first,stone,floor); Pier(second,stone,floor);
                    break;
            }
        }

        private static void Watchtower(Transform t,Material stone,Material floor)
        {
            Pillar(t,-1.5f,5.7f,stone); Pillar(t,1.5f,4.5f,stone);
            Arc(t,new Vector3(0,3.7f,0),1.3f,0,180,stone);
            Box(t,"Watch_Slit_Left",new Vector3(-.45f,5.4f,0),new Vector3(.35f,1.2f,.6f),floor);
            Box(t,"Watch_Slit_Right",new Vector3(.45f,5.2f,0),new Vector3(.35f,.8f,.6f),floor);
            Arc(t,new Vector3(0,5.9f,0),.65f,0,150,stone);
        }

        private static void Stores(Transform t,Material stone,Material floor)
        {
            for(int i=-1;i<=1;i++)
            {
                float x=i*1.4f;
                Arc(t,new Vector3(x,1.7f,0),.52f,0,180,stone);
                Box(t,"Store_Recess_Sill",new Vector3(x,.6f,0),new Vector3(1.1f,.18f,.65f),floor);
                for(int side=-1;side<=1;side+=2)
                    Box(t,"Store_Recess_Jamb",new Vector3(x+side*.58f,1.2f,0),new Vector3(.2f,1.2f,.6f),stone);
                Cylinder(t,"Storage_Amphora",new Vector3(x,1.02f,.02f),new Vector3(.45f,.32f,.45f),floor);
                Cylinder(t,"Amphora_Neck",new Vector3(x,1.39f,.02f),new Vector3(.23f,.12f,.23f),floor);
            }
        }

        private static void Cloister(Transform t,Material stone)
        {
            for(int i=-1;i<=1;i++) Pillar(t,i*1.8f,i==0?3.6f:2.7f,stone);
            Arc(t,new Vector3(-.9f,2.7f,0),.75f,0,160,stone);
            Arc(t,new Vector3(.9f,2.7f,0),.75f,20,180,stone);
        }

        private static void Effigy(Transform t,Material stone,Material floor)
        {
            Box(t,"Empty_Effigy_Plinth",new Vector3(0,.35f,0),new Vector3(3.5f,.7f,.7f),floor);
            var body=Cylinder(t,"Fallen_Sculpture_Torso",new Vector3(-.5f,1.05f,0),new Vector3(.65f,.85f,.65f),stone);
            body.localRotation=Quaternion.Euler(0,0,76);
            Sphere(t,"Weathered_Effigy_Head",new Vector3(1.05f,.95f,0),new Vector3(.62f,.72f,.6f),stone);
            Arc(t,new Vector3(0,3.3f,0),1.6f,20,130,stone);
        }

        private static void Niches(Transform t,Material stone,Material floor)
        {
            for(int row=0;row<3;row++) for(int col=-1;col<=1;col++)
            {
                Vector3 p=new Vector3(col*1.4f,.55f+row*1.3f,0);
                Arc(t,p+Vector3.up*.55f,.48f,0,180,stone);
                Box(t,"Burial_Sill",p-Vector3.up*.3f,new Vector3(1.1f,.16f,.6f),stone);
                for(int side=-1;side<=1;side+=2)
                    Box(t,"Niche_Jamb",p+new Vector3(side*.53f,.1f,0),new Vector3(.16f,.8f,.5f),stone);
                Cylinder(t,"Ash_Urn",p+Vector3.up*.02f,new Vector3(.3f,.23f,.3f),floor);
            }
        }

        private static void Treasury(Transform t,Material stone,Material floor)
        {
            Arc(t,new Vector3(0,2.5f,0),1.8f,0,180,stone);
            Pillar(t,-2f,2.5f,stone); Pillar(t,2f,2.5f,stone);
            Arc(t,new Vector3(0,2.2f,0),.85f,0,360,floor);
            for(int i=-2;i<=2;i++) Box(t,"Treasury_Grille",new Vector3(i*.5f,1.5f,0),new Vector3(.11f,2.8f,.18f),stone);
            Box(t,"Seal_Diamond",new Vector3(0,2.2f,-.18f),new Vector3(.65f,.65f,.25f),floor).localRotation=Quaternion.Euler(0,0,45);
        }

        private static void Waymark(Transform t,Material stone,Material floor)
        {
            Pillar(t,0,3.7f,stone);
            Arc(t,new Vector3(0,4f,0),1.1f,0,330,floor);
            Box(t,"Waymark_Crossbar",new Vector3(0,3.9f,0),new Vector3(2.1f,.22f,.4f),stone);
            Box(t,"Broken_Direction_Arm",new Vector3(1.25f,2.8f,0),new Vector3(1.2f,.3f,.4f),floor).localRotation=Quaternion.Euler(0,0,18);
        }

        private static void Pier(Transform t,Material stone,Material floor)
        {
            Pillar(t,-1.8f,4.3f,stone); Pillar(t,1.8f,3.1f,stone);
            Arc(t,new Vector3(0,1.3f,0),1.55f,0,125,stone);
            Box(t,"Abutment_Rib",new Vector3(-1,3.6f,0),new Vector3(.45f,2.3f,.6f),floor).localRotation=Quaternion.Euler(0,0,-30);
        }

        private static void Spoke(Transform t,float height,float angle,float radius,Material material)
        {
            float a=angle*Mathf.Deg2Rad;
            Box(t,"Carved_Radial_Stone",new Vector3(Mathf.Cos(a)*radius*.65f,height+Mathf.Sin(a)*radius*.65f,0),
                new Vector3(radius*.65f,.16f,.35f),material).localRotation=Quaternion.Euler(0,0,angle);
        }

        private static void Arc(Transform t,Vector3 center,float radius,float start,float end,Material material)
        {
            int count=Mathf.CeilToInt((end-start)/15f);
            float step=(end-start)/count;
            for(int i=0;i<count;i++)
            {
                float angle=start+(i+.5f)*step, a=angle*Mathf.Deg2Rad;
                var block=Box(t,"Arch_Voussoir",center+new Vector3(Mathf.Cos(a)*radius,Mathf.Sin(a)*radius,0),
                    new Vector3(.22f,2f*radius*Mathf.Sin(step*Mathf.Deg2Rad*.5f)+.025f,.52f),material);
                block.localRotation=Quaternion.Euler(0,0,angle);
            }
        }

        private static void Pillar(Transform t,float x,float height,Material material)
        {
            Cylinder(t,"Weathered_Column",new Vector3(x,height*.5f,0),new Vector3(.6f,height*.5f,.6f),material);
            Box(t,"Column_Foot",new Vector3(x,.12f,0),new Vector3(.8f,.24f,.7f),material);
            Box(t,"Broken_Capital",new Vector3(x,height,0),new Vector3(.8f,.2f,.7f),material);
        }
        private static Transform Group(Transform parent,string name,Vector3 position,float yaw)
        {
            var t=new GameObject(name).transform; t.SetParent(parent,false); t.localPosition=position;
            t.localRotation=Quaternion.Euler(0,yaw,0); return t;
        }
        private static Transform Box(Transform t,string name,Vector3 p,Vector3 scale,Material m) => Part(t,name,p,scale,m,PrimitiveType.Cube);
        private static Transform Cylinder(Transform t,string name,Vector3 p,Vector3 scale,Material m) => Part(t,name,p,scale,m,PrimitiveType.Cylinder);
        private static Transform Sphere(Transform t,string name,Vector3 p,Vector3 scale,Material m) => Part(t,name,p,scale,m,PrimitiveType.Sphere);
        private static Transform Part(Transform parent,string name,Vector3 p,Vector3 scale,Material material,PrimitiveType shape)
        {
            var go=GameObject.CreatePrimitive(shape); go.name=name; go.layer=LayerMask.NameToLayer(GameConfig.LevelLayerName);
            go.transform.SetParent(parent,false); go.transform.localPosition=p; go.transform.localScale=scale;
            go.GetComponent<Renderer>().sharedMaterial=material;
            if(shape!=PrimitiveType.Cube)
            {
                Object.DestroyImmediate(go.GetComponent<Collider>());
                go.AddComponent<MeshCollider>().sharedMesh=go.GetComponent<MeshFilter>().sharedMesh;
            }
            return go.transform;
        }
    }
}
