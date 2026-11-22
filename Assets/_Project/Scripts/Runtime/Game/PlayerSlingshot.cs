using UnityEngine;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Nimal's slingshot (catapult): the game's shooting mechanic.
    /// Hold the left mouse button to draw the band (longer draw = faster, harder shot), release to
    /// fire a pebble. Pebbles are real physics objects: they fly in an arc under gravity, bounce,
    /// hurt and knock back whatever they hit, break barricades, and make a noise where they land
    /// that draws enemies to investigate (throw one to distract a guard). Ammo is limited:
    /// pick up more from stone piles (E). It is weapon 3 (PlayerGun switches between them).
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    public class PlayerSlingshot : MonoBehaviour
    {
        public int ammo = 12, maxAmmo = 25;
        public float minSpeed = 20f, maxSpeed = 42f, drawTime = 0.6f, cooldown = 0.35f, minDamage = 14f, maxDamage = 34f;
        public Material pebbleMaterial, trailMaterial;
        [Tooltip("Set by PlayerGun: the slingshot is weapon 3")]
        public bool active = true;

        PlayerController pc;
        Transform model, pouch, tipL, tipR;
        LineRenderer band;
        Vector3 pouchRest, modelRest;
        float draw = -1f, nextShot, kick;

        public float DrawAmount => Mathf.Clamp01(draw);
        public bool Drawing => draw >= 0f;

        void Awake()
        {
            pc = GetComponent<PlayerController>();
            model = pc.eyes != null ? pc.eyes.Find("Slingshot") : null;
            if (model != null)
            {
                pouch = model.Find("Pouch"); tipL = model.Find("TipL"); tipR = model.Find("TipR");
                band = model.GetComponent<LineRenderer>();
                if (pouch != null) pouchRest = pouch.localPosition;
                modelRest = model.localPosition;
            }
        }

        void Update()
        {
            var mouse = Mouse.current;
            bool playing = GameManager.Instance == null || GameManager.Instance.IsPlaying;
            if (model != null) model.gameObject.SetActive(active && !pc.InLocker && !pc.Health.Dead);
            if (mouse == null || !active || !playing || pc.Health.Dead || pc.InLocker) { draw = -1f; UpdateModel(); return; }

            if (mouse.leftButton.wasPressedThisFrame && Cursor.lockState == CursorLockMode.Locked && Time.time >= nextShot)
            {
                if (ammo > 0) draw = 0f;
                else GameManager.Instance?.Toast("Out of pebbles! Look for a pile of stones (E).", 2.5f);
            }
            if (Drawing)
            {
                draw += Time.deltaTime / drawTime;
                if (!mouse.leftButton.isPressed) Fire();
            }
            UpdateModel();
        }

        void Fire()
        {
            float k = Mathf.Max(0.25f, DrawAmount);   // even a quick flick goes somewhere
            draw = -1f;
            nextShot = Time.time + cooldown;
            ammo--;
            kick = 1f;

            var eyes = pc.eyes;
            var pebble = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            pebble.name = "Pebble";
            pebble.transform.position = eyes.position + eyes.forward * 0.45f - eyes.up * 0.05f;
            pebble.transform.localScale = Vector3.one * 0.07f;
            if (pebbleMaterial != null) pebble.GetComponent<MeshRenderer>().sharedMaterial = pebbleMaterial;
            var col = pebble.GetComponent<SphereCollider>();
            col.material = new PhysicsMaterial { bounciness = 0.35f, dynamicFriction = 0.6f, staticFriction = 0.6f };
            Physics.IgnoreCollision(col, pc.Body);

            var rb = pebble.AddComponent<Rigidbody>();
            rb.mass = 0.05f;
            rb.linearDamping = 0.02f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;   // fast and small: don't tunnel through walls
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.linearVelocity = eyes.forward * Mathf.Lerp(minSpeed, maxSpeed, k);

            var trail = pebble.AddComponent<TrailRenderer>();
            trail.time = 0.12f; trail.startWidth = 0.025f; trail.endWidth = 0f;
            if (trailMaterial != null) trail.sharedMaterial = trailMaterial;
            trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            pebble.AddComponent<Pebble>().damage = Mathf.Lerp(minDamage, maxDamage, k);
        }

        public void AddAmmo(int n) => ammo = Mathf.Min(maxAmmo, ammo + n);

        void UpdateModel()
        {
            if (model == null) return;
            float d = DrawAmount;
            if (pouch != null) pouch.localPosition = pouchRest + new Vector3(0f, -0.02f, -0.2f) * d;
            if (band != null && tipL != null && tipR != null && pouch != null)
            {
                band.positionCount = 3;
                band.SetPosition(0, tipL.localPosition);
                band.SetPosition(1, pouch.localPosition);
                band.SetPosition(2, tipR.localPosition);
            }
            kick = Mathf.MoveTowards(kick, 0f, Time.deltaTime * 6f);
            model.localPosition = modelRest + new Vector3(0.02f * d, 0.03f * d, 0.05f * d) + new Vector3(0f, 0.02f, -0.04f) * kick;
        }
    }
}
