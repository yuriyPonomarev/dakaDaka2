using Emberdelve.Net;
using NUnit.Framework;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
namespace Emberdelve.Tests.NetInventory
{
    public sealed class RoyaleZoneNetworkTests
    {
        private static RoyaleZoneWire Sample() => new RoyaleZoneWire { Floor = 2, Generation = 4, Active = true,
            Shrinking = true, X = 200, Z = 40, Radius = 30, DamagePerSecond = 2, SecondsToBoundary = 12 };
        [Test] public void AuthorityCircleAndPhaseRoundTrip()
        {
            var wire = Sample(); wire.Floor = 3; wire.HasFinalExit = true; wire.ExitPosition = new Vector3(200, 1.1f, 40); wire.ExitHoldDuration = 3;
            using (var writer = new FastBufferWriter(128, Allocator.Temp))
            {
                writer.WriteValueSafe(wire);
                using (var reader = new FastBufferReader(writer, Allocator.Temp))
                { reader.ReadValueSafe(out RoyaleZoneWire decoded); Assert.True(decoded.IsValid); Assert.True(decoded.Equals(wire)); }
            }
        }
        [Test] public void HeldDeadAndStaleFloorCannotPresentActiveZone()
        {
            var wire = Sample(); Assert.True(wire.VisibleFor(4, true, false));
            Assert.False(wire.VisibleFor(6, true, false)); Assert.False(wire.VisibleFor(4, false, false));
            Assert.False(wire.VisibleFor(4, true, true)); wire.Active = false; Assert.False(wire.VisibleFor(4, true, false));
            Assert.True(default(RoyaleZoneWire).IsValid); Assert.False(default(RoyaleZoneWire).VisibleFor(0, true, false));
        }
        [Test] public void UnsafeValuesRejectedAndDistanceUsesHorizontalCircle()
        {
            var wire = Sample(); Assert.That(wire.DistanceOutside(new Vector3(200, 999, 40)), Is.Zero);
            Assert.That(wire.DistanceOutside(new Vector3(240, -999, 40)), Is.EqualTo(10).Within(.001));
            wire.X = float.NaN; Assert.False(wire.IsValid); wire = Sample(); wire.Radius = -1; Assert.False(wire.IsValid);
            wire = Sample(); wire.SecondsToBoundary = float.PositiveInfinity; Assert.False(wire.IsValid);
            wire = Sample(); wire.HasFinalExit = true; wire.ExitHoldDuration = 3; Assert.False(wire.IsValid);
            wire.Floor = 3; Assert.True(wire.IsValid); wire.ExitHoldDuration = 0; Assert.False(wire.IsValid);
            wire.ExitHoldDuration = 3; wire.ExitPosition = new Vector3(float.NaN, 0, 0); Assert.False(wire.IsValid);
            wire = Sample(); wire.Floor = 0; Assert.False(wire.IsValid); wire = Sample(); wire.Floor = 4; Assert.False(wire.IsValid);
        }
    }
}
