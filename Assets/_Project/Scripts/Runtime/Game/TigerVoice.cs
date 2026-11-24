using UnityEngine;
namespace LastShift
{
    /// <summary>One voice per tiger: no overlapping growls or repeated chase-entry spam.</summary>
    public sealed class TigerVoice : MonoBehaviour
    {
        AudioSource voice; Health health;
        float nextAllowed, nextIdle;
        void Awake() { health = GetComponent<Health>(); nextIdle = Time.time + Random.Range(12f, 24f); }
        void Update()
        {
            if (health != null && health.Dead) { if (voice != null) voice.Stop(); return; }
            if (GameManager.Instance != null && !GameManager.Instance.IsPlaying) return;
            if (Time.time < nextIdle) return;
            nextIdle = Time.time + Random.Range(16f, 30f);
            TryGrowl();
        }
        public bool TryGrowl()
        {
            if ((health != null && health.Dead) || Time.time < nextAllowed || (voice != null && voice.isPlaying)) return false;
            voice = GunFX.Play(GunSound.TigerGrowl, transform.position, 0.85f);
            nextAllowed = Time.time + Mathf.Max(4f, voice.clip.length + 0.5f);
            nextIdle = Time.time + Random.Range(16f, 30f);
            return true;
        }
    }
}
