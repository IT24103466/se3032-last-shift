using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Sanjula's Mammoth. Three HIDDEN, randomised ranges around the player:
    ///   notice  (outer)  - it wakes up and lumbers toward you
    ///   aggro   (inner)  - it charges and trampling begins
    ///   leash   (far)    - if you get this far away it gives up and walks home
    /// Shooting it makes it hunt you from anywhere. Under 35% HP it becomes enraged. Press F10 to reveal the ranges.
    /// </summary>
    public class MammothAI : MonoBehaviour
    {
        enum S { Idle, Investigate, Hunt, Windup, Charge, Recover, Return, Dead }

        public float noticeR, aggroR, leashR;
        S state = S.Idle;
        Health hp; NavMeshAgent agent; CreatureAnimator anim; PlayerController player;
        Vector3 home; float timer, nextWander, atkCd, lastHurt = -99f, lostTimer, staggerAcc, footT;
        Vector3 chargeDir; bool enraged, roared;
        AudioSource audioSrc; AudioClip thud, roarClip;
        LineRenderer[] rings; bool showRings; float ringT;
        public static bool RangesRevealed;
        public float NoticeR => noticeR; public float AggroR => aggroR;
        public bool IsHunting => state == S.Hunt || state == S.Windup || state == S.Charge;

        void Start()
        {
            hp = GetComponent<Health>(); agent = GetComponent<NavMeshAgent>(); anim = GetComponent<CreatureAnimator>();
            player = FindAnyObjectByType<PlayerController>();
            home = transform.position;
            noticeR = Random.Range(34f, 42f); aggroR = Random.Range(18f, 26f); leashR = Random.Range(80f, 95f);
            hp.Damaged += OnHurt; hp.Died += OnDied;
            audioSrc = gameObject.AddComponent<AudioSource>();
            audioSrc.spatialBlend = 1f; audioSrc.minDistance = 8f; audioSrc.maxDistance = 120f; audioSrc.rolloffMode = AudioRolloffMode.Linear;
            thud = MakeThud(); roarClip = MakeRoar();
            if (agent != null && agent.enabled) { agent.Warp(home); agent.stoppingDistance = 0f; }
            Debug.Log($"[Sanjula] Mammoth ready at {home}. notice={noticeR:F0} aggro={aggroR:F0} leash={leashR:F0} (F10 reveals)");
        }

        void OnDestroy() { if (hp != null) { hp.Damaged -= OnHurt; hp.Died -= OnDied; } }

        float FlatDist(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }

        void OnHurt(float amt, Vector3 from)
        {
            lastHurt = Time.time;
            if (state == S.Dead) return;
            anim?.Flinch();
            staggerAcc += amt;
            if (state == S.Idle || state == S.Investigate || state == S.Return) { state = S.Hunt; Roar(); }
            if (!enraged && hp.Fraction < 0.35f)
            {
                enraged = true; Roar();
                GameManager.Instance?.Toast("Sanjula's Mammoth is ENRAGED!", 3f);
            }
            if (staggerAcc > hp.max * 0.12f && (state == S.Hunt))
            {
                staggerAcc = 0f; state = S.Recover; timer = 0.7f; Stop();
            }
        }

        void OnDied()
        {
            state = S.Dead;
            if (agent != null) agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            anim?.Die();
            audioSrc.PlayOneShot(roarClip, 1f);
            GameManager.Instance?.Toast("Sanjula's Mammoth has fallen!", 4f);
            for (int i = 0; i < 3; i++)
                AmmoBox.Drop(transform.position + new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f)), 20, 8);
            if (rings != null) foreach (var r in rings) if (r) r.enabled = false;
        }

        void Roar() { audioSrc.PlayOneShot(roarClip, 1f); anim?.Roar(); }
        void Stop() { if (agent != null && agent.enabled && agent.isOnNavMesh) { agent.ResetPath(); agent.velocity = Vector3.zero; } }
        void Go(Vector3 p, float speed)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
            agent.speed = speed * (enraged ? 1.2f : 1f); agent.SetDestination(p);
        }
        void Face(Vector3 p)
        {
            var d = p - transform.position; d.y = 0;
            if (d.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), 220f * Time.deltaTime);
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame) ToggleRings();
            if (showRings) UpdateRings();
            if (state == S.Dead || player == null || hp == null) return;

            bool live = GameManager.Instance != null && GameManager.Instance.IsPlaying && !player.Health.Dead;
            Vector3 pp = player.transform.position;
            float d = FlatDist(transform.position, pp);
            float mul = player.Hidden ? 0.4f : player.Crouched ? 0.6f : player.Sprinting ? 1.3f : 1f;
            float notice = noticeR * mul, aggro = aggroR * Mathf.Lerp(1f, mul, 0.6f);
            bool provoked = Time.time - lastHurt < 8f;
            atkCd -= Time.deltaTime; timer -= Time.deltaTime;

            // footsteps
            float v = agent != null && agent.enabled ? agent.velocity.magnitude : 0f;
            footT -= Time.deltaTime * v;
            if (footT <= 0f && v > 0.6f) { footT = 3.2f; audioSrc.PlayOneShot(thud, Mathf.Clamp01(v / 7f) + 0.3f); }

            if (!live) { if (state != S.Idle && state != S.Return) { state = S.Return; Stop(); } }

            switch (state)
            {
                case S.Idle:
                    if (Time.time > nextWander)
                    {
                        nextWander = Time.time + Random.Range(6f, 11f);
                        var p = home + new Vector3(Random.Range(-14f, 14f), 0, Random.Range(-14f, 14f));
                        if (NavMesh.SamplePosition(p, out var nh, 8f, NavMesh.AllAreas)) Go(nh.position, 1.6f);
                    }
                    if (live && d < aggro) { state = S.Hunt; Roar(); }
                    else if (live && d < notice) { state = S.Investigate; timer = 14f; }
                    break;

                case S.Investigate:
                    Go(pp, 3.2f);
                    if (d < aggro) { state = S.Hunt; Roar(); }
                    else if (d > notice * 1.35f || timer <= 0f) { state = S.Return; }
                    break;

                case S.Hunt:
                    Go(pp, 6.8f);
                    lostTimer = (d > leashR || FlatDist(transform.position, home) > leashR * 1.6f) && !provoked ? lostTimer + Time.deltaTime : 0f;
                    if (lostTimer > 6f || (d > notice * 1.8f && !provoked && lostTimer > 0f)) { state = S.Return; lostTimer = 0f; break; }
                    if (d > notice * 1.8f && !provoked) lostTimer += Time.deltaTime;
                    if (atkCd <= 0f && d < 6f) { state = S.Windup; timer = enraged ? 0.7f : 0.95f; chargeDir = Vector3.zero; Stop(); anim?.PlayAttack(); }
                    else if (atkCd <= 0f && d > 11f && d < 32f && Random.value < 0.4f * Time.deltaTime * 3f)
                    { state = S.Windup; timer = 0.9f; Stop(); chargeDir = Vector3.zero; Roar(); }
                    break;

                case S.Windup:
                    Face(pp);
                    if (timer <= 0f)
                    {
                        if (chargeDir == Vector3.zero && d < 8f) Stomp();
                        else { var fd = pp - transform.position; fd.y = 0; chargeDir = fd.normalized; state = S.Charge; timer = 1.4f; if (agent) agent.enabled = false; break; }
                        atkCd = enraged ? 1.4f : 2.2f; state = S.Hunt;
                    }
                    break;

                case S.Charge:
                {
                    float spd = enraged ? 14f : 12f;
                    var step = chargeDir * spd * Time.deltaTime;
                    var np = transform.position + step;
                    if (NavMesh.SamplePosition(np, out var nh, 3f, NavMesh.AllAreas) && Vector3.Distance(nh.position, np) < 2.2f)
                        transform.position = new Vector3(np.x, nh.position.y, np.z);
                    else timer = 0f;
                    transform.rotation = Quaternion.LookRotation(chargeDir);
                    if (FlatDist(transform.position, pp) < 3.8f && live)
                    { Hit(65f, 14f); timer = 0f; }
                    if (timer <= 0f)
                    {
                        if (agent) { agent.enabled = true; agent.Warp(transform.position); }
                        state = S.Recover; timer = 1.6f; atkCd = 3f;
                    }
                    break;
                }

                case S.Recover:
                    if (timer <= 0f) state = S.Hunt;
                    break;

                case S.Return:
                    Go(home, 4.2f);
                    hp.current = Mathf.Min(hp.max * 0.6f, hp.current + hp.max * 0.01f * Time.deltaTime); // slow breather, never full
                    if (live && d < aggro) { state = S.Hunt; Roar(); }
                    else if (FlatDist(transform.position, home) < 4f) { state = S.Idle; nextWander = Time.time + 3f; }
                    break;
            }
        }

        void Stomp()
        {
            audioSrc.PlayOneShot(thud, 1f);
            EnemyAI.MakeNoise(transform.position, 50f);
            float d = FlatDist(transform.position, player.transform.position);
            if (d < 8f) Hit(Mathf.Lerp(48f, 22f, d / 8f), 10f);
        }

        void Hit(float dmg, float knock)
        {
            var dir = player.transform.position - transform.position; dir.y = 0; dir.Normalize();
            if (dmg >= player.Health.current) GameManager.Instance?.Lose("Trampled by Sanjula's Mammoth.");
            player.Health.Damage(dmg, player.transform.position - dir * 1f);   // close 'from' = counts as a full-force melee hit
            player.AddKnockback(dir * knock + Vector3.up * 5f);
        }

        // ---------------- boss bar ----------------
        void OnGUI()
        {
            if (state == S.Dead || player == null || hp == null) return;
            if (state == S.Idle || state == S.Return) { if (Time.time - lastHurt > 6f) return; }
            if (FlatDist(transform.position, player.transform.position) > leashR * 1.2f) return;
            float s = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            float W = Screen.width / s, w = 720f, x = (W - w) / 2f, y = 38f;
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.7f); GUI.DrawTexture(new Rect(x - 4, y - 4, w + 8, 26), Texture2D.whiteTexture);
            GUI.color = new Color(0.25f, 0.05f, 0.03f); GUI.DrawTexture(new Rect(x, y, w, 18), Texture2D.whiteTexture);
            GUI.color = enraged ? new Color(1f, 0.25f, 0.05f) : new Color(0.8f, 0.12f, 0.1f);
            GUI.DrawTexture(new Rect(x, y, w * hp.Fraction, 18), Texture2D.whiteTexture);
            GUI.color = old;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
            st.normal.textColor = new Color(1f, 0.92f, 0.8f);
            GUI.Label(new Rect(x, y - 30f, w, 26f), enraged ? "SANJULA'S MAMMOTH  -  ENRAGED" : "SANJULA'S MAMMOTH", st);
            GUI.matrix = Matrix4x4.identity;
        }

        // ---------------- F10: reveal hidden ranges ----------------
        void ToggleRings()
        {
            showRings = !showRings; RangesRevealed = showRings;
            if (rings == null)
            {
                rings = new LineRenderer[3];
                var cols = new[] { new Color(1f, 0.9f, 0.2f), new Color(1f, 0.2f, 0.1f), new Color(0.3f, 0.7f, 1f) };
                var sh = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
                for (int i = 0; i < 3; i++)
                {
                    var g = new GameObject("MammothRing" + i); var lr = g.AddComponent<LineRenderer>();
                    lr.loop = true; lr.positionCount = 72; lr.widthMultiplier = 0.35f; lr.useWorldSpace = true;
                    lr.material = new Material(sh); lr.material.color = cols[i];
                    if (lr.material.HasProperty("_BaseColor")) lr.material.SetColor("_BaseColor", cols[i]);
                    lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    rings[i] = lr;
                }
            }
            foreach (var r in rings) r.enabled = showRings;
            GameManager.Instance?.Toast(showRings ? "Mammoth ranges: yellow=notice  red=aggro  blue=give-up (F10 hides)" : "Mammoth ranges hidden", 3f);
            ringT = 0f;
        }

        void UpdateRings()
        {
            ringT -= Time.deltaTime; if (ringT > 0f) return; ringT = 0.25f;
            var radii = new[] { noticeR, aggroR, leashR };
            var centres = new[] { transform.position, transform.position, home };
            for (int r = 0; r < 3; r++)
                for (int i = 0; i < 72; i++)
                {
                    float a = i / 72f * Mathf.PI * 2f;
                    var p = centres[r] + new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * radii[r];
                    float y = centres[r].y;
                    if (Physics.Raycast(p + Vector3.up * 60f, Vector3.down, out var h, 140f, ~(1 << 2), QueryTriggerInteraction.Ignore)) y = h.point.y;
                    rings[r].SetPosition(i, new Vector3(p.x, y + 0.6f, p.z));
                }
        }

        // ---------------- generated sounds ----------------
        static AudioClip MakeThud()
        {
            int sr = 22050, n = sr / 2; var d = new float[n];
            var rnd = new System.Random(5);
            for (int i = 0; i < n; i++) { float t = i / (float)sr; d[i] = (Mathf.Sin(2f * Mathf.PI * 38f * t) * 0.9f + ((float)rnd.NextDouble() - 0.5f) * 0.25f) * Mathf.Exp(-t * 9f); }
            var c = AudioClip.Create("mammoth_thud", n, 1, sr, false); c.SetData(d, 0); return c;
        }
        static AudioClip MakeRoar()
        {
            int sr = 22050, n = sr * 2; var d = new float[n]; float ph = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)sr;
                float f = Mathf.Lerp(90f, 380f, Mathf.Sin(Mathf.Clamp01(t / 1.4f) * Mathf.PI * 0.5f)) * (1f - 0.35f * Mathf.Clamp01((t - 1.2f) / 0.8f));
                ph += 2f * Mathf.PI * f / sr;
                float env = Mathf.Clamp01(t / 0.08f) * Mathf.Exp(-Mathf.Max(0f, t - 1f) * 2.5f);
                float saw = (ph / (2f * Mathf.PI)) % 1f * 2f - 1f;
                d[i] = (saw * 0.6f + Mathf.Sin(ph * 2f) * 0.3f) * env * (0.8f + 0.2f * Mathf.Sin(t * 40f)) * 0.8f;
            }
            var c = AudioClip.Create("mammoth_roar", n, 1, sr, false); c.SetData(d, 0); return c;
        }
    }
}
