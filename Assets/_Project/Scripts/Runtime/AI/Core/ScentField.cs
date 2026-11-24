using System;

namespace LastShift.AI.Core
{
    /// <summary>
    /// The player's scent trail as a field over the grid.
    ///
    /// Each cell stores the strength left by the most recent deposit and the time of that
    /// deposit. Strength decays exponentially, s(t) = s0 · 2^(−(t − t0) / halfLife), evaluated
    /// lazily when a cell is read, so a 15 000-cell map costs nothing per frame.
    /// The half-life depends on the cell: out in the monsoon rain scent washes away quickly,
    /// under a roof or the jungle canopy it lingers; standing water holds no scent at all.
    ///
    /// The deposit TIME is what makes tracking work: following cells whose deposit time is
    /// strictly newer than the cell you're on always leads forwards along the trail (never back
    /// to where the player was earlier), however the trail loops or crosses itself.
    /// </summary>
    public sealed class ScentField
    {
        public float HalfLifeOpen = 45f, HalfLifeCovered = 110f, Cap = 1.5f;

        readonly GridMap map;
        readonly float[] strength, time;
        static readonly float Ln2 = (float)Math.Log(2.0);

        public ScentField(GridMap map)
        {
            this.map = map;
            strength = new float[map.Count];
            time = new float[map.Count];
            for (int i = 0; i < time.Length; i++) time[i] = float.NegativeInfinity;
        }

        public GridMap Map => map;

        float HalfLife(int cell) => map.Has(cell, GridMap.CellFlags.Covered) ? HalfLifeCovered : HalfLifeOpen;

        /// <summary>Strength in the cell now (0 when nothing was ever left there).</summary>
        public float Strength(int cell, float now)
        {
            if (cell < 0 || float.IsNegativeInfinity(time[cell])) return 0f;
            return strength[cell] * (float)Math.Exp(-Ln2 * (now - time[cell]) / HalfLife(cell));
        }

        /// <summary>When the cell was last walked over (−∞ if never).</summary>
        public float DepositTime(int cell) => cell < 0 ? float.NegativeInfinity : time[cell];

        /// <summary>
        /// Leave scent around a cell: full amount in the cell, less in its neighbours (a small
        /// Gaussian-like kernel, so the trail is a few cells wide and can't be skipped diagonally).
        /// Water cells take nothing.
        /// </summary>
        public void Deposit(int cell, float amount, float now)
        {
            if (cell < 0) return;
            int cx = map.X(cell), cz = map.Z(cell);
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (!map.InBounds(x, z)) continue;
                    int c = map.Index(x, z);
                    if (map.Has(c, GridMap.CellFlags.Water)) continue;
                    float w = dx == 0 && dz == 0 ? 1f : (dx == 0 || dz == 0 ? 0.55f : 0.35f);
                    strength[c] = Math.Min(Cap, Strength(c, now) + amount * w);
                    time[c] = now;
                }
        }

        public struct Hit { public int Cell; public float Strength, Time; public bool Valid => Cell >= 0; }

        /// <summary>
        /// The most recently laid walkable cell within <paramref name="radius"/> metres whose strength is
        /// above <paramref name="threshold"/> and whose deposit time is newer than <paramref name="newerThan"/>.
        /// That is where the trail continues.
        /// </summary>
        public Hit FreshestNear(int centre, float radius, float threshold, float newerThan, float now)
        {
            var best = new Hit { Cell = -1, Time = float.NegativeInfinity };
            if (centre < 0) return best;
            int r = (int)Math.Ceiling(radius / map.CellSize), cx = map.X(centre), cz = map.Z(centre);
            float r2 = radius * radius;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (!map.InBounds(x, z)) continue;
                    float d2 = (dx * dx + dz * dz) * map.CellSize * map.CellSize;
                    if (d2 > r2) continue;
                    int c = map.Index(x, z);
                    if (!map.Walkable[c]) continue;   // only places an animal can actually stand on
                    float t = time[c];
                    if (t <= newerThan || t <= best.Time) continue;
                    float s = Strength(c, now);
                    if (s < threshold) continue;
                    best = new Hit { Cell = c, Strength = s, Time = t };
                }
            return best;
        }

        /// <summary>Forget everything (new game).</summary>
        public void Clear()
        {
            Array.Clear(strength, 0, strength.Length);
            for (int i = 0; i < time.Length; i++) time[i] = float.NegativeInfinity;
        }
    }
}
