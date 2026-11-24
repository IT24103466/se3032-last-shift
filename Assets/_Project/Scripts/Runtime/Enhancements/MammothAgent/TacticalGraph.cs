using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace LastShift
{
    /// <summary>
    /// Sanjula's Mammoth - tactical waypoint graph. A coarse set of ~100 places on the NavMesh around the mammoth's territory,
    /// linked where the NavMesh gives a straight walkable line. It is the state space for the belief (WHERE is the player?)
    /// and the search space for the A* planner. It does NOT replace Unity's navigation: the NavMeshAgent still does the walking
    /// (and local obstacle avoidance); this graph only decides which place to walk to next.
    /// Built once, spread over several frames so there is no hitch.
    /// </summary>
    public class TacticalGraph : MonoBehaviour
    {
        [Header("Coverage")]
        public float radius = 70f;
        public float spacing = 10f;
        public float maxLinkDistance = 24f;
        public float sampleRange = 4f;
        public int maxNodes = 200;
        [Header("Build speed")]
        public int candidatesPerFrame = 16;
        public int linkNodesPerFrame = 20;

        public bool Ready { get; private set; }
        public Vector3 Center { get; private set; }
        public int Count => pos.Length;
        public event Action Built;

        Vector3[] pos = new Vector3[0];
        bool[] reachable = new bool[0];
        int[][] nbr; float[][] nbrDist; Vector3[][] nbrDir;

        public Vector3 Position(int i) => pos[i];
        public bool Reachable(int i) => reachable[i];
        public int[] Neighbours(int i) => nbr[i];
        public float[] NeighbourDistances(int i) => nbrDist[i];
        public Vector3[] NeighbourDirections(int i) => nbrDir[i];
        public int ReachableCount { get; private set; }

        void Start()
        {
            Center = transform.position;
            StartCoroutine(Build());
        }

        IEnumerator Build()
        {
            var nodes = new List<Vector3>();
            int tested = 0;
            for (float x = -radius; x <= radius && nodes.Count < maxNodes; x += spacing)
                for (float z = -radius; z <= radius && nodes.Count < maxNodes; z += spacing)
                {
                    if (x * x + z * z > radius * radius) continue;
                    var c = Center + new Vector3(x, 0f, z);
                    if (Physics.Raycast(new Vector3(c.x, Center.y + 200f, c.z), Vector3.down, out var h, 500f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                        && NavMesh.SamplePosition(h.point, out var nh, sampleRange, NavMesh.AllAreas))
                    {
                        var p = nh.position;
                        if (!MammothTerritory.Contains(p)) continue;      // the camp / outside the jungle is not part of the mammoth's world
                        if (Mathf.Abs(p.x - c.x) < sampleRange * 1.5f && Mathf.Abs(p.z - c.z) < sampleRange * 1.5f) nodes.Add(p);
                    }
                    if (++tested % candidatesPerFrame == 0) yield return null;
                }

            int n = nodes.Count;
            if (n == 0) { Debug.LogWarning("[MammothAI] tactical graph: no NavMesh around the mammoth"); yield break; }
            var adj = new List<int>[n]; for (int i = 0; i < n; i++) adj[i] = new List<int>();
            float maxSqr = maxLinkDistance * maxLinkDistance;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if ((nodes[i] - nodes[j]).sqrMagnitude > maxSqr) continue;
                    if (NavMesh.Raycast(nodes[i], nodes[j], out _, NavMesh.AllAreas)) continue;   // true = blocked
                    adj[i].Add(j); adj[j].Add(i);
                }
                if (i % linkNodesPerFrame == 0) yield return null;
            }

            // keep only what the mammoth can actually walk to: flood-fill from the node nearest to home
            int start = 0; float best = float.MaxValue;
            for (int i = 0; i < n; i++) { float d = (nodes[i] - Center).sqrMagnitude; if (d < best) { best = d; start = i; } }
            var seen = new bool[n]; var stack = new Stack<int>(); stack.Push(start); seen[start] = true;
            while (stack.Count > 0) { int a = stack.Pop(); foreach (int b in adj[a]) if (!seen[b]) { seen[b] = true; stack.Push(b); } }

            pos = nodes.ToArray(); reachable = seen;
            nbr = new int[n][]; nbrDist = new float[n][]; nbrDir = new Vector3[n][];
            int rc = 0;
            for (int i = 0; i < n; i++)
            {
                if (!seen[i]) { nbr[i] = new int[0]; nbrDist[i] = new float[0]; nbrDir[i] = new Vector3[0]; continue; }
                rc++;
                int k = adj[i].Count;
                nbr[i] = adj[i].ToArray(); nbrDist[i] = new float[k]; nbrDir[i] = new Vector3[k];
                for (int m = 0; m < k; m++)
                {
                    Vector3 d = nodes[nbr[i][m]] - nodes[i]; d.y = 0f;
                    nbrDist[i][m] = d.magnitude; nbrDir[i][m] = nbrDist[i][m] > 0.01f ? d / nbrDist[i][m] : Vector3.zero;
                }
            }
            ReachableCount = rc;
            Ready = true;
            Debug.Log($"[MammothAI] tactical graph ready: {rc} reachable waypoints of {n} candidates");
            Built?.Invoke();
        }

        /// <summary>Closest reachable waypoint to a world point (flat distance). -1 if the graph is not ready.</summary>
        public int NearestNode(Vector3 p)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < pos.Length; i++)
            {
                if (!reachable[i]) continue;
                float dx = pos[i].x - p.x, dz = pos[i].z - p.z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }
    }
}
