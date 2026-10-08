using UnityEngine;

namespace FinDeMes.PlayerFoundation
{
    /// <summary>Moves a capsule in world space without owning persistent game state or animation.</summary>
    /// <remarks>The <see cref="PlayerAnimationController"/> is the sole driver of the visual Animator; this component only integrates physics motion.</remarks>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        /// <summary>Central configuration; its movement values are applied on Awake when assigned.</summary>
        public PlayerConfig Config;
        /// <summary>Configures relaxed city walking and running in meters per second.</summary>
        public float WalkSpeed = 2.1f, RunSpeed = 4.4f;
        /// <summary>Configures acceleration, turning and downward acceleration.</summary>
        public float Acceleration = 12f, TurnResponse = 14f, Gravity = 22f;
        /// <summary>Gets measured horizontal speed, including collision response.</summary>
        public float Speed { get; private set; }
        /// <summary>Gets the movement collision capsule.</summary>
        public CharacterController Capsule { get; private set; }
        Vector3 horizontal;
        float vertical;
        void Awake()
        {
            Capsule = GetComponent<CharacterController>();
            if (Config)
            {
                WalkSpeed = Config.WalkSpeed; RunSpeed = Config.RunSpeed;
                Acceleration = Config.Acceleration; TurnResponse = Config.TurnResponse; Gravity = Config.Gravity;
            }
        }
        /// <summary>Advances the collision controller with a world-space requested direction.</summary>
        /// <param name="direction">The normalized or analog world direction.</param>
        /// <param name="running">Whether moderate running is requested.</param>
        /// <param name="dt">The simulation delta in seconds.</param>
        public void Tick(Vector3 direction, bool running, float dt)
        {
            if (!Capsule || !Capsule.enabled || dt <= 0) return;
            direction.y = 0;
            direction = Vector3.ClampMagnitude(direction, 1);
            horizontal = Vector3.MoveTowards(horizontal, direction * (running ? RunSpeed : WalkSpeed), Acceleration * dt);
            if (direction.sqrMagnitude > .001f)
                transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(direction), 1 - Mathf.Exp(-TurnResponse * dt));
            if (Capsule.isGrounded && vertical < 0) vertical = -2f;
            vertical = Mathf.Max(vertical - Gravity * dt, -40f);
            var before = transform.position;
            var flags = Capsule.Move((horizontal + Vector3.up * vertical) * dt);
            if ((flags & CollisionFlags.Above) != 0 && vertical > 0) vertical = 0;
            Speed = Vector3.ProjectOnPlane(transform.position - before, Vector3.up).magnitude / dt;
        }
        /// <summary>Performs an explicit doorway transition and clears residual velocity.</summary>
        /// <param name="position">The destination feet position.</param>
        /// <param name="rotation">The destination facing rotation.</param>
        public void Relocate(Vector3 position, Quaternion rotation)
        {
            if (!Capsule) Capsule = GetComponent<CharacterController>();
            Capsule.enabled = false;
            transform.SetPositionAndRotation(position, rotation);
            horizontal = Vector3.zero; vertical = 0; Speed = 0;
            Capsule.enabled = true;
            Physics.SyncTransforms();
        }
    }
}
