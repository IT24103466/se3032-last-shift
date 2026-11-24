using System;

namespace LastShift.AI.Core
{
    /// <summary>
    /// A 2D grid laid over the level (X/Z plane), one cell per <see cref="CellSize"/> metres.
    /// It is the shared "world model" of the guard dog's AI: which cells are walkable, how high
    /// the ground is, which cells are water (washes scent, slow to cross) and which are under a
    /// roof or canopy (scent lasts longer out of the rain).
    ///
    /// Pure C# (no engine types) so the AI can be reasoned about and tested on its own.
    /// Cells are addressed by a single int index = z * Width + x.
    /// </summary>
    public sealed class GridMap
    {
        [Flags]
        public enum CellFlags : byte { None = 0, Water = 1, Covered = 2 }

        public readonly int Width, Height;
        public readonly float OriginX, OriginZ, CellSize;
        public readonly bool[] Walkable;
        public readonly float[] GroundY;
        public readonly CellFlags[] Flags;

        public int Count => Width * Height;

        public GridMap(int width, int height, float originX, float originZ, float cellSize)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("Grid must have a positive size");
            Width = width; Height = height; OriginX = originX; OriginZ = originZ; CellSize = cellSize;
            Walkable = new bool[width * height];
            GroundY = new float[width * height];
            Flags = new CellFlags[width * height];
        }

        public int Index(int x, int z) => z * Width + x;
        public int X(int cell) => cell % Width;
        public int Z(int cell) => cell / Width;
        public bool InBounds(int x, int z) => x >= 0 && z >= 0 && x < Width && z < Height;

        /// <summary>The cell containing world point (x, z), or -1 when outside the grid.</summary>
        public int CellAt(float worldX, float worldZ)
        {
            int x = (int)Math.Floor((worldX - OriginX) / CellSize), z = (int)Math.Floor((worldZ - OriginZ) / CellSize);
            return InBounds(x, z) ? Index(x, z) : -1;
        }

        public float CentreX(int cell) => OriginX + (X(cell) + 0.5f) * CellSize;
        public float CentreZ(int cell) => OriginZ + (Z(cell) + 0.5f) * CellSize;

        public bool Has(int cell, CellFlags f) => (Flags[cell] & f) != 0;

        /// <summary>Straight-line distance between two cells in metres.</summary>
        public float Distance(int a, int b)
        {
            float dx = (X(a) - X(b)) * CellSize, dz = (Z(a) - Z(b)) * CellSize;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Nearest walkable cell to <paramref name="cell"/> within <paramref name="maxRing"/> rings (or -1).</summary>
        public int NearestWalkable(int cell, int maxRing = 6)
        {
            if (cell < 0) return -1;
            if (Walkable[cell]) return cell;
            int cx = X(cell), cz = Z(cell);
            for (int r = 1; r <= maxRing; r++)
            {
                int best = -1; float bestD = float.MaxValue;
                for (int dz = -r; dz <= r; dz++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dz)) != r) continue;   // ring only
                        int x = cx + dx, z = cz + dz;
                        if (!InBounds(x, z) || !Walkable[Index(x, z)]) continue;
                        float d = dx * dx + dz * dz;
                        if (d < bestD) { bestD = d; best = Index(x, z); }
                    }
                if (best >= 0) return best;
            }
            return -1;
        }
    }
}
