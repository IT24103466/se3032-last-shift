using System;
using UnityEngine;

namespace LastShift
{
    public enum NoiseKind { Gunshot, Sprint, Other }

    /// <summary>One thing the mammoth heard. <c>pos</c> is the PERCEIVED source: the real source plus an error that grows with distance.</summary>
    public struct NoiseEvent
    {
        public Vector3 pos;
        public float strength;   // 0..1: how loud it was at the mammoth's ears
        public float sigma;      // metres: how uncertain the mammoth is about where it came from
        public NoiseKind kind;
        public float time;
    }

    /// <summary>
    /// Sanjula's Mammoth - sensors. Pure perception: it never decides or moves. It answers
    ///   "can I see the player right now?"  (field of view + distance + line of sight, shrunk by player stealth)
    ///   "what did I just hear, and roughly from where?"  (gunshots, sprinting, other noises)
    /// and remembers only the facts a real animal would: the last place/time/velocity it SAW the player.
    /// The player's true position is never exposed when he is out of sight.
    /// Uses the game's own signals (PlayerGun magazine, PlayerController.LastNoiseTime / Sprinting / Hidden / Crouched);
    /// no new noise system, no edits to other scripts.
    /// </summary>
    [DisallowMultipleComponent]
    public class MammothPerception : MonoBehaviour
    {
        [Header("Vision")]
        public float viewRange = 42f;
        [Range(20f, 360f)] public float fieldOfView = 150f;
        public float eyeHeight = 3.4f;
        [Tooltip("Seconds between vision checks (10 Hz)")]
        public float visionInterval = 0.1f;
        [Header("Stealth multipliers on view range")]
        public float hiddenMul = 0.2f, crouchMul = 0.55f, sprintMul = 1.25f;

        [Header("Hearing radius (metres) by sound")]
        public float gunshotRadius = 95f, sprintRadius = 30f, otherRadius = 16f;
        [Tooltip("Position error as a fraction of distance to the source")]
        public float localisationError = 0.15f;
        public float sprintNoiseInterval = 0.6f;

        public bool logEvents = true;

        // ---- what the rest of the brain may read
        public bool PlayerVisible { get; private set; }
        public Vector3 LastSeenPos { get; private set; }
        public Vector3 LastSeenVelocity { get; private set; }
        public float LastSeenTime { get; private set; } = -999f;
        public bool HasSeenPlayer => LastSeenTime > -900f;
        public float TimeSinceSeen => Time.time - LastSeenTime;
        /// <summary>0..1 chance-like score of how detectable the player currently is (distance falloff x stealth). Used later as the observation likelihood.</summary>
        public float Detectability { get; private set; }
        public NoiseEvent LastNoise { get; private set; }
        public bool HasHeardNoise => LastNoise.time > 0f;
        public float NoiseAge => Time.time - LastNoise.time;
        public Vector3 EyePosition => transform.position + Vector3.up * eyeHeight;

        public event Action<NoiseEvent> NoiseHeard;
        public event Action<bool> SightChanged;   // true = just spotted, false = just lost

        PlayerController player;
        PlayerGun gun;
        readonly RaycastHit[] hitBuf = new RaycastHit[10];
        float nextVision, nextSprintNoise, lastNoiseStamp;
        int lastSlot = -1, lastMag;
        Vector3 prevSeenPos; float prevSeenTime = -999f;

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            if (player != null) { gun = player.GetComponent<PlayerGun>(); lastNoiseStamp = player.LastNoiseTime; }
        }

        void Update()
        {
            if (player == null) return;
            if (player.Health == null || player.Health.Dead) { SetVisible(false, 0f); return; }
            PollHearing();                       // cheap reads, every frame so a shot is never missed
            if (Time.time >= nextVision) { nextVision = Time.time + visionInterval; PollVision(); }
        }

        // ------------------------------------------------------------------ vision
        float StealthMul()
        {
            if (player.Hidden) return hiddenMul;
            if (player.Sprinting) return sprintMul;
            if (player.Crouched) return crouchMul;
            return 1f;
        }

        void PollVision()
        {
            Vector3 eye = EyePosition;
            Vector3 target = player.Chest;
            Vector3 to = target - eye;
            float dist = to.magnitude;
            float range = viewRange * StealthMul();
            Detectability = 0f;

            bool seen = false;
            if (dist <= range && dist > 0.01f)
            {
                Vector3 flat = to; flat.y = 0f;
                float angle = flat.sqrMagnitude > 0.0001f ? Vector3.Angle(transform.forward, flat) : 0f;
                if (angle <= fieldOfView * 0.5f && HasLineOfSight(eye, to, dist))
                {
                    seen = true;
                    // linear falloff with distance: easier to notice up close
                    Detectability = Mathf.Clamp01(1f - dist / range) * 0.8f + 0.2f;
                }
            }
            SetVisible(seen, dist);
            if (seen) RecordSighting(player.transform.position);
        }

        /// <summary>Could I see something standing at this point right now (field of view, range, line of sight)? Used by the belief to rule places out.</summary>
        public bool CanSeePoint(Vector3 point, out float distance)
        {
            Vector3 eye = EyePosition;
            Vector3 to = point - eye;
            distance = to.magnitude;
            if (distance > viewRange || distance < 0.01f) return false;
            Vector3 flat = to; flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f && Vector3.Angle(transform.forward, flat) > fieldOfView * 0.5f) return false;
            return HasLineOfSight(eye, to, distance);
        }

        bool HasLineOfSight(Vector3 from, Vector3 to, float dist)
        {
            int n = Physics.RaycastNonAlloc(from, to / dist, hitBuf, dist, ~(1 << 2), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var h = hitBuf[i].collider;
                if (h == null) continue;
                if (h.transform.IsChildOf(transform)) continue;       // my own body
                if (h.transform.IsChildOf(player.transform)) continue; // the player himself
                return false;                                           // terrain, wall, tree, rock ...
            }
            return true;
        }

        void RecordSighting(Vector3 pos)
        {
            float t = Time.time;
            if (prevSeenTime > 0f && t - prevSeenTime < 0.6f && t > prevSeenTime)
                LastSeenVelocity = Vector3.Lerp(LastSeenVelocity, (pos - prevSeenPos) / (t - prevSeenTime), 0.5f);
            else LastSeenVelocity = Vector3.zero;
            LastSeenPos = pos; LastSeenTime = t;
            prevSeenPos = pos; prevSeenTime = t;
        }

        void SetVisible(bool v, float dist)
        {
            if (v == PlayerVisible) return;
            PlayerVisible = v;
            if (logEvents) Debug.Log(v ? $"[MammothAI] SPOTTED player at {dist:F0} m" : $"[MammothAI] LOST sight of player (last seen {LastSeenPos})");
            SightChanged?.Invoke(v);
        }

        // ------------------------------------------------------------------ hearing
        void PollHearing()
        {
            bool shot = false;
            // gunshot: the active gun's magazine went down (reloading raises it, so it is not a false positive)
            if (gun != null && gun.OnGun && gun.Current != null)
            {
                int slot = gun.Slot, mag = gun.Current.mag;
                if (slot == lastSlot && mag < lastMag) shot = true;
                lastSlot = slot; lastMag = mag;
            }
            else lastSlot = -1;

            if (shot) { Hear(NoiseKind.Gunshot, gunshotRadius); lastNoiseStamp = player.LastNoiseTime; return; }

            // sprinting
            if (player.Sprinting && Time.time >= nextSprintNoise)
            {
                nextSprintNoise = Time.time + sprintNoiseInterval;
                Hear(NoiseKind.Sprint, sprintRadius);
            }
            // anything else the game flags as noise (rustling in bushes, melee, interacting)
            float stamp = player.LastNoiseTime;
            if (stamp > lastNoiseStamp)
            {
                lastNoiseStamp = stamp;
                Hear(NoiseKind.Other, otherRadius);
            }
        }

        void Hear(NoiseKind kind, float radius)
        {
            Vector3 src = player.transform.position;
            Vector3 d = src - transform.position; d.y = 0f;
            float dist = d.magnitude;
            if (dist > radius) return;                         // too far to hear
            float strength = Mathf.Clamp01(1f - dist / radius);
            float sigma = Mathf.Max(2f, dist * localisationError);
            Vector2 err = UnityEngine.Random.insideUnitCircle * sigma;   // the mammoth cannot pinpoint the source
            var ev = new NoiseEvent { pos = src + new Vector3(err.x, 0f, err.y), strength = strength, sigma = sigma, kind = kind, time = Time.time };
            LastNoise = ev;
            if (logEvents) Debug.Log($"[MammothAI] HEARD {kind} ~{dist:F0} m away (strength {strength:F2}, +/-{sigma:F0} m)");
            NoiseHeard?.Invoke(ev);
        }
    }
}
