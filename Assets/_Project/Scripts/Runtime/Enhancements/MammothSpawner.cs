using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace LastShift
{
    /// <summary>Builds "Sanjula's Mammoth" at runtime from the scene's Elephant (cloned, scaled, recoloured, extra tusks + fur) and drops it deep in the jungle.</summary>
    public class MammothSpawner : MonoBehaviour
    {
        static readonly Vector2[] Candidates =
        {
            new Vector2(16,-33), new Vector2(14,-30), new Vector2(20,-34), new Vector2(10,-36),
            new Vector2(24,-24), new Vector2(14,-22), new Vector2(34,-26), new Vector2(6,-28),
            new Vector2(30,-14), new Vector2(46,-22), new Vector2(40,-34), new Vector2(58,-18)
        };

        /// <summary>Spawn is retried (NavMesh / scene objects may not be ready on the first frame) so a mammoth exists in EVERY session.</summary>
        IEnumerator Start()
        {
            const int attempts = 40;
            for (int i = 1; i <= attempts; i++)
            {
                yield return null;
                if (FindObjectsByType<MammothAI>().Length > 0) yield break;     // exactly one mammoth per session
                if (SafeTrySpawn(i == attempts)) { Debug.Log($"[Sanjula] Mammoth spawned (attempt {i}), connectivity {lastLinks}/16."); yield break; }
                yield return new WaitForSeconds(0.25f);
            }
            Debug.LogError("[Sanjula] The mammoth could not be spawned after " + attempts + " attempts.");
        }

        int lastLinks;
        bool SafeTrySpawn(bool lastChance)
        {
            try { return TrySpawn(lastChance); }
            catch (System.Exception e) { Debug.LogWarning("[Sanjula] spawn attempt failed, retrying: " + e.Message); return false; }
        }

        bool TrySpawn(bool lastChance)
        {
            EnemyAI src = null;
            foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsInactive.Include))
                if (e.kind == EnemyKind.Elephant) { src = e; break; }
            if (src == null) { if (lastChance) Debug.LogWarning("[Sanjula] No Elephant to clone."); return false; }

            var pl = FindAnyObjectByType<PlayerController>();
            Vector3 camp = pl != null ? pl.transform.position : new Vector3(7f, 0f, -57f);   // the player starts in the camp
            MammothTerritory.Init(camp, new Vector3(30f, 0f, -25f));   // jungle centre for the first pass
            Vector3 home = Vector3.zero; bool ok = false;
            var spots = new List<Vector3>();
            var cands = new List<Vector2>(Candidates);
            for (int i = 0; i < 24; i++)      // plus random points in the jungle, so it never depends on a few hard-coded spots
                cands.Add(new Vector2(Random.Range(MammothTerritory.Min.x + 5f, MammothTerritory.Max.x - 5f), Random.Range(MammothTerritory.Min.y + 5f, MammothTerritory.Max.y - 5f)));
            foreach (var c in cands)
            {
                var wp = new Vector3(c.x, 0f, c.y);
                if (MammothTerritory.InCamp(wp) || Vector2.Distance(c, new Vector2(camp.x, camp.z)) < MammothTerritory.CampRadius + 8f) continue;
                if (Physics.Raycast(new Vector3(c.x, 300f, c.y), Vector3.down, out var h, 600f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                    && NavMesh.SamplePosition(h.point, out var nh, 6f, NavMesh.AllAreas)
                    && MammothTerritory.Contains(nh.position)
                    && Vector3.Distance(nh.position, src.transform.position) > 12f)
                    spots.Add(nh.position);
            }
            if (spots.Count > 0)
            {
                // a different (random) valid spot each session; prefer ones that are not too far from the camp so the mammoth is discoverable
                // a spot is only good if it is connected to a lot of walkable jungle (an isolated pocket gives a useless patrol / search graph)
                var scored = new List<(Vector3 p, int links, float dist)>();
                foreach (var sp in spots) scored.Add((sp, Connectivity(sp), Mathf.Abs(Vector3.Distance(sp, camp) - 55f)));
                int maxLinks = 0; foreach (var sc in scored) maxLinks = Mathf.Max(maxLinks, sc.links);
                scored.RemoveAll(sc => sc.links < Mathf.Max(Mathf.Min(10, maxLinks), maxLinks - 3));      // keep only the best-connected spots (never empties the list)
                scored.Sort((x, y) => x.dist.CompareTo(y.dist));
                int pool = Mathf.Min(scored.Count, 5);
                var pick = scored[Random.Range(0, pool)]; home = pick.p; lastLinks = pick.links; ok = true;
            }
            if (!ok) { if (lastChance) Debug.LogWarning("[Sanjula] No NavMesh spot for the mammoth."); return false; }

            bool wasActive = src.gameObject.activeSelf;
            src.gameObject.SetActive(false);
            var go = Instantiate(src.gameObject);
            src.gameObject.SetActive(wasActive);
            go.name = "Sanjulas Mammoth";
            DestroyImmediate(go.GetComponent<EnemyAI>());
            go.transform.position = home;
            MammothTerritory.Init(camp, home);

            var hp = go.GetComponent<Health>(); hp.max = 1400f; hp.regenDelay = 0f; hp.regenPerSecond = 0f;
            var cap = go.GetComponent<CapsuleCollider>();
            const float S = 1.6f;
            if (cap != null) { cap.center *= S; cap.radius *= S; cap.height *= S; }
            var agent = go.GetComponent<NavMeshAgent>();
            if (agent != null) { agent.radius = 1.7f; agent.height = 4.8f; agent.acceleration = 14f; agent.angularSpeed = 140f; }

            var model = go.transform.Find("Model");
            if (model != null)
            {
                model.localScale *= S;
                Recolor(model);
                AddTusks(model);
                AddFur(model);
            }
            var anim = go.GetComponent<CreatureAnimator>();
            if (anim != null) { anim.cadence = 1.7f; }
            go.AddComponent<MammothPerception>();   // milestone 1: sensors
            go.AddComponent<TacticalGraph>();       // milestone 2: waypoint graph + Bayesian belief (observe and log only until milestone 5)
            go.AddComponent<MammothBelief>();
            go.AddComponent<MammothPlanner>();      // milestone 4: A* route planner (self-checks against Dijkstra at start-up)
            go.AddComponent<MammothMemory>();       // milestone 3: sightings, clues, searched places, route heat
            go.AddComponent<MammothAI>();
            go.AddComponent<MammothDecision>();     // milestone 5: utility-based decision engine; MammothAI executes its choice
            go.AddComponent<MammothSelfTest>();     // F9 / F8 / F7 rule checks
            go.AddComponent<MammothDebugView>();    // F11 mind visualiser
            go.SetActive(true);
            return true;
        }

        /// <summary>How many of 16 probe points (rings at 15 m and 30 m) around a spot are walkable, inside the jungle and reachable in a straight NavMesh line.</summary>
        static int Connectivity(Vector3 p)
        {
            int n = 0;
            for (int ring = 1; ring <= 2; ring++)
                for (int i = 0; i < 8; i++)
                {
                    float a = i / 8f * Mathf.PI * 2f, r = ring * 15f;
                    var q = p + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
                    if (NavMesh.SamplePosition(q, out var nh, 5f, NavMesh.AllAreas) && MammothTerritory.Contains(nh.position) && !NavMesh.Raycast(p, nh.position, out _, NavMesh.AllAreas)) n++;
                }
            return n;
        }

        static void Recolor(Transform model)
        {
            var tint = new Color(0.30f, 0.17f, 0.10f);
            foreach (var r in model.GetComponentsInChildren<Renderer>())
                foreach (var m in r.materials)
                {
                    if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.Lerp(m.GetColor("_BaseColor"), tint, 0.75f));
                    else if (m.HasProperty("_Color")) m.color = Color.Lerp(m.color, tint, 0.75f);
                }
        }

        static GameObject Prim(PrimitiveType t, Transform parent, Color c)
        {
            var g = GameObject.CreatePrimitive(t);
            Destroy(g.GetComponent<Collider>());
            g.transform.SetParent(parent, false);
            var r = g.GetComponent<Renderer>();
            if (r.material.HasProperty("_BaseColor")) r.material.SetColor("_BaseColor", c); else r.material.color = c;
            return g;
        }

        static void AddTusks(Transform model)
        {
            var head = model.Find("Head") ?? FindDeep(model, "Head");
            Vector3 hp = head != null ? model.InverseTransformPoint(head.position) : new Vector3(0f, 2.2f, 2.0f);
            var ivory = new Color(0.92f, 0.88f, 0.76f);
            foreach (float side in new[] { -1f, 1f })
            {
                Vector3 prev = Vector3.zero; const int N = 9;
                for (int i = 0; i <= N; i++)
                {
                    float t = i / (float)N;
                    Vector3 p = hp + new Vector3(side * (0.55f + 0.75f * Mathf.Sin(t * 2.2f)), -0.35f - 1.1f * t + 1.9f * t * t, 0.7f + 2.1f * t);
                    if (i > 0)
                    {
                        var seg = Prim(PrimitiveType.Capsule, model, ivory);
                        Vector3 d = p - prev;
                        seg.transform.localPosition = (p + prev) * 0.5f;
                        seg.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d.normalized);
                        float th = Mathf.Lerp(0.34f, 0.07f, t);
                        seg.transform.localScale = new Vector3(th, d.magnitude * 0.62f, th);
                    }
                    prev = p;
                }
            }
        }

        static void AddFur(Transform model)
        {
            var fur = new Color(0.16f, 0.09f, 0.05f);
            var rng = new System.Random(24103715);
            float R() => (float)rng.NextDouble();
            for (int i = 0; i < 26; i++)
            {
                var g = Prim(PrimitiveType.Sphere, model, fur);
                bool back = i < 14;
                float z = Mathf.Lerp(-1.5f, 1.6f, R());
                g.transform.localPosition = back ? new Vector3((R() - 0.5f) * 1.2f, 3.05f + R() * 0.3f, z)
                                                 : new Vector3((i % 2 == 0 ? -1f : 1f) * (0.85f + R() * 0.15f), 1.7f + R() * 0.8f, z);
                float s = 0.5f + R() * 0.55f;
                g.transform.localScale = new Vector3(s, s * (back ? 0.8f : 1.3f), s * 1.4f);
            }
        }

        static Transform FindDeep(Transform t, string n)
        {
            foreach (var c in t.GetComponentsInChildren<Transform>()) if (c.name == n) return c;
            return null;
        }
    }
}
