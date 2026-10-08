using FinDeMes.Management;
using FinDeMes.Core.Audio;
using FinDeMes.PlayerFoundation;
using UnityEngine;
using UnityEngine.AI;

namespace FinDeMes.Polleria
{
    /// <summary>Shared physical presentation for a Management-owned Polleria employee.</summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(NavMeshAgent))]
    public sealed class PolleriaEmployeeActor : MonoBehaviour
    {
        public string EmployeeId { get; private set; }
        public EmployeeRole Role { get; private set; }
        public NavMeshAgent Agent { get; private set; }
        public Transform CarrySocket { get; private set; }
        public GameObject CarriedPlate { get; private set; }
        public bool HasValidPath { get; private set; }
        public Vector3 LastValidNavMeshPosition { get; private set; }
        public Vector3 RequestedDestination => lastDestination;
        public bool HasRequestedDestination => hasDestination;
        public bool HasReachedDestination
        {
            get
            {
                if (!Agent || !Agent.enabled || !Agent.isOnNavMesh || !hasDestination || !HasValidPath || Agent.pathPending)
                    return false;

                float reachDistance = Agent.stoppingDistance + .12f;
                if (!Agent.hasPath)
                    return (Agent.nextPosition - lastDestination).sqrMagnitude <= reachDistance * reachDistance;

                return Agent.pathStatus == NavMeshPathStatus.PathComplete
                    && Agent.remainingDistance <= reachDistance;
            }
        }
        Animator animator;
        ProceduralHumanoidLocomotion locomotion;
        NavMeshPath path;
        Vector3 lastDestination;
        bool hasDestination;

        public bool Bind(EmployeeReadModel employee, GameObject visualPrefab, Vector3 spawnPosition, Quaternion spawnRotation,
            RuntimeAnimatorController locomotionController = null)
        {
            if (employee == null || !employee.HasEmployee) return false;
            EmployeeId = employee.EmployeeId;
            Role = employee.Role;
            transform.SetPositionAndRotation(spawnPosition, spawnRotation);
            Agent = GetComponent<NavMeshAgent>();
            if (!Agent) Agent = gameObject.AddComponent<NavMeshAgent>();
            if (!Agent)
            {
                Debug.LogError("Polleria employee actor could not acquire its required NavMeshAgent.", this);
                return false;
            }
            var footsteps = GetComponent<FootstepAudioEmitter>() ?? gameObject.AddComponent<FootstepAudioEmitter>();
            footsteps.SetSurfaceHint(FootstepSurface.Tile);
            Agent.radius = .28f;
            Agent.height = 1.8f;
            Agent.stoppingDistance = .2f;
            Agent.autoBraking = true;
            Agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            if (!Agent.isOnNavMesh && NavMesh.SamplePosition(spawnPosition, out NavMeshHit spawn, 2f, NavMesh.AllAreas)) Agent.Warp(spawn.position);
            if (Agent.isOnNavMesh) LastValidNavMeshPosition = Agent.nextPosition;

            if (visualPrefab)
            {
                GameObject visual = Instantiate(visualPrefab, transform);
                visual.name = "EmployeeVisual";
                animator = visual.GetComponentInChildren<Animator>(true);
            }
            // Civilian visuals carry a valid Humanoid avatar but no controller. Bind the
            // project-owned locomotion controller so employees have a real idle pose.
            if (animator && locomotionController)
                animator.runtimeAnimatorController = locomotionController;
            // The visual NPC controller contains root-motion clips. The NavMeshAgent owns
            // the actor root; allowing the Animator to apply those deltas moves the visual
            // child independently and makes the employee appear to run away from its agent.
            if (animator) animator.applyRootMotion = false;
            if (animator && animator.isHuman && animator.avatar && animator.avatar.isValid && !animator.runtimeAnimatorController)
            {
                locomotion = animator.GetComponent<ProceduralHumanoidLocomotion>()
                    ?? animator.gameObject.AddComponent<ProceduralHumanoidLocomotion>();
            }
            BuildCarrySocket();
            path = new NavMeshPath();
            return Agent.isOnNavMesh;
        }

        void BuildCarrySocket()
        {
            Transform hand = animator && animator.isHuman ? animator.GetBoneTransform(HumanBodyBones.RightHand) : null;
            var socket = new GameObject("CarrySocket").transform;
            CarrySocket = socket;
            if (hand)
            {
                socket.SetParent(hand, false);
                socket.localPosition = new Vector3(0f, .04f, .11f);
                socket.localRotation = Quaternion.identity;
            }
            else
            {
                socket.SetParent(transform, false);
                socket.localPosition = new Vector3(.22f, 1.02f, .12f);
                socket.localRotation = Quaternion.identity;
            }
        }

        public bool MoveTo(Vector3 destination)
        {
            if (!Agent || !Agent.enabled || !Agent.isOnNavMesh) { HasValidPath = false; return false; }
            if (!IsFinite(destination))
            {
                HasValidPath = false;
                Agent.ResetPath();
                hasDestination = false;
                Debug.LogError("Polleria employee rejected a non-finite NavMesh destination. employee="
                    + EmployeeId + " role=" + Role + " destination=" + destination, this);
                return false;
            }
            if (!NavMesh.SamplePosition(destination, out NavMeshHit target, 1.5f, Agent.areaMask))
            {
                HasValidPath = false;
                Agent.ResetPath();
                hasDestination = false;
                return false;
            }
            destination = target.position;
            if (hasDestination && (destination - lastDestination).sqrMagnitude < .0025f) return HasValidPath;
            lastDestination = destination;
            hasDestination = true;
            if (!NavMesh.CalculatePath(Agent.nextPosition, destination, Agent.areaMask, path)
                || path.status != NavMeshPathStatus.PathComplete)
            {
                HasValidPath = false;
                Agent.ResetPath();
                return false;
            }
            HasValidPath = Agent.SetDestination(destination);
            return HasValidPath;
        }

        static bool IsFinite(Vector3 value)
            => !float.IsNaN(value.x) && !float.IsInfinity(value.x)
                && !float.IsNaN(value.y) && !float.IsInfinity(value.y)
                && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        public void ClearDestination()
        {
            hasDestination = false;
            HasValidPath = false;
            if (Agent && Agent.isOnNavMesh) Agent.ResetPath();
        }

        public void Carry(GameObject plate)
        {
            if (!plate || !CarrySocket) return;
            CarriedPlate = plate;
            var presentation = plate.GetComponent<PolleriaPlatePresentation>();
            if (presentation)
                presentation.AttachForCarry(CarrySocket, Vector3.zero, Quaternion.Euler(0f, 0f, -8f));
            else
            {
                plate.transform.SetParent(CarrySocket, false);
                plate.transform.localPosition = Vector3.zero;
                plate.transform.localRotation = Quaternion.Euler(0f, 0f, -8f);
            }
        }

        public GameObject ReleasePlate()
        {
            GameObject plate = CarriedPlate;
            if (plate) plate.transform.SetParent(null, true);
            CarriedPlate = null;
            return plate;
        }

        public void ForgetStoredPlate() => CarriedPlate = null;

        void Update()
        {
            if (Agent && Agent.enabled && Agent.isOnNavMesh)
                LastValidNavMeshPosition = Agent.nextPosition;
            if (animator && animator.runtimeAnimatorController && Agent)
            {
                float speed = Agent.enabled && Agent.isOnNavMesh ? Agent.velocity.magnitude : 0f;
                animator.SetFloat("Speed", Mathf.Clamp01(speed / 4.4f));
            }
            if (locomotion && Agent)
                locomotion.SetExternalMotion(Agent.enabled && Agent.isOnNavMesh ? Agent.velocity : Vector3.zero,
                    !Agent.enabled || Agent.isOnNavMesh);
        }

        void OnDestroy()
        {
            if (CarriedPlate) Destroy(CarriedPlate);
        }
    }
}
