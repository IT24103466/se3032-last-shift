using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// The factory's intruder siren. When the alarm goes off it wails from the factory roof (a 3D
    /// sound you can hear across the estate and place by ear) and, for the first seconds, loudly
    /// everywhere, so the player knows the whole estate has been woken. It keeps going quietly
    /// while the game is on and fades out when the game ends or is paused.
    /// The sound is synthesised (two-tone rising and falling wail), so no audio file is needed.
    /// </summary>
    public class AlarmSiren : MonoBehaviour
    {
        const float LoudFor = 12f;            // seconds at full volume before it settles down
        static AudioClip clip;
        AudioSource near, far;
        float started;

        /// <summary>Start the siren (once per game).</summary>
        public static void Sound(Vector3 roof)
        {
            if (FindAnyObjectByType<AlarmSiren>() != null) return;
            var go = new GameObject("AlarmSiren");
            go.transform.position = roof;
            go.AddComponent<AlarmSiren>();
        }

        void Start()
        {
            if (clip == null) clip = MakeWail();
            started = Time.time;
            // From the factory roof: heard (and located) across the estate
            far = gameObject.AddComponent<AudioSource>();
            far.clip = clip; far.loop = true; far.spatialBlend = 1f; far.volume = 1f;
            far.rolloffMode = AudioRolloffMode.Linear; far.minDistance = 15f; far.maxDistance = 160f; far.dopplerLevel = 0f;
            far.Play();
            // In your ears for the first seconds: the moment you're discovered
            near = gameObject.AddComponent<AudioSource>();
            near.clip = clip; near.loop = true; near.spatialBlend = 0f; near.volume = 0.45f;
            near.Play();
        }

        void Update()
        {
            var gm = GameManager.Instance;
            bool on = gm == null || gm.IsPlaying;
            float t = Time.time - started;
            float nearTarget = on ? (t < LoudFor ? 0.45f : 0.12f) : 0f;
            float farTarget = on ? 1f : 0f;
            near.volume = Mathf.MoveTowards(near.volume, nearTarget, Time.unscaledDeltaTime * 0.3f);
            far.volume = Mathf.MoveTowards(far.volume, farTarget, Time.unscaledDeltaTime * 0.6f);
            if (gm != null && (gm.phase == GameManager.Phase.Won || gm.phase == GameManager.Phase.Lost) && far.volume <= 0f) Destroy(gameObject);
        }

        /// <summary>A 4-second loop: the pitch rises and falls twice (air-raid style), with a little grit.</summary>
        static AudioClip MakeWail()
        {
            const int sr = 22050; const float len = 4f;
            int n = (int)(sr * len);
            var d = new float[n];
            double phase = 0;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float sweep = 0.5f - 0.5f * Mathf.Cos(2f * Mathf.PI * t / 2f);       // 0 -> 1 -> 0 every 2 s
                float f = Mathf.Lerp(520f, 1050f, sweep);
                phase += 2.0 * System.Math.PI * f / sr;
                float s = Mathf.Sin((float)phase);
                float sq = Mathf.Clamp(s * 3f, -1f, 1f);                            // a harder, horn-like edge
                d[i] = (0.55f * sq + 0.3f * Mathf.Sin((float)(phase * 2.0)) + 0.15f * s) * 0.6f;
            }
            var c = AudioClip.Create("AlarmSiren", n, 1, sr, false);
            c.SetData(d, 0);
            return c;
        }
    }
}
