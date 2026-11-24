using UnityEngine;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>
    /// A slingshot pebble (a real Rigidbody). The first thing it hits takes the damage: people and
    /// animals are hurt and knocked back, barricades break. Every impact clacks: enemies nearby
    /// come to investigate the sound, so a pebble thrown past a guard can lure him away.
    /// </summary>
    [RequireComponent(typeof(Rigidbody))]
    public class Pebble : MonoBehaviour
    {
        public float damage = 20f;
        bool spent;
        Rigidbody rb;
        Vector3 previousPosition;

        void Awake() { rb = GetComponent<Rigidbody>(); previousPosition = transform.position; }
        void FixedUpdate()
        {
            if (!spent) EnemyAI.NoticeProjectile(previousPosition, transform.position, transform);
            previousPosition = transform.position;
        }
        void Start() => Destroy(gameObject, 8f);

        void OnCollisionEnter(Collision c)
        {
            if (spent) return;
            spent = true;
            Vector3 dir = c.relativeVelocity.sqrMagnitude > 0.01f ? -c.relativeVelocity.normalized : rb.linearVelocity.normalized;
            float speedFactor = Mathf.Clamp(c.relativeVelocity.magnitude / 25f, 0.4f, 1.2f);

            var hp = c.collider.GetComponentInParent<Health>();
            if (hp != null && hp.GetComponent<PlayerController>() == null && !hp.Dead)
            {
                hp.Damage(damage * speedFactor, transform.position - dir * 3f);
                var ai = hp.GetComponent<EnemyAI>();
                if (ai != null) ai.Knockback(dir * 3.5f);
                GameManager.Instance?.HitMarker();
            }
            else
            {
                var gi = c.collider.GetComponentInParent<GreyboxInteractable>();
                if (gi != null && gi.kind == GreyboxKind.Barricade) gi.Interact();
            }
            EnemyAI.MakeNoise(transform.position, 10f);
        }
    }
}
