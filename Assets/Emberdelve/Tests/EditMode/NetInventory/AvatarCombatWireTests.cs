using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
namespace Emberdelve.Tests
{
    public class AvatarCombatWireTests
    {
        private static AvatarCombatWire Sample() => new AvatarCombatWire { Main="ashen_short_sword",Off="emberbulwark_shield",Pattern="diagonal",Utility="pitch_torch",Swing=true,Guard=false,SwingId=42,Elapsed=.2f,Windup=.4f,Active=.2f,Recovery=.5f,EyeOffset=Vector3.up*1.68f,Aim=Quaternion.Euler(20,130,0),SampleTime=12.5 };
        [TestCase(false)] [TestCase(true)]
        public void FloorTransitSurvivesWireAndChangeDetection(bool inTransit)
        {
            var state = Sample(); state.InFloorTransit = inTransit;
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out AvatarCombatWire decoded);
                    Assert.True(decoded.IsValid); Assert.That(decoded.InFloorTransit, Is.EqualTo(inTransit));
                    Assert.True(decoded.Equals(state));
                }
            }
            var changed = state; changed.InFloorTransit = !inTransit;
            Assert.False(changed.Equals(state));
        }
        [TestCase(false)] [TestCase(true)]
        public void CrouchStanceSurvivesWireAndChangeDetection(bool crouched)
        {
            var state = Sample(); state.Crouched = crouched;
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out AvatarCombatWire decoded);
                    Assert.That(decoded.IsValid, Is.True);
                    Assert.That(decoded.Crouched, Is.EqualTo(crouched));
                    Assert.That(decoded.Equals(state), Is.True);
                }
            }
            var changed = state; changed.Crouched = !crouched;
            Assert.That(changed.Equals(state), Is.False);
        }
        [Test] public void LoadedCrossbowSurvivesWireAndCannotDescribeAnotherWeaponOrReload()
        {
            var state = Sample(); state.Main = "ember_crossbow"; state.Utility = "";
            state.Pattern = ""; state.Swing = false; state.CrossbowLoaded = true;
            Assert.IsTrue(state.IsValid);
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out AvatarCombatWire decoded);
                    Assert.IsTrue(decoded.CrossbowLoaded); Assert.IsTrue(decoded.Equals(state));
                    decoded.CrossbowLoaded = false; Assert.IsFalse(decoded.Equals(state));
                }
            }
            state.Main = "hollow_bow"; Assert.IsFalse(state.IsValid);
            state.Main = "ember_crossbow"; state.Reloading = true; state.ReloadDuration = 2f;
            Assert.IsFalse(state.IsValid);
        }

        [Test] public void AcceptedTrajectoryAndLoadoutSurviveRoundTrip()
        {
            var original=Sample();
            using(var writer=new FastBufferWriter(512,Allocator.Temp))
            {
                writer.WriteValueSafe(original);
                using(var reader=new FastBufferReader(writer,Allocator.Temp))
                { reader.ReadValueSafe(out AvatarCombatWire decoded); Assert.That(decoded.IsValid,Is.True); Assert.That(decoded.Equals(original),Is.True); }
            }
        }
        [Test] public void ClockDoesNotRewindOrPredictBeyondBoundedSampleAge()
        {
            var state=Sample(); Assert.That(state.PresentationElapsed(11),Is.EqualTo(.2f).Within(.0001));
            Assert.That(state.PresentationElapsed(100),Is.EqualTo(.3f).Within(.0001));
            state.Elapsed=1.09f; Assert.That(state.PresentationElapsed(100),Is.EqualTo(1.1f).Within(.0001));
        }
        [Test] public void CancellationAndWeaponChangeAreNotSuppressedAsEqualStates()
        {
            var original=Sample(); var stopped=original; stopped.Swing=false; Assert.That(stopped.Equals(original),Is.False);
            var switched=original; switched.Main="ember_rapier"; Assert.That(switched.Equals(original),Is.False);
            var guarded=original; guarded.Guard=true; Assert.That(guarded.Equals(original),Is.False);
            var utility=original; utility.Utility=""; Assert.That(utility.Equals(original),Is.False);
        }
        [Test] public void NonFinitePoseAndTimingAreRejected()
        {
            var state=Sample(); state.Elapsed=float.NaN; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.Aim=default; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.SampleTime=double.PositiveInfinity; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.EyeOffset=new Vector3(float.PositiveInfinity,0,0); Assert.That(state.IsValid,Is.False);
        }
        [Test] public void DrinkProgressAndCancellationSurviveRoundTrip()
        {
            var original = Sample(); original.Utility = "blood_draught";
            original.Drinking = true; original.DrinkProgress = .35f; original.DrinkDuration = 1.8f;
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(original);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out AvatarCombatWire decoded);
                    Assert.That(decoded.IsValid, Is.True);
                    Assert.That(decoded.Equals(original), Is.True);
                }
            }
            var changed = original; changed.Drinking = false; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.DrinkProgress = .5f; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.DrinkDuration = 2f; Assert.That(changed.Equals(original), Is.False);
        }
        [Test] public void RecoilSurvivesRoundTripAndUsesTheContactClock()
        {
            var state=Sample(); state.Swing=false; state.Recoil=true; state.StopFraction=.5f;
            state.RecoilDuration=.3f; state.Elapsed=.55f;
            Assert.That(state.IsValid,Is.True);
            Assert.That(state.RecoilElapsed(12.5),Is.EqualTo(.05f).Within(.0001f));
            Assert.That(state.RecoilElapsed(100),Is.EqualTo(.15f).Within(.0001f),"prediction remains bounded");
            using(var writer=new FastBufferWriter(512,Allocator.Temp))
            {
                writer.WriteValueSafe(state);
                using(var reader=new FastBufferReader(writer,Allocator.Temp))
                { reader.ReadValueSafe(out AvatarCombatWire decoded); Assert.That(decoded.IsValid,Is.True); Assert.That(decoded.Equals(state),Is.True); }
            }
            var changed=state; changed.StopFraction=.4f; Assert.That(changed.Equals(state),Is.False);
            changed=state; changed.RecoilDuration=.4f; Assert.That(changed.Equals(state),Is.False);
            changed=state; changed.Recoil=false; Assert.That(changed.Equals(state),Is.False);
        }
        [Test] public void InvalidOrContradictoryRecoilIsRejected()
        {
            var state=Sample(); state.Recoil=true; state.RecoilDuration=.3f;
            Assert.That(state.IsValid,Is.False,"cannot be swinging and recoiling simultaneously");
            state.Swing=false; Assert.That(state.IsValid,Is.False,"contact cannot be in the future");
            state.Elapsed=.5f; Assert.That(state.IsValid,Is.True);
            foreach(float value in new[]{float.NaN,float.PositiveInfinity,-.1f,1.1f})
            { var bad=state; bad.StopFraction=value; Assert.That(bad.IsValid,Is.False); }
            foreach(float value in new[]{float.NaN,float.PositiveInfinity,-.1f,0f})
            { var bad=state; bad.RecoilDuration=value; Assert.That(bad.IsValid,Is.False); }
        }
        [Test] public void InvalidDrinkStateIsRejected()
        {
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -.1f, 1.1f })
            {
                var state = Sample(); state.DrinkProgress = value; Assert.That(state.IsValid, Is.False);
            }
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1f })
            {
                var state = Sample(); state.DrinkDuration = value; Assert.That(state.IsValid, Is.False);
            }
            var drinking = Sample(); drinking.Drinking = true;
            Assert.That(drinking.IsValid, Is.False, "a drink requires positive duration");
            drinking.DrinkDuration = 1.8f; drinking.Utility = "";
            Assert.That(drinking.IsValid, Is.False, "a drink requires a held utility");
        }

        [Test] public void ReloadStateProgressAndCancellationSurviveRoundTrip()
        {
            var original = Sample(); original.Swing = false; original.Main = "ember_crossbow";
            original.Reloading = true; original.ReloadProgress = .4f; original.ReloadDuration = 2.2f;
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(original);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                { reader.ReadValueSafe(out AvatarCombatWire decoded); Assert.That(decoded.IsValid, Is.True); Assert.That(decoded.Equals(original), Is.True); }
            }
            var changed = original; changed.Reloading = false; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.ReloadProgress = .5f; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.ReloadDuration = 1.1f; Assert.That(changed.Equals(original), Is.False);
        }

        [Test] public void ReloadCannotClaimConcurrentAttackGuardOrInvalidClock()
        {
            var state = Sample(); state.Reloading = true; state.ReloadDuration = 2.2f;
            Assert.That(state.IsValid, Is.False, "committed attack cannot also reload");
            state.Swing = false; Assert.That(state.IsValid, Is.True);
            var bad = state; bad.Guard = true; Assert.That(bad.IsValid, Is.False);
            bad = state; bad.Main = ""; Assert.That(bad.IsValid, Is.False);
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -.1f, 1.1f })
            { bad = state; bad.ReloadProgress = value; Assert.That(bad.IsValid, Is.False); }
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -1f, 0f, 121f })
            { bad = state; bad.ReloadDuration = value; Assert.That(bad.IsValid, Is.False); }
        }

        [Test] public void DrawProgressFullHoldAndCancellationSurviveRoundTrip()
        {
            var original = Sample(); original.Swing = false; original.Utility = "";
            original.Main = "hollow_bow"; original.Drawing = true; original.DrawProgress = .6f;
            using (var writer = new FastBufferWriter(512, Allocator.Temp))
            {
                writer.WriteValueSafe(original);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                {
                    reader.ReadValueSafe(out AvatarCombatWire decoded);
                    Assert.That(decoded.IsValid, Is.True);
                    Assert.That(decoded.Equals(original), Is.True);
                    Assert.That(decoded.Drawing, Is.True);
                    Assert.That(decoded.DrawProgress, Is.EqualTo(.6f));
                }
            }
            var full = original; full.DrawProgress = 1f;
            Assert.That(full.IsValid, Is.True, "a fully charged bow may remain drawn");
            Assert.That(full.Equals(original), Is.False);
            var canceled = full; canceled.Drawing = false; canceled.DrawProgress = 0f;
            Assert.That(canceled.IsValid, Is.True);
            Assert.That(canceled.Equals(full), Is.False, "shot/cancel must clear retained draw pose");
            var started = canceled; started.Drawing = true;
            Assert.That(started.IsValid, Is.True);
            Assert.That(started.Equals(canceled), Is.False, "zero-progress draw is distinct from ready");
        }

        [Test] public void DrawRejectsInvalidProgressAndConflictingActions()
        {
            var state = Sample(); state.Swing = false; state.Utility = "";
            state.Main = "hollow_bow"; state.Drawing = true; state.DrawProgress = .5f;
            Assert.That(state.IsValid, Is.True);
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, -.1f, 1.1f })
            { var bad = state; bad.DrawProgress = value; Assert.That(bad.IsValid, Is.False); }
            var conflict = state; conflict.Guard = true; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Swing = true; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Recoil = true; conflict.RecoilDuration = .2f;
            conflict.Elapsed = conflict.Windup; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Reloading = true; conflict.ReloadDuration = 1f; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Drinking = true; conflict.DrinkDuration = 1f;
            conflict.Utility = "blood_draught"; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Main = ""; Assert.That(conflict.IsValid, Is.False);
            conflict = state; conflict.Drawing = false;
            Assert.That(conflict.IsValid, Is.False, "inactive draw cannot retain a visible charge");
        }

        [Test] public void OwnerAmmoCountsAndWeaponIdentitySurviveRoundTrip()
        {
            var original = new RangedAmmoWire { Weapon = "ember_crossbow", Magazine = 3, Reserve = 17 };
            using (var writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteValueSafe(original);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                { reader.ReadValueSafe(out RangedAmmoWire decoded); Assert.That(decoded.IsValid, Is.True); Assert.That(decoded.Equals(original), Is.True); }
            }
            var changed = original; changed.Magazine--; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.Reserve--; Assert.That(changed.Equals(original), Is.False);
            changed = original; changed.Weapon = "hollow_bow"; Assert.That(changed.Equals(original), Is.False);
            Assert.That(default(RangedAmmoWire).IsValid, Is.True, "empty state clears a stowed weapon");
            changed = original; changed.Weapon = ""; Assert.That(changed.IsValid, Is.False);
            changed = original; changed.Magazine = 100; Assert.That(changed.IsValid, Is.False);
            changed = original; changed.Reserve = -1; Assert.That(changed.IsValid, Is.False);
        }

        [Test] public void DisabledObserverUsesServerReloadProgressAndCancellationWithoutRunningGameplay()
        {
            var oldRole = Emberdelve.Core.NetRole.Current;
            var go = new GameObject("ReloadReplica");
            try
            {
                Emberdelve.Core.NetRole.Set(Emberdelve.Core.NetRoleKind.Client);
                var ranged = go.AddComponent<Emberdelve.Combat.RangedCombat>();
                var rig = go.GetComponent<Emberdelve.Player.PlayerRig>();
                rig.AssignParty(0, false);
                ranged.enabled = false;
                ranged.ApplyReplicatedReload("ember_crossbow", true, .4f, 2.2f);
                Assert.That(ranged.IsReloading, Is.True);
                Assert.That(ranged.ReloadProgress, Is.EqualTo(.4f));
                Assert.That(ranged.ReloadDuration, Is.EqualTo(2.2f));
                Assert.That(ranged.IsDrawing, Is.False, "a presentation sample does not start an attack");
                ranged.ApplyReplicatedReload("ember_crossbow", false, 0f, 0f);
                Assert.That(ranged.IsReloading, Is.False);
                ranged.ApplyReplicatedReload("ember_crossbow", true, float.NaN, 2.2f);
                Assert.That(ranged.IsReloading, Is.False, "invalid state fails closed");
                Emberdelve.Core.NetRole.Set(Emberdelve.Core.NetRoleKind.DedicatedServer);
                ranged.ApplyReplicatedReload("ember_crossbow", true, .5f, 2.2f);
                ranged.ApplyReplicatedAmmo("ember_crossbow", 99, 1000);
                Assert.That(ranged.IsReloading, Is.False, "replica APIs do not control simulation");
                Assert.That(ranged.Magazine, Is.Zero);
                Assert.That(ranged.Reserve, Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(go);
                Emberdelve.Core.NetRole.Set(oldRole);
            }
        }
    }
}
