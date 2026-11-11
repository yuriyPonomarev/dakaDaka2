using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
namespace Emberdelve.Net
{
    // Server-owned state, not an intent. Retained for observers joining mid-swing and explicit cancellation.
    public struct AvatarCombatWire : INetworkSerializable, IEquatable<AvatarCombatWire>
    {
        public FixedString64Bytes Main, Off, Pattern, Utility;
        public bool Swing, Guard, Drinking;
        public float DrinkProgress, DrinkDuration;
        public int SwingId;
        public float Elapsed, Windup, Active, Recovery;
        public Vector3 EyeOffset;
        public Quaternion Aim;
        public double SampleTime;
        public bool IsValid => Finite(DrinkProgress) && DrinkProgress >= 0f && DrinkProgress <= 1f
            && Finite(DrinkDuration) && DrinkDuration >= 0f
            && (!Drinking || (DrinkDuration > 0f && Utility.Length > 0))
            && Finite(Elapsed) && Finite(Windup) && Finite(Active) && Finite(Recovery)
            && Elapsed>=0 && Windup>=0 && Active>=0 && Recovery>=0 && Windup+Active+Recovery<=30
            && !double.IsNaN(SampleTime) && !double.IsInfinity(SampleTime)
            && Finite(EyeOffset.x) && Finite(EyeOffset.y) && Finite(EyeOffset.z) && EyeOffset.sqrMagnitude<100
            && Finite(Aim.x) && Finite(Aim.y) && Finite(Aim.z) && Finite(Aim.w)
            && Mathf.Abs(Quaternion.Dot(Aim,Aim)-1)<.01f;
        private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
        public float PresentationElapsed(double now) => Mathf.Min(Windup+Active+Recovery,Elapsed+Mathf.Clamp((float)(now-SampleTime),0,.1f));
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T:IReaderWriter
        {
            s.SerializeValue(ref Main); s.SerializeValue(ref Off); s.SerializeValue(ref Pattern); s.SerializeValue(ref Utility);
            s.SerializeValue(ref Drinking); s.SerializeValue(ref DrinkProgress); s.SerializeValue(ref DrinkDuration);
            s.SerializeValue(ref Swing); s.SerializeValue(ref Guard); s.SerializeValue(ref SwingId);
            s.SerializeValue(ref Elapsed); s.SerializeValue(ref Windup); s.SerializeValue(ref Active); s.SerializeValue(ref Recovery);
            s.SerializeValue(ref EyeOffset); s.SerializeValue(ref Aim); s.SerializeValue(ref SampleTime);
        }
        public bool Equals(AvatarCombatWire o) => Utility.Equals(o.Utility)&&Main.Equals(o.Main)&&Off.Equals(o.Off)&&Pattern.Equals(o.Pattern)
            && Drinking==o.Drinking&&DrinkProgress==o.DrinkProgress&&DrinkDuration==o.DrinkDuration
            && Swing==o.Swing&&Guard==o.Guard&&SwingId==o.SwingId&&Elapsed==o.Elapsed&&Windup==o.Windup&&Active==o.Active&&Recovery==o.Recovery
            && EyeOffset.Equals(o.EyeOffset)&&Aim.Equals(o.Aim)&&SampleTime==o.SampleTime;
    }
}
