using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using LastShift;

namespace GuardAgentTools
{
    /// <summary>
    /// Tools > Guard Agent
    ///   Attach Agents To Everyone In The Factory   gives every guard and worker a brain with his own role (Sentinel, Gatekeeper, Tea keeper, Lookout); undoable, no duplicates
    ///   Attach Agent To Guard      puts GuardAgentBridge on the selected guard (or, with nothing selected, the guard nearest the factory yard); undoable, no duplicates
    ///   Detach Agent               removes it again (the stock EnemyAI takes over)
    ///   Export Zone Graph          measures the open scene: NavMesh-snapped centre of every zone, NavMesh path length between all zones, and line of sight between zones.
    ///                              Reads GuardAgent/data/zone_defs.json (python export_zone_defs.py) and writes GuardAgent/data/zone_graph.json.
    ///                              Retrain with the exported graph so the policy is trained on the real level (python train.py).
    /// </summary>
    public static class GuardAgentMenu
    {
        const string DataDir = "GuardAgent/data";

        [MenuItem("Tools/Guard Agent/Attach Agent To Guard")]
        public static void Attach()
        {
            var guard = PickGuard();
            if (guard == null) { Debug.LogWarning("[Guard Agent] no Guard (EnemyAI kind Guard) found in the open scene"); return; }
            if (guard.GetComponent<GuardAgentBridge>() != null) { Debug.Log($"[Guard Agent] {guard.name} already has the agent"); return; }
            var b = Undo.AddComponent<GuardAgentBridge>(guard.gameObject);
            b.role = AgentRole.Sentinel; b.toughness = 1f;
            EditorUtility.SetDirty(b);
            if (!Application.isPlaying) EditorSceneManager.MarkAllScenesDirty();
            Debug.Log($"[Guard Agent] agent attached to '{guard.name}' (position {guard.transform.position}). Start the brain: cd GuardAgent ; .venv\\Scripts\\python -m guard_agent.server   then press Play.");
        }

        [MenuItem("Tools/Guard Agent/Detach Agent")]
        public static void Detach()
        {
            int n = 0;
            foreach (var b in Object.FindObjectsByType<GuardAgentBridge>(FindObjectsInactive.Include).ToList()) { Undo.DestroyObjectImmediate(b); n++; }
            if (!Application.isPlaying && n > 0) EditorSceneManager.MarkAllScenesDirty();
            Debug.Log($"[Guard Agent] removed {n} agent bridge(s)");
        }

        // ------------------------------------------------------------------ the guard and the factory staff
        /// <summary>ONE security guard gets the full brain (Sentinel: keep the player out of the factory, put him down, score on damage). He roams freely.
        /// The other guards are not touched: they keep the game's own AI.</summary>
        const string SecurityGuard = "Guard_14";

        /// <summary>The factory staff (the workers): each gets a brain whose goals stay INSIDE the factory boundary: he waits at his post, covers the doors, guards his part of the stock,
        /// and never leaves the building. (role, post zone, and where to move him if he does not already stand inside; NaN = he is already inside)</summary>
        static readonly Dictionary<string, (AgentRole role, string post, float x, float z)> Staff = new Dictionary<string, (AgentRole, string, float, float)>
        {
            { "Worker_15", (AgentRole.Lookout,   "rolling",   float.NaN, 0f) },   // rolling floor: spot, report, survive
            { "Worker_16", (AgentRole.Lookout,   "loft",      float.NaN, 0f) },   // withering loft
            { "Worker_17", (AgentRole.TeaKeeper, "storeroom", float.NaN, 0f) },   // storeroom: guard the tea chests
            { "Worker_18", (AgentRole.TeaKeeper, "boiler",    40f, 66f) },        // was in the line houses: moved into the boiler room
        };

        static void PlaceInside(EnemyAI e, float x, float z)
        {
            if (!NavMesh.SamplePosition(new Vector3(x, 9f, z), out var hit, 6f, NavMesh.AllAreas)) { Debug.LogWarning($"[Guard Agent] no NavMesh near ({x},{z}) for {e.name}"); return; }
            Undo.RecordObject(e.transform, "Place inside");
            e.transform.position = hit.position;
            var agent = e.GetComponent<NavMeshAgent>();
            if (agent != null && agent.enabled && agent.isOnNavMesh) agent.Warp(hit.position);
            Undo.RecordObject(e, "Place inside");
            e.patrolRoute = null;                 // the stock AI (used only when the brain is off) wanders around his new post instead of walking back outside
            e.wanderRadius = 5f;
        }

        static void Configure(GuardAgentBridge b, AgentRole role, bool inside, string post)
        {
            Undo.RecordObject(b, "Agent setup");
            b.role = role; b.useRolePreset = true; b.stayInside = inside; b.postZone = post; b.toughness = 1f;
            EditorUtility.SetDirty(b);
        }

        [MenuItem("Tools/Guard Agent/Attach Agents To Everyone In The Factory")]
        public static void AttachAll()
        {
            int added = 0, removed = 0;
            var lines = new List<string>();
            var people = Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude).Where(x => x.kind == EnemyKind.Guard || x.kind == EnemyKind.Worker).OrderBy(x => x.name).ToList();
            string guardName = people.Any(x => x.name == SecurityGuard) ? SecurityGuard : (PickGuard() != null ? PickGuard().name : null);
            foreach (var e in people)
            {
                var b = e.GetComponent<GuardAgentBridge>();
                if (e.name == guardName)
                {
                    if (b == null) { b = Undo.AddComponent<GuardAgentBridge>(e.gameObject); added++; }
                    Configure(b, AgentRole.Sentinel, false, "");
                    lines.Add($"{e.name} (Guard) -> SECURITY GUARD agent: Sentinel, roams freely, full goal set");
                }
                else if (Staff.TryGetValue(e.name, out var st))
                {
                    if (b == null) { b = Undo.AddComponent<GuardAgentBridge>(e.gameObject); added++; }
                    Configure(b, st.role, true, st.post);
                    if (!float.IsNaN(st.x)) PlaceInside(e, st.x, st.z);
                    lines.Add($"{e.name} (Worker) -> {st.role}, stays inside the factory (post: {st.post})");
                }
                else
                {
                    if (b != null) { Undo.DestroyObjectImmediate(b); removed++; }
                    lines.Add($"{e.name} ({e.kind}) -> stock AI (no agent)");
                }
            }
            if (!Application.isPlaying) EditorSceneManager.MarkAllScenesDirty();
            Debug.Log($"[Guard Agent] 1 security guard + factory staff: {added} bridges added, {removed} removed.\n" + string.Join("\n", lines) +
                      "\nStart the brain:  cd GuardAgent ; .\\start_brain.ps1   then press Play. (The hunter and the other guards keep the game's own AI.)");
        }

        static EnemyAI PickGuard()
        {
            var sel = Selection.activeGameObject != null ? Selection.activeGameObject.GetComponentInParent<EnemyAI>() : null;
            if (sel != null && sel.kind == EnemyKind.Guard) return sel;
            var yard = new Vector3(30f, 8f, 46f);
            return Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude).Where(e => e.kind == EnemyKind.Guard)
                .OrderBy(e => Vector3.Distance(e.transform.position, yard)).FirstOrDefault();
        }

        // ------------------------------------------------------------------ zone graph export
        [System.Serializable] class ZoneDef { public string name; public float x0, x1, z0, z1; public bool protected_; public bool bag; }
        [System.Serializable] class EdgeDef { public int a, b; }
        [System.Serializable] class Defs { public ZoneDefJson[] zones; public EdgeDef[] edges; }
        [System.Serializable] class ZoneDefJson { public string name; public float x0, x1, z0, z1; public bool protectedFlag; public bool bag; }

        [MenuItem("Tools/Guard Agent/Export Zone Graph")]
        public static void ExportZoneGraph()
        {
            string defsPath = Path.Combine(DataDir, "zone_defs.json");
            if (!File.Exists(defsPath)) { Debug.LogError("[Guard Agent] GuardAgent/data/zone_defs.json missing: run  python export_zone_defs.py  in GuardAgent first"); return; }
            // JsonUtility cannot map the word 'protected', so the file uses it as a plain key: read it by text replace
            string text = File.ReadAllText(defsPath).Replace("\"protected\"", "\"protectedFlag\"");
            var defs = JsonUtility.FromJson<Defs>(text);
            int n = defs.zones.Length;
            var rects = defs.zones;
            var rng = new System.Random(7);

            // 10 NavMesh-snapped sample points per zone; the zone point is the one nearest the centre
            var samples = new List<Vector3>[n];
            var point = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var z = rects[i]; samples[i] = new List<Vector3>();
                Vector3 centre = new Vector3(0.5f * (z.x0 + z.x1), 8f, 0.5f * (z.z0 + z.z1));
                for (int k = 0; k < 60 && samples[i].Count < 10; k++)
                {
                    Vector3 p = new Vector3(Mathf.Lerp(z.x0, z.x1, (float)rng.NextDouble()), 30f, Mathf.Lerp(z.z0, z.z1, (float)rng.NextDouble()));
                    // sample from above: pick the NavMesh point nearest in XZ, any height
                    if (NavMesh.SamplePosition(new Vector3(p.x, GroundY(p.x, p.z), p.z), out var hit, 6f, NavMesh.AllAreas) && hit.position.x >= z.x0 - 1f && hit.position.x <= z.x1 + 1f && hit.position.z >= z.z0 - 1f && hit.position.z <= z.z1 + 1f)
                        samples[i].Add(hit.position);
                }
                if (NavMesh.SamplePosition(new Vector3(centre.x, GroundY(centre.x, centre.z), centre.z), out var c, 10f, NavMesh.AllAreas)) point[i] = c.position;
                else if (samples[i].Count > 0) point[i] = samples[i][0];
                else { Debug.LogError($"[Guard Agent] zone '{z.name}' has no NavMesh: bake the NavMesh first"); return; }
            }

            // a zone's point must be on the same NavMesh island as the yard: of its sample points, take the one nearest the centre that has a complete path from the yard
            var reach = new NavMeshPath();
            Vector3 reference = point[System.Array.FindIndex(rects, r => r.name == "yard_w") >= 0 ? System.Array.FindIndex(rects, r => r.name == "yard_w") : 0];
            var report = new List<string>();
            for (int i = 0; i < n; i++)
            {
                Vector3 centre = new Vector3(0.5f * (rects[i].x0 + rects[i].x1), 0f, 0.5f * (rects[i].z0 + rects[i].z1));
                var cands = new List<Vector3>(samples[i]) { point[i] };
                cands = cands.OrderBy(v => (new Vector3(v.x, 0f, v.z) - centre).sqrMagnitude).ToList();
                bool found = false;
                foreach (var v in cands)
                    if (NavMesh.CalculatePath(reference, v, NavMesh.AllAreas, reach) && reach.status == NavMeshPathStatus.PathComplete) { if ((v - point[i]).sqrMagnitude > 0.01f) report.Add($"{rects[i].name} moved to a connected spot ({v.x:F0},{v.z:F0})"); point[i] = v; found = true; break; }
                if (!found) report.Add($"{rects[i].name}: NO sample point is connected to the yard (isolated NavMesh island or sealed room)");
            }
            if (report.Count > 0) Debug.LogWarning("[Guard Agent] " + string.Join("; ", report));

            // path length between every pair of zones (NavMesh)
            var dist = new float[n, n];
            var path = new NavMeshPath();
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    if (i == j) { dist[i, j] = 0f; continue; }
                    if (NavMesh.CalculatePath(point[i], point[j], NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete)
                    {
                        float len = 0f; for (int c = 1; c < path.corners.Length; c++) len += Vector3.Distance(path.corners[c - 1], path.corners[c]);
                        dist[i, j] = len;
                    }
                    else dist[i, j] = 1000f;
                }

            // line of sight between zones: fraction of sample pairs (eye height) with nothing solid in between
            var los = new float[n, n];
            for (int i = 0; i < n; i++)
                for (int j = i; j < n; j++)
                {
                    int total = 0, clear = 0;
                    foreach (var a in samples[i].Take(8))
                        foreach (var b in samples[j].Take(8))
                        {
                            if (i == j && a == b) continue;
                            total++;
                            if (Vector3.Distance(a, b) > 40f) continue;
                            if (!Physics.Linecast(a + Vector3.up * 1.6f, b + Vector3.up * 1.4f, ~(1 << 2), QueryTriggerInteraction.Ignore)) clear++;
                        }
                    los[i, j] = los[j, i] = total > 0 ? (float)clear / total : 0f;
                }

            var sb = new StringBuilder();
            sb.Append("{\n \"scene\": \"").Append(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name).Append("\",\n \"zones\": [\n");
            for (int i = 0; i < n; i++)
            {
                var z = rects[i];
                sb.Append($"  {{\"name\":\"{z.name}\",\"rect\":[{F(z.x0)},{F(z.x1)},{F(z.z0)},{F(z.z1)}],\"protected\":{B(z.protectedFlag)},\"bag\":{B(z.bag)},\"point\":[{F(point[i].x)},{F(point[i].z)}]}}{(i < n - 1 ? "," : "")}\n");
            }
            sb.Append(" ],\n \"edges\": [");
            sb.Append(string.Join(",", defs.edges.Select(e => $"{{\"a\":{e.a},\"b\":{e.b}}}")));
            sb.Append("],\n \"dist\": ").Append(Matrix(dist, n)).Append(",\n \"los\": ").Append(Matrix(los, n)).Append("\n}\n");
            Directory.CreateDirectory(DataDir);
            string outPath = Path.Combine(DataDir, "zone_graph.json");
            File.WriteAllText(outPath, sb.ToString());
            var unreachable = new List<string>();
            for (int i = 0; i < n; i++) for (int j = i + 1; j < n; j++) if (dist[i, j] >= 999f) unreachable.Add($"{rects[i].name}-{rects[j].name}");
            Debug.Log($"[Guard Agent] zone graph written: {outPath} ({n} zones). Unreachable pairs: {(unreachable.Count == 0 ? "none" : string.Join(", ", unreachable))}");
        }

        static float GroundY(float x, float z)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var o = t.transform.position; var s = t.terrainData.size;
                if (x >= o.x && x <= o.x + s.x && z >= o.z && z <= o.z + s.z) return o.y + t.SampleHeight(new Vector3(x, 0f, z));
            }
            return 8f;
        }

        static string F(float v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        static string B(bool v) => v ? "true" : "false";
        static string Matrix(float[,] m, int n)
        {
            var rows = new List<string>();
            for (int i = 0; i < n; i++) { var r = new List<string>(); for (int j = 0; j < n; j++) r.Add(F(m[i, j])); rows.Add("[" + string.Join(",", r) + "]"); }
            return "[" + string.Join(",", rows) + "]";
        }
    }
}
