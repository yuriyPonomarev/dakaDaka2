using System;
using Emberdelve.Flow;
using Emberdelve.Interaction;
using Emberdelve.Player;
using Unity.Netcode;
using UnityEngine;

namespace Emberdelve.Net
{
    public struct DescentPortalWire : INetworkSerializable, IEquatable<DescentPortalWire>
    {
        public byte Floor, Count;
        public uint Generation;
        public ushort Available;
        private Vector3 _p0, _p1, _p2, _p3, _p4, _p5, _p6, _p7, _p8;
        public Vector3 Position(int index) => index switch
        { 0 => _p0, 1 => _p1, 2 => _p2, 3 => _p3, 4 => _p4, 5 => _p5, 6 => _p6, 7 => _p7, 8 => _p8, _ => Vector3.zero };
        public void SetPosition(int index, Vector3 position)
        {
            switch (index)
            {
                case 0: _p0 = position; break; case 1: _p1 = position; break; case 2: _p2 = position; break;
                case 3: _p3 = position; break; case 4: _p4 = position; break; case 5: _p5 = position; break;
                case 6: _p6 = position; break; case 7: _p7 = position; break; case 8: _p8 = position; break;
                default: throw new ArgumentOutOfRangeException(nameof(index));
            }
        }
        public bool IsAvailable(int index) => index >= 0 && index < Count && (Available & (1 << index)) != 0;
        public bool IsValid
        {
            get
            {
                if (Floor > 3 || ((Floor == 0 || Floor == 3) ? Count != 0 : Count != 3 && Count != 6 && Count != 9)
                    || (Available >> Count) != 0) return false;
                for (int i = 0; i < Count; i++)
                {
                    var p = Position(i);
                    if (!float.IsFinite(p.x) || !float.IsFinite(p.y) || !float.IsFinite(p.z)
                        || Mathf.Abs(p.x) > 100000 || Mathf.Abs(p.y) > 10000 || Mathf.Abs(p.z) > 100000) return false;
                }
                return true;
            }
        }
        public static DescentPortalWire Capture(RunDirector director, PlayerRig actor)
        {
            var session = director?.SessionOf(actor);
            if (session == null || actor == null || !director.RoyaleMode.HasValue) return default;
            var encounter = director.FloorCoordinator.EncounterFor(session.Floor);
            if (encounter == null) return default;
            int size = (int)director.RoyaleMode.Value;
            var state = new DescentPortalWire { Floor = (byte)session.Floor, Generation = actor.FloorMovementGeneration,
                Count = (byte)(encounter.Groups.Count * size) };
            bool open = director.IsRoyaleFloorOpen(session.Floor) && actor.IsInPlay && !actor.IsDead;
            foreach (var group in encounter.Groups)
                foreach (var slot in group.GetComponentsInChildren<DescentPortalSlot>(true))
                {
                    int index = group.Group * size + slot.Slot;
                    state.SetPosition(index, slot.transform.position);
                    if (open && group.gameObject.activeSelf && !group.SlotUsed(slot.Slot)) state.Available |= (ushort)(1 << index);
                }
            return state;
        }
        public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
        {
            s.SerializeValue(ref Floor); s.SerializeValue(ref Count); s.SerializeValue(ref Generation); s.SerializeValue(ref Available);
            s.SerializeValue(ref _p0); s.SerializeValue(ref _p1); s.SerializeValue(ref _p2); s.SerializeValue(ref _p3);
            s.SerializeValue(ref _p4); s.SerializeValue(ref _p5); s.SerializeValue(ref _p6); s.SerializeValue(ref _p7); s.SerializeValue(ref _p8);
        }
        public bool Equals(DescentPortalWire other)
        {
            if (Floor != other.Floor || Count != other.Count || Generation != other.Generation || Available != other.Available) return false;
            for (int i = 0; i < Count; i++) if (Position(i) != other.Position(i)) return false;
            return true;
        }
        public override bool Equals(object obj) => obj is DescentPortalWire other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Floor, Count, Generation, Available);
    }
}
