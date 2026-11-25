using UnityEngine;
using UnityEngine.InputSystem;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>
    /// Nimal, the player: first-person movement (walk, sprint with stamina, crouch, jump),
    /// recoil and knockback from hits, hiding (crouch in a hiding zone, or get into a locker), the torch,
    /// and using things (doors, barricades, tea bags, lockers, ammo boxes, stone piles).
    /// </summary>
    [RequireComponent(typeof(CharacterController), typeof(Health))]
    public class PlayerController : MonoBehaviour
    {
        const float StandHeight = 1.8f, CrouchHeight = 1.0f, Radius = 0.3f, StandEye = 1.65f, CrouchEye = 0.85f;

        public float walkSpeed = 3.6f, sprintSpeed = 6.4f, crouchSpeed = 1.9f, jumpHeight = 1.05f, gravity = -20f;
        public float lookSensitivity = 0.1f;
        public float maxStamina = 100f, sprintDrain = 20f, staminaRegen = 16f;
        public Transform eyes;
        public Light torch;

        public CharacterController Body { get; private set; }
        public Health Health { get; private set; }
        public float Stamina { get; private set; }
        public bool Crouched { get; private set; }
        public bool Sprinting { get; private set; }
        public bool InHideZone { get; private set; }
        public bool InLocker => locker != null;
        public bool Hidden => InLocker || (Crouched && InHideZone);
        public bool TorchOn => torch != null && torch.enabled;
        public string Prompt { get; private set; }
        public float SearchProgress { get; private set; }
        public float LastNoiseTime { get; set; } = -99f;
        public Vector3 Chest => transform.position + Vector3.up * (Crouched ? 0.7f : 1.3f);

        float pitch, vertical, shake;
        Vector3 knock;
        GreyboxInteractable locker;
        Vector3 beforeLocker;
        TeaBag searching;

        void Awake()
        {
            Body = GetComponent<CharacterController>();
            Health = GetComponent<Health>();
            Stamina = maxStamina;
            if (eyes == null) eyes = transform.Find("Eyes");
            // Legacy scenes contain a shadowless fill light which leaks into nearby walls.
            foreach (var light in GetComponentsInChildren<Light>(true))
                if (light.name == "ViewmodelLight") light.enabled = false;
            if (torch != null)
            {
                torch.spotAngle = 65f; torch.innerSpotAngle = 38f;
                torch.range = 40f; torch.intensity = 8f;
                torch.shadows = LightShadows.Soft;
            }
            Health.Damaged += (a, from) => shake = 0.25f;
        }

        GameManager GM => GameManager.Instance;
        void Start()
        {
            if (GetComponent<FirstPersonHands>() == null) gameObject.AddComponent<FirstPersonHands>();
        }
        bool Playing => GM == null || GM.IsPlaying;

        void Update()
        {
            var kb = Keyboard.current; var mouse = Mouse.current;
            if (kb == null || mouse == null) return;
            if (!Playing || Health.Dead) { Prompt = null; return; }

            Look(mouse);
            CheckZones();

            if (InLocker)
            {
                Prompt = "E  Get out of the locker";
                if (kb.eKey.wasPressedThisFrame) LeaveLocker();
                return;
            }

            bool crouchHeld = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
            if (crouchHeld != Crouched) SetCrouch(crouchHeld);
            Move(kb);
            Interact(kb);
            if (kb.fKey.wasPressedThisFrame && torch != null) torch.enabled = !torch.enabled;
            Checkpoints(kb);

            // Camera: crouch height and a little shake when hit
            float eyeY = Mathf.MoveTowards(eyes.localPosition.y, Crouched ? CrouchEye : StandEye, Time.deltaTime * 4f);
            shake = Mathf.Max(0f, shake - Time.deltaTime);
            Vector2 s = Random.insideUnitCircle * shake * 0.15f;
            eyes.localPosition = new Vector3(s.x, eyeY + s.y, 0f);
        }

        // Demo / viva checkpoints (F1-F6): jump straight to each part of the level
        static readonly (Key key, Vector3 pos, float yaw, string name)[] Points =
        {
            (Key.F1, new Vector3(8.5f, float.NaN, -55f), 35f, "Jungle camp"),
            (Key.F2, new Vector3(30f, float.NaN, -43f), 0f, "Stream crossing"),
            (Key.F3, new Vector3(30f, float.NaN, -3f), 0f, "Edge of the tea estate"),
            (Key.F4, new Vector3(47.5f, float.NaN, 36f), 0f, "Fence breach"),
            (Key.F5, new Vector3(57.5f, 8.1f, 64.5f), 0f, "Dispatch warehouse"),
            (Key.F6, new Vector3(13f, 8.1f, 46f), -90f, "Main gate"),
        };

        void Checkpoints(Keyboard kb)
        {
            foreach (var p in Points)
            {
                if (!kb[p.key].wasPressedThisFrame) continue;
                var pos = p.pos;
                if (float.IsNaN(pos.y) && Physics.Raycast(new Vector3(pos.x, 80f, pos.z), Vector3.down, out var g, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    pos.y = g.point.y + 0.1f;
                Body.enabled = false;
                transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, p.yaw, 0f));
                Body.enabled = true;
                vertical = 0f; knock = Vector3.zero; pitch = 0f;
                GM?.Toast("Checkpoint: " + p.name, 2f);
            }
        }

        void Look(Mouse mouse)
        {
            if (Cursor.lockState != CursorLockMode.Locked) return;
            Vector2 d = mouse.delta.ReadValue() * lookSensitivity;
            transform.Rotate(0f, d.x, 0f);
            pitch = Mathf.Clamp(pitch - d.y, -85f, 85f);
            eyes.localEulerAngles = new Vector3(pitch, 0f, 0f);
        }

        void Move(Keyboard kb)
        {
            var input = new Vector2((kb.dKey.isPressed ? 1 : 0) - (kb.aKey.isPressed ? 1 : 0),
                                    (kb.wKey.isPressed ? 1 : 0) - (kb.sKey.isPressed ? 1 : 0));
            input = Vector2.ClampMagnitude(input, 1f);
            bool wantSprint = (kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed) && input.y > 0.1f && !Crouched;
            Sprinting = wantSprint && Stamina > 1f;
            Stamina = Sprinting ? Mathf.Max(0f, Stamina - sprintDrain * Time.deltaTime)
                                : Mathf.Min(maxStamina, Stamina + staminaRegen * Time.deltaTime);
            float speed = Crouched ? crouchSpeed : Sprinting ? sprintSpeed : walkSpeed;
            Vector3 move = (transform.right * input.x + transform.forward * input.y) * speed;

            if (Body.isGrounded)
            {
                if (vertical < 0f) vertical = -2f;
                if (kb.spaceKey.wasPressedThisFrame && Stamina >= 5f)
                {
                    vertical = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    Stamina -= 5f;
                }
            }
            vertical += gravity * Time.deltaTime;
            move.y = vertical;
            move += knock;
            knock = Vector3.MoveTowards(knock, Vector3.zero, 18f * Time.deltaTime);
            Body.Move(move * Time.deltaTime);
            if (transform.position.y < -40f && GM != null) GM.Lose("You fell out of the world.");
        }

        /// <summary>Gun kick: the view jumps up (and a little sideways) when a shot goes off.</summary>
        public void AddRecoil(float up, float side)
        {
            pitch = Mathf.Clamp(pitch - up, -85f, 85f);
            eyes.localEulerAngles = new Vector3(pitch, 0f, 0f);
            transform.Rotate(0f, side, 0f);
        }

        public void SpendStamina(float amount) => Stamina = Mathf.Max(0f, Stamina - amount);

        public void AddKnockback(Vector3 v)
        {
            v.y = Mathf.Max(v.y, 0f);
            knock += v;
            if (v.y > 0.1f) vertical = Mathf.Max(vertical, v.y);
        }

        void SetCrouch(bool down)
        {
            if (!down && Physics.SphereCast(transform.position + Vector3.up * Radius, Radius * 0.9f, Vector3.up, out _,
                                            StandHeight - Radius, ~0, QueryTriggerInteraction.Ignore)) return;   // no room to stand
            Crouched = down;
            Body.height = down ? CrouchHeight : StandHeight;
            Body.center = new Vector3(0f, Body.height * 0.5f, 0f);
        }

        // Trigger zones live on the Ignore Raycast layer: check them by overlap, every frame
        void CheckZones()
        {
            InHideZone = false;
            var hits = Physics.OverlapCapsule(transform.position + Vector3.up * 0.3f, transform.position + Vector3.up * 1.2f, 0.35f,
                                              1 << 2, QueryTriggerInteraction.Collide);
            foreach (var h in hits)
            {
                if (h.CompareTag("HideSpot")) InHideZone = true;
                if (GM == null) continue;
                if (h.GetComponent<AlarmZone>() != null) GM.RaiseAlarm();
                if (h.GetComponent<ExitZone>() != null) GM.TryEscape();
            }
        }

        /// <summary>
        /// What the player is looking at, ignoring his own body. A plain Raycast from the eyes hits
        /// the player's own CharacterController when he looks down (the eyes sit inside its top),
        /// which made anything on the floor - tea bags, ammo boxes, stone piles - impossible to use.
        /// </summary>
        bool LookingAt(float range, out RaycastHit best)
        {
            best = default;
            float bestDist = float.MaxValue;
            foreach (var h in Physics.RaycastAll(eyes.position, eyes.forward, range, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (h.collider.transform.IsChildOf(transform)) continue;   // my own body / hands
                if (h.distance < bestDist) { bestDist = h.distance; best = h; }
            }
            return bestDist < float.MaxValue;
        }

        void Interact(Keyboard kb)
        {
            Prompt = null;
            var target = PickupTargeting.Find(eyes, transform);
            var bag = target as TeaBag; var pile = target as PebblePile;
            var ammo = target as AmmoBox; var thing = target as GreyboxInteractable;
            if (bag != searching) { searching = null; SearchProgress = 0f; }
            if (ammo != null)
            {
                Prompt = "E  Take the ammo";
                var gun = GetComponent<PlayerGun>();
                if (kb.eKey.wasPressedThisFrame && gun != null) ammo.Take(gun);
                return;
            }
            if (pile != null)
            {
                Prompt = "E  Pick up pebbles";
                var sling = GetComponent<PlayerSlingshot>();
                if (kb.eKey.wasPressedThisFrame && sling != null) pile.Take(sling);
                return;
            }

            // Tea bags: hold E to rummage through
            if (bag != null && !bag.searched)
            {
                Prompt = "Hold E  Search the tea bag";
                if (kb.eKey.isPressed)
                {
                    if (searching != bag) { searching = bag; SearchProgress = 0f; }
                    SearchProgress += Time.deltaTime / 1.4f;
                    LastNoiseTime = Time.time;   // rustling
                    if (SearchProgress >= 1f) { bag.Search(); SearchProgress = 0f; searching = null; }
                }
                else { SearchProgress = 0f; searching = null; }
                return;
            }
            SearchProgress = 0f; searching = null;
            if (bag != null) { Prompt = "Already searched"; return; }
            if (thing == null || !thing.gameObject.activeInHierarchy) return;

            switch (thing.kind)
            {
                case GreyboxKind.HideSpot:
                    Prompt = "E  Hide in the locker";
                    if (kb.eKey.wasPressedThisFrame) EnterLocker(thing);
                    break;
                case GreyboxKind.Barricade:
                    Prompt = "E or hit it  Tear down the barricade";
                    if (kb.eKey.wasPressedThisFrame) { thing.Interact(); LastNoiseTime = Time.time; }
                    break;
                case GreyboxKind.Pickup:
                    break;
                default:
                    Prompt = thing.locked ? $"{thing.label}: locked" : (thing.IsOpen ? $"E  Close {thing.label.ToLower()}" : $"E  Open {thing.label.ToLower()}");
                    if (kb.eKey.wasPressedThisFrame && !thing.locked) thing.Interact();
                    break;
            }
        }

        void EnterLocker(GreyboxInteractable l)
        {
            var point = l.transform.Find("HidePoint");
            if (point == null) return;
            locker = l;
            beforeLocker = transform.position;
            Body.enabled = false;
            if (Crouched) SetCrouch(false);
            transform.position = point.position - Vector3.up * StandEye;
            transform.rotation = Quaternion.Euler(0f, l.transform.eulerAngles.y, 0f);
            pitch = 0f;
        }

        void LeaveLocker()
        {
            var front = locker.transform.position + locker.transform.forward * 0.75f;
            transform.position = Physics.CheckCapsule(front + Vector3.up * 0.4f, front + Vector3.up * 1.4f, 0.28f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                ? beforeLocker : front;
            locker = null;
            Body.enabled = true;
        }
    }
}
