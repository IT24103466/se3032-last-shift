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
            new Vector2(24,-24), new Vector2(14,-22), new Vector2(34,-26), new Vector2(6,-28)
        };

        void Start()
        {
            EnemyAI src = null;
            foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsInactive.Include))
                if (e.kind == EnemyKind.Elephant) { src = e; break; }
            if (src == null) { Debug.LogWarning("[Sanjula] No Elephant to clone - mammoth skipped."); return; }

            Vector3 home = Vector3.zero; bool ok = false;
            foreach (var c in Candidates)
            {
                if (Physics.Raycast(new Vector3(c.x, 300f, c.y), Vector3.down, out var h, 600f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                    && NavMesh.SamplePosition(h.point, out var nh, 6f, NavMesh.AllAreas)
                    && Vector3.Distance(nh.position, src.transform.position) > 12f)
                { home = nh.position; ok = true; break; }
            }
            if (!ok) { Debug.LogWarning("[Sanjula] No NavMesh spot for the mammoth."); return; }

            bool wasActive = src.gameObject.activeSelf;
            src.gameObject.SetActive(false);
            var go = Instantiate(src.gameObject);
            src.gameObject.SetActive(wasActive);
            go.name = "Sanjulas Mammoth";
            DestroyImmediate(go.GetComponent<EnemyAI>());
            go.transform.position = home;

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
            go.SetActive(true);
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
