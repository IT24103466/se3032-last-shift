using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Sanjula's Mammoth - the body. It does not decide anything about WHAT to do: MammothDecision (utility engine) picks the action
    /// from the belief / memory / perception, and this class executes it:
    ///   Patrol, InvestigateSound, TrackLastKnown, Search, Pursue, ReturnHome  -> movement (via the tactical A* planner for long trips,
    ///                                                                            ONE NavMeshAgent does all the walking)
    ///   Windup / Charge / Recover / Dead                                       -> the combat controller (stomp, charge, stagger, death)
    /// Three HIDDEN, randomised ranges remain its territorial personality: notice (outer), aggro (inner), leash (far). F10 reveals them.
    /// Under 35% HP it becomes enraged.
    /// </summary>
    public class MammothAI : MonoBehaviour
    {
        enum C { Free, Windup, Charge, Recover, Dead }

        public float noticeR, aggroR, leashR;
        C cs = C.Free;
        Health hp; NavMeshAgent agent; CreatureAnimator anim; PlayerController player;
        MammothPerception perc; MammothBelief belief; MammothMemory memory; MammothPlanner planner; MammothDecision decision;
        Vector3 home; float timer, nextWander, atkCd, lastHurt = -99f, staggerAcc, footT, nextRoar;
        Vector3 chargeDir; bool enraged;
        AudioSource audioSrc; AudioClip thud, roarClip;
        LineRenderer[] rings; bool showRings; float ringT;
        public static bool RangesRevealed;

        // movement bookkeeping
        MammothAction lastAction = MammothAction.Patrol;
        Vector3 lastDest; float nextDest, nextReplan; bool routeActive; int routeIdx; Vector3 routeGoal;
        Vector3 searchPos; int searchNode = -1; float searchDeadline;

        public float NoticeR => noticeR; public float AggroR => aggroR; public float LeashR => leashR;
        public Vector3 Home => home;
        public bool Enraged => enraged;
        public float LastHurtTime => lastHurt;
        public bool IsDead => cs == C.Dead;
        public bool PlayerLive { get; private set; }
        public MammothAction CurrentAction => decision != null ? decision.Current : MammothAction.Patrol;
        public bool IsHunting => cs == C.Windup || cs == C.Charge || CurrentAction == MammothAction.Pursue;

        void Start()
        {
            hp = GetComponent<Health>(); agent = GetComponent<NavMeshAgent>(); anim = GetComponent<CreatureAnimator>();
            perc = GetComponent<MammothPerception>(); belief = GetComponent<MammothBelief>(); memory = GetComponent<MammothMemory>();
            planner = GetComponent<MammothPlanner>(); decision = GetComponent<MammothDecision>();
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
            if (cs == C.Dead) return;
            anim?.Flinch();
            staggerAcc += amt;
            if (perc != null) perc.RegisterHit(from);          // a hit tells it which way the shot came from
            if (!enraged && hp.Fraction < 0.35f)
            {
                enraged = true; Roar(); routeActive = false;
                GameManager.Instance?.Toast("Sanjula's Mammoth is ENRAGED!", 3f);
            }
            if (staggerAcc > hp.max * 0.12f && cs == C.Free && CurrentAction == MammothAction.Pursue)
            {
                staggerAcc = 0f; cs = C.Recover; timer = 0.7f; Stop();
            }
        }

        void OnDied()
        {
            cs = C.Dead;
            if (agent != null) agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            anim?.Die();
            audioSrc.PlayOneShot(roarClip, 1f);
            GameManager.Instance?.Toast("Sanjula's Mammoth has fallen!", 4f);
            for (int i = 0; i < 3; i++)
                AmmoBox.Drop(transform.position + new Vector3(Random.Range(-3f, 3f), 0f, Random.Range(-3f, 3f)), 20, 8);
            if (rings != null) foreach (var r in rings) if (r) r.enabled = false;
        }

        void Roar() { if (Time.time < nextRoar) return; nextRoar = Time.time + 6f; audioSrc.PlayOneShot(roarClip, 1f); anim?.Roar(); }
        void Stop() { if (agent != null && agent.enabled && agent.isOnNavMesh) { agent.ResetPath(); agent.velocity = Vector3.zero; } routeActive = false; }
        void Face(Vector3 p)
        {
            var d = p - transform.position; d.y = 0;
            if (d.sqrMagnitude > 0.01f) transform.rotation = Quaternion.RotateTowards(transform.rotation, Quaternion.LookRotation(d), 220f * Time.deltaTime);
        }

        // ------------------------------------------------------------------ movement (one NavMeshAgent; the planner only picks waypoints)
        void Direct(Vector3 p, float speed)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
            agent.speed = speed * (enraged ? 1.2f : 1f);
            if (Time.time < nextDest && (p - lastDest).sqrMagnitude < 4f) return;   // do not recalculate the path every frame
            nextDest = Time.time + 0.2f; lastDest = p;
            if (NavMesh.SamplePosition(p, out var nh, 8f, NavMesh.AllAreas)) agent.SetDestination(nh.position);
        }

        /// <summary>Go to a point. Short trips: straight to the NavMeshAgent. Long trips: A* waypoint route, one waypoint at a time.</summary>
        void MoveTo(Vector3 target, float speed)
        {
            if (agent == null || !agent.enabled || !agent.isOnNavMesh) return;
            Vector3 pos = transform.position;
            if (FlatDist(pos, target) < 22f || planner == null || !planner.Ready) { routeActive = false; Direct(target, speed); return; }
            if (!routeActive || (target - routeGoal).sqrMagnitude > 100f || Time.time > nextReplan)
            {
                planner.PlanToPosition(pos, target);
                routeActive = planner.LastPlanFound; routeGoal = target; routeIdx = 0; nextReplan = Time.time + 3f;
            }
            if (!routeActive) { Direct(target, speed); return; }
            while (routeIdx < planner.Route.Count && FlatDist(pos, planner.Route[routeIdx]) < 5f) routeIdx++;
            Vector3 wp = routeIdx < planner.Route.Count ? planner.Route[routeIdx] : target;
            Direct(wp, speed);
            if (!agent.pathPending && agent.pathStatus != NavMeshPathStatus.PathComplete && routeIdx < planner.Route.Count)
            {
                planner.BlockNode(planner.Graph.NearestNode(wp), 25f);      // the agent cannot get there: plan around it
                nextReplan = 0f;
            }
        }

        // ------------------------------------------------------------------ the loop
        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f10Key.wasPressedThisFrame) ToggleRings();
            if (showRings) UpdateRings();
            if (cs == C.Dead || player == null || hp == null) return;

            PlayerLive = GameManager.Instance != null && GameManager.Instance.IsPlaying && !player.Health.Dead;
            Vector3 pp = player.transform.position;
            float d = FlatDist(transform.position, pp);
            atkCd -= Time.deltaTime; timer -= Time.deltaTime;

            // footsteps
            float v = agent != null && agent.enabled ? agent.velocity.magnitude : 0f;
            footT -= Time.deltaTime * v;
            if (footT <= 0f && v > 0.6f) { footT = 3.2f; audioSrc.PlayOneShot(thud, Mathf.Clamp01(v / 7f) + 0.3f); }

            switch (cs)
            {
                case C.Free: Execute(pp, d); break;

                case C.Windup:
                    Face(pp);
                    if (timer <= 0f)
                    {
                        if (chargeDir == Vector3.zero && d < 8f) Stomp();
                        else { var fd = pp - transform.position; fd.y = 0; chargeDir = fd.normalized; cs = C.Charge; timer = 1.4f; if (agent) agent.enabled = false; break; }
                        atkCd = enraged ? 1.4f : 2.2f; cs = C.Free;
                    }
                    break;

                case C.Charge:
                {
                    float spd = enraged ? 14f : 12f;
                    var np = transform.position + chargeDir * spd * Time.deltaTime;
                    if (NavMesh.SamplePosition(np, out var nh, 3f, NavMesh.AllAreas) && Vector3.Distance(nh.position, np) < 2.2f)
                        transform.position = new Vector3(np.x, nh.position.y, np.z);
                    else timer = 0f;
                    transform.rotation = Quaternion.LookRotation(chargeDir);
                    if (FlatDist(transform.position, pp) < 3.8f && PlayerLive) { Hit(65f, 14f); timer = 0f; }
                    if (timer <= 0f)
                    {
                        if (agent) { agent.enabled = true; agent.Warp(transform.position); }
                        cs = C.Recover; timer = 1.6f; atkCd = 3f; routeActive = false;
                    }
                    break;
                }

                case C.Recover:
                    if (timer <= 0f) cs = C.Free;
                    break;
            }
        }

        void Execute(Vector3 pp, float d)
        {
            var act = decision != null ? decision.Current : MammothAction.Patrol;
            if (act != lastAction)
            {
                routeActive = false; nextDest = 0f;
                if (act == MammothAction.Pursue) Roar();
                if (act == MammothAction.Search) searchNode = -1;
                lastAction = act;
            }
            switch (act)
            {
                case MammothAction.Patrol: Wander(); break;
                case MammothAction.InvestigateSound: DoInvestigate(); break;
                case MammothAction.TrackLastKnown: DoTrack(pp); break;
                case MammothAction.Search:
                case MammothAction.Ambush: DoSearch(); break;
                case MammothAction.Pursue: DoPursue(pp, d); break;
                case MammothAction.ReturnHome: DoReturn(); break;
            }
        }

        void Wander()
        {
            if (Time.time <= nextWander) return;
            nextWander = Time.time + Random.Range(6f, 11f);
            var p = home + new Vector3(Random.Range(-14f, 14f), 0, Random.Range(-14f, 14f));
            if (NavMesh.SamplePosition(p, out var nh, 8f, NavMesh.AllAreas)) { lastDest = nh.position; Direct(nh.position, 1.6f); }
        }

        void DoInvestigate()
        {
            if (memory == null || !memory.TryGetFreshClue(out var clue, 20f)) return;
            MoveTo(clue.pos, 3.8f);
            if (FlatDist(transform.position, clue.pos) < 7f) { memory.MarkCluesChecked(clue.pos, 12f); MarkSearchedHere(); }
        }

        void DoTrack(Vector3 pp)
        {
            if (perc == null) { MoveTo(pp, 3.5f); return; }
            bool vis = perc.PlayerVisible;
            Vector3 tgt = vis ? perc.LastSeenPos : perc.LastSeenPos + perc.LastSeenVelocity * Mathf.Min(perc.TimeSinceSeen, 2.5f);   // lead a little: he was moving
            MoveTo(tgt, vis ? 3.6f : 5f);
            if (!vis && FlatDist(transform.position, perc.LastSeenPos) < 6f)
            {
                MarkSearchedHere();
                if (memory != null) memory.MarkCluesChecked(transform.position, 12f);
            }
        }

        void DoSearch()
        {
            if (belief == null || !belief.Ready || memory == null) { Wander(); return; }
            if (searchNode < 0 || FlatDist(transform.position, searchPos) < 5f || Time.time > searchDeadline)
            {
                if (searchNode >= 0) MarkSearchedHere();
                PickSearchNode();
            }
            if (searchNode >= 0) MoveTo(searchPos, enraged ? 4.8f : 3.6f);
        }

        void PickSearchNode()
        {
            var g = belief.Graph; int best = -1; float bs = -1f; Vector3 pos = transform.position;
            for (int i = 0; i < g.Count; i++)
            {
                if (!g.Reachable(i)) continue;
                float p = belief.Probability(i);
                float score = p * (1f - 0.85f * memory.SearchPenalty(i)) / (1f + FlatDist(pos, g.Position(i)) / 50f);
                if (score > bs) { bs = score; best = i; }
            }
            searchNode = best; searchDeadline = Time.time + 20f;
            if (best >= 0) searchPos = g.Position(best);
        }

        void MarkSearchedHere()
        {
            if (memory == null || planner == null || !planner.Ready) return;
            var g = planner.Graph; int n = g.NearestNode(transform.position);
            if (n < 0) return;
            memory.MarkSearched(n);
            foreach (int nb in g.Neighbours(n)) if (FlatDist(g.Position(nb), transform.position) < 14f) memory.MarkSearched(nb);
        }

        void DoPursue(Vector3 pp, float d)
        {
            bool vis = perc == null || perc.PlayerVisible;
            Vector3 tgt = vis ? pp : perc.LastSeenPos;
            MoveTo(tgt, 6.8f);
            if (!vis || atkCd > 0f) return;
            if (d < 6f) { cs = C.Windup; timer = enraged ? 0.7f : 0.95f; chargeDir = Vector3.zero; Stop(); anim?.PlayAttack(); }
            else if (d > 11f && d < 32f && Random.value < 0.4f * Time.deltaTime * 3f) { cs = C.Windup; timer = 0.9f; Stop(); chargeDir = Vector3.zero; Roar(); }
        }

        void DoReturn()
        {
            MoveTo(home, 4.2f);
            hp.current = Mathf.Min(hp.max * 0.6f, hp.current + hp.max * 0.01f * Time.deltaTime);   // slow breather, never full
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
            if (cs == C.Dead || player == null || hp == null) return;
            var act = decision != null ? decision.Current : MammothAction.Patrol;
            bool engaged = cs != C.Free || act == MammothAction.Pursue || act == MammothAction.TrackLastKnown;
            if (!engaged && Time.time - lastHurt > 6f) return;
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
