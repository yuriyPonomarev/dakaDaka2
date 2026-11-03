using System.Collections.Generic;
using System.Globalization;
using Emberdelve.Net.Rules;
using UnityEngine;

namespace Emberdelve.Net
{
    /// <summary>
    /// U3B3, client process: the minimal presence of enemies. Client processes have no enemies (the scene's enemy spawner is a server system and
    /// NetSceneRoles switches it off for clients), so the server sends each client snapshots through its own player object (NetPlayerRig
    /// EnemyPoseRpc, unreliable, a few times a second; EnemyVitalsRpc, reliable, on change) and this class keeps one PROXY per enemy: a capsule
    /// that eases to the last position and disappears when the server reports the enemy dead. A proxy has no collider, no brain and no health
    /// component; it only shows. SPLIT (documented): enemies are not NetworkObjects yet, so they have no animation, no hit reactions and no
    /// replication outside the owner's own snapshot channel.
    ///
    ///   [client] enemy-view id=.. health=.. max=.. dead=..       when an enemy's reported vitals change
    ///   [client] enemy-view summary total=.. alive=.. dead=.. poses=..   at most every 2 s while enemies are known
    /// </summary>
    public static class NetEnemyViews
    {
        private sealed class Entry
        {
            public NetEnemyProxy Proxy;
            public float Health;
            public float MaxHealth;
            public bool Dead;
            public bool HasVitals;
        }

        private static readonly Dictionary<int, Entry> Entries = new Dictionary<int, Entry>();
        private static int _poses;
        private static float _nextSummary;

        public static int Total => Entries.Count;
        public static int PoseCount => _poses;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics() { Clear(); }

        public static int AliveCount
        {
            get
            {
                int alive = 0;
                foreach (KeyValuePair<int, Entry> pair in Entries) if (!pair.Value.Dead) alive++;
                return alive;
            }
        }

        public static void Clear()
        {
            foreach (KeyValuePair<int, Entry> pair in Entries)
                if (pair.Value.Proxy != null) Object.Destroy(pair.Value.Proxy.gameObject);
            Entries.Clear();
            _poses = 0;
        }

        private static Entry Get(int id)
        {
            if (!Entries.TryGetValue(id, out Entry entry))
            {
                entry = new Entry();
                Entries[id] = entry;
            }

            return entry;
        }

        public static void OnPose(in EnemyPoseWire pose)
        {
            if (float.IsNaN(pose.X) || float.IsNaN(pose.Y) || float.IsNaN(pose.Z)) return;
            Entry entry = Get(pose.Enemy);
            if (entry.Dead) return;
            _poses++;

            Vector3 where = new Vector3(pose.X, pose.Y, pose.Z);
            if (entry.Proxy == null)
            {
                GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "NetEnemyProxy_" + pose.Enemy;
                Collider collider = body.GetComponent<Collider>();
                if (collider != null) Object.Destroy(collider);
                entry.Proxy = body.AddComponent<NetEnemyProxy>();
                entry.Proxy.Snap(where, pose.Yaw);
            }
            else
            {
                entry.Proxy.SetTarget(where, pose.Yaw);
            }
        }

        public static void OnVitals(in EnemyVitalsWire vitals)
        {
            Entry entry = Get(vitals.Enemy);
            bool changed = !entry.HasVitals || entry.Dead != vitals.Dead || !Mathf.Approximately(entry.Health, vitals.Health) ||
                           !Mathf.Approximately(entry.MaxHealth, vitals.MaxHealth);
            entry.HasVitals = true;
            entry.Health = vitals.Health;
            entry.MaxHealth = vitals.MaxHealth;
            if (vitals.Dead && !entry.Dead && entry.Proxy != null) Object.Destroy(entry.Proxy.gameObject);
            entry.Dead = vitals.Dead;

            if (changed)
            {
                Debug.Log(NetTuning.LogPrefix + "[client] enemy-view id=" + vitals.Enemy + " health=" + F(vitals.Health) +
                          " max=" + F(vitals.MaxHealth) + " dead=" + vitals.Dead);
            }
        }

        /// <summary>Called from the owner's NetOwnerCombatView.Update: at most one summary line every 2 s while enemies are known.</summary>
        public static void TickSummary(float unscaledNow)
        {
            if (Entries.Count == 0 || unscaledNow < _nextSummary) return;
            _nextSummary = unscaledNow + 2f;
            int alive = AliveCount;
            Debug.Log(NetTuning.LogPrefix + "[client] enemy-view summary total=" + Entries.Count + " alive=" + alive + " dead=" + (Entries.Count - alive) + " poses=" + _poses);
        }

        private static string F(float value) { return value.ToString("F1", CultureInfo.InvariantCulture); }
    }

    /// <summary>U3B3, client: the capsule that stands for one enemy. Eases to the last position the server reported.</summary>
    public sealed class NetEnemyProxy : MonoBehaviour
    {
        private const float Ease = 10f;
        private Vector3 _target;
        private float _yaw;

        public void Snap(Vector3 where, float yaw)
        {
            _target = where;
            _yaw = yaw;
            transform.position = where;
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        }

        public void SetTarget(Vector3 where, float yaw)
        {
            _target = where;
            _yaw = yaw;
        }

        private void Update()
        {
            float t = 1f - Mathf.Exp(-Ease * Time.deltaTime);
            transform.position = Vector3.Lerp(transform.position, _target, t);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, _yaw, 0f), t);
        }
    }
}
