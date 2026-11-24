using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;

namespace LastShift
{
    // ======================================================================================================================================================================
    // The ISOLATED agent brain: the same brain as GuardAgent/guard_agent (Python), running inside Unity, so an agent needs no server and no connection.
    //   percept -> zone of the person -> Bayes belief over the intruder's zone (team radio folded in) -> trained PPO network (exported to Resources/GuardAgent) picks a tactical goal
    //   -> reflexes (reload, cover, shoot on a clear shot, step for a line) -> stay-inside rule for the factory staff -> command (goto / engage / cover / reload).
    // The network weights and the zone map are exported from Python by GuardAgent/export_policies.py. Every agent owns its own EmbeddedBrain: nothing is shared except the radio (AgentTeam).
    // ======================================================================================================================================================================

    [Serializable] public class AgentZoneData { public int n; public string[] names; public float[] rect; public bool[] prot, bag; public float[] point, dist, los; public bool[] adj; }
    [Serializable] public class AgentPolicyData { public string role; public int inDim, h1, h2, outDim; public float[] w0, b0, w1, b1, w2, b2; }

    public class AgentNoise { public Vector2 pos; public float strength = 0.7f; public bool gunshot; }

    /// <summary>Everything the person perceives this tick (positions are (x, z)).</summary>
    public class AgentPercept
    {
        public float dt = 0.5f;
        public Vector2 guardPos; public float hp; public int ammo; public bool reloading;
        public bool playerVisible; public Vector2 playerPos; public float playerHpFrac = 1f; public bool clearShot;
        public bool alarm; public bool hasAlarmEvent; public Vector2 alarmEventPos;
        public List<AgentNoise> noises = new List<AgentNoise>();
        public float damageTaken;
        // scoring fields (never fed to the belief or the policy)
        public float damageDealtNow; public bool intruderInFactory, intruderSearchingBag; public int bagsSearchedTotal;
    }

    public class AgentCommand
    {
        public string mode = "goto", zone = "", why = "", role = "";
        public bool run, hasTarget; public Vector2 target;
        public float score, eta;
        public string[] beliefTop = new string[0], route = new string[0];
    }

    /// <summary>The premises as a graph of zones, exported from the level (NavMesh path lengths, measured line of sight).</summary>
    public class AgentZoneGraph
    {
        public int n; public string[] names; public float[] rect, point, dist, los; public bool[] prot, bag, adj;
        static AgentZoneGraph cache;

        public static AgentZoneGraph Load()
        {
            if (cache != null) return cache;
            var ta = Resources.Load<TextAsset>("GuardAgent/zone_graph_flat");
            if (ta == null) { Debug.LogError("[Agent] Resources/GuardAgent/zone_graph_flat.json is missing: run GuardAgent/export_policies.py"); return null; }
            var d = JsonUtility.FromJson<AgentZoneData>(ta.text);
            cache = new AgentZoneGraph { n = d.n, names = d.names, rect = d.rect, point = d.point, dist = d.dist, los = d.los, prot = d.prot, bag = d.bag, adj = d.adj };
            return cache;
        }

        public int Idx(string name) => Array.IndexOf(names, name);
        public float Dist(int i, int j) => dist[i * n + j];
        public float Los(int i, int j) => los[i * n + j];
        public bool Adj(int i, int j) => adj[i * n + j];
        public Vector2 Centre(int i) => new Vector2(0.5f * (rect[4 * i] + rect[4 * i + 1]), 0.5f * (rect[4 * i + 2] + rect[4 * i + 3]));
        public Vector2 Point(int i) => new Vector2(point[2 * i], point[2 * i + 1]);

        /// <summary>The smallest rectangle containing the point wins (the catwalk sits inside the rolling floor); outside every rectangle: the nearest centre.</summary>
        public int ZoneOf(float x, float z)
        {
            int best = -1; float area = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float x0 = rect[4 * i], x1 = rect[4 * i + 1], z0 = rect[4 * i + 2], z1 = rect[4 * i + 3];
                if (x >= x0 && x <= x1 && z >= z0 && z <= z1 && (x1 - x0) * (z1 - z0) < area) { best = i; area = (x1 - x0) * (z1 - z0); }
            }
            if (best >= 0) return best;
            float bd = float.MaxValue;
            for (int i = 0; i < n; i++) { float d = Vector2.Distance(Centre(i), new Vector2(x, z)); if (d < bd) { bd = d; best = i; } }
            return best;
        }

        /// <summary>A* over the zone graph (heuristic: straight line between centres).</summary>
        public List<int> Path(int a, int b)
        {
            var open = new List<int> { a }; var g = new float[n]; var f = new float[n]; var came = new int[n];
            for (int i = 0; i < n; i++) { g[i] = float.MaxValue; f[i] = float.MaxValue; came[i] = -1; }
            g[a] = 0f; f[a] = Vector2.Distance(Centre(a), Centre(b));
            while (open.Count > 0)
            {
                int cur = open.OrderBy(i => f[i]).First();
                if (cur == b) { var p = new List<int> { cur }; while (came[cur] >= 0) { cur = came[cur]; p.Insert(0, cur); } return p; }
                open.Remove(cur);
                for (int j = 0; j < n; j++)
                {
                    if (!Adj(cur, j)) continue;
                    float t = g[cur] + Dist(cur, j);
                    if (t < g[j]) { came[j] = cur; g[j] = t; f[j] = t + Vector2.Distance(Centre(j), Centre(b)); if (!open.Contains(j)) open.Add(j); }
                }
            }
            return new List<int> { a };
        }
    }

    /// <summary>The trained PPO policy: 48 -> 128 -> 128 -> 16, tanh, the deterministic action is the arg-max of the output.</summary>
    public class AgentPolicy
    {
        readonly AgentPolicyData d;
        readonly float[] a1, a2, o;
        public AgentPolicy(AgentPolicyData data) { d = data; a1 = new float[d.h1]; a2 = new float[d.h2]; o = new float[d.outDim]; }

        public static AgentPolicy Load(string role)
        {
            var ta = Resources.Load<TextAsset>("GuardAgent/policy_" + role);
            if (ta == null) return null;
            return new AgentPolicy(JsonUtility.FromJson<AgentPolicyData>(ta.text));
        }

        static void Layer(float[] x, float[] w, float[] b, float[] y, bool tanh)
        {
            int inn = x.Length;
            for (int r = 0; r < y.Length; r++)
            {
                float s = b[r]; int off = r * inn;
                for (int c = 0; c < inn; c++) s += w[off + c] * x[c];
                y[r] = tanh ? (float)Math.Tanh(s) : s;
            }
        }

        public int Act(float[] obs)
        {
            Layer(obs, d.w0, d.b0, a1, true); Layer(a1, d.w1, d.b1, a2, true); Layer(a2, d.w2, d.b2, o, false);
            int best = 0; for (int i = 1; i < o.Length; i++) if (o[i] > o[best]) best = i;
            return best;
        }
    }

    /// <summary>The team radio and the player's learned habits, shared by every agent in the scene (a shout carries only: "he was in zone Z, N seconds ago").</summary>
    public static class AgentTeam
    {
        static readonly List<(float t, int zone, string who)> reports = new List<(float, int, string)>();
        public static readonly Dictionary<string, int> Targets = new Dictionary<string, int>();      // who is in / heading to which zone (so the staff spread out)
        static float[] entry;                                                                        // where the player crosses the fence: counts, decayed

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)] static void Reset() { reports.Clear(); Targets.Clear(); entry = null; }

        public static void Report(string who, int zone)
        {
            float now = Time.time;
            reports.RemoveAll(r => now - r.t >= 15f);
            reports.Add((now, zone, who));
        }

        public static List<(float age, int zone)> Fresh(string who, float maxAge)
        {
            float now = Time.time;
            return reports.Where(r => r.who != who && now - r.t <= maxAge).Select(r => (now - r.t, r.zone)).OrderBy(r => r.Item1).ToList();
        }

        public static void RecordEntry(int zone, int n)
        {
            if (entry == null || entry.Length != n) { entry = new float[n]; for (int i = 0; i < n; i++) entry[i] = 0.5f; }
            for (int i = 0; i < n; i++) entry[i] *= 0.97f;
            entry[zone] += 1f;
        }

        /// <summary>Where he probably is at the start: mostly outside, with the learned fence-crossing habit.</summary>
        public static float[] Prior(AgentZoneGraph zg)
        {
            int n = zg.n;
            if (entry == null || entry.Length != n) { entry = new float[n]; for (int i = 0; i < n; i++) entry[i] = 0.5f; }
            var p = new float[n]; float ps = 0f, es = 0f;
            for (int i = 0; i < n; i++) { p[i] = 0.2f; }
            p[zg.Idx("tea_field")] = 2.0f;
            foreach (var v in p) ps += v; foreach (var v in entry) es += v;
            var r = new float[n];
            for (int i = 0; i < n; i++) r[i] = p[i] / ps * 0.6f + entry[i] / es * 0.4f;
            return r;
        }
    }

    /// <summary>Bayes filter over WHERE the intruder is (one probability per zone).</summary>
    public class AgentBelief
    {
        readonly AgentZoneGraph zg; readonly int n; readonly float[,] T;
        public float[] b; public int lastSeenZone = -1; public float lastSeenAge = 99f;

        public AgentBelief(AgentZoneGraph g)
        {
            zg = g; n = g.n; T = new float[n, n]; b = new float[n];
            var pull = new float[n]; for (int i = 0; i < n; i++) pull[i] = 1f;
            foreach (var z in new[] { "warehouse", "warehouse", "loft", "storeroom", "line_houses" }) pull[zg.Idx(z)] += 0.35f;
            pull[zg.Idx("gate_road")] += 0.3f;
            const float move = 0.22f;
            for (int i = 0; i < n; i++)
            {
                var nb = Enumerable.Range(0, n).Where(j => zg.Adj(i, j)).ToList();
                T[i, i] = 1f - move;
                if (nb.Count > 0)
                {
                    float sum = 0f; var w = new float[nb.Count];
                    for (int k = 0; k < nb.Count; k++) { w[k] = pull[nb[k]] / Mathf.Max(zg.Dist(i, nb[k]), 1f); sum += w[k]; }
                    for (int k = 0; k < nb.Count; k++) T[i, nb[k]] = move * w[k] / sum;
                }
                else T[i, i] = 1f;
            }
        }

        public void Reset(float[] prior)
        {
            float s = prior.Sum(); for (int i = 0; i < n; i++) b[i] = prior[i] / s;
            lastSeenZone = -1; lastSeenAge = 99f;
        }

        public void Predict(float dt)
        {
            int steps = Mathf.Max(1, (int)Math.Round(dt));
            for (int s = 0; s < steps; s++)
            {
                var nx = new float[n];
                for (int i = 0; i < n; i++) { float m = b[i]; if (m == 0f) continue; for (int j = 0; j < n; j++) nx[j] += m * T[i, j]; }
                b = nx;
            }
            lastSeenAge += dt; Norm();
        }

        public void ObserveVisible(int zone) { Array.Clear(b, 0, n); b[zone] = 1f; lastSeenZone = zone; lastSeenAge = 0f; }
        public void ObserveNotVisible(float[] pSee) { for (int i = 0; i < n; i++) b[i] *= 1f - Mathf.Clamp(pSee[i], 0f, 0.97f); Norm(); }
        public void ObserveNoise(int zone, float strength, float spread = 14f) { for (int i = 0; i < n; i++) b[i] *= 0.12f + strength * Mathf.Exp(-zg.Dist(zone, i) / spread); Norm(); }
        public void Inject(int zone, float weight) { for (int i = 0; i < n; i++) b[i] = (1f - weight) * b[i] + (i == zone ? weight : 0f); Norm(); }

        void Norm()
        {
            float s = b.Sum();
            if (float.IsNaN(s) || s < 1e-9f) { for (int i = 0; i < n; i++) b[i] = 1f / n; }
            else for (int i = 0; i < n; i++) b[i] /= s;
        }
    }

    /// <summary>Weapon, goals and focus of a role (mirrors GuardAgent/guard_agent/config.py).</summary>
    public class AgentRoleSpec
    {
        public string key; public float hp, walk, run, gunRange; public int mag;
        public float engageRange, coverHp;
        public float damage, killBonus, insidePerS, breach, escape, death, step, bagSearchPerS, bagTaken, contact, trackPerS;
        public string[] focus;      // null = every protected zone

        static readonly Dictionary<string, AgentRoleSpec> roles = new Dictionary<string, AgentRoleSpec>
        {
            { "sentinel",   new AgentRoleSpec { key = "sentinel",   hp = 100, walk = 1.8f, run = 5.2f, gunRange = 22, mag = 12, engageRange = 22, coverHp = 0.25f, damage = 1, killBonus = 40, insidePerS = -0.6f, breach = -15, escape = -60,  death = -30, step = -0.01f } },
            { "gatekeeper", new AgentRoleSpec { key = "gatekeeper", hp = 100, walk = 1.8f, run = 5.2f, gunRange = 22, mag = 12, engageRange = 22, coverHp = 0.25f, damage = 1, killBonus = 60, insidePerS = -0.1f, breach = -3,  escape = -120, death = -30, step = -0.01f, focus = new[] { "gate_road", "yard_w", "yard_e" } } },
            { "tea_keeper", new AgentRoleSpec { key = "tea_keeper", hp = 80,  walk = 1.4f, run = 4.6f, gunRange = 10, mag = 2,  engageRange = 7,  coverHp = 0.4f,  damage = 1, killBonus = 40, insidePerS = 0f,    breach = -2,  escape = -20,  death = -40, step = -0.01f, bagSearchPerS = -1f, bagTaken = -12, focus = new[] { "warehouse", "loft", "storeroom", "line_houses" } } },
            { "lookout",    new AgentRoleSpec { key = "lookout",    hp = 80,  walk = 1.4f, run = 4.6f, gunRange = 10, mag = 2,  engageRange = 6,  coverHp = 0.6f,  damage = 0.3f, killBonus = 15, insidePerS = -0.1f, breach = -3, escape = -10,  death = -70, step = -0.01f, contact = 12, trackPerS = 0.6f } },
        };
        public static AgentRoleSpec Get(string key) => roles.TryGetValue(key, out var r) ? r : roles["sentinel"];
    }

    /// <summary>One person's brain. Create one per agent; call NewGame() when a game starts, Decide() every decision tick, EndGame() when it ends.</summary>
    public class EmbeddedBrain
    {
        readonly AgentZoneGraph zg; readonly int n; readonly AgentRoleSpec spec; readonly AgentPolicy policy; readonly AgentBelief belief;
        readonly string id; readonly bool confined; readonly bool[] allowed; readonly bool[] focus; readonly int post;
        readonly int aEngage, aCover, aReload;
        readonly System.Random rng = new System.Random();

        bool alarm, visible, wasVisible, breached; float underFire, iHp = 1f, score, damageDealt; int guardZone, bagsSeen, contacts;
        bool heardGunshot, heardRadio; (int zone, float until)? committed;
        float curHp, curAmmo; bool curReloading;

        public bool Ready => zg != null;
        public bool HasPolicy => policy != null;
        public float Score => score;
        public float DamageDealt => damageDealt;

        public EmbeddedBrain(string roleKey, string agentId, bool confined, string postZone, string[] focusOverride)
        {
            zg = AgentZoneGraph.Load(); if (zg == null) return;
            n = zg.n; spec = AgentRoleSpec.Get(roleKey); id = agentId; this.confined = confined;
            policy = AgentPolicy.Load(spec.key); belief = new AgentBelief(zg);
            allowed = new bool[n]; for (int i = 0; i < n; i++) allowed[i] = !confined || zg.prot[i];
            post = postZone != null ? zg.Idx(postZone) : -1;
            focus = new bool[n]; var names = (focusOverride != null && focusOverride.Length > 0) ? focusOverride : spec.focus;
            if (names == null) for (int i = 0; i < n; i++) focus[i] = zg.prot[i];
            else foreach (var nm in names) { int k = zg.Idx(nm); if (k >= 0) focus[k] = true; }
            aEngage = n; aCover = n + 1; aReload = n + 2;
            NewGame();
        }

        public void NewGame()
        {
            if (!Ready) return;
            belief.Reset(AgentTeam.Prior(zg));
            alarm = visible = wasVisible = breached = false; underFire = 0f; iHp = 1f; score = 0f; damageDealt = 0f; bagsSeen = 0; contacts = 0; committed = null;
        }

        public float EndGame(string outcome)
        {
            if (outcome == "escape") score += spec.escape; else if (outcome == "kill") score += spec.killBonus; else if (outcome == "guard_down") score += spec.death;
            return score;
        }

        // ------------------------------------------------------------------ perception -> belief
        void Update(AgentPercept p)
        {
            var W = spec; float dt = p.dt;
            guardZone = zg.ZoneOf(p.guardPos.x, p.guardPos.y);
            curHp = p.hp; curAmmo = p.ammo; curReloading = p.reloading;
            alarm = p.alarm || alarm;
            underFire = p.damageTaken > 0f ? 3f : Mathf.Max(0f, underFire - dt);
            wasVisible = visible; visible = p.playerVisible;
            if (p.hasAlarmEvent) { int ez = zg.ZoneOf(p.alarmEventPos.x, p.alarmEventPos.y); belief.Inject(ez, 0.7f); AgentTeam.RecordEntry(ez, n); }
            float ageBefore = belief.lastSeenAge;
            belief.Predict(dt);
            heardGunshot = false;
            foreach (var ev in p.noises)
            {
                float x = ev.pos.x, z = ev.pos.y;
                if (ev.gunshot)
                {
                    float d = Vector2.Distance(ev.pos, p.guardPos), sigma = 3f + 0.2f * d;      // he hears THAT a shot was fired and roughly where from: blurred more with distance
                    x += Gauss() * sigma; z += Gauss() * sigma;
                    belief.ObserveNoise(zg.ZoneOf(x, z), ev.strength + 0.2f, 24f); heardGunshot = true;
                }
                else belief.ObserveNoise(zg.ZoneOf(x, z), ev.strength);
            }
            heardRadio = false;
            if (!visible)
            {
                var fresh = AgentTeam.Fresh(id, 6f);
                if (fresh.Count > 0)
                {
                    var (age, zone) = fresh[0];
                    belief.Inject(zone, 0.45f * Mathf.Exp(-age / 3f));
                    if (age < belief.lastSeenAge) { belief.lastSeenZone = zone; belief.lastSeenAge = age; }
                    heardRadio = true;
                }
            }
            if (visible)
            {
                int pz = zg.ZoneOf(p.playerPos.x, p.playerPos.y);
                belief.ObserveVisible(pz); AgentTeam.Report(id, pz); iHp = p.playerHpFrac;
            }
            else
            {
                var pv = new float[n];
                for (int z = 0; z < n; z++) pv[z] = Mathf.Min(0.95f, 0.85f * (alarm ? 1.4f : 1f) * zg.Los(guardZone, z)) * (1f - Mathf.Exp(-dt));
                belief.ObserveNotVisible(pv);
            }
            // score bookkeeping (only the game's scoring fields)
            damageDealt += p.damageDealtNow; score += W.damage * p.damageDealtNow + W.step * dt;
            if (p.intruderInFactory) { score += W.insidePerS * dt; if (!breached) { breached = true; score += W.breach; } }
            if (p.intruderSearchingBag) score += W.bagSearchPerS * dt;
            if (p.bagsSearchedTotal > bagsSeen) { score += W.bagTaken * (p.bagsSearchedTotal - bagsSeen); bagsSeen = p.bagsSearchedTotal; }
            if (visible) { score += W.trackPerS * dt; if (!wasVisible && ageBefore > 25f) { contacts++; score += W.contact; } }
        }

        float Gauss() { double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2)); }

        float[] Observation()
        {
            var o = new float[3 * n + 9];
            o[guardZone] = 1f;
            for (int i = 0; i < n; i++) o[n + i] = belief.b[i];
            if (belief.lastSeenZone >= 0) o[2 * n + belief.lastSeenZone] = 1f;
            int k = 3 * n;
            o[k] = visible ? 1f : 0f;
            o[k + 1] = Mathf.Min(1f, belief.lastSeenAge / 60f);
            o[k + 2] = alarm ? 1f : 0f;
            o[k + 3] = iHp;
            o[k + 4] = Mathf.Max(0f, curHp / spec.hp);
            o[k + 5] = curAmmo / spec.mag;
            o[k + 6] = curReloading ? 1f : 0f;
            o[k + 7] = Mathf.Min(1f, underFire / 3f);
            float fm = 0f; for (int i = 0; i < n; i++) if (focus[i]) fm += belief.b[i];
            o[k + 8] = fm;
            for (int i = 0; i < o.Length; i++) o[i] = Mathf.Clamp(o[i], -1f, 1f);
            return o;
        }

        // ------------------------------------------------------------------ decision
        public AgentCommand Decide(AgentPercept p)
        {
            if (!Ready) return new AgentCommand { mode = "reload", why = "no zone graph", role = spec != null ? spec.key : "" };
            Update(p);
            var o = Observation();
            float hpFrac = o[3 * n + 4], ammoFrac = o[3 * n + 5], fire = o[3 * n + 7]; bool reloading = o[3 * n + 6] > 0.5f;
            float dist = visible ? Vector2.Distance(p.playerPos, p.guardPos) : 99f;
            int a; string reason;
            if (ammoFrac <= 0.01f && !reloading) { a = visible && fire > 0.3f ? aCover : aReload; reason = "magazine empty"; }
            else if (visible && hpFrac < spec.coverHp && fire > 0.3f) { a = aCover; reason = "hurt and under fire"; }
            else
            {
                if (policy != null) { a = policy.Act(o); reason = "learned policy"; }
                else { a = BestInside(); reason = "no policy: belief planner"; }
                if (visible && a < n && p.clearShot && ammoFrac > 0.01f && dist <= Mathf.Min(spec.gunRange, spec.engageRange)) { a = aEngage; reason += " + reflex: clear shot"; }
            }
            // he sees the player but something blocks the shot: step towards him for a clear line
            if (visible && !p.clearShot && ammoFrac > 0.01f && a != aCover && a != aReload && dist <= Mathf.Max(6f, Mathf.Min(spec.gunRange, spec.engageRange)))
            {
                int pz = zg.ZoneOf(p.playerPos.x, p.playerPos.y);
                if (allowed[pz])
                {
                    AgentTeam.Targets[id] = pz;
                    return new AgentCommand { mode = "goto", zone = zg.names[pz], hasTarget = true, target = p.playerPos, run = true, why = reason + " | no clear shot: moving for a line", score = score, role = spec.key, beliefTop = BeliefTop() };
                }
            }
            if (confined && a < n && !allowed[a]) { a = BestInside(); reason += " | stays inside"; }
            else if (confined && a < n && !visible && CrowdAt(a) >= 2) { a = BestInside(); reason += " | spreads out"; }
            if (heardRadio && !visible) reason += " | radio";
            if (heardGunshot) reason += " | gunshot";
            return ToCommand(a, reason);
        }

        int CrowdAt(int z) { int c = 0; foreach (var kv in AgentTeam.Targets) if (kv.Key != id && kv.Value == z) c++; return c; }

        /// <summary>The best zone INSIDE the building to wait in: where the intruder probably is, or behind the door he is most likely to come through; a small bonus for the post, a penalty for crowding.</summary>
        int BestInside()
        {
            int best = guardZone; float bs = float.MinValue;
            for (int z = 0; z < n; z++)
            {
                if (!allowed[z]) continue;
                float outsideNext = 0f; for (int j = 0; j < n; j++) if (zg.Adj(z, j) && !allowed[j]) outsideNext += belief.b[j];
                float s = belief.b[z] + 0.6f * outsideNext + (z == post ? 0.25f : 0f) - 0.01f * zg.Dist(guardZone, z) - 0.45f * CrowdAt(z);
                if (s > bs) { bs = s; best = z; }
            }
            return best;
        }

        AgentCommand ToCommand(int a, string reason)
        {
            var cmd = new AgentCommand { role = spec.key };
            if (a < n)
            {
                float now = Time.time;
                if (committed.HasValue && committed.Value.zone != a && now < committed.Value.until && !visible) a = committed.Value.zone;       // stay on the goal chosen a moment ago unless something new appears
                else committed = (a, now + 2f);
                var route = zg.Path(guardZone, a);
                float len = 0f; for (int i = 0; i < route.Count - 1; i++) len += zg.Dist(route[i], route[i + 1]);
                cmd.mode = "goto"; cmd.zone = zg.names[a]; cmd.hasTarget = true; cmd.target = zg.Point(a); cmd.run = alarm || visible;
                cmd.route = route.Select(r => zg.names[r]).ToArray(); cmd.eta = len / (alarm ? spec.run : spec.walk * 1.4f);
            }
            else if (a == aEngage)
            {
                cmd.mode = "engage"; cmd.zone = zg.names[guardZone];
                if (!visible && belief.lastSeenZone >= 0 && belief.lastSeenAge < 8f)
                {
                    int lz = belief.lastSeenZone;
                    if (!allowed[lz]) { lz = BestInside(); reason += " | stays inside"; }          // he was last seen outside: wait at the door instead of chasing him out
                    if (lz != guardZone) { cmd.mode = "goto"; cmd.zone = zg.names[lz]; cmd.hasTarget = true; cmd.target = zg.Point(lz); cmd.run = true; }
                }
            }
            else if (a == aCover) { cmd.mode = "cover"; cmd.zone = zg.names[guardZone]; }
            else { cmd.mode = "reload"; cmd.zone = zg.names[guardZone]; }
            AgentTeam.Targets[id] = a < n ? a : guardZone;
            cmd.why = reason; cmd.score = score; cmd.beliefTop = BeliefTop();
            return cmd;
        }

        string[] BeliefTop()
        {
            return Enumerable.Range(0, n).OrderByDescending(i => belief.b[i]).Take(3).Select(i => zg.names[i] + " " + belief.b[i].ToString("F2", CultureInfo.InvariantCulture)).ToArray();
        }

        // ------------------------------------------------------------------ for the parity test
        public float[] DebugObservation(AgentPercept p) { Update(p); return Observation(); }
        public float[] DebugBelief => belief.b;
    }
}
