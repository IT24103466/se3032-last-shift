using UnityEngine;
using UnityEngine.AI;

namespace LastShift.CustomArtSandbox
{
    /// <summary>
    /// Sandbox-only helper: drives a character that has no NavMesh around (EnemyAI and the agent are
    /// disabled in the sandbox scene) so CreatureAnimator can be checked: idle, walk, aim and shoot, melee.
    /// Only the Sandbox_* scenes use it.
    /// </summary>
    public class SandboxWalker : MonoBehaviour
    {
        public float walkSpeed = 1.6f;
        public float walkDistance = 3f;
        public bool cycleStates = true;
        public int forcedState = -1;   // 0 idle, 1 walk, 2 aim + shoot, 3 melee

        CreatureAnimator anim;
        Vector3 origin;
        float t, shootTimer;
        int state;

        void Start()
        {
            anim = GetComponent<CreatureAnimator>();
            origin = transform.position;
            var ai = GetComponent<EnemyAI>(); if (ai != null) ai.enabled = false;
            var ag = GetComponent<NavMeshAgent>(); if (ag != null) ag.enabled = false;
        }

        void Update()
        {
            t += Time.deltaTime;
            state = forcedState >= 0 ? forcedState : (cycleStates ? ((int)(t / 6f)) % 4 : 0);
            if (anim != null) anim.aiming = state == 2;
            switch (state)
            {
                case 1:
                    float s = Mathf.PingPong(t * walkSpeed, walkDistance * 2f) - walkDistance;
                    float dir = Mathf.Sign(Mathf.Cos(t * walkSpeed * Mathf.PI / (walkDistance * 2f)));
                    transform.position = origin + new Vector3(0f, 0f, s * 0.5f);
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(0f, dir > 0 ? 0f : 180f, 0f), 6f * Time.deltaTime);
                    break;
                case 2:
                    shootTimer -= Time.deltaTime;
                    if (shootTimer <= 0f) { shootTimer = 0.9f; if (anim != null) anim.Shoot(); }
                    break;
                case 3:
                    shootTimer -= Time.deltaTime;
                    if (shootTimer <= 0f) { shootTimer = 1.2f; if (anim != null) anim.PlayAttack(); }
                    break;
            }
        }
    }
}
