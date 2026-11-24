using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace LastShift
{
    /// <summary>
    /// Brings Sanjula's Mammoth under the one-fight-at-a-time rule (<see cref="ThreatDirector"/>)
    /// WITHOUT changing his MammothAI code: while the mammoth wants to hunt but doesn't have the
    /// turn (another enemy is fighting the player, or the player is at the camp), its AI is paused
    /// and it stands still; the moment the turn is free, it carries on where it was.
    /// Added at runtime to every MammothAI in the level.
    /// </summary>
    [DisallowMultipleComponent]
    public class MammothTurnGate : MonoBehaviour
    {
        MammothAI ai;
        NavMeshAgent agent;
        PlayerController player;
        Health health;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            new GameObject("MammothTurnGate_Finder").AddComponent<Finder>();
        }

        static void OnLoaded(Scene s, LoadSceneMode m)
        {
            if (FindAnyObjectByType<Finder>() == null) new GameObject("MammothTurnGate_Finder").AddComponent<Finder>();
        }

        /// <summary>The mammoth is spawned at runtime by its own spawner, so look for it for a little while.</summary>
        class Finder : MonoBehaviour
        {
            float until;
            void Start() => until = Time.time + 30f;
            void Update()
            {
                foreach (var m in FindObjectsByType<MammothAI>(FindObjectsInactive.Exclude))
                    if (m.GetComponent<MammothTurnGate>() == null) m.gameObject.AddComponent<MammothTurnGate>();
                if (Time.time > until) Destroy(gameObject);
            }
        }

        void Awake()
        {
            ai = GetComponent<MammothAI>();
            agent = GetComponent<NavMeshAgent>();
            health = GetComponent<Health>();
        }

        void LateUpdate()
        {
            if (ai == null) return;
            if (player == null) player = FindAnyObjectByType<PlayerController>();
            if (player == null) return;
            if (health != null && health.Dead) { ThreatDirector.Release(ai); if (!ai.enabled) ai.enabled = true; return; }

            if (!ai.IsHunting) { if (!ai.enabled) Resume(); return; }

            if (ThreatDirector.TryAttack(ai, "Sanjula's Mammoth", player.transform.position)) { if (!ai.enabled) Resume(); }
            else if (ai.enabled) Pause();
        }

        void Pause()
        {
            ai.enabled = false;
            if (agent != null && agent.enabled && agent.isOnNavMesh) { agent.isStopped = true; agent.velocity = Vector3.zero; }
        }

        void Resume()
        {
            ai.enabled = true;
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.isStopped = false;
        }
    }
}
