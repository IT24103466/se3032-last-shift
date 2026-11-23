using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Side threat map (top-right). Rotates with you (you always face up). Shows animals, guards, workers,
    /// the hunter and Sanjula's Mammoth, each with its detection range ring. Press M to enlarge / shrink, N to hide.
    /// The mammoth's own hidden ranges are only drawn after you press F10.
    /// </summary>
    public class ThreatMap : MonoBehaviour
    {
        struct Blip { public Transform t; public EnemyAI ai; public MammothAI mam; public Health hp; public Color col; public float ring; public string tag; }

        PlayerController player;
        readonly List<Blip> blips = new List<Blip>();
        float refresh;
        bool big, hidden;
        Texture2D disc, ring;

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            disc = MakeTex(false); ring = MakeTex(true);
        }

        static Texture2D MakeTex(bool outline)
        {
            const int N = 256;
            var t = new Texture2D(N, N, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color[N * N];
            for (int y = 0; y < N; y++) for (int x = 0; x < N; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(N / 2f, N / 2f)) / (N / 2f);
                float a = outline ? Mathf.Clamp01(1f - Mathf.Abs(d - 0.985f) / 0.012f) : Mathf.Clamp01((1f - d) / 0.01f);
                px[y * N + x] = new Color(1, 1, 1, a);
            }
            t.SetPixels(px); t.Apply(); return t;
        }

        static (Color, float, string) Style(EnemyKind k)
        {
            switch (k)
            {
                case EnemyKind.Tiger: return (new Color(1f, 0.6f, 0.1f), 18f, "Tiger");
                case EnemyKind.Deer: return (new Color(0.4f, 0.9f, 0.4f), 0f, "Deer");
                case EnemyKind.Elephant: return (new Color(0.75f, 0.75f, 0.8f), 10f, "Elephant");
                case EnemyKind.Guard: return (new Color(1f, 0.2f, 0.2f), 22f, "Guard");
                case EnemyKind.Worker: return (new Color(1f, 0.9f, 0.2f), 14f, "Worker");
                default: return (new Color(0.8f, 0.1f, 0.5f), 30f, "Hunter");
            }
        }

        void Rebuild()
        {
            blips.Clear();
            foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude))
            {
                var (c, r, n) = Style(e.kind);
                blips.Add(new Blip { t = e.transform, ai = e, hp = e.GetComponent<Health>(), col = c, ring = r, tag = n });
            }
            foreach (var m in FindObjectsByType<MammothAI>(FindObjectsInactive.Exclude))
                blips.Add(new Blip { t = m.transform, mam = m, hp = m.GetComponent<Health>(), col = new Color(0.65f, 0.35f, 1f), tag = "MAMMOTH" });
        }

        void Update()
        {
            if (Keyboard.current == null) return;
            if (Keyboard.current.mKey.wasPressedThisFrame) big = !big;
            if (Keyboard.current.nKey.wasPressedThisFrame) hidden = !hidden;
            if (Time.unscaledTime > refresh) { refresh = Time.unscaledTime + 1.5f; Rebuild(); }
        }

        void OnGUI()
        {
            if (hidden || player == null || GameManager.Instance == null || !GameManager.Instance.IsPlaying) return;
            float s = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float W = Screen.width / s;
            float size = big ? 520f : 270f, range = big ? 110f : 55f;
            var rect = new Rect(W - size - 18f, 16f, size, size);
            float sc = size / 2f / range;
            var old = GUI.color;

            GUI.color = new Color(0.02f, 0.05f, 0.04f, 0.78f); GUI.DrawTexture(rect, Texture2D.whiteTexture);
            GUI.BeginClip(rect);
            var c = new Vector2(size / 2f, size / 2f);
            // distance grid rings every 20 m
            GUI.color = new Color(0.4f, 0.7f, 0.5f, 0.25f);
            for (float r = 20f; r < range * 1.5f; r += 20f) GUI.DrawTexture(new Rect(c.x - r * sc, c.y - r * sc, r * 2f * sc, r * 2f * sc), ring);

            var fwd = player.transform.forward; fwd.y = 0; fwd.Normalize();
            var right = new Vector3(fwd.z, 0, -fwd.x);
            var pp = player.transform.position;

            foreach (var b in blips)
            {
                if (b.t == null) continue;
                if (b.hp != null && b.hp.Dead) continue;
                var d = b.t.position - pp; d.y = 0;
                var p = new Vector2(c.x + Vector3.Dot(d, right) * sc, c.y - Vector3.Dot(d, fwd) * sc);
                bool chasing = (b.ai != null && b.ai.Chasing) || (b.mam != null && b.mam.IsHunting);
                float pulse = chasing ? 0.55f + 0.45f * Mathf.Sin(Time.unscaledTime * 10f) : 1f;

                float r1 = b.ring, r2 = 0f;
                if (b.mam != null && MammothAI.RangesRevealed) { r1 = b.mam.NoticeR; r2 = b.mam.AggroR; }
                if (r1 > 0f)
                {
                    GUI.color = new Color(b.col.r, b.col.g, b.col.b, chasing ? 0.9f : 0.55f);
                    GUI.DrawTexture(new Rect(p.x - r1 * sc, p.y - r1 * sc, r1 * 2f * sc, r1 * 2f * sc), ring);
                    if (r2 > 0f) GUI.DrawTexture(new Rect(p.x - r2 * sc, p.y - r2 * sc, r2 * 2f * sc, r2 * 2f * sc), ring);
                }
                float dot = b.mam != null ? 15f : b.ai != null && b.ai.kind == EnemyKind.Elephant ? 11f : 8f;
                GUI.color = new Color(b.col.r, b.col.g, b.col.b, pulse);
                if (p.x > -20 && p.x < size + 20 && p.y > -20 && p.y < size + 20)
                {
                    GUI.DrawTexture(new Rect(p.x - dot / 2f, p.y - dot / 2f, dot, dot), disc);
                    if (b.mam != null || big)
                    {
                        var st = new GUIStyle(GUI.skin.label) { fontSize = big ? 12 : 11, fontStyle = FontStyle.Bold };
                        st.normal.textColor = GUI.color;
                        GUI.Label(new Rect(p.x + dot / 2f + 2f, p.y - 9f, 90f, 18f), b.tag, st);
                    }
                }
                else   // off-map: arrow-ish edge marker
                {
                    var dir = (p - c).normalized; var e = c + dir * (size / 2f - 6f);
                    GUI.DrawTexture(new Rect(e.x - 3f, e.y - 3f, 6f, 6f), disc);
                }
            }
            // player
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(c.x - 6f, c.y - 6f, 12f, 12f), disc);
            GUI.color = new Color(1f, 1f, 1f, 0.8f);
            GUI.DrawTexture(new Rect(c.x - 1.5f, c.y - 24f, 3f, 18f), Texture2D.whiteTexture);   // facing line
            GUI.EndClip();

            GUI.color = new Color(0.55f, 0.85f, 0.6f, 0.9f);
            foreach (var r in new[] { new Rect(rect.x, rect.y, rect.width, 2), new Rect(rect.x, rect.yMax - 2, rect.width, 2), new Rect(rect.x, rect.y, 2, rect.height), new Rect(rect.xMax - 2, rect.y, 2, rect.height) })
                GUI.DrawTexture(r, Texture2D.whiteTexture);
            var lab = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            lab.normal.textColor = new Color(0.8f, 0.95f, 0.85f);
            GUI.Label(new Rect(rect.x, rect.yMax + 2f, size + 40f, 20f), "THREAT MAP  M: size  N: hide  ring = detection range", lab);
            GUI.color = old; GUI.matrix = Matrix4x4.identity;
        }
    }
}
