using System;
using System.Collections.Generic;

namespace LastShift.AI.Core
{
    /// <summary>
    /// Weighted A* on a <see cref="GridMap"/> with 8-way moves and per-cell dynamic costs.
    ///
    /// Edge cost a→b = |ab| × m(b), where m(b) ≥ <c>minMultiplier</c> is supplied by the caller
    /// (so the same search serves several "moods": follow the scent trail, avoid the player's
    /// line of fire, avoid recently blocked cells...). Returning +∞ blocks a cell.
    ///
    /// Heuristic h(n) = octile(n, goal) × minMultiplier. Octile distance is the exact shortest
    /// path length on an empty 8-connected grid, and every real step costs at least its length ×
    /// minMultiplier, so h never overestimates (admissible) and satisfies h(a) ≤ c(a,b) + h(b)
    /// (consistent): with epsilon = 1 the first time the goal is popped its path is optimal.
    /// epsilon &gt; 1 inflates h (f = g + ε·h): fewer nodes expanded, path cost ≤ ε × optimal.
    ///
    /// Bookkeeping uses "search stamps" so nothing is cleared between searches (O(1) reset),
    /// and the open list is a binary heap with lazy deletion (stale entries are skipped).
    /// If the goal can't be reached (or the expansion budget runs out) it returns a partial
    /// path to the closest node it found, so the agent still moves sensibly.
    /// </summary>
    public sealed class GridAStar
    {
        public delegate float CostFn(int cell);

        public struct Result
        {
            public bool Found;       // reached the goal exactly
            public bool Partial;     // path ends at the closest reachable cell instead
            public int Expanded;     // nodes taken off the open list
            public float Cost;       // g of the end cell
        }

        const float Sqrt2 = 1.41421356f;
        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        readonly GridMap map;
        readonly float[] g;
        readonly int[] parent;
        readonly int[] openStamp, closedStamp;
        readonly MinHeap heap = new MinHeap(1024);
        int stamp;

        public GridAStar(GridMap map)
        {
            this.map = map;
            int n = map.Count;
            g = new float[n]; parent = new int[n]; openStamp = new int[n]; closedStamp = new int[n];
        }

        /// <summary>For the debug view: was this cell expanded by the most recent search?</summary>
        public bool WasExpanded(int cell) => closedStamp[cell] == stamp;

        public float Octile(int a, int b)
        {
            int dx = Math.Abs(map.X(a) - map.X(b)), dz = Math.Abs(map.Z(a) - map.Z(b));
            int lo = Math.Min(dx, dz), hi = Math.Max(dx, dz);
            return (hi - lo + lo * Sqrt2) * map.CellSize;
        }

        public Result FindPath(int start, int goal, CostFn cost, float minMultiplier, float epsilon, int maxExpand, List<int> outPath)
        {
            outPath.Clear();
            var res = new Result();
            if (start < 0 || goal < 0) return res;
            if (++stamp == int.MaxValue) { Array.Clear(openStamp, 0, openStamp.Length); Array.Clear(closedStamp, 0, closedStamp.Length); stamp = 1; }
            heap.Clear();

            float hw = minMultiplier * Math.Max(1f, epsilon);
            g[start] = 0f; parent[start] = -1; openStamp[start] = stamp;
            heap.Push(start, Octile(start, goal) * hw);
            int best = start; float bestH = Octile(start, goal);

            while (heap.Count > 0)
            {
                int cur = heap.Pop();
                if (closedStamp[cur] == stamp) continue;            // stale duplicate
                closedStamp[cur] = stamp;
                res.Expanded++;

                float h = Octile(cur, goal);
                if (h < bestH) { bestH = h; best = cur; }
                if (cur == goal) { res.Found = true; best = goal; break; }
                if (res.Expanded >= maxExpand) break;

                int cx = map.X(cur), cz = map.Z(cur);
                for (int i = 0; i < 8; i++)
                {
                    int nx = cx + DX[i], nz = cz + DZ[i];
                    if (!map.InBounds(nx, nz)) continue;
                    int nb = map.Index(nx, nz);
                    if (!map.Walkable[nb] || closedStamp[nb] == stamp) continue;
                    bool diagonal = i >= 4;
                    // No corner cutting: a diagonal step needs both orthogonal neighbours open
                    if (diagonal && (!map.Walkable[map.Index(cx + DX[i], cz)] || !map.Walkable[map.Index(cx, cz + DZ[i])])) continue;
                    float m = cost(nb);
                    if (float.IsInfinity(m) || float.IsNaN(m)) continue;
                    if (m < minMultiplier) m = minMultiplier;           // keep the heuristic admissible
                    float ng = g[cur] + (diagonal ? Sqrt2 : 1f) * map.CellSize * m;
                    if (openStamp[nb] == stamp && ng >= g[nb]) continue;
                    openStamp[nb] = stamp; g[nb] = ng; parent[nb] = cur;
                    heap.Push(nb, ng + Octile(nb, goal) * hw);
                }
            }

            if (!res.Found) res.Partial = best != start;
            res.Cost = g[best];
            for (int c = best; c != -1; c = parent[c]) outPath.Add(c);
            outPath.Reverse();
            return res;
        }

        /// <summary>Binary min-heap of (cell, priority). Duplicates are allowed; A* skips closed ones.</summary>
        sealed class MinHeap
        {
            int[] items; float[] keys;
            public int Count { get; private set; }

            public MinHeap(int capacity) { items = new int[capacity]; keys = new float[capacity]; }

            public void Clear() => Count = 0;

            public void Push(int item, float key)
            {
                if (Count == items.Length) { Array.Resize(ref items, Count * 2); Array.Resize(ref keys, Count * 2); }
                int i = Count++;
                while (i > 0)
                {
                    int p = (i - 1) >> 1;
                    if (keys[p] <= key) break;
                    items[i] = items[p]; keys[i] = keys[p]; i = p;
                }
                items[i] = item; keys[i] = key;
            }

            public int Pop()
            {
                int top = items[0];
                int last = items[--Count]; float lastKey = keys[Count];
                int i = 0;
                while (true)
                {
                    int c = 2 * i + 1;
                    if (c >= Count) break;
                    if (c + 1 < Count && keys[c + 1] < keys[c]) c++;
                    if (keys[c] >= lastKey) break;
                    items[i] = items[c]; keys[i] = keys[c]; i = c;
                }
                items[i] = last; keys[i] = lastKey;
                return top;
            }
        }
    }
}
