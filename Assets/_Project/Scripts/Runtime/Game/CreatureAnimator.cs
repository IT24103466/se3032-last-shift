using System.Collections.Generic;
using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Animates the named pivots shared by the blockout and custom NPC models, without a rig.
    /// Movement drives the gait; combat controllers call PlayAttack/Shoot; Health drives death.
    /// Only visual children move, leaving navigation and gameplay colliders on the root intact.
    /// </summary>
    public class CreatureAnimator : MonoBehaviour
    {
        public float stride = 32f, cadence = 2.4f, lunge = 0.45f;
        [Tooltip("Set by the combat controller while the character is fighting with a gun")]
        public bool aiming;
        [Tooltip("Long guns are held with both hands")]
        public bool twoHanded;
        [Min(0f)] public float idleBreathing = 0.008f;
        [Min(0.1f)] public float deathDuration = 1.35f;

        public enum Motion { Standing, Walking, Attacking, Dying, Dead }
        public Motion CurrentMotion { get; private set; }

        class Limb
        {
            public Transform t;
            public Quaternion rest, deathFrom;
            public float phase, scale; public Vector3 position, restScale;
        }
        readonly List<Limb> limbs = new List<Limb>();
        Transform model, head, tail, trunk, armR, armL;
        Quaternion modelRotation, headRest, tailRest, trunkRest, armRRest, armLRest, deathRotation;
        Vector3 modelRest, modelScale, lastPos, deathPosition;
        Renderer[] renderers;
        Health health;
        CorpsePhysics corpse;
        NpcPresentation presentation;
        EnemyAI presentationAI;
        HumanLocomotion humanMotion;
        float crouchPose;
        MammothAI presentationMammoth;
        bool corpseAttempted;
        Vector3 lastVelocity;
        Transform torso, upperBody;
        UnityEngine.AI.NavMeshAgent nav;
        bool humanoid, ownsTurning, originalNavRotation, turning;
        Vector3 targetPoint, hitDirection = Vector3.back, fallDirection, fallPivot;
        float targetUntil = -1f, bodyTurnSpeed = 120f, torsoYaw, gazeYaw, gazePitch;
        float armYaw, armPitch, nextGlance, glanceUntil, glanceYaw, glancePitch;
        float bodyHeight, fallPitch, fallRoll;
        Quaternion upperDeathRotation, headDeathRotation;
        System.Random poseRandom;
        float phase, speed, idlePhase, aimBlend, deathFloor;
        float attackT = -1f, shootT = -1f, flinchT = -1f, dieT = -1f, roarT = -1f;

        void Awake()
        {
            model = transform.Find("Model");
            if (model == null) { enabled = false; return; }
            modelRest = model.localPosition;
            modelRotation = model.localRotation;
            modelScale = model.localScale;
            renderers = model.GetComponentsInChildren<Renderer>(true);
            foreach (var t in model.GetComponentsInChildren<Transform>(true))
            {
                switch (t.name)
                {
                    case "Leg_FL": case "Leg_BR": Add(t, 0f, 1f); break;
                    case "Leg_FR": case "Leg_BL": Add(t, Mathf.PI, 1f); break;
                    case "Leg_L": Add(t, 0f, 1f); break;
                    case "Leg_R": Add(t, Mathf.PI, 1f); break;
                    case "Arm_L": armL = t; armLRest = t.localRotation; Add(t, Mathf.PI, 0.8f); break;
                    case "Arm_R": armR = t; armRRest = t.localRotation; Add(t, 0f, 0.8f); break;
                    case "Head": head = t; headRest = t.localRotation; break;
                    case "Tail": tail = t; tailRest = t.localRotation; break;
                    case "Trunk": trunk = t; trunkRest = t.localRotation; break;
                }
            }
            humanoid = armL != null && armR != null;
            nav = GetComponent<UnityEngine.AI.NavMeshAgent>();
            torso = model.Find("Torso") ?? model.Find("Body");
            // Imported parts are siblings. Group the chest, head and shoulders at the waist
            // so upper-body turns do not pull the arms away from the torso.
            if (humanoid && torso != null && torso.parent == model && head != null && head.parent == model)
            {
                var group = new GameObject("UpperBodyPose");
                upperBody = group.transform;
                upperBody.SetParent(model, false);
                upperBody.localPosition = Vector3.up * Mathf.Max(0.1f, head.localPosition.y * 0.58f);
                torso.SetParent(upperBody, true);
                head.SetParent(upperBody, true);
                armL.SetParent(upperBody, true);
                armR.SetParent(upperBody, true);
            }
            foreach (var limb in limbs) { limb.position = limb.t.localPosition; limb.restScale = limb.t.localScale; }
            health = GetComponent<Health>();
            poseRandom = new System.Random(Mathf.RoundToInt(transform.position.x * 397f + transform.position.z * 17f));
            nextGlance = Time.time + 6f + (float)poseRandom.NextDouble() * 9f;
            idlePhase = Mathf.Repeat(Vector3.Dot(transform.position, new Vector3(1.7f, 0.3f, 2.3f)), Mathf.PI * 2f);
            lastPos = transform.position;
        }

        // Runtime spawners can resize the visual model after Instantiate/Awake (the mammoth).
        void Start() { if (model != null) { modelScale = model.localScale; presentation = new NpcPresentation(transform, model); presentationAI = GetComponent<EnemyAI>(); presentationMammoth = GetComponent<MammothAI>(); } }

        void OnEnable()
        {
            lastPos = transform.position;
            if (health != null) { health.Died += Die; health.Damaged += OnDamaged; }
        }

        void OnDisable()
        {
            if (health != null) { health.Died -= Die; health.Damaged -= OnDamaged; }
            RestoreNavigationRotation();
        }

        void OnDestroy() { corpse?.Dispose(); presentation?.Dispose(); }

        void OnDamaged(float amount, Vector3 from)
        {
            var away = transform.position - from; away.y = 0f;
            if (away.sqrMagnitude > 0.001f) hitDirection = transform.InverseTransformDirection(away.normalized);
            Flinch();
            presentation?.Injured();
        }

        /// <summary>Controllers supply only targets they already know about; no extra player detection.</summary>
        public bool FaceTarget(Vector3 point, float response = 8f)
        {
            if (!humanoid || dieT >= 0f) return false;
            targetPoint = point;
            targetUntil = Time.time + 0.25f;
            bodyTurnSpeed = Mathf.Max(60f, response * 18f);
            return true;
        }

        public bool CanAimAt(Vector3 point)
        {
            if (!humanoid) return true;
            if (dieT >= 0f) return false;
            Vector3 local = transform.InverseTransformDirection(point - (head != null ? head.position : transform.position));
            float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            return Mathf.Abs(Mathf.DeltaAngle(torsoYaw + armYaw, yaw)) < 14f && Mathf.Abs(pitch - armPitch) < 15f;
        }

        void RestoreNavigationRotation()
        {
            if (ownsTurning && nav != null) nav.updateRotation = originalNavRotation;
            ownsTurning = turning = false;
        }

        float Range(float min, float max) => Mathf.Lerp(min, max, (float)poseRandom.NextDouble());

        void TrackTarget(float dt, float gait)
        {
            bool tracking = Time.time <= targetUntil;
            float yaw = 0f, pitch = 0f;
            if (tracking)
            {
                // Let navigation face the path while walking. When stationary, small aim changes
                // belong to the arms; only larger angles ask the hips/feet to catch up.
                bool stationary = nav == null || !nav.enabled || nav.velocity.sqrMagnitude < 0.04f;
                if (stationary)
                {
                    if (!ownsTurning && nav != null) { originalNavRotation = nav.updateRotation; ownsTurning = true; nav.updateRotation = false; }
                    Vector3 flat = targetPoint - transform.position; flat.y = 0f;
                    if (flat.sqrMagnitude > 0.001f)
                    {
                        float angle = Vector3.SignedAngle(transform.forward, flat, Vector3.up);
                        if (Mathf.Abs(angle) > 55f) turning = true;
                        if (Mathf.Abs(angle) < 28f) turning = false;
                        if (turning)
                        {
                            float step = Mathf.Min(Mathf.Max(0f, Mathf.Abs(angle) - 25f), bodyTurnSpeed * dt);
                            transform.Rotate(0f, Mathf.Sign(angle) * step, 0f, Space.World);
                        }
                    }
                }
                else RestoreNavigationRotation();
                Vector3 local = transform.InverseTransformDirection(targetPoint - (head != null ? head.position : transform.position + Vector3.up));
                yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            }
            else
            {
                RestoreNavigationRotation();
                if (speed < 0.2f && !aiming && attackT < 0f && Time.time >= nextGlance)
                {
                    glanceYaw = Range(-32f, 32f); glancePitch = Range(-6f, 9f);
                    glanceUntil = Time.time + Range(1.2f, 2.3f);
                    nextGlance = glanceUntil + Range(8f, 16f);
                }
                if (Time.time < glanceUntil && speed < 0.2f && !aiming) { yaw = glanceYaw; pitch = glancePitch; }
            }
            float blend = 1f - Mathf.Exp(-7f * dt);
            float wantedTorso = tracking ? Mathf.Clamp(Mathf.Sign(yaw) * Mathf.Max(0f, Mathf.Abs(yaw) - 22f), -32f, 32f) : 0f;
            if (upperBody == null) wantedTorso = 0f;
            torsoYaw = Mathf.LerpAngle(torsoYaw, wantedTorso, blend);
            gazeYaw = Mathf.LerpAngle(gazeYaw, Mathf.Clamp(yaw - torsoYaw, -60f, 60f), blend);
            gazePitch = Mathf.Lerp(gazePitch, Mathf.Clamp(pitch, -30f, 35f), blend);
            armYaw = Mathf.LerpAngle(armYaw, tracking ? Mathf.Clamp(yaw - torsoYaw, -28f, 28f) : 0f, blend);
            armPitch = Mathf.Lerp(armPitch, tracking ? Mathf.Clamp(pitch, -25f, 30f) : 0f, blend);
            if (upperBody != null) upperBody.localRotation = Quaternion.Euler(gait * 2f, torsoYaw, 0f);
            if (head != null) head.localRotation *= Quaternion.Euler(gazePitch, gazeYaw, 0f);
        }
        void Add(Transform t, float p, float s) => limbs.Add(new Limb { t = t, rest = t.localRotation, phase = p, scale = s });
        public void PlayAttack() { if (dieT < 0f) attackT = 0f; }
        public void Flinch() { if (dieT < 0f) flinchT = 0f; }
        public void Roar() { if (dieT < 0f) roarT = 0f; }
        public void Shoot() { if (dieT < 0f) shootT = 0f; }

        public void Die()
        {
            if (model == null || dieT >= 0f) return; // Health and the AI can both notify us.
            dieT = 0f;
            aiming = false;
            attackT = shootT = flinchT = roarT = -1f;
            deathRotation = model.localRotation;
            deathPosition = model.localPosition;
            RestoreNavigationRotation();
            deathFloor = LowestPoint();
            float top = deathFloor;
            foreach (var r in renderers) if (r != null && r.enabled) top = Mathf.Max(top, r.bounds.max.y);
            bodyHeight = Mathf.Max(0.4f, top - deathFloor);
            fallDirection = hitDirection.sqrMagnitude > 0.01f ? hitDirection.normalized : Vector3.back;
            // A frontal hit knocks backwards; side hits produce a diagonal fall, not a rigid roll.
            fallPitch = fallDirection.z * (humanoid ? 88f : 18f);
            fallRoll = -fallDirection.x * 65f;
            if (!humanoid) fallRoll = fallDirection.x >= 0f ? -78f : 78f;
            if (Mathf.Abs(fallPitch) + Mathf.Abs(fallRoll) < 50f) fallPitch = -80f;
            fallPivot = model.InverseTransformPoint(transform.position + Vector3.up * (bodyHeight * (humanoid ? 0.46f : 0.35f)));
            upperDeathRotation = upperBody != null ? upperBody.localRotation : Quaternion.identity;
            headDeathRotation = head != null ? head.localRotation : Quaternion.identity;
            // Sample actual terrain beneath the body instead of treating the whole level as flat.
            var hits = Physics.RaycastAll(transform.position + Vector3.up * 0.4f, Vector3.down, 4f, ~(1 << 2), QueryTriggerInteraction.Ignore);
            float nearest = float.PositiveInfinity;
            foreach (var hit in hits)
                if (!hit.transform.IsChildOf(transform) && hit.normal.y > 0.4f && hit.distance < nearest)
                { nearest = hit.distance; deathFloor = hit.point.y; }
            foreach (var limb in limbs) limb.deathFrom = limb.t.localRotation;
            CurrentMotion = Motion.Dying;
        }

        // Used by the animation sandbox to replay the death pose without killing the NPC.
        public void ResetPose()
        {
            if (model == null || (health != null && health.Dead)) return;
            corpse?.Dispose();
            corpseAttempted = false; lastVelocity = Vector3.zero;
            dieT = attackT = shootT = flinchT = roarT = -1f;
            phase = speed = aimBlend = 0f;
            targetUntil = -1f; torsoYaw = gazeYaw = gazePitch = armYaw = armPitch = 0f;
            RestoreNavigationRotation();
            if (upperBody != null) upperBody.localRotation = Quaternion.identity;
            aiming = false;
            model.SetLocalPositionAndRotation(modelRest, modelRotation);
            model.localScale = modelScale;
            foreach (var limb in limbs) { limb.t.localRotation = limb.rest; limb.t.localPosition = limb.position; limb.t.localScale = limb.restScale; }
            if (head != null) head.localRotation = headRest;
            if (tail != null) tail.localRotation = tailRest;
            if (trunk != null) trunk.localRotation = trunkRest;
            lastPos = transform.position;
            presentation?.Reset();
            CurrentMotion = Motion.Standing;
        }

        float LowestPoint()
        {
            float lowest = float.PositiveInfinity;
            foreach (var r in renderers)
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                    lowest = Mathf.Min(lowest, r.bounds.min.y);
            return float.IsInfinity(lowest) ? model.position.y : lowest;
        }

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f || model == null) return;
            if (health != null && health.Dead) Die();
            if (dieT >= 0f) { AnimateDeath(dt); TickPresentation(dt, false, true); return; }

            Vector3 delta = transform.position - lastPos;
            delta.y = 0f;
            lastPos = transform.position;
            lastVelocity = delta.magnitude > Mathf.Max(2f, dt * 20f) ? Vector3.zero : delta / dt;
            float measured = lastVelocity.magnitude;
            float blend = 1f - Mathf.Exp(-10f * dt);
            speed = Mathf.Lerp(speed, Mathf.Min(measured, 12f), blend);
            float gait = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(speed / 2.5f));
            phase += speed * cadence * dt;
            idlePhase += dt * 1.7f;
            float breath = Mathf.Sin(idlePhase) * (1f - gait);
            float amp = gait * stride;
            foreach (var limb in limbs)
                limb.t.localRotation = limb.rest * Quaternion.Euler(
                    Mathf.Sin(phase + limb.phase) * amp * limb.scale, 0f, 0f);

            // A small breathing/weight-shift pose remains visible when the agent is stopped.
            model.localScale = Vector3.Scale(modelScale, new Vector3(1f, 1f + breath * 0.003f, 1f));
            model.localRotation = modelRotation * Quaternion.Euler(0f, 0f,
                breath * 0.5f + Mathf.Sin(phase) * gait * 1.2f);
            Vector3 offset = Vector3.up * (breath * idleBreathing + Mathf.Abs(Mathf.Sin(phase)) * 0.04f * gait);
            if (tail != null) tail.localRotation = tailRest * Quaternion.Euler(0f, Mathf.Sin(idlePhase * 1.3f) * 18f, 0f);
            if (trunk != null) trunk.localRotation = trunkRest * Quaternion.Euler(Mathf.Sin(idlePhase * 0.6f) * 10f, breath * 8f, 0f);
            if (head != null) head.localRotation = headRest * Quaternion.Euler(
                breath * 1.5f + Mathf.Sin(phase * 2f) * amp * 0.08f, breath * 2f, 0f);
            if (armL != null) armL.localRotation *= Quaternion.Euler(-7f + breath * 2f, Mathf.Sin(idlePhase * 0.61f) * 2f, -5f + Mathf.Sin(idlePhase * 0.87f) * 2f);
            if (armR != null) armR.localRotation *= Quaternion.Euler(-9f - breath * 2f, Mathf.Sin(idlePhase * 0.73f) * 2f, 5f + Mathf.Cos(idlePhase * 0.83f) * 2f);
            if (humanoid) TrackTarget(dt, gait);

            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, dt * 5f);
            float kick = Pulse(ref shootT, dt, 0.2f);
            if (aimBlend > 0f)
            {
                if (armR != null) armR.localRotation = Quaternion.Slerp(armR.localRotation,
                    Quaternion.Euler(0f, armYaw, 0f) * armRRest * Quaternion.Euler(-86f + armPitch + 14f * kick + Mathf.Sin(idlePhase) * 0.7f, -6f, 0f), aimBlend);
                if (armL != null && twoHanded) armL.localRotation = Quaternion.Slerp(armL.localRotation,
                    Quaternion.Euler(0f, armYaw, 0f) * armLRest * Quaternion.Euler(-78f + armPitch + 10f * kick, 32f, 0f), aimBlend);
                offset += Vector3.back * kick * 0.06f;
            }

            bool attacking = attackT >= 0f || kick > 0f;
            if (attackT >= 0f)
            {
                attackT = Mathf.Min(1f, attackT + dt / 0.6f);
                // Wind up for 0.3s, matching EnemyAI's hit delay, then follow through and recover.
                float swing = attackT < 0.5f ? Mathf.SmoothStep(0f, 1f, attackT * 2f)
                    : 1f - Mathf.SmoothStep(0f, 1f, (attackT - 0.5f) * 2f);
                float thrust = Mathf.Sin(Mathf.Clamp01((attackT - 0.25f) / 0.75f) * Mathf.PI);
                offset += Vector3.forward * lunge * thrust;
                if (armR != null) armR.localRotation = armRRest * Quaternion.Euler(-145f * swing, 0f, -15f * swing);
                if (armL != null) armL.localRotation = armLRest * Quaternion.Euler(-35f * swing, 0f, 10f * swing);
                if (head != null) head.localRotation *= Quaternion.Euler(-12f * swing, 0f, 0f);
                if (attackT >= 1f) attackT = -1f;
            }
            offset += Vector3.back * 0.15f * Pulse(ref flinchT, dt, 0.25f);
            float roar = Pulse(ref roarT, dt, 0.6f);
            if (head != null && roar > 0f) head.localRotation = headRest * Quaternion.Euler(-25f * roar, 0f, 0f);
            if (humanoid)
            {
                if (humanMotion == null) humanMotion = GetComponent<HumanLocomotion>();
                crouchPose = humanMotion != null ? humanMotion.Crouch : 0f;
                offset.y -= 0.38f * crouchPose;
                if (upperBody != null) upperBody.localRotation *= Quaternion.Euler(12f * crouchPose, 0f, 0f);
                foreach (var limb in limbs)
                    if (limb.t.name.StartsWith("Leg_"))
                    {
                        // These models have rigid legs; shorten the stance without sinking feet.
                        limb.t.localScale = Vector3.Scale(limb.restScale, new Vector3(1f, 1f - 0.4f * crouchPose, 1f));
                        if (humanMotion != null && humanMotion.Airborne)
                            limb.t.localRotation *= Quaternion.Euler(-30f * Mathf.Sin(humanMotion.AirPhase * Mathf.PI), 0f, 0f);
                    }
            }
            model.localPosition = modelRest + offset;
            CurrentMotion = attacking ? Motion.Attacking : speed > 0.1f ? Motion.Walking : Motion.Standing;
            TickPresentation(dt, aiming || attacking || (Time.time <= targetUntil) || (presentationAI != null && presentationAI.IsPursuing) || (presentationMammoth != null && presentationMammoth.IsHunting), false);
        }

        // ---- Performance: faces (eye/brow mesh deform) and cloth are only worth updating up close.
        // Deforming every NPC's face mesh and cloth every frame cost about 1.8 ms per NPC (~35 ms a
        // frame for 19 NPCs in the profiler). Near the camera they update every 2nd frame (30 Hz is plenty
        // for blinking and cloth); a bit further away every 6th frame; beyond that, where the detail can't
        // be seen, not at all. NPCs are staggered so they don't all update on the same frame, and the
        // skipped time is carried over, so expressions and cloth still catch up smoothly.
        const float PresentationFull = 20f, PresentationReduced = 40f;
        float presentationDt;
        int presentationSlot = -1;   // stagger: NPCs update on different frames (spreads the work)

        void TickPresentation(float dt, bool pursuing, bool dead)
        {
            if (presentation == null) return;
            presentationDt = Mathf.Min(presentationDt + dt, 0.25f);
            var cam = Camera.main;
            float d2 = cam != null ? (cam.transform.position - transform.position).sqrMagnitude : 0f;
            if (d2 > PresentationReduced * PresentationReduced) return;
            if (presentationSlot < 0) presentationSlot = Random.Range(0, 6);
            int every = d2 > PresentationFull * PresentationFull ? 6 : 2;
            if ((Time.frameCount + presentationSlot) % every != 0) return;
            presentation.Tick(presentationDt, pursuing, dead);
            presentationDt = 0f;
        }

        static float Pulse(ref float timer, float dt, float duration)
        {
            if (timer < 0f) return 0f;
            timer = Mathf.Min(1f, timer + dt / duration);
            float value = Mathf.Sin(timer * Mathf.PI);
            if (timer >= 1f) timer = -1f;
            return Mathf.Max(0f, value);
        }

        void AnimateDeath(float dt)
        {
            if (corpse != null && corpse.Active)
            {
                CurrentMotion = corpse.Sleeping ? Motion.Dead : Motion.Dying;
                return; // PhysX owns the visuals now; never write a keyed pose over it.
            }
            if (!corpseAttempted && dieT >= 0.3f)
            {
                corpseAttempted = true;
                corpse = new CorpsePhysics();
                if (corpse.Begin(transform, model, transform.TransformDirection(fallDirection) * 0.9f, lastVelocity))
                {
                    CurrentMotion = Motion.Dying;
                    return;
                }
            }
            if (dieT >= 1f) return;
            dieT = Mathf.Min(1f, dieT + dt / Mathf.Max(0.7f, deathDuration));
            float buckle = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(dieT / 0.28f));
            float fall = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((dieT - 0.18f) / 0.6f));
            float settle = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((dieT - 0.78f) / 0.22f));
            model.localScale = modelScale;
            var targetRotation = modelRotation * Quaternion.Euler(fallPitch, 0f, fallRoll);
            model.localRotation = Quaternion.Slerp(deathRotation, targetRotation, fall);
            // Rotate about the hips while they drop: the body loses support before tipping.
            Vector3 scaledPivot = Vector3.Scale(fallPivot, modelScale);
            model.localPosition = deathPosition + deathRotation * scaledPivot - model.localRotation * scaledPivot;
            Vector3 drop = transform.InverseTransformVector(Vector3.down * bodyHeight * (0.22f * buckle + 0.23f * fall));
            model.localPosition += drop;
            model.position += transform.TransformDirection(fallDirection) * bodyHeight * (0.12f * fall);
            if (upperBody != null)
                upperBody.localRotation = Quaternion.Slerp(upperDeathRotation,
                    Quaternion.Euler(24f * (1f - settle) + 9f * settle, -fallDirection.x * 16f, fallDirection.x * 12f), buckle);
            foreach (var limb in limbs)
            {
                bool arm = limb.t == armL || limb.t == armR;
                float side = limb.t.name.Contains("_L") || limb.t.name.EndsWith("FL") || limb.t.name.EndsWith("BL") ? -1f : 1f;
                float curl = arm ? Mathf.Lerp(-65f, -18f, settle) : Mathf.Lerp(-42f, -24f, fall);
                var pose = limb.rest * Quaternion.Euler(curl + side * (arm ? 12f : 16f), side * (arm ? 8f : 5f), side * (arm ? 22f : 9f));
                limb.t.localRotation = Quaternion.Slerp(limb.deathFrom, pose, arm ? Mathf.Clamp01(buckle * 0.6f + fall * 0.4f) : buckle);
                // The existing models have one rigid segment per limb: a hip/shoulder fold,
                // rather than pretending there are knees/elbows or rotating the entire actor rigidly.
            }
            if (head != null) head.localRotation = Quaternion.Slerp(headDeathRotation,
                headRest * Quaternion.Euler(18f, -fallDirection.x * 18f, 9f), fall);
            if (tail != null) tail.localRotation = Quaternion.Slerp(tail.localRotation, tailRest * Quaternion.Euler(0f, 0f, 18f), fall);
            if (trunk != null) trunk.localRotation = Quaternion.Slerp(trunk.localRotation, trunkRest * Quaternion.Euler(35f, 12f, 0f), fall);
            float lowest = LowestPoint();
            // Prevent penetration throughout; let the corpse finish settling down to the floor.
            float correction = deathFloor - lowest;
            model.position += Vector3.up * (correction > 0f ? correction : correction * settle);
            CurrentMotion = dieT >= 1f ? Motion.Dead : Motion.Dying;
        }
    }
}
