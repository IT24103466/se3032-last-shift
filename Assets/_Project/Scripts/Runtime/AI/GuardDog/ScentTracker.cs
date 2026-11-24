using System.Diagnostics;
using LastShift.AI.Core;
using UnityEngine;
using UnityEngine.AI;
using Debug = UnityEngine.Debug;

namespace LastShift.GuardDog
{
    /// <summary>
    /// The world model shared by the guard dog's AI:
    ///  - a navigation grid made from the baked NavMesh (walkable cells, ground height),
    ///    plus which cells are water (the jungle stream) and which are under cover;
    ///  - the player's scent trail (<see cref="ScentField"/>), topped up four times a second
    ///    from where the player is and what he's doing.
    /// One per level, created by <see cref="GuardDogSpawner"/>.
    /// </summary>
    public class ScentTracker : MonoBehaviour
    {
        // Grid covers the jungle, the tea estate and the factory premises (see CLAUDE.md level plan)
        public const float MinX = -22f, MinZ = -66f, MaxX = 82f, MaxZ = 82f, Cell = 1f;
        const float DepositInterval = 0.25f;

        public static ScentTracker Instance { get; private set; }
        public GridMap Grid { get; private set; }
        public ScentField Scent { get; private set; }

        PlayerController player;
        float nextDeposit;

        public void Init(PlayerController p)
        {
            Instance = this;
            player = p;
            var sw = Stopwatch.StartNew();
            Grid = BuildGrid();
            Scent = new ScentField(Grid);
            int walk = 0, water = 0, cov = 0;
            for (int i = 0; i < Grid.Count; i++)
            {
                if (Grid.Walkable[i]) walk++;
                if (Grid.Has(i, GridMap.CellFlags.Water)) water++;
                if (Grid.Has(i, GridMap.CellFlags.Covered)) cov++;
            }
            Debug.Log($"[GuardDog] Grid {Grid.Width}x{Grid.Height} built in {sw.ElapsedMilliseconds} ms: {walk} walkable, {water} water, {cov} covered cells.");
        }

        void OnDestroy() { if (Instance == this) Instance = null; }

        void Update()
        {
            if (player == null || Scent == null || Time.time < nextDeposit) return;
            nextDeposit = Time.time + DepositInterval;
            if (player.Health != null && player.Health.Dead) return;

            // How much scent a step leaves: running sweats and stamps, creeping leaves little.
            // Standing still (or hiding) still pools scent where he is.
            float speed = player.Body != null ? new Vector3(player.Body.velocity.x, 0f, player.Body.velocity.z).magnitude : 0f;
            float amount = player.InLocker ? 0.5f
                         : speed < 0.3f ? 0.3f
                         : player.Sprinting ? 0.9f
                         : player.Crouched ? 0.35f : 0.6f;
            Scent.Deposit(CellOf(player.transform.position), amount, Time.time);
        }

        public int CellOf(Vector3 p) => Grid.CellAt(p.x, p.z);

        public Vector3 WorldOf(int cell) => new Vector3(Grid.CentreX(cell), Grid.GroundY[cell], Grid.CentreZ(cell));

        /// <summary>The jungle stream's centre line (same curve the level builder carved).</summary>
        public static float StreamZ(float x) => -34f + 5f * Mathf.Sin(0.07f * x + 1f);

        // ------------------------------------------------------------------ grid from the NavMesh

        /// <summary>
        /// Rasterises the NavMesh triangles onto the grid: a cell is walkable if a triangle covers
        /// its centre; where floors overlap (factory loft over the rolling floor) the lowest one is
        /// kept, because the dog can't climb ladders. Then marks water and cover.
        /// </summary>
        static GridMap BuildGrid()
        {
            int w = Mathf.CeilToInt((MaxX - MinX) / Cell), h = Mathf.CeilToInt((MaxZ - MinZ) / Cell);
            var map = new GridMap(w, h, MinX, MinZ, Cell);
            for (int i = 0; i < map.Count; i++) map.GroundY[i] = float.PositiveInfinity;

            var tri = NavMesh.CalculateTriangulation();
            var v = tri.vertices; var idx = tri.indices;
            for (int t = 0; t + 2 < idx.Length; t += 3)
            {
                Vector3 a = v[idx[t]], b = v[idx[t + 1]], c = v[idx[t + 2]];
                int x0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.x, b.x, c.x) - MinX) / Cell));
                int x1 = Mathf.Min(w - 1, Mathf.FloorToInt((Mathf.Max(a.x, b.x, c.x) - MinX) / Cell));
                int z0 = Mathf.Max(0, Mathf.FloorToInt((Mathf.Min(a.z, b.z, c.z) - MinZ) / Cell));
                int z1 = Mathf.Min(h - 1, Mathf.FloorToInt((Mathf.Max(a.z, b.z, c.z) - MinZ) / Cell));
                float den = (b.z - c.z) * (a.x - c.x) + (c.x - b.x) * (a.z - c.z);
                if (Mathf.Abs(den) < 1e-6f) continue;   // degenerate (vertical) triangle
                for (int z = z0; z <= z1; z++)
                    for (int x = x0; x <= x1; x++)
                    {
                        float px = MinX + (x + 0.5f) * Cell, pz = MinZ + (z + 0.5f) * Cell;
                        float l1 = ((b.z - c.z) * (px - c.x) + (c.x - b.x) * (pz - c.z)) / den;
                        float l2 = ((c.z - a.z) * (px - c.x) + (a.x - c.x) * (pz - c.z)) / den;
                        float l3 = 1f - l1 - l2;
                        if (l1 < -0.02f || l2 < -0.02f || l3 < -0.02f) continue;
                        float y = l1 * a.y + l2 * b.y + l3 * c.y;
                        int cell = map.Index(x, z);
                        if (y < map.GroundY[cell]) { map.GroundY[cell] = y; map.Walkable[cell] = true; }
                    }
            }

            for (int i = 0; i < map.Count; i++)
            {
                if (!map.Walkable[i]) { map.GroundY[i] = 0f; continue; }
                float x = map.CentreX(i), z = map.CentreZ(i), y = map.GroundY[i];
                if (z < -15f && Mathf.Abs(z - StreamZ(x)) < 2.6f) map.Flags[i] |= GridMap.CellFlags.Water;
                if (Physics.Raycast(new Vector3(x, y + 0.6f, z), Vector3.up, 40f, ~(1 << 2), QueryTriggerInteraction.Ignore))
                    map.Flags[i] |= GridMap.CellFlags.Covered;
            }
            return map;
        }
    }
}
