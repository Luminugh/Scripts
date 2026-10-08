using UnityEngine;
using UnityEngine.AI;

namespace FinDeMes.Polleria
{
    /// <summary>Owns the customer's stable transition between NavMesh travel and a table seat.</summary>
    public sealed class PolleriaCustomerSeatState
    {
        const float SeatPositionToleranceSqr = .0025f;
        readonly Transform customerRoot;
        readonly NavMeshAgent agent;
        Transform currentSeat;
        bool isSeated;

        public bool IsSeated => isSeated;

        public PolleriaCustomerSeatState(Transform customerRoot, NavMeshAgent agent)
        {
            this.customerRoot = customerRoot;
            this.agent = agent;
        }

        public bool Enter(Transform seat)
        {
            if (!customerRoot || !seat) return false;

            if (agent && agent.enabled)
            {
                if (agent.isOnNavMesh) agent.ResetPath();
                agent.enabled = false;
            }

            currentSeat = seat;
            customerRoot.SetPositionAndRotation(seat.position, customerRoot.rotation);
            isSeated = true;
            return true;
        }

        public bool IsSeatedAt(Transform seat)
        {
            return isSeated && customerRoot && seat && seat == currentSeat
                && (customerRoot.position - seat.position).sqrMagnitude <= SeatPositionToleranceSqr;
        }

        public void Leave()
        {
            isSeated = false;
            currentSeat = null;
        }
    }
}
