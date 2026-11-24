using UnityEngine;

namespace LastShift
{
    /// <summary>
    /// The mammoth's roaming territory: the jungle (an axis-aligned region of the map) MINUS a circular exclusion zone around the
    /// player's camp. Everything that moves the mammoth (wandering, investigating, searching, tracking, pursuing, charging, the A*
    /// waypoint graph) goes through <see cref="Contains"/> / <see cref="Clamp"/>, so it can be seen roaming the jungle but never
    /// walks into the camp. Static + tiny on purpose: one shared definition, no per-mammoth duplication.
    /// </summary>
    public static class MammothTerritory
    {
        /// <summary>Jungle region in world X/Z (min corner, max corner). Widened at start-up to include the mammoth's home.</summary>
        public static Vector2 Min = new Vector2(-12f, -52f), Max = new Vector2(90f, -2f);
        public static Vector3 Camp;
        public static float CampRadius = 30f;
        /// <summary>Clamp keeps a margin outside the camp circle so the NavMeshAgent (radius 1.7) never clips into it.</summary>
        public const float Margin = 3f;
        public static bool Ready { get; private set; }

        public static void Init(Vector3 camp, Vector3 home)
        {
            Camp = camp;
            Min = new Vector2(Mathf.Min(-12f, home.x - 10f), Mathf.Min(-52f, home.z - 10f));
            Max = new Vector2(Mathf.Max(90f, home.x + 10f), Mathf.Max(-2f, home.z + 10f));
            Ready = true;
        }

        public static void Reset() { Ready = false; }

        static float CampDist(Vector3 p) { float dx = p.x - Camp.x, dz = p.z - Camp.z; return Mathf.Sqrt(dx * dx + dz * dz); }

        public static bool InCamp(Vector3 p) => Ready && CampDist(p) < CampRadius;

        /// <summary>True when the point is inside the jungle region and outside the camp exclusion zone.</summary>
        public static bool Contains(Vector3 p)
        {
            if (!Ready) return true;
            return p.x >= Min.x && p.x <= Max.x && p.z >= Min.y && p.z <= Max.y && CampDist(p) >= CampRadius;
        }

        /// <summary>Nearest legal point (keeps Y): clamp into the region, then push radially out of the camp circle.</summary>
        public static Vector3 Clamp(Vector3 p)
        {
            if (!Ready) return p;
            Vector3 orig = p;
            for (int it = 0; it < 3; it++)
            {
                p.x = Mathf.Clamp(p.x, Min.x, Max.x); p.z = Mathf.Clamp(p.z, Min.y, Max.y);
                float dx = p.x - Camp.x, dz = p.z - Camp.z, d = Mathf.Sqrt(dx * dx + dz * dz), r = CampRadius + Margin;
                if (d >= r) return p;
                if (d < 0.01f) { dx = 0f; dz = 1f; d = 1f; }
                p.x = Camp.x + dx / d * r; p.z = Camp.z + dz / d * r;
            }
            if (Contains(p)) return p;
            // the camp circle crosses the region border: take the legal point on the exclusion ring that is nearest to where we wanted to go
            float best = float.MaxValue; Vector3 bp = p;
            for (int a = 0; a < 72; a++)
            {
                float ang = a * 5f * Mathf.Deg2Rad, rr = CampRadius + Margin;
                var q = new Vector3(Camp.x + Mathf.Cos(ang) * rr, orig.y, Camp.z + Mathf.Sin(ang) * rr);
                if (!Contains(q)) continue;
                float dd = (q.x - orig.x) * (q.x - orig.x) + (q.z - orig.z) * (q.z - orig.z);
                if (dd < best) { best = dd; bp = q; }
            }
            return bp;
        }
    }
}
