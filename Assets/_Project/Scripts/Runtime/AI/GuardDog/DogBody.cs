using System.Collections;
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
        bool Leaping { get; }
        bool CanLeap { get; }

        void MoveTo(Vector3 target, float speed);
        void Stop();
        void Face(Vector3 point);
        void SetSniffing(bool on);
        void Bark();
        void Growl();
        void Whimper();
        bool TryBite(PlayerController player);
        bool TryLeap(PlayerController player);
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
        public float maxHealth = 70f, biteDamage = 7f, biteCooldown = 1.2f, biteKnockback = 3.5f;
        [Tooltip("Kalu goes down after this many hits, whatever the weapon (a shotgun blast counts as one)")]
        public int shotsToKill = 5;
        [Tooltip("The pounce: a jump at the player from a few metres away that ends in a bite")]
        public float leapDamage = 10f, leapCooldown = 3f, leapTime = 0.38f, leapHeight = 0.8f;

        NavMeshAgent agent;
        Health health;
        CreatureAnimator anim;
        AudioSource voice;
        Transform headTilt;
        float nextBite, sniffBlend, nextLeap, lastHitAt = -99f;
        int hitsTaken;
        bool sniffing, leaping;

        public Transform Transform => transform;
        public Vector3 Position => transform.position;
        public Vector3 Forward => transform.forward;
        public Vector3 Eyes => transform.position + Vector3.up * 0.75f + transform.forward * 0.55f;
        public bool Alive => health != null && !health.Dead;
        public float HealthFraction => health.Fraction;
        public float Speed => agent != null && agent.enabled ? agent.velocity.magnitude : 0f;
        public bool Leaping => leaping;
        public bool CanLeap => !leaping && Time.time >= nextLeap && Alive && agent.enabled && agent.isOnNavMesh;
        public int HitsTaken => hitsTaken;

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            health = GetComponent<Health>();
            anim = GetComponent<CreatureAnimator>();
            foreach (var t in GetComponentsInChildren<Transform>()) if (t.name == "HeadTilt") { headTilt = t; break; }
            voice = gameObject.AddComponent<AudioSource>();
            // Mostly 3D (you can tell where he is) but with some 2D so the bark is always heard clearly
            voice.spatialBlend = 0.75f; voice.minDistance = 8f; voice.maxDistance = 90f; voice.rolloffMode = AudioRolloffMode.Linear;
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

        public void Bark() { if (Say(DogSounds.Bark, 1f)) { anim?.Roar(); lastBark = Time.time; } }
        float nextHurtVoice;
        bool Say(AudioClip clip,float volume,bool hurt=false)
        {
            if(voice==null || clip==null) return false;
            if(hurt) {if(Time.time<nextHurtVoice)return false;nextHurtVoice=Time.time+Mathf.Max(0.8f,clip.length);}
            else if(voice.isPlaying)return false;
            voice.Stop();voice.clip=clip;voice.volume=volume;voice.Play();return true;
        }

        // ---- "WOOF!" over his head when he barks, so you see where the barking comes from
        float lastBark = -99f;
        GUIStyle woofStyle;

        void OnGUI()
        {
            if (!Alive || Time.time - lastBark > 0.7f) return;
            var cam = Camera.main;
            if (cam == null) return;
            Vector3 head = transform.position + Vector3.up * 1.3f;
            float dist = Vector3.Distance(cam.transform.position, head);
            if (dist > 60f) return;
            Vector3 sp = cam.WorldToScreenPoint(head);
            if (sp.z <= 0f) return;   // behind the camera
            if (woofStyle == null)
            {
                woofStyle = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                woofStyle.normal.textColor = new Color(1f, 0.85f, 0.3f);
            }
            float rise = (Time.time - lastBark) * 30f;
            woofStyle.fontSize = Mathf.RoundToInt(Mathf.Lerp(34f, 16f, dist / 60f) * Screen.height / 1080f);
            var r = new Rect(sp.x - 100f, Screen.height - sp.y - 30f - rise, 200f, 40f);
            var shadow = new GUIStyle(woofStyle); shadow.normal.textColor = new Color(0f, 0f, 0f, 0.8f);
            GUI.Label(new Rect(r.x + 2f, r.y + 2f, r.width, r.height), "WOOF!", shadow);
            GUI.Label(r, "WOOF!", woofStyle);
        }
        public void Growl() => Say(DogSounds.Growl, 0.8f);
        public void Whimper() => Say(DogSounds.Whimper, 0.7f);

        public bool TryBite(PlayerController player)
        {
            if (Time.time < nextBite || player == null || player.Health.Dead) return false;
            nextBite = Time.time + biteCooldown;
            anim?.PlayAttack();
            Say(DogSounds.Growl, 0.6f);
            player.Health.Damage(biteDamage, transform.position);
            Vector3 push = player.transform.position - transform.position; push.y = 0f;
            player.AddKnockback(push.normalized * biteKnockback + Vector3.up * 1.2f);
            return true;
        }

        /// <summary>Pounce: jump at the player and bite him on landing.</summary>
        public bool TryLeap(PlayerController player)
        {
            if (!CanLeap || player == null || player.Health.Dead) return false;
            StartCoroutine(Leap(player));
            return true;
        }

        IEnumerator Leap(PlayerController player)
        {
            leaping = true;
            nextLeap = Time.time + leapCooldown;
            Bark();
            anim?.PlayAttack();
            agent.isStopped = true; agent.velocity = Vector3.zero;
            agent.updatePosition = false;                 // the jump moves the body; the agent catches up on landing

            Vector3 start = transform.position;
            bool bit = false;
            for (float t = 0f; t < leapTime; t += Time.deltaTime)
            {
                if (!Alive) { Drop(start); yield break; }
                float k = t / leapTime;
                Vector3 target = player.transform.position;
                Vector3 dir = target - start; dir.y = 0f;
                Vector3 land = dir.sqrMagnitude > 0.01f ? target - dir.normalized * 0.9f : start;   // land just in front of him
                Vector3 pos = Vector3.Lerp(start, land, k);
                pos.y = Mathf.Lerp(start.y, target.y, k) + Mathf.Sin(k * Mathf.PI) * leapHeight;
                transform.position = pos;
                if (dir.sqrMagnitude > 0.01f) transform.rotation = Quaternion.LookRotation(dir);
                if (!bit && k > 0.55f && Vector3.Distance(pos, target) < 1.8f && !player.Health.Dead)
                {
                    bit = true;
                    Say(DogSounds.Growl, 0.8f);
                    player.Health.Damage(leapDamage, transform.position);
                    player.AddKnockback(dir.normalized * (biteKnockback + 1.5f) + Vector3.up * 1.5f);
                }
                yield return null;
            }

            if (!Alive) { Drop(start); yield break; }
            Vector3 landAt = NavMesh.SamplePosition(transform.position, out var hit, 2.5f, NavMesh.AllAreas) ? hit.position : start;
            agent.Warp(landAt);
            agent.updatePosition = true;
            agent.isStopped = false;
            nextBite = Time.time + biteCooldown * 0.5f;   // straight into a normal bite if he's still there
            leaping = false;
        }

        /// <summary>Shot dead in mid-air: fall to the ground.</summary>
        void Drop(Vector3 fallback)
        {
            transform.position = NavMesh.SamplePosition(transform.position, out var hit, 2.5f, NavMesh.AllAreas) ? hit.position : fallback;
            leaping = false;
        }

        public void Heal(float amount) => health.current = Mathf.Min(health.max, health.current + amount);

        /// <summary>Fell off the NavMesh (pushed, spawned badly): put it back on the nearest point.</summary>
        public void Unstick()
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, 6f, NavMesh.AllAreas)) agent.Warp(hit.position);
        }

        void OnHurt(float amount, Vector3 from)
        {
            // Five hits and Kalu is down, whatever the weapon: each separate hit counts once (the
            // 8 pellets of one shotgun blast all land in the same moment, so they count as one shot).
            if (Time.time - lastHitAt > 0.15f) hitsTaken++;
            lastHitAt = Time.time;
            health.current = hitsTaken >= shotsToKill ? 0f : health.max * (1f - hitsTaken / (float)shotsToKill);
            if (!Alive) return;   // Health raises Died right after this (current is 0)
            Say(DogSounds.Yelp, 1f, true);
            anim?.Flinch();
        }

        void OnDied()
        {
            Say(DogSounds.Yelp, 1f, true);
            anim?.Die();
            if (agent.enabled) agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            GameManager.Instance?.Toast("Kalu the estate dog is down.", 2.5f);
        }
    }
}
