using System.Collections.Generic;
using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Procedural animation for the primitive-built characters (no rigs needed):
    /// legs and arms swing with walking speed, the tail and trunk sway, the body lunges on an
    /// attack, flinches when hit, and topples over when knocked out.
    /// Parts are found by name under the "Model" child: Leg_FL/FR/BL/BR (animals), Leg_L/R and
    /// Arm_L/R (people), Head, Tail, Trunk. People with guns raise them to aim (both arms for a
    /// long gun) and jerk back when they fire.
    /// </summary>
    public class CreatureAnimator : MonoBehaviour
    {
        public float stride = 32f, cadence = 2.4f, lunge = 0.45f;
        [Tooltip("Set by EnemyAI while the character is fighting with a gun")]
        public bool aiming;
        [Tooltip("Long guns are held with both hands")]
        public bool twoHanded;

        class Limb { public Transform t; public Quaternion rest; public float phase; public float scale; }
        readonly List<Limb> limbs = new List<Limb>();
        Transform model, head, tail, trunk, armR, armL;
        Quaternion headRest, tailRest, trunkRest, armRRest, armLRest;
        float aimBlend, shootT = -1f;
        Vector3 modelRest, lastPos;
        float phase, speed, attackT = -1f, flinchT = -1f, dieT = -1f, roarT = -1f;

        void Awake()
        {
            model = transform.Find("Model");
            if (model == null) { enabled = false; return; }
            modelRest = model.localPosition;
            foreach (var t in model.GetComponentsInChildren<Transform>())
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
            lastPos = transform.position;
        }

        void Add(Transform t, float p, float s) => limbs.Add(new Limb { t = t, rest = t.localRotation, phase = p, scale = s });

        public void PlayAttack() => attackT = 0f;
        public void Flinch() => flinchT = 0f;
        public void Roar() => roarT = 0f;
        public void Die() => dieT = 0f;
        public void Shoot() => shootT = 0f;

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            Vector3 v = (transform.position - lastPos) / dt; v.y = 0f;
            lastPos = transform.position;
            speed = Mathf.Lerp(speed, v.magnitude, 10f * dt);

            if (dieT >= 0f)
            {
                dieT += dt / 0.7f;
                float k = Mathf.Clamp01(dieT);
                model.localRotation = Quaternion.Euler(0f, 0f, 90f * k * k);
                foreach (var l in limbs) l.t.localRotation = Quaternion.Slerp(l.t.localRotation, l.rest, 5f * dt);
                return;
            }

            phase += speed * cadence * dt;
            float amp = Mathf.Clamp01(speed / 2.5f) * stride;
            foreach (var l in limbs)
                l.t.localRotation = l.rest * Quaternion.Euler(Mathf.Sin(phase + l.phase) * amp * l.scale, 0f, 0f);

            // Gun up: arms forward (and the support hand under the barrel for a long gun); kick on each shot
            aimBlend = Mathf.MoveTowards(aimBlend, aiming ? 1f : 0f, dt * 5f);
            if (aimBlend > 0f)
            {
                float kick = 0f;
                if (shootT >= 0f) { shootT += dt / 0.2f; kick = Mathf.Sin(Mathf.Clamp01(shootT) * Mathf.PI); if (shootT >= 1f) shootT = -1f; }
                if (armR != null) armR.localRotation = Quaternion.Slerp(armR.localRotation, armRRest * Quaternion.Euler(-86f + 14f * kick, -6f, 0f), aimBlend);
                if (armL != null && twoHanded) armL.localRotation = Quaternion.Slerp(armL.localRotation, armLRest * Quaternion.Euler(-78f + 10f * kick, 32f, 0f), aimBlend);
            }

            float t = Time.time;
            if (tail != null) tail.localRotation = tailRest * Quaternion.Euler(0f, Mathf.Sin(t * 2.2f) * 18f, Mathf.Sin(t * 1.3f) * 6f);
            if (trunk != null) trunk.localRotation = trunkRest * Quaternion.Euler(Mathf.Sin(t * 0.9f) * 10f, Mathf.Sin(t * 0.6f) * 12f, 0f);
            if (head != null) head.localRotation = headRest * Quaternion.Euler(Mathf.Sin(phase * 2f) * amp * 0.08f, 0f, 0f);

            Vector3 offset = Vector3.up * Mathf.Abs(Mathf.Sin(phase)) * 0.04f * Mathf.Clamp01(speed / 2f);
            if (attackT >= 0f)
            {
                attackT += dt / 0.45f;
                float k = Mathf.Sin(Mathf.Clamp01(attackT) * Mathf.PI);
                offset += Vector3.forward * lunge * k;
                if (armR != null) armR.localRotation = armRRest * Quaternion.Euler(-150f * k, 0f, 0f);   // swing the baton
                if (head != null && armR == null) head.localRotation = headRest * Quaternion.Euler(-20f * k, 0f, 0f);
                if (attackT >= 1f) attackT = -1f;
            }
            if (flinchT >= 0f)
            {
                flinchT += dt / 0.25f;
                offset += Vector3.back * 0.15f * Mathf.Sin(Mathf.Clamp01(flinchT) * Mathf.PI);
                if (flinchT >= 1f) flinchT = -1f;
            }
            if (roarT >= 0f)
            {
                roarT += dt / 0.6f;
                if (head != null) head.localRotation = headRest * Quaternion.Euler(-25f * Mathf.Sin(Mathf.Clamp01(roarT) * Mathf.PI), 0f, 0f);
                if (roarT >= 1f) roarT = -1f;
            }
            model.localPosition = modelRest + offset;
        }
    }
}
