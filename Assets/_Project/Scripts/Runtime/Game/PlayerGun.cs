using UnityEngine;
using UnityEngine.InputSystem;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>One firearm: its magazine, spare ammo and handling.</summary>
    [System.Serializable]
    public class Gun
    {
        public string name = "Pistol";
        public GunSound sound = GunSound.Pistol;
        public int magSize = 6, mag = 6, reserve = 36, maxReserve = 120;
        [Tooltip("Damage per pellet (a pistol fires one pellet)")]
        public float damage = 25f;
        public int pellets = 1;
        [Tooltip("Cone half-angle in degrees")]
        public float spread = 0.6f;
        public float interval = 0.22f, reloadTime = 0.5f, range = 80f, recoil = 2.2f;
        [Tooltip("Shotguns load one shell at a time (and firing interrupts the reload)")]
        public bool shellByShell;
        [Tooltip("Hold the trigger to keep firing (the M16); semi-automatic guns fire once per click")]
        public bool automatic;
        public Transform model, muzzle, pump;
    }

    /// <summary>
    /// Nimal's firearms: a pistol (1) and a pump shotgun (2), plus the slingshot (3).
    /// Left click fires; R tops up the pistol. Both guns reload automatically when empty.
    /// the mouse wheel or number keys switch. Shots are hitscan raycasts from the camera with a
    /// small random spread (the shotgun fires 8 pellets in a wide cone). A hit damages and knocks
    /// back characters (a shot to the head does double damage), breaks barricades and pushes
    /// rigidbodies. Every shot is loud: nearby enemies come to look, and a shot inside the
    /// factory premises sets off the alarm.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class PlayerGun : MonoBehaviour
    {
        public Gun[] guns =
        {
            new Gun(),
            new Gun { name = "Shotgun", sound = GunSound.Shotgun, magSize = 1, mag = 1, reserve = 17, maxReserve = 48,
                      damage = 13f, pellets = 8, spread = 4.5f, interval = 1.25f, reloadTime = 0.75f, range = 40f, recoil = 6f },
        };
        public Material tracerMaterial, sparkMaterial;

        PlayerController pc;
        PlayerSlingshot sling;
        PlayerCombat combat;
        int slot;                       // 0, 1 = guns; 2 = slingshot
        float nextShot, reloadT = -1f, switchT = 1f, kick, pumpT = -1f, bloom;
        float reloadDelay;
        Vector3[] rest;

        public int Slot => slot;
        public float GripPress => kick;
        public bool CanAddAmmo => System.Array.Exists(guns, g => g.reserve < g.maxReserve);
        public bool OnGun => slot < guns.Length;
        public Gun Current => OnGun ? guns[slot] : null;
        public bool Reloading => reloadT >= 0f || reloadDelay > 0f;
        public float ReloadProgress => reloadT >= 0f && Current != null ? Mathf.Clamp01(reloadT / Current.reloadTime) : 0f;
        /// <summary>How wide the crosshair should be, in degrees (spread plus recent recoil).</summary>
        public float Spread => OnGun ? Current.spread + bloom : 0f;

        void Awake()
        {
            pc = GetComponent<PlayerController>();
            sling = GetComponent<PlayerSlingshot>();
            combat = GetComponent<PlayerCombat>();
            foreach (var g in guns) ConfigureHandling(g, true);
            GunFX.Init(tracerMaterial, sparkMaterial);
            rest = new Vector3[guns.Length];
            for (int i = 0; i < guns.Length; i++)
                if (guns[i].model != null) rest[i] = guns[i].model.localPosition;
            Select(0, true);
        }

        public static void ConfigureHandling(Gun g, bool startingLoadout = false)
        {
            if (g.sound != GunSound.Pistol && g.sound != GunSound.Shotgun) return;
            int capacity = g.sound == GunSound.Pistol ? 6 : 1;
            g.reserve = Mathf.Min(g.maxReserve, g.reserve + Mathf.Max(0, g.mag - capacity));
            g.magSize = capacity; g.mag = Mathf.Clamp(g.mag, 0, capacity);
            g.reloadTime = g.sound == GunSound.Pistol ? 0.5f : 0.75f;
            if (g.sound == GunSound.Shotgun) g.interval = 1.25f;
            g.shellByShell = false;
            if (startingLoadout) { g.mag = capacity; g.reserve = g.sound == GunSound.Pistol ? 36 : 17; }
        }

        void Update()
        {
            var mouse = Mouse.current; var kb = Keyboard.current;
            bool playing = GameManager.Instance == null || GameManager.Instance.IsPlaying;
            bool busy = pc.InLocker || pc.Health.Dead || (combat != null && combat.Swinging);
            for (int i = 0; i < guns.Length; i++)
                if (guns[i].model != null) guns[i].model.gameObject.SetActive(i == slot && !busy);
            if (sling != null) sling.active = slot == guns.Length;
            if (mouse == null || kb == null || !playing || busy) { Animate(); return; }

            // Switching
            if (kb.digit1Key.wasPressedThisFrame) Select(0);
            if (kb.digit2Key.wasPressedThisFrame) Select(1);
            if (kb.digit3Key.wasPressedThisFrame && sling != null) Select(guns.Length);
            if (kb.digit4Key.wasPressedThisFrame && guns.Length > 2) Select(2);          // the M16 (added by Tools > Custom Art > Add M16 To Player)
            float wheel = mouse.scroll.ReadValue().y;
            if (Mathf.Abs(wheel) > 0.1f)
            {
                int count = guns.Length + (sling != null ? 1 : 0);
                Select(((slot + (wheel < 0f ? 1 : -1)) % count + count) % count);
            }

            if (OnGun)
            {
                var g = Current;
                bool locked = Cursor.lockState == CursorLockMode.Locked;
                if (kb.rKey.wasPressedThisFrame && g.sound != GunSound.Shotgun) StartReload();
                bool trigger = g.automatic ? mouse.leftButton.isPressed : mouse.leftButton.wasPressedThisFrame;
                if (trigger && locked && !Reloading && switchT >= 1f && Time.time >= nextShot)
                {
                    if (g.mag > 0)
                    {
                        if (!Reloading) Fire(g);
                    }
                    else
                    {
                        GunFX.Play(GunSound.Empty, transform.position, 0.6f);
                        nextShot = Time.time + 0.25f;
                        if (g.reserve > 0) StartReload();
                        else GameManager.Instance?.Toast("Out of ammo! Find an ammo box, or switch weapon (1-3).", 2.5f);
                    }
                }
                if (g.mag == 0 && g.reserve > 0 && !Reloading) StartReload();
                UpdateReload(g);
            }
            bloom = Mathf.MoveTowards(bloom, 0f, Time.deltaTime * 6f);
            Animate();
        }

        void Select(int s, bool instant = false)
        {
            if (s == slot && !instant) return;
            slot = s;
            reloadT = -1f;
            reloadDelay = 0f;
            switchT = instant ? 1f : 0f;
        }

        // ------------------------------------------------------------------ shooting

        void Fire(Gun g)
        {
            g.mag--;
            nextShot = Time.time + g.interval;
            kick = 1f;
            bloom = Mathf.Min(bloom + g.recoil * 0.3f, 3f);
            if (g.pump != null) pumpT = 0f;

            var eyes = pc.eyes;
            Vector3 muzzle = g.muzzle != null ? g.muzzle.position : eyes.position + eyes.forward * 0.6f;
            bool hitSomeone = false, headshot = false;
            for (int i = 0; i < g.pellets; i++)
            {
                float s = g.spread + bloom * 0.5f + (pc.Sprinting ? 1.5f : 0f) - (pc.Crouched ? 0.3f : 0f);
                Vector2 r = Random.insideUnitCircle * Mathf.Max(0.05f, s);
                Vector3 dir = Quaternion.AngleAxis(r.x, eyes.up) * Quaternion.AngleAxis(r.y, eyes.right) * eyes.forward;
                Vector3 end = eyes.position + dir * g.range;
                if (Cast(eyes.position, dir, g.range, out var hit))
                {
                    end = hit.point;
                    var (h, head) = Apply(g, hit, dir);
                    hitSomeone |= h; headshot |= head;
                    if (i < 4) GunFX.Tracer(muzzle, hit.point, g.pellets > 1 ? 0.012f : 0.02f);
                }
                else if (i < 4) GunFX.Tracer(muzzle, eyes.position + dir * g.range, g.pellets > 1 ? 0.012f : 0.02f);
                EnemyAI.NoticeProjectile(eyes.position, end, transform);
            }
            if (hitSomeone) GameManager.Instance?.HitMarker(headshot);

            GunFX.Flash(muzzle);
            GunFX.Play(g.sound, muzzle, 1f);
            pc.AddRecoil(g.recoil, Random.Range(-0.3f, 0.3f) * g.recoil);
            pc.LastNoiseTime = Time.time;
            EnemyAI.MakeNoise(transform.position, 40f);
            GameManager.Instance?.OnPlayerGunshot();
            if (g.mag == 0 && g.reserve > 0)
            {
                if (g.sound == GunSound.Shotgun) reloadDelay = 0.5f;
                else StartReload();
            }
        }

        /// <summary>The first thing along the ray that isn't the player or a trigger.</summary>
        bool Cast(Vector3 from, Vector3 dir, float range, out RaycastHit best)
        {
            best = default;
            float bestD = float.MaxValue;
            foreach (var h in Physics.RaycastAll(from, dir, range, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(transform) || h.distance >= bestD) continue;
                best = h; bestD = h.distance;
            }
            return bestD < float.MaxValue;
        }

        (bool hit, bool head) Apply(Gun g, RaycastHit hit, Vector3 dir)
        {
            var hp = hit.collider.GetComponentInParent<Health>();
            if (hp != null && hp.GetComponent<PlayerController>() == null && !hp.Dead)
            {
                var ai = hp.GetComponent<EnemyAI>();
                bool head = ai != null && ai.IsHuman && hit.point.y - hp.transform.position.y > 1.48f;
                float falloff = Mathf.Lerp(1f, 0.5f, hit.distance / g.range);
                bool humanShotgun = g.sound == GunSound.Shotgun && ai != null && ai.IsHuman;
                hp.Damage(g.damage * falloff * (head ? 2f : 1f) * (humanShotgun ? 1.35f : 1f), transform.position);
                if (ai != null) ai.Knockback(dir * (humanShotgun ? 2.2f : g.pellets > 1 ? 0.9f : 2.5f));
                GunFX.Impact(hit.point, hit.normal, true);
                return (true, head);
            }
            var gi = hit.collider.GetComponentInParent<GreyboxInteractable>();
            if (gi != null && gi.kind == GreyboxKind.Barricade && !gi.IsOpen) gi.Interact();
            if (hit.rigidbody != null) hit.rigidbody.AddForceAtPosition(dir * 4f, hit.point, ForceMode.Impulse);
            GunFX.Impact(hit.point, hit.normal, false);
            return (false, false);
        }

        // ------------------------------------------------------------------ ammo

        void StartReload()
        {
            var g = Current;
            if (g == null || Reloading || g.mag >= g.magSize || g.reserve <= 0) return;
            reloadT = 0f;
            GunFX.Play(GunFX.ReloadFor(g.sound), transform.position, 0.7f);
        }

        void UpdateReload(Gun g)
        {
            if (!Reloading) return;
            if (reloadDelay > 0f)
            {
                reloadDelay = Mathf.Max(0f, reloadDelay - Time.deltaTime);
                if (reloadDelay <= 0f) StartReload();
                return;
            }
            reloadT += Time.deltaTime;
            if (reloadT < g.reloadTime) return;
            int take = Mathf.Min(g.magSize - g.mag, g.reserve);
            g.mag += take; g.reserve -= take;
            reloadT = -1f;
        }

        /// <summary>Picked up from an ammo box or a knocked-out enemy. Returns false if there was no room.</summary>
        public bool AddAmmo(int pistolRounds, int shells)
        {
            int before = guns[0].reserve + guns[1].reserve;
            guns[0].reserve = Mathf.Min(guns[0].maxReserve, guns[0].reserve + pistolRounds);
            guns[1].reserve = Mathf.Min(guns[1].maxReserve, guns[1].reserve + shells);
            if (guns.Length > 2)                                                       // the M16 takes rifle rounds: twice what a pistol box holds
            {
                before += guns[2].reserve;
                guns[2].reserve = Mathf.Min(guns[2].maxReserve, guns[2].reserve + pistolRounds * 2);
                return guns[0].reserve + guns[1].reserve + guns[2].reserve > before;
            }
            return guns[0].reserve + guns[1].reserve > before;
        }

        // ------------------------------------------------------------------ viewmodel

        void Animate()
        {
            switchT = Mathf.Min(1f, switchT + Time.deltaTime / 0.3f);
            kick = Mathf.MoveTowards(kick, 0f, Time.deltaTime * 7f);
            for (int i = 0; i < guns.Length; i++)
            {
                var g = guns[i];
                if (g.model == null || i != slot) continue;
                float raise = 1f - switchT;                                         // comes up from below when drawn
                float dip = Reloading ? Mathf.Sin(Mathf.Clamp01(g.shellByShell ? 0.5f : ReloadProgress) * Mathf.PI) : 0f;
                float bob = pc.Body.velocity.magnitude > 0.5f ? Mathf.Sin(Time.time * (pc.Sprinting ? 14f : 9f)) * 0.008f : 0f;
                g.model.localPosition = rest[i] + new Vector3(0f, bob - 0.25f * raise - 0.08f * dip, -0.07f * kick);
                g.model.localRotation = Quaternion.Euler(-12f * kick - 30f * raise, 0f, 35f * dip);
                if (g.pump != null)
                {
                    float p = 0f;
                    if (pumpT >= 0f) { pumpT += Time.deltaTime / 0.45f; p = Mathf.Sin(Mathf.Clamp01((pumpT - 0.3f) / 0.7f) * Mathf.PI); if (pumpT >= 1f) pumpT = -1f; }
                    if (Reloading) p = Mathf.Max(p, 0.3f * Mathf.Sin(Mathf.Clamp01(reloadT / g.reloadTime) * Mathf.PI));
                    g.pump.localPosition = new Vector3(g.pump.localPosition.x, g.pump.localPosition.y, PumpRest - 0.09f * p);
                }
            }
        }

        const float PumpRest = 0.22f;
    }
}
