using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
namespace Emberdelve.Tests
{
    public class AvatarCombatWireTests
    {
        private static AvatarCombatWire Sample() => new AvatarCombatWire { Main="ashen_short_sword",Off="emberbulwark_shield",Pattern="diagonal",Swing=true,Guard=false,SwingId=42,Elapsed=.2f,Windup=.4f,Active=.2f,Recovery=.5f,EyeOffset=Vector3.up*1.68f,Aim=Quaternion.Euler(20,130,0),SampleTime=12.5 };
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
        }
        [Test] public void NonFinitePoseAndTimingAreRejected()
        {
            var state=Sample(); state.Elapsed=float.NaN; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.Aim=default; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.SampleTime=double.PositiveInfinity; Assert.That(state.IsValid,Is.False);
            state=Sample(); state.EyeOffset=new Vector3(float.PositiveInfinity,0,0); Assert.That(state.IsValid,Is.False);
        }
    }
}
