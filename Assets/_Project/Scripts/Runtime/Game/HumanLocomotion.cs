using UnityEngine;
using UnityEngine.AI;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>Human crouching for clearance/combat and collision-checked short jumps/off-mesh links.</summary>
    [DefaultExecutionOrder(50)]
    public sealed class HumanLocomotion : MonoBehaviour
    {
        EnemyAI ai; NavMeshAgent agent; Health health; CapsuleCollider body;
        float standingHeight, bodyHeight; Vector3 bodyCenter;
        float crouch, nextProbe, nextJump, jumpTime = -1f, jumpDuration, jumpHeight;
        Vector3 jumpStart, jumpEnd, destination; bool hadPath, linkJump, originalTraverse;
        public float Crouch => crouch;
        public bool Airborne => jumpTime >= 0f;
        public float AirPhase => Airborne ? Mathf.Clamp01(jumpTime / jumpDuration) : 0f;

        void Awake()
        {
            ai = GetComponent<EnemyAI>(); agent = GetComponent<NavMeshAgent>(); health = GetComponent<Health>();
            body = GetComponent<CapsuleCollider>(); standingHeight = agent.height;
            if (body != null) { bodyHeight = body.height; bodyCenter = body.center; }
            originalTraverse = agent.autoTraverseOffMeshLink; agent.autoTraverseOffMeshLink = false;
        }

        void OnDisable()
        {
            if (agent != null) { agent.updatePosition = true; agent.autoTraverseOffMeshLink = originalTraverse; }
        }

        void Update()
        {
            if (ai == null || !ai.IsHuman || health.Dead || !agent.enabled || !agent.isOnNavMesh) return;
            if (GameManager.Instance != null && !GameManager.Instance.IsPlaying) return;
            if (Airborne) { AdvanceJump(Time.deltaTime); return; }
            bool lowRoof = Blocked(transform.position, standingHeight, true);
            bool combatCrouch = ai.IsPursuing && agent.velocity.sqrMagnitude < 0.25f && (ai.Reloading || ai.Aiming);
            crouch = Mathf.MoveTowards(crouch, lowRoof || combatCrouch ? 1f : 0f, Time.deltaTime * 4f);
            agent.height = Mathf.Lerp(standingHeight, Mathf.Max(agent.radius * 2f, standingHeight * 0.5f), crouch);
            if (body != null)
            {
                body.height = Mathf.Lerp(bodyHeight, Mathf.Max(body.radius * 2f, bodyHeight * 0.5f), crouch);
                body.center = bodyCenter - Vector3.up * ((bodyHeight - body.height) * 0.5f);
            }
            if (Time.time < nextJump) return;
            if (agent.isOnOffMeshLink)
            {
                var link = agent.currentOffMeshLinkData;
                if (link.valid) TryJump(link.endPos, true);
                return;
            }
            if (Time.time < nextProbe || !agent.hasPath) return;
            nextProbe = Time.time + 0.2f;
            Vector3 dir = agent.steeringTarget - transform.position; dir.y = 0f;
            if (dir.sqrMagnitude < 0.2f) return; dir.Normalize();
            foreach (var hit in Physics.RaycastAll(transform.position + Vector3.up * 0.3f, dir, 0.85f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (hit.transform.IsChildOf(transform) || hit.collider.GetComponentInParent<GreyboxInteractable>() != null) continue;
                float obstacle = hit.collider.bounds.max.y - transform.position.y;
                if (obstacle < 0.12f || obstacle > 0.65f) continue;
                float across = Mathf.Abs(Vector3.Dot(hit.collider.bounds.size, new Vector3(Mathf.Abs(dir.x), 0f, Mathf.Abs(dir.z))));
                TryJump(transform.position + dir * Mathf.Clamp(hit.distance + across + 0.6f, 0.9f, 2.4f), false);
                break;
            }
        }

        bool Blocked(Vector3 feet, float height, bool upperOnly = false)
        {
            float radius = Mathf.Max(0.12f, agent.radius * 0.85f);
            Vector3 lower = feet + Vector3.up * (upperOnly ? standingHeight * 0.65f : radius + 0.07f);
            Vector3 upper = feet + Vector3.up * Mathf.Max(radius + 0.07f, height - radius);
            foreach (var c in Physics.OverlapCapsule(lower, upper, radius, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!c.transform.IsChildOf(transform)) return true;
            return false;
        }

        public bool TryJump(Vector3 landing, bool offMeshLink = false)
        {
            if (Airborne || health.Dead || !agent.enabled || !agent.isOnNavMesh || Time.time < nextJump) return false;
            if (!NavMesh.SamplePosition(landing, out var sample, 0.45f, agent.areaMask)) return false;
            Vector3 start = transform.position, end = sample.position;
            float distance = Vector3.Distance(start, end);
            if (distance < 0.4f || distance > 3f || Mathf.Abs(end.y - start.y) > 0.85f) return false;
            float height = Mathf.Max(0.9f, end.y - start.y + 0.35f);
            for (int i = 1; i <= 16; i++)
            {
                float t = i / 16f;
                if (Blocked(Vector3.Lerp(start, end, t) + Vector3.up * (4f * height * t * (1f - t)), agent.height)) return false;
            }
            jumpStart = start; jumpEnd = end; jumpHeight = height; jumpDuration = Mathf.Clamp(distance / 3.2f, 0.5f, 0.85f);
            jumpTime = 0f; linkJump = offMeshLink; hadPath = agent.hasPath; destination = agent.destination;
            agent.isStopped = true; agent.updatePosition = false;
            return true;
        }

        void AdvanceJump(float dt)
        {
            jumpTime += dt; float t = AirPhase;
            Vector3 next = Vector3.Lerp(jumpStart, jumpEnd, t) + Vector3.up * (4f * jumpHeight * t * (1f - t));
            if (Blocked(next, agent.height)) { FinishJump(false); return; }
            transform.position = next;
            if (t >= 1f) FinishJump(true);
        }

        void FinishJump(bool landed)
        {
            if (linkJump && agent.isOnOffMeshLink) agent.CompleteOffMeshLink();
            agent.updatePosition = true;
            // Return to a valid surface if a moving obstacle interrupted the arc.
            Vector3 point = landed ? jumpEnd : jumpStart;
            if (NavMesh.SamplePosition(point, out var hit, 0.5f, agent.areaMask)) agent.Warp(hit.position);
            agent.isStopped = false; jumpTime = -1f; nextJump = Time.time + 1.5f;
            if (hadPath) agent.SetDestination(destination);
        }
    }
}
