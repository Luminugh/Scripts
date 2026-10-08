using UnityEngine;
using UnityEngine.SceneManagement;
using FinDeMes.Vehicles;

/// <summary>Provides inexpensive grounded arcade driving, independent of input and imported visuals.</summary>
[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public sealed class ArcadeVehicle : MonoBehaviour
{
    /// <summary>References the authoritative vehicle attributes; legacy tuning remains active when unset.</summary>
    public VehicleGameplayDefinition GameplayDefinition;
    /// <summary>Controls forward acceleration in metres per second squared.</summary>
    public float Acceleration = 5.5f;
    /// <summary>Controls reverse acceleration.</summary>
    public float ReverseAcceleration = 3f;
    /// <summary>Limits forward speed in metres per second.</summary>
    public float MaxForwardSpeed = 14f;
    /// <summary>Limits reverse speed.</summary>
    public float MaxReverseSpeed = 5f;
    /// <summary>Controls braking deceleration.</summary>
    public float Braking = 12f;
    /// <summary>Defines maximum steering angle in degrees.</summary>
    public float SteeringAngle = 32f;
    /// <summary>Defines approximate axle separation in metres.</summary>
    public float Wheelbase = 2.1f;
    /// <summary>Controls steering interpolation speed.</summary>
    public float SteeringResponse = 8f;
    /// <summary>Controls lateral grip.</summary>
    public float LateralGrip = 9f;
    /// <summary>Defines downward ground probe length.</summary>
    public float GroundDistance = .45f;
    /// <summary>Defines probe half-width, origin height and half-length for interchangeable vehicle bodies.</summary>
    public Vector3 GroundProbeExtents = new Vector3(.65f,.2f,.95f);
    [Header("Small Curb Assist")]
    [Tooltip("Allows the arcade body to pass shallow curbs without climbing tall obstacles.")]
    public bool CurbAssistEnabled = true;
    [Range(.05f, .35f)] public float MaxStepHeight = .22f;
    [Min(.2f)] public float StepProbeDistance = 1.3f;
    [Range(.1f, 12f)] public float StepAssistStrength = 8f;
    [Range(.05f, 1f)] public float GroundProbeDistance = .45f;
    /// <summary>References independently editable visual wheels.</summary>
    public Transform[] Wheels = new Transform[0];
    /// <summary>Marks steering wheels in the corresponding wheel array.</summary>
    public bool[] SteerWheels = new bool[0];
    /// <summary>Defines visual wheel radius.</summary>
    public float WheelRadius = .28f;
    /// <summary>Exposes the current grounded state.</summary>
    public bool IsGrounded { get; private set; }
    /// <summary>Exposes signed forward speed.</summary>
    public float Speed => Vector3.Dot(Body.linearVelocity, transform.forward);
    /// <summary>Exposes the physical body to development tools.</summary>
    public Rigidbody Body { get { if (!body) body = GetComponent<Rigidbody>(); return body; } }
    Rigidbody body;
    Vector2 command;
    bool brake;
    float steer, spin;
    Vector3 resetPosition;
    Quaternion resetRotation;
    Vector3 lastSafePosition;
    Quaternion lastSafeRotation;
    float stuckTimer;
    Quaternion[] wheelRotations;

    void Awake()
    {
        ApplyGameplayDefinition();
        SetResetPose(transform.position, transform.rotation);
        lastSafePosition = transform.position;
        lastSafeRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
        wheelRotations = new Quaternion[Wheels.Length];
        for (int i = 0; i < Wheels.Length; i++) if (Wheels[i]) wheelRotations[i] = Wheels[i].localRotation;
    }

    /// <summary>Accepts steering and throttle in [-1,1] and an independent brake command.</summary>
    public void SetInput(Vector2 steeringThrottle, bool strongBrake)
    { command = Vector2.ClampMagnitude(steeringThrottle, 1.414214f); command.x = Mathf.Clamp(command.x,-1,1); command.y = Mathf.Clamp(command.y,-1,1); brake = strongBrake; }

    void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        Step(dt);
        if (IsGrounded && Mathf.Abs(Speed) > 1.25f)
        {
            lastSafePosition = transform.position;
            lastSafeRotation = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            stuckTimer = 0f;
            return;
        }
        if (IsGrounded && command.y > .8f && Mathf.Abs(Speed) < .35f)
        {
            stuckTimer += dt;
            float delay = GameplayDefinition ? GameplayDefinition.StuckRecoverySeconds : 5f;
            if (stuckTimer >= Mathf.Max(1f, delay)) RecoverFromStuck();
        }
        else stuckTimer = 0f;
    }

    void ApplyGameplayDefinition()
    {
        if (!GameplayDefinition) return;
        MaxForwardSpeed = GameplayDefinition.MaxForwardSpeedMetersPerSecond;
        Acceleration = GameplayDefinition.AccelerationMetersPerSecondSquared;
        SteeringAngle = GameplayDefinition.HandlingDegrees;
    }

    /// <summary>Restores the most recent grounded moving pose after sustained throttle with no progress.</summary>
    public bool RecoverFromStuck()
    {
        if (!Body || !IsGrounded) return false;
        Body.position = lastSafePosition + Vector3.up * .12f;
        Body.rotation = lastSafeRotation;
        Body.linearVelocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;
        command = Vector2.zero;
        brake = false;
        steer = 0f;
        stuckTimer = 0f;
        return true;
    }

    /// <summary>Advances forces once; also supports isolated Editor physics-scene validation.</summary>
    public void Step(float dt)
    {
        var rb = Body;
        var physics = gameObject.scene.GetPhysicsScene();
        IsGrounded = false;
        Vector3 normal = Vector3.zero;
        for (int i = 0; i < 4; i++)
        {
            Vector3 origin = transform.TransformPoint(new Vector3(i % 2 == 0 ? -GroundProbeExtents.x : GroundProbeExtents.x, GroundProbeExtents.y, i < 2 ? -GroundProbeExtents.z : GroundProbeExtents.z));
            if (physics.Raycast(origin, Vector3.down, out RaycastHit hit, Mathf.Max(GroundDistance, GroundProbeDistance), ~0, QueryTriggerInteraction.Ignore) && hit.normal.y > .6f)
            { IsGrounded = true; normal += hit.normal; }
        }
        steer = Mathf.MoveTowards(steer, command.x, SteeringResponse * dt);
        if (!IsGrounded) return;
        normal.Normalize();
        Vector3 forward = Vector3.ProjectOnPlane(transform.forward, normal).normalized;
        float speed = Vector3.Dot(rb.linearVelocity, forward);
        ApplySmallStepAssist(physics, forward, normal, speed, dt);
        float lateral = Vector3.Dot(rb.linearVelocity, transform.right);
        rb.AddForce(-transform.right * lateral * Mathf.Min(LateralGrip, 1f / dt), ForceMode.Acceleration);
        bool opposing = command.y * speed < -.2f;
        float deceleration = brake || opposing ? Braking : .6f;
        if (brake || opposing || Mathf.Abs(command.y) < .01f)
            rb.AddForce(-forward * Mathf.Sign(speed) * Mathf.Min(Mathf.Abs(speed) / dt, deceleration), ForceMode.Acceleration);
        else if ((command.y > 0 && speed < MaxForwardSpeed) || (command.y < 0 && speed > -MaxReverseSpeed))
            rb.AddForce(forward * command.y * (command.y > 0 ? Acceleration : ReverseAcceleration), ForceMode.Acceleration);
        float yaw = Mathf.Clamp(speed, -8f, 8f) / Mathf.Max(.5f, Wheelbase) * Mathf.Tan(steer * SteeringAngle * Mathf.Deg2Rad);
        rb.angularVelocity = new Vector3(0, Mathf.Lerp(rb.angularVelocity.y, yaw, 1 - Mathf.Exp(-SteeringResponse * dt)), 0);
        // Clamp only longitudinal speed, preserving gravity and collision response on other axes.
        if (speed > MaxForwardSpeed) rb.linearVelocity -= forward * (speed - MaxForwardSpeed);
        if (speed < -MaxReverseSpeed) rb.linearVelocity -= forward * (speed + MaxReverseSpeed);
    }

    /// <summary>Applies bounded upward assistance when the vehicle meets a shallow, traversable curb.</summary>
    void ApplySmallStepAssist(PhysicsScene physics, Vector3 forward, Vector3 groundNormal, float speed, float dt)
    {
        if (!CurbAssistEnabled || !IsGrounded || Mathf.Abs(command.y) < .1f || Mathf.Abs(speed) < .15f) return;
        float direction = Mathf.Sign(command.y);
        Vector3 travel = forward * direction;
        float currentGround = transform.position.y - GroundProbeExtents.y;
        Vector3 baseOrigin = transform.TransformPoint(new Vector3(0, GroundProbeExtents.y, GroundProbeExtents.z * direction));
        baseOrigin.y = currentGround + .03f;
        if (!physics.Raycast(baseOrigin, travel, out RaycastHit lowHit, StepProbeDistance, ~0, QueryTriggerInteraction.Ignore)) return;
        if (lowHit.collider && lowHit.collider.transform.IsChildOf(transform)) return;
        if (lowHit.normal.y > .7f) return;

        // Measure the obstacle's top, not the horizontal face hit point. Measuring the
        // side hit made stepHeight equal the probe's arbitrary height, so the assist
        // silently rejected ordinary curbs.
        Vector3 topProbe = lowHit.point + travel * .06f + Vector3.up * (MaxStepHeight + .08f);
        if (!physics.Raycast(topProbe, Vector3.down, out RaycastHit topHit,
                MaxStepHeight + .16f, ~0, QueryTriggerInteraction.Ignore) || topHit.normal.y < .7f) return;
        float stepHeight = topHit.point.y - currentGround;
        if (stepHeight < -.05f || stepHeight > MaxStepHeight) return;

        // A second horizontal probe rejects walls taller than the supported step.
        Vector3 clearanceOrigin = baseOrigin + Vector3.up * (MaxStepHeight + .06f);
        if (physics.Raycast(clearanceOrigin, travel, StepProbeDistance, ~0, QueryTriggerInteraction.Ignore)) return;

        // Lift only while advancing into a measured shallow step, and cap upward speed
        // so the assist cannot launch the vehicle or bypass a tall obstacle.
        if (Body.linearVelocity.y < 1.1f)
        {
            float lift = Mathf.Clamp(stepHeight / Mathf.Max(.01f, MaxStepHeight), .25f, 1f);
            Body.AddForce(Vector3.up * StepAssistStrength * lift, ForceMode.Acceleration);
        }
        Body.linearVelocity = Vector3.ClampMagnitude(Body.linearVelocity, Mathf.Max(MaxForwardSpeed, MaxReverseSpeed) * 1.15f);
    }

    void LateUpdate()
    {
        spin += Speed / Mathf.Max(.05f, WheelRadius) * Mathf.Rad2Deg * Time.deltaTime;
        for (int i = 0; i < Wheels.Length; i++)
            if (Wheels[i] && wheelRotations != null && i < wheelRotations.Length)
                Wheels[i].localRotation = Quaternion.AngleAxis(i < SteerWheels.Length && SteerWheels[i] ? steer * SteeringAngle : 0, Vector3.up) * wheelRotations[i] * Quaternion.AngleAxis(spin, Vector3.right);
    }

    /// <summary>Updates the safe test spawn used by reset.</summary>
    public void SetResetPose(Vector3 position, Quaternion rotation)
    { resetPosition = position; resetRotation = Quaternion.Euler(0, rotation.eulerAngles.y, 0); }

    /// <summary>Restores the selected safe spawn and clears velocity and input.</summary>
    [ContextMenu("Reset to test spawn")]
    public void ResetVehicle()
    {
        Body.position = resetPosition; Body.rotation = resetRotation;
        Body.linearVelocity = Vector3.zero; Body.angularVelocity = Vector3.zero;
        command = Vector2.zero; brake = false; steer = 0;
    }
}
