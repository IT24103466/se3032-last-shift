using System;
using System.Collections.Generic;
using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// Sanjula's Mammoth - tactical route planner: A* over the TacticalGraph.
    ///
    ///   f(n) = g(n) + h(n)
    ///   g(n) = accumulated cost: for every edge  length * (1 + slopeWeight * |dy|/length) + extraNodeCost(next)
    ///   h(n) = straight-line (flat) distance to the goal. Every edge costs at least its flat length, so h never overestimates
    ///          (admissible) and, by the triangle inequality, is consistent: A* returns an optimal route and never re-opens a node.
    ///
    /// extraNodeCost lets the caller add tactical costs (e.g. avoid being seen, avoid places just searched) without changing the algorithm.
    /// Temporarily blocked waypoints (the agent could not get there) are skipped until they expire.
    ///
    /// Relationship with Unity navigation: the planner chooses the ROUTE (a list of waypoints). MammothAI then gives the
    /// NavMeshAgent ONE destination at a time (the next waypoint); the agent does the steering, slopes and local avoidance.
    /// Only one controller moves the mammoth.
    /// Allocation-free after construction: arrays and the binary heap are reused between calls.
    /// </summary>
    [DefaultExecutionOrder(5)]
    public class MammothPlanner : MonoBehaviour
    {
        [Tooltip("Extra cost per unit of slope (rise/run): the mammoth prefers level ground")]
        public float slopeWeight = 2f;
        public bool logSelfCheck = true;

        public TacticalGraph Graph { get; private set; }
        public readonly List<Vector3> Route = new List<Vector3>();
        public readonly List<int> RouteNodes = new List<int>();
        public float RouteCost { get; private set; }
        public int LastExpanded { get; private set; }
        public int PlansMade { get; private set; }
        public bool LastPlanFound { get; private set; }

        public Func<int, float> ExtraNodeCost;   // optional tactical cost for entering a node (>= 0)

        float[] g, blockedUntil; int[] came; byte[] state;   // 0 unseen, 1 open, 2 closed
        int[] heapNode; float[] heapKey; int heapSize;

        void Start()
        {
            Graph = GetComponent<TacticalGraph>();
            if (Graph == null) { enabled = false; return; }
            if (Graph.Ready) Init(); else Graph.Built += Init;
        }

        void OnDestroy() { if (Graph != null) Graph.Built -= Init; }

        void Init()
        {
            int n = Graph.Count;
            g = new float[n]; came = new int[n]; state = new byte[n]; blockedUntil = new float[n];
            heapNode = new int[n * 8 + 16]; heapKey = new float[n * 8 + 16];
            if (logSelfCheck) SelfCheck();
        }

        public bool Ready => g != null;

        /// <summary>Temporarily remove a waypoint from planning (e.g. the NavMeshAgent could not reach it).</summary>
        public void BlockNode(int node, float seconds) { if (blockedUntil != null && node >= 0 && node < blockedUntil.Length) blockedUntil[node] = Time.time + seconds; }

        public bool PlanToPosition(Vector3 from, Vector3 to)
        {
            if (!Ready) return false;
            return Plan(Graph.NearestNode(from), Graph.NearestNode(to));
        }

        /// <summary>A* from node a to node b. On success fills Route / RouteNodes / RouteCost.</summary>
        public bool Plan(int start, int goal)
        {
            Route.Clear(); RouteNodes.Clear(); RouteCost = 0f; LastExpanded = 0; LastPlanFound = false;
            if (!Ready || start < 0 || goal < 0) return false;
            PlansMade++;
            int n = Graph.Count;
            Array.Clear(state, 0, n);
            heapSize = 0;
            g[start] = 0f; came[start] = -1; state[start] = 1;
            Push(start, Heuristic(start, goal));
            float now = Time.time;
            int expanded = 0;

            while (heapSize > 0)
            {
                int cur = Pop(out float key);
                if (state[cur] == 2) continue;                     // stale heap entry
                state[cur] = 2; expanded++;
                if (cur == goal) { Rebuild(goal); LastExpanded = expanded; LastPlanFound = true; return true; }
                var nb = Graph.Neighbours(cur); var nd = Graph.NeighbourDistances(cur);
                Vector3 pc = Graph.Position(cur);
                for (int k = 0; k < nb.Length; k++)
                {
                    int nx = nb[k];
                    if (state[nx] == 2 || (blockedUntil[nx] > now && nx != goal)) continue;
                    float len = Mathf.Max(0.01f, nd[k]);
                    float rise = Mathf.Abs(Graph.Position(nx).y - pc.y);
                    float cost = len * (1f + slopeWeight * rise / len) + (ExtraNodeCost != null ? Mathf.Max(0f, ExtraNodeCost(nx)) : 0f);
                    float ng = g[cur] + cost;
                    if (state[nx] == 0 || ng < g[nx])
                    {
                        g[nx] = ng; came[nx] = cur; state[nx] = 1;
                        Push(nx, ng + Heuristic(nx, goal));
                    }
                }
            }
            LastExpanded = expanded;
            return false;
        }

        float Heuristic(int a, int goal)
        {
            Vector3 p = Graph.Position(a), q = Graph.Position(goal);
            float dx = p.x - q.x, dz = p.z - q.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        void Rebuild(int goal)
        {
            RouteCost = g[goal];
            for (int c = goal; c >= 0; c = came[c]) RouteNodes.Add(c);
            RouteNodes.Reverse();
            for (int i = 0; i < RouteNodes.Count; i++) Route.Add(Graph.Position(RouteNodes[i]));
        }

        // ---- binary min-heap with lazy deletion (duplicates allowed; stale ones are skipped on pop)
        void Push(int node, float key)
        {
            if (heapSize >= heapNode.Length) { Array.Resize(ref heapNode, heapSize * 2); Array.Resize(ref heapKey, heapSize * 2); }
            int i = heapSize++;
            while (i > 0)
            {
                int p = (i - 1) >> 1;
                if (heapKey[p] <= key) break;
                heapNode[i] = heapNode[p]; heapKey[i] = heapKey[p]; i = p;
            }
            heapNode[i] = node; heapKey[i] = key;
        }

        int Pop(out float key)
        {
            int top = heapNode[0]; key = heapKey[0];
            heapSize--;
            if (heapSize > 0)
            {
                int node = heapNode[heapSize]; float k = heapKey[heapSize]; int i = 0;
                while (true)
                {
                    int c = 2 * i + 1; if (c >= heapSize) break;
                    if (c + 1 < heapSize && heapKey[c + 1] < heapKey[c]) c++;
                    if (heapKey[c] >= k) break;
                    heapNode[i] = heapNode[c]; heapKey[i] = heapKey[c]; i = c;
                }
                heapNode[i] = node; heapKey[i] = k;
            }
            return top;
        }

        // ---- one-off validation at start-up: A* must equal Dijkstra (A* with h = 0) on the real graph
        void SelfCheck()
        {
            int far = -1; float best = 0f, hs = Time.realtimeSinceStartup;
            int s = Graph.NearestNode(Graph.Center);
            for (int i = 0; i < Graph.Count; i++)
                if (Graph.Reachable(i)) { float d = Heuristic(s, i); if (d > best) { best = d; far = i; } }
            if (s < 0 || far < 0) return;
            if (!Plan(s, far)) { Debug.LogWarning("[MammothAI] planner self-check: no route home -> farthest waypoint"); return; }
            float astarCost = RouteCost; int astarExp = LastExpanded; int hops = RouteNodes.Count;
            float dijk = DijkstraCost(s, far);
            Debug.Log($"[MammothAI] planner self-check: home->farthest ({best:F0} m straight) A* cost {astarCost:F1} ({hops} waypoints, {astarExp} expanded) vs Dijkstra {dijk:F1} -> {(Mathf.Abs(astarCost - dijk) < 0.01f ? "OPTIMAL" : "MISMATCH")}");
            Route.Clear(); RouteNodes.Clear(); PlansMade = 0;
        }

        float DijkstraCost(int start, int goal)
        {
            int n = Graph.Count; var dist = new float[n]; var done = new bool[n];
            for (int i = 0; i < n; i++) dist[i] = float.MaxValue;
            dist[start] = 0f;
            for (int it = 0; it < n; it++)
            {
                int u = -1; float bd = float.MaxValue;
                for (int i = 0; i < n; i++) if (!done[i] && dist[i] < bd) { bd = dist[i]; u = i; }
                if (u < 0) break; done[u] = true; if (u == goal) return dist[u];
                var nb = Graph.Neighbours(u); var nd = Graph.NeighbourDistances(u);
                for (int k = 0; k < nb.Length; k++)
                {
                    float len = Mathf.Max(0.01f, nd[k]);
                    float rise = Mathf.Abs(Graph.Position(nb[k]).y - Graph.Position(u).y);
                    float c = len * (1f + slopeWeight * rise / len);
                    if (dist[u] + c < dist[nb[k]]) dist[nb[k]] = dist[u] + c;
                }
            }
            return float.MaxValue;
        }
    }
}
