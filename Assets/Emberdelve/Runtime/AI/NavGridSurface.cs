using UnityEngine;
using Emberdelve.Core;
using Emberdelve.Interaction;

namespace Emberdelve.AI
{
    /// <summary>Single-surface navigation for solid terraces. Links follow real stair treads;
    /// a nearby high cell never implies permission to climb its vertical edge.</summary>
    public static class NavGridSurface
    {
        public const float MaximumStep = .25f;
        private static readonly RaycastHit[] GroundHits = new RaycastHit[64];
        private static readonly Collider[] BodyHits = new Collider[64];
        private static readonly Vector2Int[] Directions = {
            new Vector2Int(1,0), new Vector2Int(-1,0), new Vector2Int(0,1), new Vector2Int(0,-1),
            new Vector2Int(1,1), new Vector2Int(1,-1), new Vector2Int(-1,1), new Vector2Int(-1,-1)
        };

        public static void Bake(NavGridData grid, Transform geometry, int mask)
        {
            grid.surfaceHeights = new float[grid.width * grid.height];
            grid.surfaceLinks = new byte[grid.width * grid.height];
            char[] walkable = grid.mask.ToCharArray();
            for(int y=0;y<grid.height;y++) for(int x=0;x<grid.width;x++)
            {
                int index=y*grid.width+x;
                if(!grid.IsWalkable(x,y)) continue;
                Vector3 point=grid.origin+new Vector3((x+.5f)*grid.cellSize,0,(y+.5f)*grid.cellSize);
                if(!TryGround(point,grid.origin.y+4.25f,grid.origin.y-.25f,mask,geometry,out float height) ||
                    !BodyClear(new Vector3(point.x,height,point.z),mask,geometry))
                { walkable[index]='#'; continue; }
                grid.surfaceHeights[index]=height-grid.origin.y;
            }
            grid.mask=new string(walkable);
            for(int y=0;y<grid.height;y++) for(int x=0;x<grid.width;x++)
            {
                if(!grid.IsWalkable(x,y)) continue;
                for(int d=0;d<Directions.Length;d++)
                {
                    int nx=x+Directions[d].x,ny=y+Directions[d].y;
                    if(!grid.IsWalkable(nx,ny)) continue;
                    if(SurfaceConnected(grid.CellCenter(x,y),grid.CellCenter(nx,ny),mask,geometry))
                        grid.surfaceLinks[y*grid.width+x]|=(byte)(1<<d);
                }
            }
        }

        public static bool SurfaceConnected(Vector3 from, Vector3 to, int mask, Transform geometry=null)
        {
            float horizontal=new Vector2(to.x-from.x,to.z-from.z).magnitude;
            if(Mathf.Abs(to.y-from.y)>horizontal*.65f+MaximumStep) return false;
            int samples=Mathf.Max(1,Mathf.CeilToInt(horizontal/.2f));
            float previous=from.y;
            for(int i=1;i<=samples;i++)
            {
                Vector3 point=Vector3.Lerp(from,to,i/(float)samples);
                if(!TryGround(point,previous+MaximumStep+.02f,previous-MaximumStep-.02f,mask,geometry,out float height)) return false;
                if(Mathf.Abs(height-previous)>MaximumStep+.005f) return false;
                point.y=height;
                if(!BodyClear(point,mask,geometry)) return false;
                previous=height;
            }
            return Mathf.Abs(previous-to.y)<.06f;
        }

        public static bool TryStep(Vector3 from, Vector3 requested, int mask, out Vector3 grounded)
        {
            grounded=from;
            int samples=Mathf.Max(1,Mathf.CeilToInt(new Vector2(requested.x-from.x,requested.z-from.z).magnitude/.2f));
            float previous=from.y;
            for(int i=1;i<=samples;i++)
            {
                Vector3 point=Vector3.Lerp(from,requested,i/(float)samples);
                if(!TryGround(point,previous+MaximumStep+.02f,previous-MaximumStep-.02f,mask,null,out float height)) return false;
                if(Mathf.Abs(height-previous)>MaximumStep+.005f) return false;
                point.y=height;
                if(!BodyClear(point,mask,null)) return false;
                previous=height;
            }
            grounded=new Vector3(requested.x,previous,requested.z);
            return true;
        }

        private static bool Relevant(Collider collider, Transform geometry)
        {
            return collider!=null && (geometry==null || collider.transform.IsChildOf(geometry)) &&
                collider.GetComponentInParent<DungeonDoorInteractable>()==null;
        }

        private static bool TryGround(Vector3 point,float top,float bottom,int mask,Transform geometry,out float height)
        {
            int count=Physics.RaycastNonAlloc(new Vector3(point.x,top,point.z),Vector3.down,GroundHits,top-bottom,mask,QueryTriggerInteraction.Ignore);
            height=float.NegativeInfinity;
            if(count==GroundHits.Length) return false;
            for(int i=0;i<count;i++)
                if(Relevant(GroundHits[i].collider,geometry) && GroundHits[i].normal.y>=.65f)
                    height=Mathf.Max(height,GroundHits[i].point.y);
            return !float.IsNegativeInfinity(height);
        }

        private static bool BodyClear(Vector3 foot,int mask,Transform geometry)
        {
            // Clearance starts above the step envelope so a normal tread does not block its own ascent.
            int count=Physics.OverlapCapsuleNonAlloc(foot+Vector3.up*.55f,foot+Vector3.up*1.55f,.22f,BodyHits,mask,QueryTriggerInteraction.Ignore);
            if(count==BodyHits.Length) return false;
            for(int i=0;i<count;i++) if(Relevant(BodyHits[i],geometry)) return false;
            return true;
        }
    }
}
