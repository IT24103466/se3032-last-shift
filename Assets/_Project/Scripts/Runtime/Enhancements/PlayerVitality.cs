using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Permanent health: no regeneration, ever. Each bullet takes a fixed chunk and the damage stays
    /// (scarred chunks are shown on the bar). You die only when the whole pool is gone.
    /// Ranged hits (bullets) are softened so a few shots wound instead of kill.
    /// </summary>
    public class PlayerVitality : MonoBehaviour
    {
        [Tooltip("Fraction of bullet damage that is actually taken (the rest is absorbed)")]
        public float rangedTaken = 0.45f;
        public float meleeRangeThreshold = 4f;

        PlayerController player;
        Health hp;
        float lastHitT = -99f, lastHitAmt;
        AudioSource beat;
        AudioClip beatClip;
        float nextBeat;

        public float Fraction => hp != null ? hp.Fraction : 1f;

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            if (player == null) { enabled = false; return; }
            hp = player.Health;
            hp.Damaged += OnDamaged;
            beat = gameObject.AddComponent<AudioSource>();
            beat.spatialBlend = 0f; beat.volume = 0.8f;
            beatClip = MakeThump();
        }

        void OnDestroy() { if (hp != null) hp.Damaged -= OnDamaged; }

        void OnDamaged(float amount, Vector3 from)
        {
            lastHitT = Time.time; lastHitAmt = amount;
            if (hp.current > 0f && (from - player.transform.position).magnitude > meleeRangeThreshold)
                hp.current = Mathf.Min(hp.max, hp.current + amount * (1f - rangedTaken));   // bullets wound, not execute
        }

        void Update()
        {
            if (hp == null) return;
            hp.regenPerSecond = 0f;          // wounds do not heal
            hp.regenDelay = 99999f;
            if (!hp.Dead && hp.Fraction < 0.4f && Time.time > nextBeat)
            {
                beat.PlayOneShot(beatClip, Mathf.Lerp(0.25f, 0.9f, 1f - hp.Fraction / 0.4f));
                nextBeat = Time.time + Mathf.Lerp(1.1f, 0.45f, 1f - hp.Fraction / 0.4f);
            }
        }

        static AudioClip MakeThump()
        {
            int sr = 22050, n = sr / 3;
            var d = new float[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                d[i] = Mathf.Sin(2f * Mathf.PI * 55f * t) * Mathf.Exp(-t * 18f) + Mathf.Sin(2f * Mathf.PI * 80f * (t - 0.14f)) * Mathf.Exp(-Mathf.Abs(t - 0.14f) * 30f) * (t > 0.14f ? 0.7f : 0f);
            }
            var c = AudioClip.Create("heartbeat", n, 1, sr, false); c.SetData(d, 0); return c;
        }

        void OnGUI()
        {
            if (hp == null || GameManager.Instance == null) return;
            float s = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float W = Screen.width / s, H = 1080f;
            float x = 16f, y = H - 112f, w = 360f, h = 20f;
            float f = hp.Fraction;
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.65f); GUI.DrawTexture(new Rect(x - 3, y - 3, w + 6, h + 6), Texture2D.whiteTexture);
            GUI.color = new Color(0.25f, 0.02f, 0.02f, 0.95f); GUI.DrawTexture(new Rect(x, y, w, h), Texture2D.whiteTexture); // scarred (lost) part
            Color live = f > 0.6f ? new Color(0.35f, 0.8f, 0.3f) : f > 0.3f ? new Color(0.95f, 0.7f, 0.15f) : new Color(0.9f, 0.15f, 0.1f);
            GUI.color = live; GUI.DrawTexture(new Rect(x, y, w * f, h), Texture2D.whiteTexture);
            GUI.color = new Color(0, 0, 0, 0.7f);   // chunk dividers = 10 permanent segments
            for (int i = 1; i < 10; i++) GUI.DrawTexture(new Rect(x + w * i / 10f - 1f, y, 2f, h), Texture2D.whiteTexture);
            float since = Time.time - lastHitT;
            if (since < 0.6f) { GUI.color = new Color(1, 1, 1, 1f - since / 0.6f); GUI.DrawTexture(new Rect(x, y, w * Mathf.Min(1f, (hp.current + lastHitAmt) / hp.max), h), Texture2D.whiteTexture); }
            GUI.color = old;
            string cond = f > 0.7f ? "HEALTHY" : f > 0.4f ? "WOUNDED" : f > 0.2f ? "CRITICAL" : "DYING";
            var st = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            st.normal.textColor = Color.white;
            GUI.Label(new Rect(x, y - 24f, 460f, 24f), $"VITALITY  {Mathf.CeilToInt(hp.current)}/{Mathf.CeilToInt(hp.max)}  -  {cond}  (wounds do not heal)", st);
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
