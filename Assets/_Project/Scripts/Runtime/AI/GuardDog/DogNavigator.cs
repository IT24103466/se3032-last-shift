using System.Collections.Generic;
using System.Diagnostics;
using LastShift.AI.Core;
using UnityEngine;
using UnityEngine.AI;

namespace LastShift.GuardDog
{
    /// <summary>
    /// Global path planning for the dog: weighted A* on the scent grid with a cost model that
    /// changes with what the dog is doing. Unity's NavMeshAgent is only used underneath for the
    /// last few metres of local steering (slopes, avoiding other characters) — it can't express
    /// "prefer the scent trail" or "keep out of the player's line of fire", which are the point.
    ///
    /// Cost multiplier m(cell) per mode (edge cost = step length × m):
    ///   Walk  : 1                                               (patrol, going home)
    ///   Track : 1 − 0.6·min(1, scent)  (min 0.4)                (stay ON the trail, like a tracker dog,
    ///                                                            instead of cutting corners)
    ///   Chase : 1, inflated heuristic ε = 1.5                   (fast re-plans while running)
    ///   Flank : 1 + 4·inAimCone(cell)                           (approach outside the gun's cone)
    /// Water (the stream) costs +∞ in every mode: Kalu never goes into the water. If the player is
    /// across the stream, A* returns a partial path and the dog waits on the bank, barking.
    /// Cells where the dog recently got stuck (a closed door) cost +∞ for a while.
    /// </summary>
    public sealed class DogNavigator
    {
        public enum Mode { Walk, Track, Chase, Flank }

        const int MaxExpand = 9000;

        readonly ScentTracker world;
        readonly GridMap map;
        readonly GridAStar astar;
        readonly float[] blockedUntil;
        readonly List<int> path = new List<int>();
        readonly Stopwatch sw = new Stopwatch();
        readonly PlayerController player;

        int index, goalCell = -1;
        Mode mode;
        float plannedAt = -99f, now;
        bool forceNext;

        public GridAStar.Result LastResult { get; private set; }
        public float LastPlanMs { get; private set; }
        public int PlansMade { get; private set; }
        public Mode CurrentMode => mode;
        public IReadOnlyList<int> Path => path;
        public int PathIndex => index;
        public GridAStar Search => astar;
        public bool HasPath => path.Count > 0 && index < path.Count;
        public bool GoalUnreachable => LastResult.Partial;

        public DogNavigator(ScentTracker world, PlayerController player)
        {
            this.world = world; this.player = player;
            map = world.Grid;
            astar = new GridAStar(map);
            blockedUntil = new float[map.Count];
        }

        float Cost(int c)
        {
            if (blockedUntil[c] > now) return float.PositiveInfinity;
            if (map.Has(c, GridMap.CellFlags.Water)) return float.PositiveInfinity;   // never into the stream
            switch (mode)
            {
                case Mode.Track:
                    return 1f - 0.6f * Mathf.Min(1f, world.Scent.Strength(c, now));
                case Mode.Flank:
                    return 1f + 4f * InAimCone(c);
                default:
                    return 1f;
            }
        }

        static float MinMultiplier(Mode m) => m == Mode.Track ? 0.4f : 1f;
        static float Epsilon(Mode m) => m == Mode.Chase ? 1.5f : 1f;
        static float ReplanEvery(Mode m) => m == Mode.Chase || m == Mode.Flank ? 0.4f : 1.5f;

        /// <summary>1 inside the player's aim cone (±20°, 35 m), fading out to 0 at ±35°.</summary>
        float InAimCone(int c)
        {
            if (player == null || player.eyes == null) return 0f;
            Vector3 to = world.WorldOf(c) - player.transform.position; to.y = 0f;
            float d = to.magnitude;
            if (d < 1.5f || d > 35f) return 0f;
            Vector3 f = player.eyes.forward; f.y = 0f;
            float ang = Vector3.Angle(f, to);
            return Mathf.Clamp01((35f - ang) / 15f);
        }

        /// <summary>
        /// Make sure there is a path to <paramref name="goal"/> in this mode. Re-plans only when
        /// it has to (new mode, goal moved to another cell, path used up, or the periodic refresh
        /// for moving targets), so a 10 Hz brain doesn't run A* ten times a second.
        /// </summary>
        public void PlanTo(Vector3 from, Vector3 goal, Mode m, float time, bool force = false)
        {
            now = time;
            int g = map.NearestWalkable(world.CellOf(goal));
            if (g < 0) return;
            bool stale = time - plannedAt > ReplanEvery(m);
            if (!force && !forceNext && m == mode && HasPath && (g == goalCell || !stale)) return;
            forceNext = false;

            int s = map.NearestWalkable(world.CellOf(from));
            mode = m; goalCell = g; plannedAt = time;
            sw.Restart();
            LastResult = astar.FindPath(s, g, Cost, MinMultiplier(m), Epsilon(m), MaxExpand, path);
            sw.Stop();
            LastPlanMs = (float)sw.Elapsed.TotalMilliseconds;
            PlansMade++;
            index = path.Count > 1 ? 1 : 0;
        }

        public void Clear() { path.Clear(); index = 0; goalCell = -1; }

        /// <summary>
        /// Where to steer now: advances past reached cells, then looks ahead and picks the
        /// farthest path cell (up to 8) in a straight, NavMesh-clear line ("string pulling"),
        /// so the dog runs smooth lines instead of grid zigzags.
        /// </summary>
        public bool Steer(Vector3 pos, out Vector3 target)
        {
            target = pos;
            if (!HasPath) return false;
            while (index < path.Count - 1 && Flat(world.WorldOf(path[index]), pos) < 1.1f) index++;
            int pick = index;
            for (int k = Mathf.Min(path.Count - 1, index + 8); k > index; k--)
                if (!NavMesh.Raycast(pos, world.WorldOf(path[k]), out _, NavMesh.AllAreas) && !CrossesWater(pos, world.WorldOf(path[k]))) { pick = k; break; }
            target = world.WorldOf(path[pick]);
            return true;
        }

        /// <summary>Would a straight run from a to b cut through the stream? (then don't take the shortcut)</summary>
        bool CrossesWater(Vector3 a, Vector3 b)
        {
            float len = Flat(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 0.5f));
            for (int i = 1; i <= steps; i++)
            {
                int c = world.CellOf(Vector3.Lerp(a, b, i / (float)steps));
                if (c >= 0 && map.Has(c, GridMap.CellFlags.Water)) return true;
            }
            return false;
        }

        public bool Arrived(Vector3 pos, float tolerance)
            => !HasPath || (index >= path.Count - 1 && Flat(world.WorldOf(path[path.Count - 1]), pos) < tolerance);

        /// <summary>
        /// Got stuck going this way (closed door, crowd): avoid the next stretch of the path for a
        /// while and re-plan around it. Cells within 2 m of the dog are never blocked, or it
        /// could wall itself in.
        /// </summary>
        public void MarkBlockedAhead(Vector3 dogPos, float seconds, float time)
        {
            int marked = 0;
            for (int k = index; k < path.Count && marked < 3; k++)
            {
                int c = path[k];
                if (Flat(world.WorldOf(c), dogPos) < 2f) continue;
                blockedUntil[c] = time + seconds;
                marked++;
            }
            forceNext = true;
        }

        static float Flat(Vector3 a, Vector3 b) { a.y = b.y = 0f; return Vector3.Distance(a, b); }
    }
}
