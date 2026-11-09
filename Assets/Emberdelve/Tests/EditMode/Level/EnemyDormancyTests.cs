using Emberdelve.AI;
using Emberdelve.Core;
using Emberdelve.Data;
using NUnit.Framework;
using UnityEngine;

namespace Emberdelve.Level.Tests
{
    public sealed class EnemyDormancyTests
    {
        private EnemyData _data;
        private sealed class Target : ICombatEntity
        {
            public EntityKind Kind => EntityKind.RemotePlayer;
            public int CombatId => 42;
            public int TeamId => 1;
            public Vector3 CombatCenter => new Vector3(0, 1.15f, 2f);
            public Collider HeadCollider => null;
            public bool IsDead => false;
            public float CurrentHealth => 100;
            public float MaxHealth => 100;
            public void ApplyDamage(DamageInfo damage) { }
        }

        [SetUp] public void SetUp()
        {
            _data=ScriptableObject.CreateInstance<EnemyData>();
            _data.sightRange=10f; _data.hearingRange=4f; _data.attackRange=3f;
            _data.attackWindup=.4f;
        }
        [TearDown] public void TearDown() { Object.DestroyImmediate(_data); }
        private EnemyBrain Brain() => new EnemyBrain(_data,null,Vector3.zero,0,startsDormant:true);
        private static BrainOrder Tick(EnemyBrain brain,float dt,ICombatEntity target=null) =>
            brain.Tick(dt,Time.time,Vector3.zero,Vector3.forward,target);

        [Test] public void NoTargetRemainsMotionlessAndDoesNotPatrol()
        {
            var brain=Brain();
            for(int i=0;i<20;i++) { var order=Tick(brain,1); Assert.That(order.Speed,Is.Zero); Assert.That(order.RequestAttack,Is.False); }
            Assert.That(brain.State,Is.EqualTo(EnemyState.Dormant));
        }
        [Test] public void AggroMustCompleteRiseBeforeMovementOrAttack()
        {
            var brain=Brain(); var target=new Target();
            Assert.That(Tick(brain,.1f,target).RequestAttack,Is.False);
            Assert.That(brain.State,Is.EqualTo(EnemyState.Waking));
            var hold=Tick(brain,EnemyBrain.WakeSeconds-.01f,target);
            Assert.That(hold.Speed,Is.Zero); Assert.That(hold.RequestAttack,Is.False);
            Assert.That(brain.State,Is.EqualTo(EnemyState.Waking));
            Assert.That(Tick(brain,.02f,target).RequestAttack,Is.False);
            Assert.That(brain.State,Is.EqualTo(EnemyState.Chase));
            Tick(brain,.1f,target); Tick(brain,.1f,target);
            Assert.That(brain.IsWindingUp,Is.True,"The ordinary attack telegraph still follows the rise.");
            Assert.That(Tick(brain,.5f,target).RequestAttack,Is.True);
        }
        [Test] public void DamageWakesOnceAndFurtherHitsDoNotRestartRise()
        {
            var brain=Brain();
            brain.NotifyDamaged(DamageInfo.Invalid);
            Assert.That(brain.IsWaking,Is.True);
            Tick(brain,.7f);
            brain.NotifyDamaged(DamageInfo.Invalid);
            Assert.That(brain.WakeElapsed,Is.EqualTo(.7f));
            Tick(brain,.6f);
            Assert.That(brain.State,Is.EqualTo(EnemyState.Chase));
            Tick(brain,20f);
            Assert.That(brain.IsDormant,Is.False,"Awakened enemies never pretend to be dead again.");
        }
        [Test] public void DeathDuringRiseCannotCompleteIntoLivingState()
        {
            var brain=Brain(); Tick(brain,.1f,new Target()); brain.MarkDead();
            var order=Tick(brain,10,new Target());
            Assert.That(brain.State,Is.EqualTo(EnemyState.Dead));
            Assert.That(order.RequestAttack,Is.False); Assert.That(order.Speed,Is.Zero);
        }
        [Test] public void StunCannotSkipTheRise()
        {
            var brain=Brain(); brain.SetStunned(true);
            Assert.That(brain.IsDormant,Is.True);
            brain.SetStunned(false);
            Assert.That(brain.IsWaking,Is.True); Assert.That(brain.WakeElapsed,Is.Zero);
        }
    }
}
