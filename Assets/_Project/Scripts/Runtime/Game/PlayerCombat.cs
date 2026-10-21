using UnityEngine;
using UnityEngine.InputSystem;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>
    /// Nimal fights with a wooden stick: left click swings it. A hit damages and knocks back
    /// whatever is in front (people, animals) and breaks barricades. Swinging costs stamina.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class PlayerCombat : MonoBehaviour
    {
        public float damage = 30f, range = 2.4f, radius = 0.5f, cooldown = 0.5f, knockback = 6f, staminaCost = 7f;
        public Transform viewmodel;

        PlayerController pc;
        float nextSwing, swingT = -1f;
        bool hitDone;
        Quaternion restRot;
        Vector3 restPos;

        public System.Action<string> OnHitSomething;

        void Awake()
        {
            pc = GetComponent<PlayerController>();
            if (viewmodel == null && pc.eyes != null) viewmodel = pc.eyes.Find("Viewmodel");
            if (viewmodel != null) { restRot = viewmodel.localRotation; restPos = viewmodel.localPosition; }
        }

        void Update()
        {
            var mouse = Mouse.current;
            bool playing = GameManager.Instance == null || GameManager.Instance.IsPlaying;
            if (viewmodel != null) viewmodel.gameObject.SetActive(!pc.InLocker);
            if (mouse == null || !playing || pc.Health.Dead || pc.InLocker) return;

            if (mouse.leftButton.wasPressedThisFrame && Time.time >= nextSwing && pc.Stamina > 2f
                && Cursor.lockState == CursorLockMode.Locked)
            {
                nextSwing = Time.time + cooldown;
                swingT = 0f;
                hitDone = false;
                pc.LastNoiseTime = Time.time;
                pc.SpendStamina(staminaCost);
            }

            if (swingT >= 0f)
            {
                swingT += Time.deltaTime / 0.35f;
                if (!hitDone && swingT >= 0.35f) { hitDone = true; DoHit(); }
                if (viewmodel != null)
                {
                    // wind up, then a fast diagonal swing, then back
                    float a = swingT < 0.3f ? -swingT / 0.3f * 35f : Mathf.Lerp(-35f, 70f, Mathf.Clamp01((swingT - 0.3f) / 0.25f)) * (1f - Mathf.Clamp01((swingT - 0.6f) / 0.4f));
                    viewmodel.localRotation = restRot * Quaternion.Euler(-a, 0f, a * 0.4f);
                    viewmodel.localPosition = restPos + new Vector3(-0.1f, 0f, 0.1f) * Mathf.Sin(Mathf.Clamp01(swingT) * Mathf.PI);
                }
                if (swingT >= 1f) { swingT = -1f; if (viewmodel != null) { viewmodel.localRotation = restRot; viewmodel.localPosition = restPos; } }
            }
            else if (viewmodel != null)
            {
                // idle sway while walking
                float bob = pc.Body.velocity.magnitude > 0.5f ? Mathf.Sin(Time.time * (pc.Sprinting ? 14f : 9f)) * 0.012f : 0f;
                viewmodel.localPosition = restPos + new Vector3(0f, bob, 0f);
            }
        }

        void DoHit()
        {
            // Stamina is spent on the swing
            var eyes = pc.eyes;
            var hits = Physics.SphereCastAll(eyes.position, radius, eyes.forward, range, ~(1 << 2), QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider.transform.IsChildOf(transform)) continue;
                var hp = h.collider.GetComponentInParent<Health>();
                if (hp != null && !hp.Dead)
                {
                    hp.Damage(damage, transform.position);
                    var ai = hp.GetComponent<EnemyAI>();
                    if (ai != null) ai.Knockback((hp.transform.position - transform.position).normalized * knockback);
                    OnHitSomething?.Invoke(hp.name);
                    return;
                }
                var gi = h.collider.GetComponentInParent<GreyboxInteractable>();
                if (gi != null && gi.kind == GreyboxKind.Barricade) { gi.Interact(); return; }
                if (hp == null && gi == null) return;   // hit a wall
            }
        }
    }
}
