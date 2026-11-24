using UnityEngine;
using UnityEngine.AI;

namespace LastShift.GuardDog
{
    /// <summary>
    /// What the brain can ask the body to do. The brain only talks to this interface, so the
    /// decision logic doesn't know about NavMeshAgents, animators or audio.
    /// </summary>
    public interface IDogBody
    {
        Transform Transform { get; }
        Vector3 Position { get; }
        Vector3 Forward { get; }
        Vector3 Eyes { get; }
        bool Alive { get; }
        float HealthFraction { get; }
        float Speed { get; }

        void MoveTo(Vector3 target, float speed);
        void Stop();
        void Face(Vector3 point);
        void SetSniffing(bool on);
        void Bark();
        void Growl();
        void Whimper();
        bool TryBite(PlayerController player);
        void Heal(float amount);
        void Unstick();
    }

    /// <summary>
    /// Kalu's body: drives the NavMeshAgent (local steering only), the procedural animation, the
    /// nose-to-the-ground sniffing pose, sounds, biting and being hurt.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent), typeof(Health))]
    public class DogBody : MonoBehaviour, IDogBody
    {
        public float maxHealth = 70f, biteDamage = 9f, biteCooldown = 1.1f, biteKnockback = 3f;

        NavMeshAgent agent;
        Health health;
        CreatureAnimator anim;
        AudioSource voice;
        Transform headTilt;
        float nextBite, sniffBlend;
        bool sniffing;

        public Transform Transform => transform;
        public Vector3 Position => transform.position;
        public Vector3 Forward => transform.forward;
        public Vector3 Eyes => transform.position + Vector3.up * 0.75f + transform.forward * 0.55f;
        public bool Alive => health != null && !health.Dead;
        public float HealthFraction => health.Fraction;
        public float Speed => agent != null && agent.enabled ? agent.velocity.magnitude : 0f;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            health = GetComponent<Health>();
            anim = GetComponent<CreatureAnimator>();
            foreach (var t in GetComponentsInChildren<Transform>()) if (t.name == "HeadTilt") { headTilt = t; break; }
            voice = gameObject.AddComponent<AudioSource>();
            voice.spatialBlend = 1f; voice.minDistance = 3f; voice.maxDistance = 70f; voice.rolloffMode = AudioRolloffMode.Linear;
            agent.acceleration = 22f; agent.angularSpeed = 540f; agent.stoppingDistance = 0.2f; agent.autoBraking = true;
            health.max = maxHealth; health.current = maxHealth;
            health.Damaged += OnHurt;
            health.Died += OnDied;
        }

        void OnDestroy() { if (health != null) { health.Damaged -= OnHurt; health.Died -= OnDied; } }

        void LateUpdate()
        {
            sniffBlend = Mathf.MoveTowards(sniffBlend, sniffing ? 1f : 0f, Time.deltaTime * 3f);
            if (headTilt != null) headTilt.localRotation = Quaternion.Euler(38f * sniffBlend + Mathf.Sin(Time.time * 9f) * 4f * sniffBlend, 0f, 0f);
        }

        /// <summary>One line about the NavMeshAgent for the debug view.</summary>
        public string SteeringInfo => agent == null || !agent.enabled ? "agent off"
            : $"onNavMesh {agent.isOnNavMesh}  stopped {agent.isStopped}  path {agent.pathStatus}  vel {agent.velocity.magnitude:F1}  to dest {Vector3.Distance(transform.position, agent.destination):F1} m";

        public void MoveTo(Vector3 target, float speed)
        {
            if (!agent.enabled) return;
            if (!agent.isOnNavMesh) { Unstick(); return; }
            agent.isStopped = false;
            agent.speed = speed;
            if ((agent.destination - target).sqrMagnitude > 0.09f) agent.SetDestination(target);
        }

        public void Stop()
        {
            if (!agent.enabled || !agent.isOnNavMesh) return;
            agent.isStopped = true;
            agent.velocity = Vector3.zero;
        }

        public void Face(Vector3 p)
        {
            Vector3 to = p - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), Time.deltaTime * 10f);
        }

        public void SetSniffing(bool on) => sniffing = on;

        public void Bark() { voice.PlayOneShot(DogSounds.Bark, 1f); anim?.Roar(); }
        public void Growl() => voice.PlayOneShot(DogSounds.Growl, 0.8f);
        public void Whimper() => voice.PlayOneShot(DogSounds.Whimper, 0.7f);

        public bool TryBite(PlayerController player)
        {
            if (Time.time < nextBite || player == null || player.Health.Dead) return false;
            nextBite = Time.time + biteCooldown;
            anim?.PlayAttack();
            voice.PlayOneShot(DogSounds.Growl, 0.6f);
            player.Health.Damage(biteDamage, transform.position);
            Vector3 push = player.transform.position - transform.position; push.y = 0f;
            player.AddKnockback(push.normalized * biteKnockback + Vector3.up * 1.2f);
            return true;
        }

        public void Heal(float amount) => health.current = Mathf.Min(health.max, health.current + amount);

        /// <summary>Fell off the NavMesh (pushed, spawned badly): put it back on the nearest point.</summary>
        public void Unstick()
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, 6f, NavMesh.AllAreas)) agent.Warp(hit.position);
        }

        void OnHurt(float amount, Vector3 from)
        {
            if (!Alive) return;
            voice.PlayOneShot(DogSounds.Yelp, 1f);
            anim?.Flinch();
        }

        void OnDied()
        {
            voice.PlayOneShot(DogSounds.Yelp, 1f);
            anim?.Die();
            if (agent.enabled) agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            GameManager.Instance?.Toast("Kalu the estate dog is down.", 2.5f);
        }
    }
}
