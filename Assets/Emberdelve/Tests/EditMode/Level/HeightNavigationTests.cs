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
        [Test] public void SafeCircleSearchFindsReachableEdgeWhenCenterIsIsolated()
        {
            var grid = ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.width = 7; grid.height = 3; grid.cellSize = 1; grid.mask = "...#..." + "...#..." + "...#...";
                var finder = new NavGridPathfinder(grid); var path = new List<Vector3>();
                var center = new Vector2(4.5f, 1.5f);
                Assert.False(finder.TryFindPath(grid.CellCenter(0, 1), grid.CellCenter(4, 1), path));
                Assert.True(finder.TryFindPathInsideCircle(grid.CellCenter(0, 1), center, 2.1f, path));
                Assert.That(path[path.Count - 1], Is.EqualTo(grid.CellCenter(2, 1)));
                Assert.False(finder.TryFindPathInsideCircle(grid.CellCenter(0, 1), center, 1f, path));
                Assert.That(path, Is.Empty, "No stale escape route after unreachable search");
                Assert.False(finder.TryFindPathInsideCircle(grid.CellCenter(0, 1), center, float.NaN, path));
            }
            finally { Object.DestroyImmediate(grid); }
        }

        [Test] public void SafeCircleSearchRespectsCornersAndHeightLinks()
        {
            var grid = ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.width = 2; grid.height = 2; grid.cellSize = 1; grid.mask = ".##.";
                var finder = new NavGridPathfinder(grid); var path = new List<Vector3>();
                Assert.False(finder.TryFindPathInsideCircle(grid.CellCenter(0, 0), new Vector2(1.5f, 1.5f), .1f, path));
                grid.width = 3; grid.height = 1; grid.mask = "...";
                grid.surfaceHeights = new[] { 0f, .9f, 1.8f }; grid.surfaceLinks = new byte[] { 1, 3, 2 };
                Assert.True(finder.TryFindPathInsideCircle(grid.CellCenter(0, 0), new Vector2(2.5f, .5f), .1f, path));
                Assert.That(path[path.Count - 1].y, Is.EqualTo(1.8f));
                grid.surfaceLinks[1] = 2;
                Assert.False(finder.TryFindPathInsideCircle(grid.CellCenter(0, 0), new Vector2(2.5f, .5f), .1f, path));
            }
            finally { Object.DestroyImmediate(grid); }
        }

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
        [Test] public void BakeKeepsSupportedUndercroftBelowTheModuleOrigin()
        {
            Box(new Vector3(2, -1.75f, 2), new Vector3(4, .5f, 4));
            Physics.SyncTransforms();
            var grid = ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.origin = _origin; grid.width = grid.height = 2; grid.cellSize = 2; grid.mask = "....";
                NavGridSurface.Bake(grid, _root.transform, 1);
                Assert.True(grid.IsWalkable(0, 0));
                Assert.That(grid.CellCenter(0, 0).y, Is.EqualTo(_origin.y - 1.5f).Within(.01f));
                Assert.True(grid.CanTraverse(0, 0, 0, 1));
            }
            finally { Object.DestroyImmediate(grid); }
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

        [Test] public void BakeKeepsWalkableGroundUnderDoorwayLintel()
        {
            Box(new Vector3(3,-.25f,1),new Vector3(6,.5f,2));
            Box(new Vector3(3,3.6f,1),new Vector3(2,.4f,2));
            Physics.SyncTransforms();
            var grid=ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.width=3;grid.height=1;grid.cellSize=2;grid.origin=_origin;grid.mask="...";
                NavGridSurface.Bake(grid,_root.transform,1);
                Assert.That(grid.CellCenter(1,0).y,Is.EqualTo(_origin.y).Within(.001f));
                var path=new List<Vector3>();
                Assert.True(new NavGridPathfinder(grid).TryFindPath(grid.CellCenter(0,0),grid.CellCenter(2,0),path));
            }
            finally { Object.DestroyImmediate(grid); }
        }

        [TestCase(1.8f)] [TestCase(3.6f)]
        public void BakeKeepsSolidTerraceTopAndRejectsItsCliff(float height)
        {
            Box(new Vector3(3,-.25f,1),new Vector3(6,.5f,2));
            Box(new Vector3(3,height*.5f,1),new Vector3(2,height,2));
            Physics.SyncTransforms();
            var grid=ScriptableObject.CreateInstance<NavGridData>();
            try
            {
                grid.width=3;grid.height=1;grid.cellSize=2;grid.origin=_origin;grid.mask="...";
                NavGridSurface.Bake(grid,_root.transform,1);
                Assert.That(grid.CellCenter(1,0).y,Is.EqualTo(_origin.y+height).Within(.001f));
                Assert.False(grid.CanTraverse(0,0,1,0));
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
