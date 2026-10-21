using UnityEngine;

namespace LastShift
{
    /// <summary>Hit points for the player and every character. Fires Damaged / Died events.</summary>
    public class Health : MonoBehaviour
    {
        public float max = 100f;
        public float current;
        [Tooltip("Seconds without being hit before health starts coming back (0 = never)")]
        public float regenDelay;
        public float regenPerSecond;

        public event System.Action<float, Vector3> Damaged;   // amount, attacker position
        public event System.Action Died;

        float lastHit = -99f;
        public bool Dead => current <= 0f;
        public float Fraction => max > 0f ? current / max : 0f;

        void Awake() => current = max;

        void Update()
        {
            if (Dead || regenPerSecond <= 0f || current >= max) return;
            if (Time.time - lastHit > regenDelay) current = Mathf.Min(max, current + regenPerSecond * Time.deltaTime);
        }

        public void Damage(float amount, Vector3 from)
        {
            if (Dead) return;
            lastHit = Time.time;
            current = Mathf.Max(0f, current - amount);
            Damaged?.Invoke(amount, from);
            if (current <= 0f) Died?.Invoke();
        }

        public void Restore() { current = max; }
    }
}
