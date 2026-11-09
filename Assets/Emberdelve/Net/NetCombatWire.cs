using System;
using Emberdelve.BladeDefense;
using Emberdelve.Combat;
using Emberdelve.Net.Rules;
using Unity.Netcode;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, server to owner, reliable: the server's answer to ONE combat request that passed the wire gate (requests the gate rejected get
    /// no answer: replying to a flood would only amplify it). Carries the request's sequence number and kind so the client can match it,
    /// what the server decided (accepted or refused, with the reason code) and one number the decision produced: the charge the server
    /// MEASURED for a draw release, or 0.
    /// </summary>
    public struct CombatResultWire : INetworkSerializable
    {
        public uint Sequence;
        public byte Kind;
        public byte Outcome;
        public byte Reason;
        public float Charge;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Sequence);
            serializer.SerializeValue(ref Kind);
            serializer.SerializeValue(ref Outcome);
            serializer.SerializeValue(ref Reason);
            serializer.SerializeValue(ref Charge);
        }

        public CombatOutcome OutcomeKind { get { return Outcome == (byte)CombatOutcome.Accepted ? CombatOutcome.Accepted : CombatOutcome.Refused; } }

        public CombatRefusalCode ReasonCode { get { return Reason <= (byte)CombatRefusalCode.Unsupported ? (CombatRefusalCode)Reason : CombatRefusalCode.Unsupported; } }
    }

    /// <summary>
    /// U3B3, server to clients, reliable: a melee swing began on this player's object. Everything a client needs to draw the same swing
    /// (the weapon and the pattern by id, the combo step and the three phase lengths the AUTHORITY resolved). Presentation only; the hit,
    /// its location and its damage are decided on the server and arrive separately as <see cref="CombatHitWire"/>.
    /// </summary>
    public struct CombatSwingWire : INetworkSerializable
    {
        public string WeaponId;
        public string PatternId;
        public byte ComboStep;
        public float Windup;
        public float Active;
        public float Recovery;

        // U3C: the trajectory the swing COMMITTED to (the victim-visible warning), taken by the server from the authority's swing record.
        // A client derives its warning from these numbers plus the pattern it knows by id; it never computes a trajectory of its own.
        public byte Trajectory;
        public byte Height;
        public byte Committed;
        public float AimYaw;
        public float AimPitch;
        public float MinBladeYaw;
        public float MaxBladeYaw;
        public float MinBladePitch;
        public float MaxBladePitch;
        public float Reach;

        /// <summary>U3C3: 0 for an ordinary swing, else the 1-based number of the riposte strike (presentation: the owner's cue and the observers' swing).</summary>
        public byte Riposte;

        /// <summary>Copies the authority's committed-trajectory warning into the wire fields (server side).</summary>
        public void SetWarning(in BladeWarning warning, bool aimCommitted)
        {
            Trajectory = (byte)warning.Trajectory;
            Height = (byte)warning.Height;
            Committed = aimCommitted ? (byte)1 : (byte)0;
            AimYaw = warning.AimYaw;
            AimPitch = warning.AimPitch;
            MinBladeYaw = warning.MinBladeYaw;
            MaxBladeYaw = warning.MaxBladeYaw;
            MinBladePitch = warning.MinBladePitch;
            MaxBladePitch = warning.MaxBladePitch;
            Reach = warning.Reach;
        }

        public BladeTrajectory TrajectoryKind { get { return Trajectory <= (byte)BladeTrajectory.Thrust ? (BladeTrajectory)Trajectory : BladeTrajectory.None; } }

        public BladeHeight HeightBand { get { return Height <= (byte)BladeHeight.High ? (BladeHeight)Height : BladeHeight.Unspecified; } }

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsWriter)
            {
                if (WeaponId == null) WeaponId = "";
                if (PatternId == null) PatternId = "";
            }

            serializer.SerializeValue(ref WeaponId);
            serializer.SerializeValue(ref PatternId);
            serializer.SerializeValue(ref ComboStep);
            serializer.SerializeValue(ref Windup);
            serializer.SerializeValue(ref Active);
            serializer.SerializeValue(ref Recovery);
            serializer.SerializeValue(ref Trajectory);
            serializer.SerializeValue(ref Height);
            serializer.SerializeValue(ref Committed);
            serializer.SerializeValue(ref AimYaw);
            serializer.SerializeValue(ref AimPitch);
            serializer.SerializeValue(ref MinBladeYaw);
            serializer.SerializeValue(ref MaxBladeYaw);
            serializer.SerializeValue(ref MinBladePitch);
            serializer.SerializeValue(ref MaxBladePitch);
            serializer.SerializeValue(ref Reach);
            serializer.SerializeValue(ref Riposte);
        }
    }

    /// <summary>
    /// U3B3, server to clients, reliable: an arrow or bolt left this player's object. The launch state the AUTHORITY resolved (muzzle,
    /// aim direction, speed, gravity, maximum flight time, range), enough for a client to run D2's fixed-step integrator
    /// (<c>ProjectileBallistics.Advance</c>) and draw the same parabola. The flight's hit, damage and end are the server's.
    /// </summary>
    public struct CombatShotWire : INetworkSerializable
    {
        public uint Serial;
        public string WeaponId;
        public float OriginX, OriginY, OriginZ;
        public float DirX, DirY, DirZ;
        public float Speed;
        public float Gravity;
        public float MaxFlightTime;
        public float Range;
        public float Charge;
        public float Radius;
        public float Length;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            if (serializer.IsWriter && WeaponId == null) WeaponId = "";
            serializer.SerializeValue(ref Serial);
            serializer.SerializeValue(ref WeaponId);
            serializer.SerializeValue(ref OriginX);
            serializer.SerializeValue(ref OriginY);
            serializer.SerializeValue(ref OriginZ);
            serializer.SerializeValue(ref DirX);
            serializer.SerializeValue(ref DirY);
            serializer.SerializeValue(ref DirZ);
            serializer.SerializeValue(ref Speed);
            serializer.SerializeValue(ref Gravity);
            serializer.SerializeValue(ref MaxFlightTime);
            serializer.SerializeValue(ref Range);
            serializer.SerializeValue(ref Charge);
            serializer.SerializeValue(ref Radius);
            serializer.SerializeValue(ref Length);
        }
    }

    /// <summary>U3B3, server to clients, reliable: the flight with this serial ended where it stopped (so a client's replica stops there).</summary>
    public struct CombatFlightEndWire : INetworkSerializable
    {
        public uint Serial;
        public float X, Y, Z;
        public float AirTime;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Serial);
            serializer.SerializeValue(ref X);
            serializer.SerializeValue(ref Y);
            serializer.SerializeValue(ref Z);
            serializer.SerializeValue(ref AirTime);
        }
    }

    /// <summary>
    /// U3B3, server to clients, reliable: a hit the server resolved and applied. The ids are server-issued LABELS (a client only
    /// uses them to find the views and to print); nothing in here is an instruction. Damage is the number the target's health lost.
    /// </summary>
    public struct CombatHitWire : INetworkSerializable
    {
        public ulong EventId;
        public float PointX, PointY, PointZ;
        public byte Source;
        public int Attacker;
        public int Target;
        public bool TargetIsEnemy;
        public float Amount;
        public byte Location;
        public bool Blocked;
        public bool GuardBroken;
        public bool Killed;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref EventId);
            serializer.SerializeValue(ref PointX);
            serializer.SerializeValue(ref PointY);
            serializer.SerializeValue(ref PointZ);
            serializer.SerializeValue(ref Source);
            serializer.SerializeValue(ref Attacker);
            serializer.SerializeValue(ref Target);
            serializer.SerializeValue(ref TargetIsEnemy);
            serializer.SerializeValue(ref Amount);
            serializer.SerializeValue(ref Location);
            serializer.SerializeValue(ref Blocked);
            serializer.SerializeValue(ref GuardBroken);
            serializer.SerializeValue(ref Killed);
        }
    }

    /// <summary>
    /// U3B3: the server's health picture of one player body (see <see cref="CombatVitalsData"/>), the payload of an everyone-read /
    /// server-write NetworkVariable. The IEquatable implementation lets change detection compare values (the SkillStateWire pattern).
    /// </summary>
    public struct CombatVitalsWire : INetworkSerializable, IEquatable<CombatVitalsWire>
    {
        public float Health;
        public float MaxHealth;
        public bool Dead;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Health);
            serializer.SerializeValue(ref MaxHealth);
            serializer.SerializeValue(ref Dead);
        }

        public static CombatVitalsWire From(in CombatVitalsData data)
        {
            return new CombatVitalsWire { Health = data.Health, MaxHealth = data.MaxHealth, Dead = data.Dead };
        }

        /// <summary>The value as pure data (re-sanitized: a NaN or negative number on the wire becomes 0).</summary>
        public CombatVitalsData ToData() { return new CombatVitalsData(Health, MaxHealth, Dead); }

        public bool Equals(CombatVitalsWire other) { return ToData().Equals(other.ToData()); }

        public override bool Equals(object obj) { return obj is CombatVitalsWire other && Equals(other); }

        public override int GetHashCode() { return ToData().GetHashCode(); }
    }

    /// <summary>U3B3, server to a client, unreliable: where one enemy is (a lost message is replaced by the next).</summary>
    public struct EnemyPoseWire : INetworkSerializable
    {
        public int Enemy;
        public float X, Y, Z;
        public float Yaw;

        public bool IsValid => Enemy > 0 && IsFinite(X) && IsFinite(Y) && IsFinite(Z) && IsFinite(Yaw);

        private static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Enemy);
            serializer.SerializeValue(ref X);
            serializer.SerializeValue(ref Y);
            serializer.SerializeValue(ref Z);
            serializer.SerializeValue(ref Yaw);
        }
    }

    /// <summary>U3B3, server to a client, reliable: one enemy's health and death (sent on first sight and on every change).</summary>
    public struct EnemyVitalsWire : INetworkSerializable
    {
        public int Enemy;
        public float Health;
        public float MaxHealth;
        public bool Dead;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Enemy);
            serializer.SerializeValue(ref Health);
            serializer.SerializeValue(ref MaxHealth);
            serializer.SerializeValue(ref Dead);
        }
    }

    /// <summary>Versioned server-authored enemy presentation state. Gameplay decisions and health stay exclusively on the server.</summary>
    public struct EnemyPresentationWire : INetworkSerializable
    {
        public const byte CurrentVersion = 4;
        public const byte WindingUpFlag = 1;
        public const byte DeadFlag = 2;
        public const byte DormantFlag = 4;
        public const byte WakingFlag = 8;
        public const byte KnownFlags = WindingUpFlag | DeadFlag | DormantFlag | WakingFlag;

        public byte Version;
        public int Enemy;
        public byte VisualKind;
        public byte Flags;
        public uint SwingsBegun;
        public uint HitSequence;
        public float WindupSeconds;
        public float SwingSeconds;
        public byte AttackPhase;
        public float PhaseElapsed;
        public float PhaseDuration;
        public float RecoilStart;
        public double SampleTime;
        public float WakeElapsed;

        public bool IsWindingUp => (Flags & WindingUpFlag) != 0;
        public bool IsDormant => (Flags & DormantFlag) != 0;
        public bool IsWaking => (Flags & WakingFlag) != 0;
        public bool IsDead => (Flags & DeadFlag) != 0;
        public string VisualKindName => VisualKind <= 3 ? new[] { "Minion", "Rogue", "Warrior", "Mage" }[VisualKind] : "Unknown";
        public bool IsValid => Version == CurrentVersion && Enemy > 0 && VisualKind <= 3 && (Flags & ~KnownFlags) == 0 &&
                               !((IsDormant || IsWaking) && (IsDead || IsWindingUp || AttackPhase != 0)) && !(IsDormant && IsWaking) &&
                               float.IsFinite(WakeElapsed) && WakeElapsed >= 0f && WakeElapsed <= 5f &&
                               !float.IsNaN(WindupSeconds) && !float.IsInfinity(WindupSeconds) && WindupSeconds >= 0f && WindupSeconds <= 10f &&
                               float.IsFinite(SwingSeconds) && SwingSeconds >= 0f && SwingSeconds <= 30f && AttackPhase <= 4 &&
                               float.IsFinite(PhaseElapsed) && PhaseElapsed >= 0f &&
                               float.IsFinite(PhaseDuration) && PhaseDuration >= 0f && PhaseDuration <= 40f &&
                               float.IsFinite(RecoilStart) && RecoilStart >= 0f && RecoilStart <= 1f &&
                               PhaseElapsed <= PhaseDuration && (AttackPhase == 0 || PhaseDuration >= 0.02f) &&
                               !double.IsNaN(SampleTime) && !double.IsInfinity(SampleTime) && SampleTime >= 0d;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Version);
            serializer.SerializeValue(ref Enemy);
            serializer.SerializeValue(ref VisualKind);
            serializer.SerializeValue(ref Flags);
            serializer.SerializeValue(ref SwingsBegun);
            serializer.SerializeValue(ref HitSequence);
            serializer.SerializeValue(ref WindupSeconds);
            serializer.SerializeValue(ref SwingSeconds);
            serializer.SerializeValue(ref AttackPhase);
            serializer.SerializeValue(ref PhaseElapsed);
            serializer.SerializeValue(ref PhaseDuration);
            serializer.SerializeValue(ref RecoilStart);
            serializer.SerializeValue(ref SampleTime);
            serializer.SerializeValue(ref WakeElapsed);
        }
    }
}
