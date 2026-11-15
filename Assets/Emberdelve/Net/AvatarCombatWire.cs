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
        public bool Crouched;
        public bool InFloorTransit;
        public bool Recoil;
        public float StopFraction, RecoilDuration;
        public float DrinkProgress, DrinkDuration;
        public bool Reloading;
        public float ReloadProgress, ReloadDuration;
        public bool Drawing;
        public bool CrossbowLoaded;
        public float DrawProgress;
        public int SwingId;
        public float Elapsed, Windup, Active, Recovery;
        public Vector3 EyeOffset;
        public Quaternion Aim;
        public double SampleTime;
        public bool IsValid => Finite(DrinkProgress) && DrinkProgress >= 0f && DrinkProgress <= 1f
            && Finite(DrawProgress) && DrawProgress >= 0f && DrawProgress <= 1f
            && (!CrossbowLoaded || (Main.ToString() == "ember_crossbow" && Utility.Length == 0 && !Reloading))
            && (Drawing || DrawProgress == 0f)
            && (!Drawing || (Main.Length > 0 && Utility.Length == 0 && !Swing && !Recoil && !Guard && !Drinking && !Reloading))
            && Finite(ReloadProgress) && ReloadProgress >= 0f && ReloadProgress <= 1f
            && Finite(ReloadDuration) && ReloadDuration >= 0f && ReloadDuration <= 120f
            && (!Reloading || (Main.Length > 0 && ReloadDuration > 0f && !Swing && !Recoil && !Guard && !Drinking))
            && Finite(StopFraction) && StopFraction >= 0f && StopFraction <= 1f
            && Finite(RecoilDuration) && RecoilDuration >= 0f && RecoilDuration <= 30f
            && (!Recoil || (!Swing && RecoilDuration > 0f && Pattern.Length > 0 && Elapsed >= Windup + Active * StopFraction))
            && Finite(DrinkDuration) && DrinkDuration >= 0f
            && (!Drinking || (DrinkDuration > 0f && Utility.Length > 0))
            && Finite(Elapsed) && Finite(Windup) && Finite(Active) && Finite(Recovery)
            && Elapsed>=0 && Windup>=0 && Active>=0 && Recovery>=0 && Windup+Active+Recovery<=30
            && !double.IsNaN(SampleTime) && !double.IsInfinity(SampleTime)
            && Finite(EyeOffset.x) && Finite(EyeOffset.y) && Finite(EyeOffset.z) && EyeOffset.sqrMagnitude<100
            && Finite(Aim.x) && Finite(Aim.y) && Finite(Aim.z) && Finite(Aim.w)
            && Mathf.Abs(Quaternion.Dot(Aim,Aim)-1)<.01f;
        private static bool Finite(float n) => !float.IsNaN(n) && !float.IsInfinity(n);
        public float PresentationElapsed(double now) => Mathf.Min(Recoil ? Windup+Active*StopFraction+RecoilDuration : Windup+Active+Recovery,
            Elapsed+Mathf.Clamp((float)(now-SampleTime),0,.1f));
        public float RecoilElapsed(double now) => Mathf.Clamp(PresentationElapsed(now)-Windup-Active*StopFraction,0,RecoilDuration);
        public float RecoilProgress(double now) => RecoilDuration > 0f ? RecoilElapsed(now)/RecoilDuration : 1f;
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T:IReaderWriter
        {
            s.SerializeValue(ref Main); s.SerializeValue(ref Off); s.SerializeValue(ref Pattern); s.SerializeValue(ref Utility);
            s.SerializeValue(ref Drinking); s.SerializeValue(ref DrinkProgress); s.SerializeValue(ref DrinkDuration);
            s.SerializeValue(ref Reloading); s.SerializeValue(ref ReloadProgress); s.SerializeValue(ref ReloadDuration);
            s.SerializeValue(ref CrossbowLoaded); s.SerializeValue(ref Drawing); s.SerializeValue(ref DrawProgress);
            s.SerializeValue(ref Crouched);
            s.SerializeValue(ref InFloorTransit);
            s.SerializeValue(ref Swing); s.SerializeValue(ref Guard); s.SerializeValue(ref SwingId);
            s.SerializeValue(ref Recoil); s.SerializeValue(ref StopFraction); s.SerializeValue(ref RecoilDuration);
            s.SerializeValue(ref Elapsed); s.SerializeValue(ref Windup); s.SerializeValue(ref Active); s.SerializeValue(ref Recovery);
            s.SerializeValue(ref EyeOffset); s.SerializeValue(ref Aim); s.SerializeValue(ref SampleTime);
        }
        public bool Equals(AvatarCombatWire o) => Utility.Equals(o.Utility)&&Main.Equals(o.Main)&&Off.Equals(o.Off)&&Pattern.Equals(o.Pattern)
            && Drinking==o.Drinking&&DrinkProgress==o.DrinkProgress&&DrinkDuration==o.DrinkDuration
            && Reloading==o.Reloading&&ReloadProgress==o.ReloadProgress&&ReloadDuration==o.ReloadDuration
            && CrossbowLoaded==o.CrossbowLoaded && Drawing==o.Drawing&&DrawProgress==o.DrawProgress
            && Recoil==o.Recoil&&StopFraction==o.StopFraction&&RecoilDuration==o.RecoilDuration
            && InFloorTransit==o.InFloorTransit && Crouched==o.Crouched && Swing==o.Swing&&Guard==o.Guard&&SwingId==o.SwingId&&Elapsed==o.Elapsed&&Windup==o.Windup&&Active==o.Active&&Recovery==o.Recovery
            && EyeOffset.Equals(o.EyeOffset)&&Aim.Equals(o.Aim)&&SampleTime==o.SampleTime;
    }
}
