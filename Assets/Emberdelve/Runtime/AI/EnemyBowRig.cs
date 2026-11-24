using UnityEngine;
using Emberdelve.Data;
using Emberdelve.Player;

namespace Emberdelve.AI
{
    /// <summary>
    /// Presentation only: the skeleton archer's bow. On Awake it builds the procedural recurve bow (<see cref="BowPresentation"/>, the
    /// same carved bow the player holds) under the left hand bone at the grip pose the archer clip was authored for (EmberdelveEnemyArcherClip
    /// solves the right hand onto this bow's nock), and <see cref="SetDraw"/> bends it with the string pulled. The server's visual driver and
    /// the client proxy drive it from the same presentation clock; the arrow itself is the authority's projectile.
    /// </summary>
    public sealed class EnemyBowRig : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private WeaponData bow;
        [SerializeField] private Vector3 gripLocalPosition;
        [SerializeField] private Quaternion gripLocalRotation = Quaternion.identity;
        [SerializeField] private float bowScale = BowScale;
        [SerializeField] private Quaternion carryLocalRotation = Quaternion.identity;
        private float _raise = -1f;

        /// <summary>Scale of the first-person bow on the skeleton: a 1.4 m longbow whose full draw reaches the archer's cheek.</summary>
        public const float BowScale = 1.3f;

        private BowPresentation _bow;
        public BowPresentation Bow => _bow;

        public void Configure(Animator target, WeaponData weapon, Vector3 localPosition, Quaternion localRotation, Quaternion carryRotation)
        {
            animator = target;
            bow = weapon;
            gripLocalPosition = localPosition;
            gripLocalRotation = localRotation;
            carryLocalRotation = carryRotation;
            bowScale = BowScale;
        }

        /// <summary>Local rotation of the bow on the hand at raise 0 (carried upright at the side) .. 1 (the shooting grip the clip was solved for).</summary>
        public static Quaternion BlendRotation(Quaternion carry, Quaternion grip, float raise) => Quaternion.Slerp(carry, grip, Mathf.Clamp01(raise));

        /// <summary>Turns the bow between its carry pose and the shooting grip (0..1), following the bow clip (EnemyAttackPhase.ArcherBowRaise).</summary>
        public void SetRaise(float raise)
        {
            if (_bow == null && EnsureBuilt() == null) return;
            raise = Mathf.Clamp01(raise);
            if (Mathf.Abs(raise - _raise) < 0.002f) return;
            _raise = raise;
            _bow.transform.localRotation = BlendRotation(carryLocalRotation, gripLocalRotation, raise);
        }

        private void Awake() => EnsureBuilt();

        /// <summary>Builds the bow under the left hand once (also callable from editor tooling, where Awake does not run).</summary>
        public BowPresentation EnsureBuilt()
        {
            if (_bow != null) return _bow;
            if (animator == null) animator = GetComponentInChildren<Animator>();
            Transform hand = animator != null && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.LeftHand) : null;
            if (hand == null || bow == null) return null;
            _bow = BowPresentation.Build(hand, bow);
            Transform t = _bow.transform;
            t.localPosition = gripLocalPosition;
            t.localRotation = carryLocalRotation;
            _raise = 0f;
            // The hand bone may carry scale; size the bow in world terms.
            Vector3 lossy = hand.lossyScale;
            float handScale = Mathf.Max(1e-4f, (Mathf.Abs(lossy.x) + Mathf.Abs(lossy.y) + Mathf.Abs(lossy.z)) / 3f);
            t.localScale = Vector3.one * (bowScale / handScale);
            return _bow;
        }

        /// <summary>Bends the bow and pulls the string (0 = slack, 1 = full draw); the nocked arrow shows only while drawn.</summary>
        public void SetDraw(float draw)
        {
            if (_bow == null && EnsureBuilt() == null) return;
            if (Mathf.Abs(_bow.Draw - Mathf.Clamp01(draw)) > 0.002f) _bow.SetDraw(draw);
        }
    }
}
