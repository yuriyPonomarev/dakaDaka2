using System;
using UnityEngine;
using Emberdelve.Combat;

namespace Emberdelve.Feedback
{
    /// <summary>
    /// B2: the local player's wall-stop feedback. When the authority reports that a swing hit level geometry, one small
    /// spark bursts at the authority's contact point and the camera takes a short visual kick (<see cref="CameraKick"/>).
    /// Presentation only: nothing here feeds combat, aim or damage. The sparks are a fixed pool created up front, so
    /// playing and updating them allocates nothing.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WallStopFeedback : MonoBehaviour
    {
        public const int PoolSize = 6;
        public const int ShardsPerSpark = 4;
        public const float SparkSeconds = 0.28f;
        public const float KickPitchDegrees = 1.1f;
        public static readonly Vector3 KickOffset = new Vector3(0f, 0f, -0.03f);

        private const float ShardSize = 0.035f;
        private const float ShardSpeed = 2.2f;

        /// <summary>Raised once per spark that is played, with the world point it was played at (tools and gates listen).</summary>
        public static event Action<Vector3> SparkSpawned;

        private sealed class Spark
        {
            public Transform Root;
            public readonly Transform[] Shards = new Transform[ShardsPerSpark];
            public readonly Vector3[] Directions = new Vector3[ShardsPerSpark];
            public float Age;
            public bool Active;
        }

        private static Material _sharedMaterial;

        private Spark[] _pool;
        private int _next;
        private CameraKick _kick;
        private Transform _aim;

        public CameraKick Kick => _kick;
        public int SparksPlayed { get; private set; }
        public int KicksPlayed { get; private set; }

        public int ActiveSparkCount
        {
            get
            {
                int count = 0;
                if (_pool != null) for (int i = 0; i < _pool.Length; i++) if (_pool[i].Active) count++;
                return count;
            }
        }

        /// <summary>Builds the spark pool and attaches the kick to the transform the player looks through.</summary>
        public void Configure(Transform aimTransform)
        {
            _aim = aimTransform;
            if (aimTransform != null)
            {
                _kick = aimTransform.GetComponent<CameraKick>();
                if (_kick == null) _kick = aimTransform.gameObject.AddComponent<CameraKick>();
            }

            if (_pool == null) BuildPool();
        }

        /// <summary>Plays the spark at the status' wall point and the camera kick. Call once per wall-stopped swing.</summary>
        public void Play(MeleeSwingStatus status)
        {
            if (_pool == null) BuildPool();

            if (status.HasWallPoint)
            {
                Vector3 normal = status.WallNormal.sqrMagnitude > 1e-6f
                    ? status.WallNormal.normalized
                    : (_aim != null ? -_aim.forward : Vector3.back);
                SpawnSpark(status.WallPoint, normal);
            }

            if (_kick != null)
            {
                _kick.Impulse(KickPitchDegrees, KickOffset);
                KicksPlayed++;
            }
        }

        private void SpawnSpark(Vector3 point, Vector3 normal)
        {
            Spark spark = _pool[_next];
            _next = (_next + 1) % _pool.Length;

            spark.Age = 0f;
            spark.Active = true;
            spark.Root.position = point;
            spark.Root.gameObject.SetActive(true);
            for (int i = 0; i < ShardsPerSpark; i++)
            {
                Vector3 direction = normal + UnityEngine.Random.insideUnitSphere * 0.8f;
                if (direction.sqrMagnitude < 1e-4f) direction = normal;
                spark.Directions[i] = direction.normalized;
                spark.Shards[i].localPosition = Vector3.zero;
                spark.Shards[i].localScale = Vector3.one * ShardSize;
            }

            SparksPlayed++;
            SparkSpawned?.Invoke(point);
        }

        private void Update()
        {
            if (_pool == null) return;

            float dt = Time.unscaledDeltaTime;
            for (int s = 0; s < _pool.Length; s++)
            {
                Spark spark = _pool[s];
                if (!spark.Active) continue;

                spark.Age += dt;
                float t = Mathf.Clamp01(spark.Age / SparkSeconds);
                if (t >= 1f)
                {
                    spark.Active = false;
                    spark.Root.gameObject.SetActive(false);
                    continue;
                }

                float scale = ShardSize * (1f - t);
                for (int i = 0; i < ShardsPerSpark; i++)
                {
                    Vector3 p = spark.Directions[i] * (ShardSpeed * spark.Age);
                    p.y -= 4.9f * spark.Age * spark.Age;
                    spark.Shards[i].localPosition = p;
                    spark.Shards[i].localScale = new Vector3(scale, scale, scale);
                }
            }
        }

        private void BuildPool()
        {
            _pool = new Spark[PoolSize];
            int ignoreLayer = LayerMask.NameToLayer("Ignore Raycast");
            Material material = SharedMaterial();

            for (int s = 0; s < PoolSize; s++)
            {
                Spark spark = new Spark();
                GameObject root = new GameObject("WallStopSpark");
                root.transform.SetParent(transform, false);
                spark.Root = root.transform;
                if (ignoreLayer >= 0) root.layer = ignoreLayer;

                for (int i = 0; i < ShardsPerSpark; i++)
                {
                    GameObject shard = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    shard.name = "Shard";
                    Collider collider = shard.GetComponent<Collider>();
                    if (collider != null) Destroy(collider);
                    if (ignoreLayer >= 0) shard.layer = ignoreLayer;
                    shard.transform.SetParent(root.transform, false);
                    Renderer renderer = shard.GetComponent<Renderer>();
                    if (renderer != null && material != null)
                    {
                        renderer.sharedMaterial = material;
                        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        renderer.receiveShadows = false;
                    }

                    spark.Shards[i] = shard.transform;
                }

                root.SetActive(false);
                _pool[s] = spark;
            }
        }

        private static Material SharedMaterial()
        {
            if (_sharedMaterial != null) return _sharedMaterial;

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) return null;

            Color color = new Color(1f, 0.82f, 0.38f, 1f);
            _sharedMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (_sharedMaterial.HasProperty("_BaseColor")) _sharedMaterial.SetColor("_BaseColor", color);
            if (_sharedMaterial.HasProperty("_Color")) _sharedMaterial.SetColor("_Color", color);
            return _sharedMaterial;
        }

        private void OnDestroy()
        {
            if (_pool == null) return;
            for (int s = 0; s < _pool.Length; s++) if (_pool[s].Root != null) Destroy(_pool[s].Root.gameObject);
        }
    }
}
