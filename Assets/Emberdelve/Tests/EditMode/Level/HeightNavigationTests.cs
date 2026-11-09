using System.Collections.Generic;
using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class HeightNavigationTests
    {
        private GameObject _root;
        private readonly Vector3 _origin=new Vector3(2200,20,2200);
        private sealed class Target : ICombatEntity
        {
            public Vector3 Center=new Vector3(0,1.15f,1);
            public EntityKind Kind=>EntityKind.RemotePlayer;
            public int CombatId=>42;
            public int TeamId=>1;
            public Vector3 CombatCenter=>Center;
            public Collider HeadCollider=>null;
            public bool IsDead=>false;
            public float CurrentHealth=>100;
            public float MaxHealth=>100;
            public void ApplyDamage(DamageInfo damage) { }
        }
        [SetUp] public void SetUp() { _root=new GameObject("HeightNavigationProof"); _root.transform.position=_origin; }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_root); }
        private void Box(Vector3 center,Vector3 size)
        {
            var go=new GameObject("Solid",typeof(BoxCollider));go.transform.SetParent(_root.transform,false);
            go.transform.localPosition=center;go.GetComponent<BoxCollider>().size=size;go.layer=0;
        }
        [Test] public void StairTreadsConnectBothWays_ButSameHeightCliffDoesNot()
        {
            Box(new Vector3(2,-.25f,0),new Vector3(8,.5f,6));
            for(int i=1;i<=12;i++) Box(new Vector3(i*.3f,i*.15f*.5f,0),new Vector3(.3f,i*.15f,3.6f));
            Box(new Vector3(4.5f,.9f,0),new Vector3(1.5f,1.8f,3.6f));
            Physics.SyncTransforms();
            Vector3 low=_origin,high=_origin+new Vector3(4.5f,1.8f,0);
            Assert.IsTrue(NavGridSurface.SurfaceConnected(low,high,1,_root.transform));
            Assert.IsTrue(NavGridSurface.SurfaceConnected(high,low,1,_root.transform));
            // Same platform's side has no stairs. Its top must never be reached by snapping upward.
            Assert.IsFalse(NavGridSurface.SurfaceConnected(_origin+new Vector3(4.5f,0,2.5f),high,1,_root.transform));
            Assert.IsFalse(NavGridSurface.TryStep(_origin+new Vector3(4.5f,0,2),_origin+new Vector3(4.5f,0,1.7f),1,out _));
        }
        [Test] public void PathfinderRespectsBakedCliffLinks_AndRetainsWaypointHeights()
        {
            var grid=ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.width=3;grid.height=1;grid.cellSize=2;grid.mask="...";
                grid.surfaceHeights=new[]{0f,.9f,1.8f};grid.surfaceLinks=new byte[]{1,3,2};
                var path=new List<Vector3>();var finder=new NavGridPathfinder(grid);
                Assert.IsTrue(finder.TryFindPath(grid.CellCenter(0,0),grid.CellCenter(2,0),path));
                Assert.AreEqual(1.8f,path[path.Count-1].y,.001f);
                grid.surfaceLinks[1]=2;grid.surfaceLinks[2]=0;
                Assert.IsFalse(finder.TryFindPath(grid.CellCenter(0,0),grid.CellCenter(2,0),path));
                grid.surfaceLinks=null;grid.surfaceHeights=null;
                Assert.IsTrue(finder.TryFindPath(grid.CellCenter(0,0),grid.CellCenter(2,0),path),"legacy flat grids remain compatible");
                Assert.AreEqual(0,path[path.Count-1].y);
            }
            finally { Object.DestroyImmediate(grid); }
        }
        [Test] public void EnemyPursuesTerraceInsteadOfAttackingOrStoppingDirectlyBelow()
        {
            var data=ScriptableObject.CreateInstance<EnemyData>();
            try
            {
                data.sightRange=20;data.hearingRange=8;data.attackRange=3;data.chaseSpeed=3;
                var brain=new EnemyBrain(data,null,Vector3.zero,0);
                var target=new Target();
                brain.Tick(.1f,Time.time,Vector3.zero,Vector3.forward,target);
                Assert.AreEqual(EnemyState.Attack,brain.State,"ground-level attack range is unchanged");
                brain.Tick(.1f,Time.time,Vector3.zero,Vector3.forward,target);
                target.Center+=Vector3.up*3.6f;
                var interrupted=brain.Tick(.1f,Time.time,Vector3.zero,Vector3.forward,target);
                Assert.IsFalse(interrupted.RequestAttack);Assert.IsFalse(brain.IsWindingUp);
                Assert.AreEqual(EnemyState.Chase,brain.State);
                var chase=brain.Tick(.1f,Time.time,Vector3.zero,Vector3.forward,target);
                Assert.Greater(chase.Speed,0,"small XZ distance cannot stop pursuit across floors");
                Assert.AreEqual(target.Center,chase.MoveTo);
            }
            finally { Object.DestroyImmediate(data); }
        }
    }
}
