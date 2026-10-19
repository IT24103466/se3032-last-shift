using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using WorldBuilder.Testing;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Expansion.
    ///
    /// Grows the level for the stealth / heist flow:
    ///   1. JUNGLE (start)      X -20..80, Z -64..0   the player starts at a camp, crosses a
    ///                          ravine with a stream, and reaches the old estate fence.
    ///   2. TEA ESTATE          X 0..60,   Z 0..40    (existing) sneak up through the tea.
    ///   3. FACTORY PREMISES    X 10..76,  Z 40..80   get in through the fence breach; entering
    ///                          sets off the alarm and security starts the chase.
    ///        east compound     line houses (lockers), lorry, water tower, fuel shed
    ///        dispatch store    X 52..64, Z 66..80: the tea bags to steal
    ///        back alley and north service lane, linking everything into loops
    ///   4. ESCAPE              through the main gate onto the estate road (west).
    ///
    /// Gameplay hand-over (for teammates): closed doors / shutters / gates, barricades,
    /// lockers and tea bags are placeholders under --- DYNAMIC --- with a GreyboxInteractable
    /// (walk test: E). Hiding zones, the alarm and escape triggers, spawn points and patrol
    /// routes are under --- GAMEPLAY ---. Vault obstacles are tagged "Vaultable", hiding spots
    /// "HideSpot".
    ///
    /// Everything is regenerated on each run. Afterwards: 3. Bake NavMesh, 4. Light Probes,
    /// 5. Bake Lighting, 6. Occlusion.
    /// </summary>
    public static class KirimalaExpansionBuilder
    {
        const string Env = "Assets/_Project/Art/Environment";
        const string MatFolder = Env + "/Materials/Props";
        const string TerrainFolder = Env + "/Terrain";
        const string MeshAsset = Env + "/Meshes/Kirimala_Expansion.asset";
        const string GroupName = "Kirimala_Expansion";
        const float Ground = 8f, LoftY = 12.5f, DoorH = 2.2f;

        // Playable areas (X/Z), also used by the backdrop hills and the map bounds
        internal static readonly Rect JungleArea = Rect.MinMaxRect(-20f, -64f, 80f, 0f);
        internal static readonly Rect EstateArea = Rect.MinMaxRect(0f, 0f, 60f, 40f);
        internal static readonly Rect CompoundArea = Rect.MinMaxRect(10f, 40f, 76f, 80f);
        internal static readonly Rect RoadArea = Rect.MinMaxRect(0f, 44f, 10f, 48f);
        const float JungleBase = -6f, JungleHeight = 30f;

        // Jungle trail from the camp to the estate footpath (which starts at 30, 0)
        internal static readonly Vector2[] Trail =
        {
            new Vector2(6f, -58f), new Vector2(16f, -48f), new Vector2(26f, -41f), new Vector2(30f, -34f),
            new Vector2(24f, -22f), new Vector2(34f, -12f), new Vector2(30f, -3f), new Vector2(30f, 0.5f)
        };
        static readonly Vector2 Camp = new Vector2(7f, -57f);
        static readonly Rect Ruin = Rect.MinMaxRect(53f, -23f, 67f, -17f);

        static Material whitewash, roofRed, rust, green, planks, crate, concrete, gravel, bark, burlap,
                        leaf, undergrowth, bamboo, rock, tarp, lorryPaint, water, sackTag;
        static KirimalaSurfaceBuilder.MeshCache boxes;
        static Dictionary<string, Mesh> meshes;
        static Transform hideRoot;
        static int hideCount;

        // ------------------------------------------------------------------ entry points

        internal static Terrain JungleTerrain() => FindTerrain("Terrain_Jungle");

        static Terrain FindTerrain(string name)
        {
            foreach (var t in Object.FindObjectsByType<Terrain>(FindObjectsInactive.Exclude))
                if (t.name == name) return t;
            return null;
        }

        /// <summary>Where the player starts (the jungle camp), or null if the jungle isn't built.</summary>
        internal static Vector3? PlayerStart()
        {
            var t = JungleTerrain();
            if (t == null) return null;
            return new Vector3(8.5f, H(t, 8.5f, -55f) + 0.1f, -55f);
        }

        [MenuItem("Tools/World Builder/Build Kirimala Expansion", priority = 86)]
        public static void Build()
        {
            var envRoot = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot);
            if (envRoot == null || envRoot.transform.Find("Structure/Blockout_Kirimala") == null)
            {
                EditorUtility.DisplayDialog("Kirimala Expansion", "Build the blockout, surfaces, props and set dressing first.", "OK");
                return;
            }
            if (!LoadMaterials()) return;

            EnsureTag("Vaultable");
            EnsureTag("HideSpot");
            LevelBakeTool.CreateHierarchy();
            var env = envRoot.transform;
            var structure = Fresh(env.Find("Structure"), GroupName);
            var props = Fresh(env.Find("Props"), GroupName);
            var dyn = Fresh(LevelBakeTool.FindRoot(LevelBakeTool.DynamicRoot).transform, GroupName);
            var gameplay = Fresh(LevelBakeTool.FindRoot(LevelBakeTool.GameplayRoot).transform, GroupName);
            var lighting = LevelBakeTool.FindRoot(LevelBakeTool.LightingRoot).transform;
            var lights = Fresh(lighting.Find("Lights"), "Expansion_Lights");
            var probes = Fresh(lighting.Find("Probes"), "Expansion_ReflectionProbes");

            boxes = KirimalaSurfaceBuilder.BoxCache();
            meshes = new Dictionary<string, Mesh>();
            hideRoot = Group(gameplay, "HidingSpots");
            hideCount = 0;

            // 1. Jungle
            var terrain = BuildTerrain(structure);
            PaintJungle(terrain);
            var jungle = Group(props, "Jungle");
            int trees = Trees(Group(jungle, "Trees"), terrain);
            trees += Understory(Group(jungle, "Understory"), terrain);
            int bushes = Undergrowth(Group(jungle, "Undergrowth"), terrain);
            Bamboo(Group(jungle, "Bamboo"), terrain);
            int rocks = Rocks(Group(jungle, "Rocks"), terrain);
            int logs = Logs(Group(jungle, "FallenLogs"), terrain);
            Stream(Group(jungle, "Stream"), terrain);
            RuinedLineHouse(Group(jungle, "Ruin_LineHouse"), terrain);
            CampSite(Group(jungle, "Camp"), terrain, lights);
            EstateFence(Group(props, "EstateBoundaryFence"), terrain);

            // 2-3. Factory premises
            var compound = Group(structure, "Compound");
            CompoundGround(compound);
            CompoundWalls(compound);
            LineHouses(Group(compound, "LineHouses"));
            Warehouse(Group(compound, "Warehouse"));
            var yardProps = Group(props, "Compound_Props");
            CompoundProps(yardProps);
            FenceBreach(yardProps);
            FactoryHideSpots();

            // Interactables, gameplay markers, lights, atmosphere
            int doors = Doors(Group(dyn, "Doors"));
            int barricades = Barricades(Group(dyn, "Barricades"));
            int bags = TeaBags(Group(dyn, "Objectives"));
            Lockers(Group(dyn, "Lockers"));
            GameplayMarkers(gameplay, terrain);
            CompoundLights(lights, probes);
            Atmosphere(lighting);

            boxes.Save();
            SaveMeshes();
            KirimalaDressingBuilder.RebuildBackdropAndBounds();   // hills now go around the new areas
            KirimalaSurfaceBuilder.ApplyIfBuilt();
            LevelBakeTool.MarkEnvironmentStatic();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[World Builder] Expansion built: jungle ({trees} trees, {bushes} bushes/ferns, {rocks} rocks, {logs} logs, " +
                      $"stream, ruin, camp), east compound, line houses, dispatch warehouse, {doors} doors, {barricades} barricades, " +
                      $"{bags} tea bags, {hideCount} hiding zones. Next: 3. Bake NavMesh, 4. Light Probes, 5. Bake Lighting.");
        }

        // ------------------------------------------------------------------ jungle terrain

        internal static float StreamZ(float x) => -34f + 5f * Mathf.Sin(0.07f * x + 1f);

        /// <summary>Jungle ground height (world Y). Matches the estate (Y 0) along Z 0, X 0-60.</summary>
        static float JungleShape(float x, float z)
        {
            float h = -0.8f + (Mathf.PerlinNoise(x * 0.045f + 11f, z * 0.045f + 3f) - 0.5f) * 2.6f
                            + (Mathf.PerlinNoise(x * 0.16f + 2f, z * 0.16f + 8f) - 0.5f) * 0.6f;
            // The trail is a little flatter than the forest floor around it
            h = Mathf.Lerp(h, -0.6f, 0.5f * (1f - Smooth(1.5f, 4f, DistanceToTrail(new Vector2(x, z)))));
            // Ravine: 3.2 m deep, banks under 40 degrees so people and robots can climb them
            float d = Mathf.Abs(z - StreamZ(x));
            h -= 3.2f * (1f - Smooth(1.5f, 7.5f, d));
            // Hills close the jungle in on the west, east and south sides, and beside the estate
            float edge = Mathf.Min(Mathf.Min(x - JungleArea.xMin, JungleArea.xMax - x), z - JungleArea.yMin);
            float beyondEstate = Smooth(0f, 4f, Mathf.Max(-x, x - 60f));
            float rise = Mathf.Max(9f * (1f - Smooth(0f, 9f, edge)), 9f * (1f - Smooth(0f, 9f, -z)) * beyondEstate);
            h += rise;
            // Blend into the estate terrain (Y 0 along its south edge)
            h = Mathf.Lerp(h, 0f, Smooth(-8f, 0f, z) * (1f - beyondEstate));
            return Mathf.Clamp(h, JungleBase + 0.2f, JungleBase + JungleHeight - 0.5f);
        }

        static Terrain BuildTerrain(Transform parent)
        {
            EnsureFolder(TerrainFolder);
            string path = TerrainFolder + "/Kirimala_Jungle.asset";
            AssetDatabase.DeleteAsset(path);

            var data = new TerrainData { heightmapResolution = 257, alphamapResolution = 256 };
            data.size = new Vector3(JungleArea.width, JungleHeight, JungleArea.height);
            int res = data.heightmapResolution;
            var h = new float[res, res];
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
            {
                float x = JungleArea.xMin + xi / (float)(res - 1) * JungleArea.width;
                float z = JungleArea.yMin + zi / (float)(res - 1) * JungleArea.height;
                h[zi, xi] = (JungleShape(x, z) - JungleBase) / JungleHeight;
            }
            data.SetHeights(0, 0, h);
            AssetDatabase.CreateAsset(data, path);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain_Jungle";
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(JungleArea.xMin, JungleBase, JungleArea.yMin);
            var t = go.GetComponent<Terrain>();
            t.allowAutoConnect = false;   // the estate terrain has a different resolution; the edges are matched by height instead
            var estate = KirimalaBlockoutBuilder.EstateTerrain();
            if (estate != null && estate != t) { t.materialTemplate = estate.materialTemplate; estate.allowAutoConnect = false; }
            return t;
        }

        static void PaintJungle(Terrain terrain)
        {
            TerrainLayer L(string n) => AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/{n}.terrainlayer");
            TerrainLayer soil = L("TL_Soil"), rockL = L("TL_Rock"), path = L("TL_Path");
            if (soil == null || rockL == null || path == null) { Debug.LogWarning("[World Builder] Terrain layers missing: run Build Kirimala Surfaces."); return; }

            // Leaf litter: the laterite soil, darkened and pushed towards olive
            string floorPath = $"{TerrainFolder}/TL_JungleFloor.terrainlayer";
            var floor = AssetDatabase.LoadAssetAtPath<TerrainLayer>(floorPath);
            if (floor == null) { floor = new TerrainLayer(); AssetDatabase.CreateAsset(floor, floorPath); }
            floor.diffuseTexture = soil.diffuseTexture;
            floor.normalMapTexture = soil.normalMapTexture;
            floor.maskMapTexture = soil.maskMapTexture;
            floor.tileSize = new Vector2(3f, 3f);
            floor.diffuseRemapMin = Vector4.zero;
            floor.diffuseRemapMax = new Vector4(0.42f, 0.47f, 0.32f, 1f);
            EditorUtility.SetDirty(floor);

            var d = terrain.terrainData;
            d.terrainLayers = new[] { floor, rockL, path, soil };
            int res = d.alphamapResolution;
            var map = new float[res, res, 4];
            Vector3 o = terrain.transform.position;
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
            {
                float u = xi / (float)(res - 1), v = zi / (float)(res - 1);
                var w = new Vector2(o.x + u * d.size.x, o.z + v * d.size.z);
                float rock = Smooth(24f, 36f, d.GetSteepness(u, v));
                float trail = 1f - Smooth(0.9f, 1.7f, DistanceToTrail(w));
                trail = Mathf.Max(trail, 0.7f * (1f - Smooth(3f, 5f, Vector2.Distance(w, Camp))));
                if (Ruin.Contains(w)) trail = Mathf.Max(trail, 0.6f);
                float mud = 1f - Smooth(1.5f, 4f, Mathf.Abs(w.y - StreamZ(w.x)));
                trail *= 1f - rock;
                mud *= (1f - rock) * (1f - trail);
                float leafLitter = Mathf.Max(0f, 1f - rock - trail - mud);
                float sum = leafLitter + rock + trail + mud;
                map[zi, xi, 0] = leafLitter / sum; map[zi, xi, 1] = rock / sum;
                map[zi, xi, 2] = trail / sum; map[zi, xi, 3] = mud / sum;
            }
            d.SetAlphamaps(0, 0, map);
            EditorUtility.SetDirty(d);
        }

        internal static float DistanceToTrail(Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Trail.Length - 1; i++)
            {
                Vector2 a = Trail[i], b = Trail[i + 1], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        static float H(Terrain t, float x, float z) => t.SampleHeight(new Vector3(x, 0f, z)) + t.transform.position.y;

        /// <summary>Is this jungle spot free for vegetation? (off the trail, stream, camp and ruin)</summary>
        static bool Free(Vector2 p, float trailGap, float streamGap)
        {
            if (p.x < -17f || p.x > 77f || p.y < -61f || p.y > -2f) return false;
            if (DistanceToTrail(p) < trailGap || Mathf.Abs(p.y - StreamZ(p.x)) < streamGap) return false;
            if (Vector2.Distance(p, Camp) < 7f) return false;
            var r = Ruin; r.xMin -= 2f; r.yMin -= 2f; r.xMax += 2f; r.yMax += 2f;
            return !r.Contains(p);
        }

        static List<Vector2> Scatter(int seed, int count, float spacing, float trailGap, float streamGap, List<Vector2> avoid = null, float avoidGap = 0f)
        {
            var rng = new System.Random(seed);
            var pts = new List<Vector2>();
            for (int tries = 0; tries < count * 60 && pts.Count < count; tries++)
            {
                var p = new Vector2(-18f + (float)rng.NextDouble() * 96f, -62f + (float)rng.NextDouble() * 61f);
                if (!Free(p, trailGap, streamGap)) continue;
                if (pts.Any(q => (q - p).sqrMagnitude < spacing * spacing)) continue;
                if (avoid != null && avoid.Any(q => (q - p).sqrMagnitude < avoidGap * avoidGap)) continue;
                pts.Add(p);
            }
            return pts;
        }

        // ------------------------------------------------------------------ jungle vegetation

        static List<Vector2> treeSpots;

        static int Trees(Transform g, Terrain t)
        {
            var crowns = Enumerable.Range(0, 4).Select(i => MeshFor($"JBlob_{i}", () => BlobMesh(17f * (i + 2), 16, 10, 0.55f))).ToArray();
            treeSpots = Scatter(101, 62, 5.5f, 2.6f, 3f);
            var rng = new System.Random(7);
            int n = 0;
            foreach (var s in treeSpots)
            {
                float y = H(t, s.x, s.y);
                float height = 9f + (float)rng.NextDouble() * 5f, radius = 0.28f + (float)rng.NextDouble() * 0.2f;
                var tree = new GameObject("JungleTree").transform;
                tree.SetParent(g, false);
                tree.position = new Vector3(s.x, y, s.y);
                Cyl("Veg_Trunk", tree, new Vector3(s.x, y + height * 0.5f - 0.4f, s.y), radius, height, bark, keepCollider: true);
                // Buttress roots
                for (int k = 0; k < 3; k++)
                {
                    float a = (k * 120f + rng.Next(40)) * Mathf.Deg2Rad;
                    var root = Cyl("Veg_Root", tree, new Vector3(s.x + Mathf.Cos(a) * radius * 1.6f, y + 0.35f, s.y + Mathf.Sin(a) * radius * 1.6f),
                                   radius * 0.45f, 1.6f, bark);
                    root.transform.rotation = Quaternion.LookRotation(new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a))) * Quaternion.Euler(-60f, 0f, 0f);
                }
                // Canopy: 4-6 overlapping clumps at different heights, so it reads as a deep, ragged
                // crown from below instead of a flat disc
                void Clump(Vector3 off, Vector3 scale, int k)
                {
                    var c = new GameObject("Veg_Crown", typeof(MeshFilter), typeof(MeshRenderer));
                    c.transform.SetParent(tree, false);
                    c.transform.position = new Vector3(s.x, y + height, s.y) + off;
                    c.transform.rotation = Quaternion.Euler(rng.Next(-12, 12), rng.Next(360), rng.Next(-12, 12));
                    c.transform.localScale = scale;
                    c.GetComponent<MeshFilter>().sharedMesh = crowns[(n + k) % crowns.Length];
                    c.GetComponent<MeshRenderer>().sharedMaterial = leaf;
                }
                int clumps = 4 + rng.Next(3);
                for (int k = 0; k < clumps; k++)
                {
                    float a = (k / (float)clumps + (float)rng.NextDouble() * 0.15f) * Mathf.PI * 2f;
                    float r = k == 0 ? 0f : 1.6f + (float)rng.NextDouble() * 1.4f;
                    float w = k == 0 ? 5f + (float)rng.NextDouble() * 1.5f : 3.4f + (float)rng.NextDouble() * 1.8f;
                    float dy = k == 0 ? 0.8f : -2.2f + (float)rng.NextDouble() * 2.6f;
                    Clump(new Vector3(Mathf.Cos(a) * r, dy, Mathf.Sin(a) * r), new Vector3(w, w * (0.7f + (float)rng.NextDouble() * 0.25f), w * 0.9f), k);
                }
                // Lianas hanging from the crown
                for (int k = 0; k < 3; k++)
                {
                    float a = rng.Next(360) * Mathf.Deg2Rad, r = 1.2f + (float)rng.NextDouble() * 2f, len = 3f + (float)rng.NextDouble() * 5f;
                    var vine = Cyl("Veg_Vine", tree, new Vector3(s.x + Mathf.Cos(a) * r, y + height - 1.6f - len * 0.5f, s.y + Mathf.Sin(a) * r), 0.03f, len, leaf);
                    vine.transform.rotation = Quaternion.Euler(rng.Next(-6, 6), 0f, rng.Next(-6, 6));
                }
                n++;
            }
            return n;
        }

        /// <summary>Small trees between the big ones: fills the space between the floor and the canopy.</summary>
        static int Understory(Transform g, Terrain t)
        {
            var crowns = Enumerable.Range(0, 4).Select(i => MeshFor($"JBlob_{i}", () => BlobMesh(17f * (i + 2), 16, 10, 0.55f))).ToArray();
            var rng = new System.Random(57);
            int n = 0;
            foreach (var s in Scatter(606, 45, 4f, 2.2f, 2.5f, treeSpots, 3f))
            {
                float y = H(t, s.x, s.y), height = 3.5f + (float)rng.NextDouble() * 2.5f;
                var tree = new GameObject("UnderstoryTree").transform;
                tree.SetParent(g, false);
                tree.position = new Vector3(s.x, y, s.y);
                var trunk = Cyl("Veg_Trunk", tree, new Vector3(s.x, y + height * 0.5f - 0.2f, s.y), 0.1f + (float)rng.NextDouble() * 0.06f, height, bark, keepCollider: true);
                trunk.transform.rotation = Quaternion.Euler(rng.Next(-7, 7), 0f, rng.Next(-7, 7));
                for (int k = 0; k < 3; k++)
                {
                    float a = rng.Next(360) * Mathf.Deg2Rad, r = k == 0 ? 0f : 0.9f, w = 2.2f + (float)rng.NextDouble() * 1.3f;
                    var c = Blob("Veg_Crown", tree, crowns[(n + k) % 4], new Vector3(s.x + Mathf.Cos(a) * r, y + height + (k == 0 ? 0.3f : -0.6f), s.y + Mathf.Sin(a) * r),
                                 new Vector3(w, w * 0.75f, w), rng.Next(360), leaf);
                }
                n++;
            }
            return n;
        }

        static int Undergrowth(Transform g, Terrain t)
        {
            var blobs = Enumerable.Range(0, 4).Select(i => MeshFor($"JBlob_{i}", () => BlobMesh(17f * (i + 2), 16, 10, 0.55f))).ToArray();
            var fern = MeshFor("JFern", () => BlobMesh(91f, 12, 8, 0.4f));
            var rng = new System.Random(13);
            int n = 0;

            // Big bushes: dense enough to crouch in (no collider; the biggest become hiding zones)
            var spots = Scatter(202, 150, 2.4f, 1.6f, 2.2f, treeSpots, 1.4f);
            foreach (var s in spots)
            {
                float sx = 1.6f + (float)rng.NextDouble() * 1.6f, sy = 1.0f + (float)rng.NextDouble() * 0.8f;
                var b = Blob("Veg_Bush", g, blobs[n % blobs.Length], new Vector3(s.x, H(t, s.x, s.y) + sy * 0.2f, s.y),
                             new Vector3(sx, sy, sx * (0.8f + (float)rng.NextDouble() * 0.4f)), rng.Next(360), undergrowth);
                if (sx > 2.9f && sy > 1.5f) HideZone($"Jungle_Bush_{n}", b.transform.position + Vector3.up * 0.2f, new Vector3(sx * 0.7f, 1.4f, sx * 0.7f));
                n++;
            }
            // Ferns along the trail edges and the stream banks
            foreach (var s in Scatter(303, 130, 1.6f, 1.2f, 1.6f, treeSpots, 1f))
            {
                float k = 0.7f + (float)rng.NextDouble() * 0.6f;
                Blob("Veg_Fern", g, fern, new Vector3(s.x, H(t, s.x, s.y) + 0.05f, s.y), new Vector3(k * 1.4f, k * 0.8f, k * 1.4f), rng.Next(360), leaf);
                n++;
            }
            return n;
        }

        static void Bamboo(Transform g, Terrain t)
        {
            var rng = new System.Random(31);
            Vector2[] clumps = { new Vector2(-6, -27), new Vector2(12, -37), new Vector2(44, -26), new Vector2(58, -45), new Vector2(70, -31), new Vector2(2, -44), new Vector2(38, -46) };
            foreach (var c in clumps)
            {
                if (!Free(c, 2.5f, 2f)) continue;
                var clump = new GameObject("BambooClump").transform;
                clump.SetParent(g, false);
                float y = H(t, c.x, c.y);
                clump.position = new Vector3(c.x, y, c.y);
                var col = clump.gameObject.AddComponent<CapsuleCollider>();   // the clump is solid at the base
                col.radius = 0.6f; col.height = 4f; col.center = new Vector3(0f, 2f, 0f);
                for (int i = 0; i < 12; i++)
                {
                    float a = (float)rng.NextDouble() * Mathf.PI * 2f, r = (float)rng.NextDouble() * 0.7f, len = 6f + (float)rng.NextDouble() * 3.5f;
                    var p = new Vector3(c.x + Mathf.Cos(a) * r, y, c.y + Mathf.Sin(a) * r);
                    var tilt = Quaternion.Euler((float)rng.NextDouble() * 16f - 8f, 0f, (float)rng.NextDouble() * 16f - 8f);
                    var culm = Cyl("Veg_Bamboo", clump, p + tilt * Vector3.up * (len * 0.5f), 0.05f + (float)rng.NextDouble() * 0.03f, len, bamboo);
                    culm.transform.rotation = tilt;
                }
            }
        }

        static int Rocks(Transform g, Terrain t)
        {
            var shapes = Enumerable.Range(0, 3).Select(i => MeshFor($"JRock_{i}", () => BlobMesh(53f * (i + 1), 14, 9, 0.75f))).ToArray();
            var rng = new System.Random(17);
            int n = 0;
            foreach (var s in Scatter(404, 28, 6f, 2f, 0.5f, treeSpots, 2f))
            {
                float sx = 1.2f + (float)rng.NextDouble() * 2.4f, sy = 0.8f + (float)rng.NextDouble() * 1.3f;
                var r = Blob("Veg_Rock", g, shapes[n % 3], new Vector3(s.x, H(t, s.x, s.y) + sy * 0.12f, s.y),
                             new Vector3(sx, sy, sx * 0.8f), rng.Next(360), rock);
                r.AddComponent<MeshCollider>().sharedMesh = shapes[n % 3];
                if (sy < 1.05f) r.tag = "Vaultable";
                n++;
            }
            // Stepping stones across the stream, west of the log bridge
            for (int i = 0; i < 5; i++)
            {
                float x = 8f + i * 0.35f, z = StreamZ(x) - 2.4f + i * 1.2f;
                var st = Blob("Veg_Rock", g, shapes[i % 3], new Vector3(x, H(t, x, z) + 0.1f, z), new Vector3(0.9f, 0.7f, 0.8f), rng.Next(360), rock);
                st.AddComponent<MeshCollider>().sharedMesh = shapes[i % 3];
                n++;
            }
            return n;
        }

        static int Logs(Transform g, Terrain t)
        {
            int n = 0;
            void Log(Vector2 a, Vector2 b, float radius, float lift = 0f)
            {
                var pa = new Vector3(a.x, H(t, a.x, a.y) + radius * 0.75f + lift, a.y);
                var pb = new Vector3(b.x, H(t, b.x, b.y) + radius * 0.75f + lift, b.y);
                var log = Cyl("Vault_Log", g, (pa + pb) * 0.5f, radius, Vector3.Distance(pa, pb), bark, keepCollider: true);
                log.transform.rotation = Quaternion.FromToRotation(Vector3.up, pb - pa);
                log.tag = "Vaultable";
                n++;
            }
            // The log bridge over the ravine on the trail (walk along it, or climb down and wade)
            var z0 = StreamZ(30f);
            Log(new Vector2(29.6f, z0 - 7.5f), new Vector2(30.4f, z0 + 7.5f), 0.55f, 0.1f);
            // Logs across the trail: jump over them
            Log(new Vector2(18.5f, -48.5f), new Vector2(21.5f, -44f), 0.38f);
            Log(new Vector2(27f, -16.5f), new Vector2(31f, -13.5f), 0.4f);
            Log(new Vector2(33f, -9f), new Vector2(35.5f, -5f), 0.35f);
            // Fallen trees in the forest (cover, vaulting)
            var rng = new System.Random(23);
            foreach (var s in Scatter(505, 9, 10f, 3f, 3f, treeSpots, 3f))
            {
                float a = rng.Next(180) * Mathf.Deg2Rad, len = 4f + (float)rng.NextDouble() * 4f;
                var dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * len * 0.5f;
                if (!Free(s + dir, 1.5f, 3f) || !Free(s - dir, 1.5f, 3f)) continue;
                Log(s - dir, s + dir, 0.35f + (float)rng.NextDouble() * 0.15f);
            }
            return n;
        }

        static void Stream(Transform g, Terrain t)
        {
            // Water surface strip following the ravine bed (no collider: the player wades)
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            int i = 0;
            for (float x = -14f; x <= 74f; x += 1f, i++)
            {
                float z = StreamZ(x), y = H(t, x, z) + 0.45f;
                float dz = 5f * 0.07f * Mathf.Cos(0.07f * x + 1f);
                var side = new Vector3(-dz, 0f, 1f).normalized * 2.3f;
                v.Add(new Vector3(x, y, z) - side); v.Add(new Vector3(x, y, z) + side);
                uv.Add(new Vector2(x * 0.3f, 0f)); uv.Add(new Vector2(x * 0.3f, 1f));
                if (i > 0) { int b = (i - 1) * 2; tris.AddRange(new[] { b, b + 1, b + 2, b + 1, b + 3, b + 2 }); }
            }
            var mesh = MeshFor("StreamWater", () =>
            {
                var m = new Mesh();
                m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tris, 0);
                m.RecalculateNormals(); m.RecalculateBounds();
                return m;
            }, rebuild: true);
            var go = new GameObject("Veg_StreamWater", typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(g, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = water;
            r.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>An abandoned estate line house in the jungle: broken walls, one collapsed roof.</summary>
        static void RuinedLineHouse(Transform g, Terrain t)
        {
            float top = new[] { H(t, Ruin.xMin, Ruin.yMin), H(t, Ruin.xMax, Ruin.yMin), H(t, Ruin.xMin, Ruin.yMax), H(t, Ruin.xMax, Ruin.yMax), H(t, Ruin.center.x, Ruin.center.y) }.Max() + 0.1f;
            float bottom = top - 2.5f;
            Box("Plinth_Ruin", g, Ruin.xMin - 0.3f, bottom, Ruin.yMin - 0.3f, Ruin.xMax + 0.3f, top, Ruin.yMax + 0.3f, concrete);
            var rng = new System.Random(77);
            var walls = Group(g, "Wall_Ruin");

            // Jagged wall: 0.9 m pieces of random height; doors are open gaps, windows have 0.9 m sills
            void Jagged(bool alongX, float fixedCoord, float a0, float a1, (float, float)[] doors, (float, float)[] windows)
            {
                for (float a = a0; a < a1 - 0.01f; a += 0.9f)
                {
                    float b = Mathf.Min(a + 0.9f, a1), mid = (a + b) * 0.5f;
                    if (doors.Any(d => mid > d.Item1 && mid < d.Item2)) continue;
                    bool win = windows.Any(w => mid > w.Item1 && mid < w.Item2);
                    float h = win ? 0.9f : (rng.NextDouble() < 0.15 ? 0.8f + (float)rng.NextDouble() * 0.3f : 1.6f + (float)rng.NextDouble() * 1.3f);
                    if (alongX) Box("Segment", walls, a, top, fixedCoord - 0.12f, b, top + h, fixedCoord + 0.12f, whitewash);
                    else Box("Segment", walls, fixedCoord - 0.12f, top, a, fixedCoord + 0.12f, top + h, b, whitewash);
                }
            }
            float r1 = 57.67f, r2 = 62.33f;
            Jagged(true, Ruin.yMin, Ruin.xMin, Ruin.xMax, new[] { (54.6f, 55.6f), (59.4f, 60.4f), (64.2f, 65.2f) }, new[] { (56f, 57.2f), (60.8f, 62f) });
            Jagged(true, Ruin.yMax, Ruin.xMin, Ruin.xMax, new (float, float)[0], new[] { (54.4f, 56f), (59f, 60.6f), (63.6f, 65.2f) });
            Jagged(false, Ruin.xMin, Ruin.yMin, Ruin.yMax, new (float, float)[0], new[] { (-20.6f, -19.4f) });
            Jagged(false, Ruin.xMax, Ruin.yMin, Ruin.yMax, new (float, float)[0], new (float, float)[0]);
            Jagged(false, r1, Ruin.yMin + 0.2f, Ruin.yMax - 0.2f, new[] { (-20.6f, -19.5f) }, new (float, float)[0]);
            Jagged(false, r2, Ruin.yMin + 0.2f, Ruin.yMax - 0.2f, new[] { (-21.5f, -20.4f) }, new (float, float)[0]);

            // Room 3's roof has fallen in: one edge on the back wall, the other on the floor
            float span = Ruin.height - 0.6f, drop = 2.4f;
            float len = Mathf.Sqrt(span * span + drop * drop), ang = Mathf.Atan2(drop, span) * Mathf.Rad2Deg;
            BoxR("Roof", g, new Vector3((r2 + Ruin.xMax) * 0.5f + 0.2f, top + drop * 0.5f + 0.1f, Ruin.center.y),
                 new Vector3(Ruin.xMax - r2 - 0.8f, 0.12f, len), Quaternion.Euler(-ang, 0f, 0f), roofRed, true);
            // Old furniture
            Box("Crate_Ruin", g, 54f, top, -18.4f, 55f, top + 0.8f, -17.6f, crate).tag = "Vaultable";
            Box("Bedframe_Ruin", g, 58.4f, top, -18.9f, 60.3f, top + 0.45f, -18f, planks);

            HideZone("Ruin_Room1", new Vector3(55.3f, top + 0.8f, -19.5f), new Vector3(3.6f, 1.6f, 4.5f));
            HideZone("Ruin_Room3_UnderRoof", new Vector3(64.6f, top + 0.6f, -21.7f), new Vector3(3.4f, 1.2f, 1.8f));
            ruinFloor = top;
        }
        static float ruinFloor;

        static void CampSite(Transform g, Terrain t, Transform lights)
        {
            float y = H(t, Camp.x, Camp.y);
            // Tarp shelter on four poles
            foreach (var p in new[] { new Vector2(-1.5f, -1.2f), new Vector2(1.5f, -1.2f), new Vector2(-1.5f, 1.2f), new Vector2(1.5f, 1.2f) })
            {
                float px = Camp.x + p.x, pz = Camp.y + p.y, ph = p.y > 0 ? 2.2f : 1.5f;
                float gy = H(t, px, pz);
                Cyl("Pole", g, new Vector3(px, gy + ph * 0.5f - 0.1f, pz), 0.05f, ph + 0.2f, bark);
            }
            BoxR("Tarp", g, new Vector3(Camp.x, y + 1.95f, Camp.y), new Vector3(3.6f, 0.03f, 3f), Quaternion.Euler(-13f, 0f, 0f), tarp, false);
            Box("Crate_Camp", g, Camp.x + 1.6f, y - 0.05f, Camp.y - 0.4f, Camp.x + 2.3f, y + 0.55f, Camp.y + 0.3f, crate).tag = "Vaultable";
            Box("Bedroll", g, Camp.x - 1.2f, y, Camp.y - 0.6f, Camp.x + 0.4f, y + 0.12f, Camp.y + 0.2f, tarp, false);
            // Campfire stones and the lantern (the only warm light in the jungle)
            for (int i = 0; i < 7; i++)
            {
                float a = i / 7f * Mathf.PI * 2f;
                var st = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                st.name = "FireStone"; Object.DestroyImmediate(st.GetComponent<Collider>());
                st.transform.SetParent(g, false);
                st.transform.position = new Vector3(Camp.x + 0.5f + Mathf.Cos(a) * 0.45f, y + 0.05f, Camp.y + 2.6f + Mathf.Sin(a) * 0.45f);
                st.transform.localScale = new Vector3(0.22f, 0.15f, 0.2f);
                st.GetComponent<MeshRenderer>().sharedMaterial = rock;
            }
            Lamp(Group(lights, "Jungle"), "Lantern_Camp", new Vector3(Camp.x + 1.9f, y + 0.85f, Camp.y - 0.05f), 9f, 2.6f, new Color(1f, 0.66f, 0.32f));
            Lamp(lights.Find("Jungle"), "Lantern_Ruin", new Vector3(61f, ruinFloor + 0.35f, -19.6f), 7f, 1.4f, new Color(1f, 0.6f, 0.3f));
        }

        /// <summary>Old barbed-wire estate boundary along Z 0, with the trail gap and a cut section.</summary>
        static void EstateFence(Transform g, Terrain jungle)
        {
            var estate = KirimalaBlockoutBuilder.EstateTerrain();
            const float z = -0.6f;
            float Ht(float x) => jungle != null ? H(jungle, x, z) : 0f;
            (float, float)[] gaps = { (28.4f, 31.6f), (9f, 11.5f) };
            for (float x = 0.5f; x < 59.6f; x += 3f)
            {
                float x1 = Mathf.Min(x + 3f, 59.5f), mid = (x + x1) * 0.5f;
                Cyl("FencePost", g, new Vector3(x, Ht(x) + 0.6f, z), 0.06f, 1.6f, bark, keepCollider: true);
                bool gap = gaps.Any(gp => mid > gp.Item1 && mid < gp.Item2);
                if (gap) continue;
                var span = new GameObject("FenceSpan", typeof(BoxCollider)).transform;   // keeps people (and robots) out
                span.SetParent(g, false);
                span.position = new Vector3(mid, (Ht(x) + Ht(x1)) * 0.5f + 0.7f, z);
                span.GetComponent<BoxCollider>().size = new Vector3(x1 - x, 1.4f, 0.12f);
                foreach (float wy in new[] { 0.35f, 0.7f, 1.05f, 1.35f })
                    BoxR("Wire", g, new Vector3(mid, (Ht(x) + Ht(x1)) * 0.5f + wy, z), new Vector3(x1 - x, 0.015f, 0.015f), Quaternion.identity, rust, false);
            }
            // Cut wire hanging loose at the second gap
            for (int i = 0; i < 3; i++)
                BoxR("Wire_Cut", g, new Vector3(9.4f, Ht(9.4f) + 0.4f + i * 0.35f, z - 0.3f), new Vector3(0.015f, 0.015f, 0.8f), Quaternion.Euler(50f + i * 10f, 20f, 0f), rust, false);
        }

        // ------------------------------------------------------------------ factory premises

        static void CompoundGround(Transform g)
        {
            Box("Foundation", g, 50f, 0f, 40f, 76f, Ground, 80f, gravel);   // east compound
            Box("Foundation", g, 10f, 0f, 76f, 50f, Ground, 80f, gravel);   // north service lane
            Box("Floor_Warehouse", g, 52f, Ground, 66f, 64f, Ground + 0.02f, 80f, concrete);
            Box("Floor_Veranda", g, 66f, Ground, 44f, 68f, Ground + 0.15f, 74f, concrete);
        }

        static void CompoundWalls(Transform g)
        {
            const float h = 2.6f, t = 0.25f;
            WallX(g, "Wall_Compound_S", 40f, 50f, 76f, Ground, h, t, new Vector4(66f, 70f, 0f, h));   // service gate (locked)
            WallZ(g, "Wall_Compound_E", 76f, 40f, 80f, Ground, h, t);
            WallX(g, "Wall_Compound_N", 80f, 10f, 76f, Ground, h, t);
            WallZ(g, "Wall_Compound_W", 10f, 76f, 80f, Ground, h, t);
            // Fuel shed by the service gate
            WallX(g, "Wall_FuelShed", 41f, 52f, 55.5f, Ground, h, 0.2f);
            WallX(g, "Wall_FuelShed", 44.5f, 52f, 55.5f, Ground, h, 0.2f, new Vector4(53.2f, 54.2f, 0f, DoorH));
            WallZ(g, "Wall_FuelShed", 52f, 41f, 44.5f, Ground, h, 0.2f);
            WallZ(g, "Wall_FuelShed", 55.5f, 41f, 44.5f, Ground, h, 0.2f);
            Box("Roof", g, 51.7f, Ground + h, 40.7f, 55.8f, Ground + h + 0.15f, 44.8f, roofRed);
            HideZone("FuelShed", new Vector3(53.75f, Ground + 1f, 42.6f), new Vector3(3f, 2f, 3f));
        }

        static void LineHouses(Transform g)
        {
            // Five workers' rooms, X 68-76, Z 44-74, opening onto a covered veranda (X 66-68)
            const float h = 3.2f, t = 0.2f;
            var holes = new List<Vector4>();
            for (int i = 0; i < 5; i++)
            {
                float zc = 47f + i * 6f;
                holes.Add(new Vector4(zc - 0.5f, zc + 0.5f, 0f, DoorH));       // door
                holes.Add(new Vector4(zc + 1.2f, zc + 2.3f, 0.9f, 2.0f));      // window: low sill, vaultable
            }
            WallZ(g, "Wall_LineHouse_W", 68f, 44f, 74f, Ground, h, t, holes.ToArray());
            WallZ(g, "Wall_LineHouse_E", 75.6f, 44f, 74f, Ground, h, t);
            WallX(g, "Wall_LineHouse_S", 44f, 68f, 75.7f, Ground, h, t);
            WallX(g, "Wall_LineHouse_N", 74f, 68f, 75.7f, Ground, h, t);
            for (int i = 1; i < 5; i++) WallX(g, "Wall_LineHouse_P", 44f + i * 6f, 68.1f, 75.5f, Ground, h, 0.15f);
            Box("Roof", g, 65.8f, Ground + h, 43.6f, 76f, Ground + h + 0.15f, 74.4f, roofRed);
            for (float z = 44.2f; z <= 74f; z += 6f) Cyl("Post", g, new Vector3(66.2f, Ground + h * 0.5f, z), 0.09f, h, planks, keepCollider: true);
            for (int i = 0; i < 5; i++)
            {
                float z0 = 44f + i * 6f;
                Box("Bed", g, 73.9f, Ground, z0 + 0.6f, 75.4f, Ground + 0.5f, z0 + 2.6f, planks);
                Box("Table", g, 72f, Ground, z0 + 4.2f, 73.2f, Ground + 0.75f, z0 + 5.4f, crate).tag = "Vaultable";
                HideZone($"LineRoom_{i + 1}_UnderBed", new Vector3(74.65f, Ground + 0.3f, z0 + 1.6f), new Vector3(1.3f, 0.6f, 1.8f));
            }
        }

        static void Warehouse(Transform g)
        {
            // Tea dispatch store: the bags to steal are inside
            const float h = 6f, t = 0.3f;
            WallX(g, "Wall_Warehouse_S", 66f, 52f, 64f, Ground, h, t, new Vector4(54.5f, 60.5f, 0f, 3.6f), new Vector4(62f, 63f, 0f, DoorH));
            WallX(g, "Wall_Warehouse_N", 79.7f, 52f, 64f, Ground, h, t);
            WallZ(g, "Wall_Warehouse_W", 52f, 66f, 79.85f, Ground, h, t, new Vector4(70f, 71f, 0f, DoorH));
            WallZ(g, "Wall_Warehouse_E", 64f, 66f, 79.85f, Ground, h, t, new Vector4(70.2f, 71.2f, 0f, DoorH));
            Box("Roof", g, 51.7f, Ground + h, 65.7f, 64.3f, Ground + h + 0.3f, 80f, roofRed);
            // Loading dock beside the shutter: jump up onto it
            Box("Vault_LoadingDock", g, 52.2f, Ground, 63.6f, 54.3f, Ground + 1f, 65.85f, concrete).tag = "Vaultable";

            // Stacks of tea bags in rows, with aisles to sneak through
            var rng = new System.Random(41);
            var rows = Group(g, "TeaBagStacks");
            foreach (var (z0, z1) in new[] { (68.6f, 69.8f), (71.6f, 72.8f), (74.6f, 75.8f), (77.8f, 79.2f) })
            for (float x = 53f; x < 62.6f; x += 2.2f)
            {
                if (z0 > 77f && x > 60f) continue;
                float top = 1.2f + (float)rng.Next(4) * 0.45f;
                var stack = Box("TeaBagStack", rows, x, Ground, z0, x + 1.5f, Ground + top, z1, burlap);
                if (top < 1.3f) stack.tag = "Vaultable";
            }
            HideZone("Warehouse_Aisle_W", new Vector3(54.9f, Ground + 0.9f, 73.7f), new Vector3(0.8f, 1.8f, 1.6f));
            HideZone("Warehouse_Aisle_E", new Vector3(61.5f, Ground + 0.9f, 76.9f), new Vector3(0.8f, 1.8f, 1.8f));
        }

        static void CompoundProps(Transform g)
        {
            // Tea lorry parked in the compound: cover, and the body hides you from the yard
            var lorry = Group(g, "Lorry");
            Box("Chassis", lorry, 55.1f, Ground + 0.55f, 47f, 57.3f, Ground + 0.9f, 55.6f, rust);
            Box("Cargo", lorry, 55f, Ground + 0.9f, 47f, 57.4f, Ground + 3.1f, 53.2f, tarp);
            Box("Cab", lorry, 55.1f, Ground + 0.9f, 53.4f, 57.3f, Ground + 2.9f, 55.6f, lorryPaint);
            Box("Windscreen", lorry, 55.3f, Ground + 2.0f, 55.55f, 57.1f, Ground + 2.75f, 55.62f, water, false);
            foreach (var (x, z) in new[] { (55f, 48.2f), (57.4f, 48.2f), (55f, 51.4f), (57.4f, 51.4f), (55f, 54.6f), (57.4f, 54.6f) })
            {
                var w = Cyl("Wheel", lorry, new Vector3(x, Ground + 0.5f, z), 0.5f, 0.35f, rust);
                w.transform.rotation = Quaternion.Euler(0f, 0f, 90f);
            }
            // Water tank tower
            var tank = Group(g, "WaterTower");
            foreach (var (dx, dz) in new[] { (-1.2f, -1.2f), (1.2f, -1.2f), (-1.2f, 1.2f), (1.2f, 1.2f) })
                Box("Leg", tank, 61f + dx - 0.1f, Ground, 47f + dz - 0.1f, 61f + dx + 0.1f, Ground + 4.6f, 47f + dz + 0.1f, rust);
            Cyl("Tank", tank, new Vector3(61f, Ground + 5.6f, 47f), 1.6f, 2f, green, keepCollider: true);
            // Vault obstacles: a low dividing wall and pallet stacks
            var low = Box("Vault_LowWall", g, 54f, Ground, 59.85f, 63.5f, Ground + 0.9f, 60.15f, whitewash);
            low.tag = "Vaultable";
            foreach (var (x, z) in new[] { (59f, 50f), (63.5f, 56f), (69.5f, 42f), (44.5f, 78.2f) })
                Box("Vault_Pallets", g, x, Ground, z, x + 1.2f, Ground + 0.9f, z + 1f, planks).tag = "Vaultable";
            // Firewood for the boiler, against the factory's east wall
            Box("Vault_Firewood", g, 50.2f, Ground, 58f, 51.1f, Ground + 0.95f, 63.5f, bark).tag = "Vaultable";
            // Drums by the fuel shed
            for (int i = 0; i < 3; i++)
                Cyl("OilDrum", g, new Vector3(56.2f + i * 0.62f, Ground + 0.45f, 41.6f), 0.29f, 0.9f, i == 1 ? roofRed : rust, keepCollider: true);
        }

        /// <summary>Collapsed fence panel where the player gets into the premises (gap X 46.5-48.5 in Fence_South).</summary>
        static void FenceBreach(Transform g)
        {
            BoxR("FencePanel_Fallen", g, new Vector3(47.6f, Ground + 0.12f, 38.9f), new Vector3(2f, 0.08f, 1.9f), Quaternion.Euler(-6f, 12f, 0f), green, false);
            var branch = Cyl("Vault_Branch", g, new Vector3(46.2f, Ground + 0.3f, 40.6f), 0.18f, 3.2f, bark, keepCollider: true);
            branch.transform.rotation = Quaternion.Euler(0f, 0f, 84f) * Quaternion.Euler(0f, 30f, 0f);
            branch.tag = "Vaultable";
        }

        static void FactoryHideSpots()
        {
            HideZone("Loft_BehindSacks", new Vector3(27f, LoftY + 0.55f, 74.5f), new Vector3(15f, 1.1f, 1.2f));
            HideZone("RollingFloor_BehindMachines", new Vector3(23.5f, Ground + 0.6f, 64.7f), new Vector3(15f, 1.2f, 1f));
            HideZone("Storeroom_TeaChests", new Vector3(48.5f, Ground + 0.6f, 58.2f), new Vector3(1.6f, 1.2f, 1.2f));
            HideZone("Boiler_BehindBoiler", new Vector3(42f, Ground + 0.9f, 72.2f), new Vector3(3f, 1.8f, 1.2f));
            HideZone("Yard_GuardHutShadow", new Vector3(12.3f, Ground + 0.9f, 51.3f), new Vector3(2.4f, 1.8f, 1f));
            HideZone("Estate_LeafShed_Table", new Vector3(45f, LeafShedFloor() + 0.5f, 22.5f), new Vector3(2.2f, 1f, 1.4f));
        }

        static float LeafShedFloor()
        {
            var t = KirimalaBlockoutBuilder.EstateTerrain();
            return t != null ? H(t, 45f, 23f) : 5f;
        }

        // ------------------------------------------------------------------ interactables (DYNAMIC)

        static int Doors(Transform g)
        {
            int n = 0;
            // Swing door in a wall along X (hinge at x0) or along Z (hinge at z0)
            void SwingX(string name, string label, float z, float x0, float x1, float h, bool locked, Material m, float angle = -95f)
            {
                var d = Pivot(g, name, new Vector3(x0, Ground, z));
                BoxR("Panel", d, new Vector3(x0 + (x1 - x0) * 0.5f, Ground + h * 0.5f, z), new Vector3(x1 - x0 - 0.04f, h - 0.02f, 0.06f), Quaternion.identity, m, true);
                Setup(d, GreyboxKind.SwingDoor, label, locked, Vector3.zero, angle); n++;
            }
            void SwingZ(string name, string label, float x, float z0, float z1, float h, bool locked, Material m, float angle = 95f)
            {
                var d = Pivot(g, name, new Vector3(x, Ground, z0));
                BoxR("Panel", d, new Vector3(x, Ground + h * 0.5f, z0 + (z1 - z0) * 0.5f), new Vector3(0.06f, h - 0.02f, z1 - z0 - 0.04f), Quaternion.identity, m, true);
                Setup(d, GreyboxKind.SwingDoor, label, locked, Vector3.zero, angle); n++;
            }
            void Shutter(string name, string label, Vector3 centre, Vector3 size, Vector3 open, bool locked, Material m)
            {
                var d = Pivot(g, name, centre);
                BoxR("Panel", d, centre, size, Quaternion.identity, m, true);
                Setup(d, GreyboxKind.SlideDoor, label, locked, open, 0f); n++;
            }

            // Factory
            Shutter("Door_Loading", "Loading bay shutter", new Vector3(19f, Ground + 2f, 52f), new Vector3(5.96f, 3.98f, 0.08f), new Vector3(0, 3.6f, 0), true, rust);
            Shutter("Door_Storeroom_Yard", "Storeroom shutter", new Vector3(44f, Ground + 1.5f, 52f), new Vector3(3.96f, 2.98f, 0.08f), new Vector3(0, 2.6f, 0), false, rust);
            SwingZ("Door_Inner_A", "Door", 36f, 59f, 61f, DoorH, false, planks);
            SwingZ("Door_Inner_B", "Door", 36f, 70f, 72f, DoorH, false, planks);
            SwingX("Door_Storeroom_Boiler", "Boiler room door", 62f, 40f, 42f, DoorH, false, rust);
            SwingZ("Door_Storeroom_East", "Side door", 50f, 55f, 57f, DoorH, false, planks, -95f);
            SwingX("Door_Factory_Back", "Back door", 76f, 25.2f, 27f, DoorH, false, planks, 95f);
            SwingX("Door_PedestrianGate", "Pedestrian gate", 40f, 28f, 32f, 2f, true, green);
            Shutter("Door_ServiceGate", "Service gate", new Vector3(68f, Ground + 1.3f, 40f), new Vector3(3.96f, 2.58f, 0.08f), new Vector3(3.9f, 0, 0), true, green);
            // Compound buildings
            SwingX("Door_FuelShed", "Fuel shed door", 44.5f, 53.2f, 54.2f, DoorH, false, rust, 95f);
            for (int i = 0; i < 5; i++)
            {
                float zc = 47f + i * 6f;
                SwingZ($"Door_LineRoom_{i + 1}", $"Room {i + 1} door", 68f, zc - 0.5f, zc + 0.5f, DoorH, i == 1 || i == 3, planks, -95f);
            }
            Shutter("Door_Warehouse_Shutter", "Warehouse shutter", new Vector3(57.5f, Ground + 1.8f, 66f), new Vector3(5.96f, 3.58f, 0.08f), new Vector3(0, 3.2f, 0), true, rust);
            SwingX("Door_Warehouse_Side", "Warehouse door", 66f, 62f, 63f, DoorH, false, rust);
            SwingZ("Door_Warehouse_Alley", "Warehouse alley door", 52f, 70f, 71f, DoorH, false, rust);
            SwingZ("Door_Warehouse_East", "Warehouse east door", 64f, 70.2f, 71.2f, DoorH, true, rust, -95f);

            // The main gate (from the blockout) opens for the escape
            var gate = GameObject.Find("Gate_Main");
            if (gate != null)
            {
                var gi = gate.GetComponent<GreyboxInteractable>() ?? gate.AddComponent<GreyboxInteractable>();
                gi.kind = GreyboxKind.SlideDoor; gi.label = "Main gate"; gi.locked = false; gi.openOffset = new Vector3(0f, 0f, 4.2f);
            }
            return n;
        }

        static int Barricades(Transform g)
        {
            int n = 0;
            void Barricade(string name, Vector3 centre, float width, bool alongX)
            {
                var b = Pivot(g, name, centre);
                for (int i = 0; i < 4; i++)
                {
                    var size = alongX ? new Vector3(width, 0.22f, 0.06f) : new Vector3(0.06f, 0.22f, width);
                    BoxR("Plank", b, centre + new Vector3(0f, -0.75f + i * 0.5f, 0f), size, Quaternion.Euler(alongX ? 0f : (i % 2 == 0 ? 6f : -6f), 0f, alongX ? (i % 2 == 0 ? 6f : -6f) : 0f), planks, false);
                }
                var col = b.gameObject.AddComponent<BoxCollider>();
                col.size = alongX ? new Vector3(width, 2f, 0.3f) : new Vector3(0.3f, 2f, width);
                var obs = b.gameObject.AddComponent<NavMeshObstacle>();   // robots have to go round (or break it)
                obs.shape = NavMeshObstacleShape.Box; obs.size = col.size; obs.carving = true;
                Setup(b, GreyboxKind.Barricade, "Barricade", false, Vector3.zero, 0f);
                n++;
            }
            Barricade("Barricade_Alley", new Vector3(51f, Ground + 1f, 66.4f), 1.95f, true);
            Barricade("Barricade_NorthLane", new Vector3(40f, Ground + 1f, 78f), 3.7f, false);
            Barricade("Barricade_Veranda", new Vector3(67f, Ground + 1.15f, 73.6f), 1.9f, true);
            return n;
        }

        static int TeaBags(Transform g)
        {
            var spots = new[]
            {
                ("TeaBag_1_Warehouse", new Vector3(56.2f, Ground, 70.7f)),
                ("TeaBag_2_Warehouse", new Vector3(62.4f, Ground, 77f)),
                ("TeaBag_3_Loft", new Vector3(23f, LoftY, 72.5f)),
                ("TeaBag_4_Storeroom", new Vector3(38.6f, Ground, 60.6f)),
                ("TeaBag_5_LineRoom3", new Vector3(73.5f, Ground, 61f)),
            };
            foreach (var (name, p) in spots)
            {
                var bag = Pivot(g, name, p);
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                body.name = "Sack";
                body.transform.SetParent(bag, false);
                body.transform.position = p + new Vector3(0f, 0.38f, 0f);
                body.transform.localScale = new Vector3(0.55f, 0.38f, 0.42f);
                body.GetComponent<MeshRenderer>().sharedMaterial = burlap;
                // Bright tag so the objective reads in the dark
                BoxR("Tag", bag, p + new Vector3(0f, 0.55f, -0.22f), new Vector3(0.22f, 0.14f, 0.01f), Quaternion.identity, sackTag, false);
                Setup(bag, GreyboxKind.Pickup, "Tea bag", false, Vector3.zero, 0f);
            }
            return spots.Length;
        }

        static void Lockers(Transform g)
        {
            void Locker(string name, Vector3 floor, float yaw)
            {
                var l = Pivot(g, name, floor);
                l.rotation = Quaternion.Euler(0f, yaw, 0f);
                var body = BoxR("Body", l, floor + l.rotation * new Vector3(0f, 0.97f, 0f), new Vector3(0.6f, 1.94f, 0.5f), l.rotation, green, true);
                var vent = BoxR("Vents", l, floor + l.rotation * new Vector3(0f, 1.6f, 0.252f), new Vector3(0.4f, 0.2f, 0.01f), l.rotation, rust, false);
                var hp = new GameObject("HidePoint").transform;   // where the player's camera goes when hidden
                hp.SetParent(l, false); hp.localPosition = new Vector3(0f, 1.55f, 0f); hp.localRotation = Quaternion.identity;
                l.gameObject.tag = "HideSpot";
                Setup(l, GreyboxKind.HideSpot, "Locker", false, Vector3.zero, 0f);
            }
            // Lockers face +Z by default (vents on the front)
            Locker("Locker_RollingFloor_1", new Vector3(10.5f, Ground, 57.5f), 90f);
            Locker("Locker_RollingFloor_2", new Vector3(10.5f, Ground, 58.2f), 90f);
            Locker("Locker_Storeroom", new Vector3(36.5f, Ground, 55.3f), 90f);
            Locker("Locker_Boiler", new Vector3(49.5f, Ground, 64.5f), -90f);
            for (int i = 0; i < 5; i++) Locker($"Locker_LineRoom_{i + 1}", new Vector3(68.5f, Ground, 44.6f + i * 6f), 0f);
            Locker("Locker_Warehouse", new Vector3(63.5f, Ground, 67f), -90f);
        }

        // ------------------------------------------------------------------ gameplay markers

        static void GameplayMarkers(Transform g, Terrain t)
        {
            // Player start at the jungle camp
            var spawn = GameObject.Find("Spawn_Player");
            var start = PlayerStart();
            if (spawn != null && start.HasValue)
            {
                Undo.RecordObject(spawn.transform, "Move player spawn");
                spawn.transform.position = start.Value;
            }

            var spawns = Group(g, "SecuritySpawns");
            Marker("Spawn_Security_GuardHut", spawns, new Vector3(12.3f, Ground + 0.1f, 49.5f), 6);
            Marker("Spawn_Security_EastCompound", spawns, new Vector3(70f, Ground + 0.1f, 42.5f), 6);
            Marker("Spawn_Security_Warehouse", spawns, new Vector3(58f, Ground + 0.1f, 70.6f), 6);
            Marker("Spawn_Security_NorthLane", spawns, new Vector3(18f, Ground + 0.1f, 78f), 6);
            Marker("Spawn_Security_LineHouses", spawns, new Vector3(67f, Ground + 0.25f, 60f), 6);
            var estate = KirimalaBlockoutBuilder.EstateTerrain();
            float E(float x, float z) => estate != null ? H(estate, x, z) : 4f;
            Marker("Spawn_Sentry_Estate_W", spawns, new Vector3(12f, E(12f, 22f) + 3f, 22f), 5);
            Marker("Spawn_Sentry_Estate_E", spawns, new Vector3(50f, E(50f, 12f) + 3f, 12f), 5);

            // Patrol routes (waypoints in order; the Agent Controller loops or ping-pongs them)
            var routes = Group(g, "PatrolRoutes");
            void Route(string name, Vector3[] pts) { var r = Group(routes, name); for (int i = 0; i < pts.Length; i++) Marker($"WP_{i:00}", r, pts[i], 2); }
            Route("Route_Estate_Drone", new[] { new Vector3(8f, E(8f, 8f) + 3f, 8f), new Vector3(52f, E(52f, 8f) + 3f, 8f), new Vector3(52f, E(52f, 34f) + 3f, 34f), new Vector3(8f, E(8f, 34f) + 3f, 34f) });
            Route("Route_Yard_Compound", new[] { new Vector3(14f, 8.1f, 43f), new Vector3(46f, 8.1f, 43f), new Vector3(62f, 8.1f, 44f), new Vector3(64.5f, 8.1f, 62f), new Vector3(58f, 8.1f, 63f), new Vector3(51f, 8.1f, 63f) });
            Route("Route_Factory", new[] { new Vector3(15f, 8.1f, 53.5f), new Vector3(34f, 8.1f, 53.5f), new Vector3(34f, 8.1f, 74f), new Vector3(16f, 8.1f, 74f) });
            Route("Route_BackLane", new[] { new Vector3(12f, 8.1f, 78f), new Vector3(38f, 8.1f, 78f), new Vector3(51f, 8.1f, 78f), new Vector3(51f, 8.1f, 68f) });

            // Triggers: alarm when the player enters the premises, escape at the main gate
            var trig = Group(g, "Triggers");
            Trigger("Trigger_Alarm_Premises", trig, new Vector3(43f, Ground + 6f, 60f), new Vector3(66f, 14f, 40f));
            Trigger("Trigger_Exit_MainGate", trig, new Vector3(2f, Ground + 1.5f, 46f), new Vector3(4f, 3f, 4f));
            Trigger("Trigger_EstateFence", trig, new Vector3(30f, 1.5f, -0.6f), new Vector3(60f, 4f, 2f));
        }

        internal static (Vector3 centre, Vector3 size)[] BoundWalls() => new[]
        {
            (new Vector3(30f, 10f, -64.5f), new Vector3(102f, 60f, 1f)),    // jungle south
            (new Vector3(-20.5f, 10f, -32f), new Vector3(1f, 60f, 66f)),    // jungle west
            (new Vector3(80.5f, 10f, -32f), new Vector3(1f, 60f, 66f)),     // jungle east
            (new Vector3(-10.5f, 10f, 0.5f), new Vector3(20f, 60f, 1f)),    // jungle north, west of the estate
            (new Vector3(70.5f, 10f, 0.5f), new Vector3(20f, 60f, 1f)),     // jungle north, east of the estate
            (new Vector3(-0.5f, 10f, 40.5f), new Vector3(1f, 60f, 81f)),    // estate + road west
            (new Vector3(60.5f, 10f, 20f), new Vector3(1f, 60f, 40f)),      // estate east
            (new Vector3(5f, 10f, 40.5f), new Vector3(10f, 60f, 1f)),       // estate north-west corner
            (new Vector3(5f, 10f, 43.5f), new Vector3(10f, 60f, 1f)),       // road south edge
            (new Vector3(5f, 10f, 48.5f), new Vector3(10f, 60f, 1f)),       // road north edge
            (new Vector3(76.5f, 10f, 60f), new Vector3(1f, 60f, 41f)),      // compound east
            (new Vector3(38f, 10f, 80.5f), new Vector3(78f, 60f, 1f)),      // compound north
        };

        // ------------------------------------------------------------------ lights & atmosphere

        static void CompoundLights(Transform lights, Transform probes)
        {
            var g = Group(lights, "Compound");
            var sodium = new Color(1f, 0.62f, 0.28f);
            void PoleLamp(string name, float x, float z, float h)
            {
                Cyl("Pole", g, new Vector3(x, Ground + h * 0.5f, z), 0.07f, h, rust, keepCollider: true);
                Lamp(g, name, new Vector3(x, Ground + h - 0.2f, z + 0.6f), 15f, 8f, sodium);
            }
            PoleLamp("Flood_CompoundSouth", 58.5f, 40.6f, 4.6f);
            PoleLamp("Flood_ServiceGate", 71.5f, 40.6f, 4.6f);
            foreach (float z in new[] { 50f, 62f, 71f })
                Lamp(g, $"Flood_Veranda_{z:0}", new Vector3(67f, Ground + 2.9f, z), 9f, 4.5f, sodium);
            Lamp(g, "Flood_WarehouseDoor", new Vector3(57.5f, Ground + 4.3f, 65.5f), 12f, 6f, sodium);
            Lamp(g, "Flood_Warehouse_W", new Vector3(55f, Ground + 5.3f, 72.5f), 11f, 6f, sodium);
            Lamp(g, "Flood_Warehouse_E", new Vector3(61f, Ground + 5.3f, 76f), 11f, 6f, sodium);
            Lamp(g, "Flood_NorthLane_W", new Vector3(24f, Ground + 2.4f, 79.6f), 11f, 5f, sodium);
            Lamp(g, "Flood_NorthLane_E", new Vector3(44f, Ground + 2.4f, 79.6f), 11f, 5f, sodium);
            Lamp(g, "Flood_Alley", new Vector3(51f, Ground + 3f, 76f), 8f, 4f, sodium);

            Probe(probes, "RP_Warehouse", new Vector3(58f, Ground + 3f, 73f), new Vector3(12f, 6f, 14f));
            Probe(probes, "RP_EastCompound", new Vector3(63f, Ground + 3f, 54f), new Vector3(26f, 8f, 28f));
        }

        static void Lamp(Transform parent, string name, Vector3 pos, float range, float intensity, Color color)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point; l.range = range; l.intensity = intensity; l.color = color;
            l.shadows = LightShadows.Soft;
            l.lightmapBakeType = LightmapBakeType.Baked;   // Shadowmask allows 4 Mixed lights; all of these are baked
            var bulb = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            bulb.name = "Bulb";
            Object.DestroyImmediate(bulb.GetComponent<Collider>());
            bulb.transform.SetParent(go.transform, false);
            bulb.transform.localScale = Vector3.one * 0.25f;
            var r = bulb.GetComponent<MeshRenderer>();
            r.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{Env}/Materials/Lighting/M_Bulb_Sodium.mat");
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveGI = ReceiveGI.LightProbes;
        }

        static void Probe(Transform parent, string name, Vector3 c, Vector3 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = c;
            var p = go.AddComponent<ReflectionProbe>();
            p.mode = ReflectionProbeMode.Baked; p.size = size; p.boxProjection = true; p.resolution = 128;
        }

        static void Atmosphere(Transform lighting)
        {
            var a = Fresh(lighting, "Atmosphere_Expansion");
            GameObject Spawn(string prefab, Vector3 pos)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
                if (asset == null) return null;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, a);
                go.transform.position = pos;
                return go;
            }
            void Rain(Vector3 centre, Vector2 size, float density)
            {
                var go = Spawn("Assets/VFX/VFX_Rain.prefab", centre);
                if (go == null) return;
                go.name = "Rain";
                var ps = go.GetComponent<ParticleSystem>();
                var shape = ps.shape; shape.scale = new Vector3(size.x, size.y, 1f);
                float area = size.x * size.y / 900f;
                var em = ps.emission; em.rateOverTimeMultiplier *= area * density;
                var main = ps.main;
                main.maxParticles = Mathf.RoundToInt(main.maxParticles * area * density);
                main.startSizeMultiplier *= 0.3f;
                main.startColor = new Color(0.75f, 0.82f, 0.9f, 0.3f);
            }
            Rain(new Vector3(30f, 14f, -32f), new Vector2(100f, 64f), 1.6f);   // under the canopy: fewer drops reach the floor
            Rain(new Vector3(60f, 13f, 53f), new Vector2(16f, 24f), 3f);       // open part of the east compound
            foreach (var p in new[] { new Vector3(18f, 1.5f, -30f), new Vector3(50f, 1.5f, -38f), new Vector3(36f, 2f, -14f) })
            {
                var f = Spawn("Assets/VFX/VFX_Fireflies.prefab", p);
                if (f != null) f.name = "Fireflies";
            }
            var wind = Spawn("Assets/Audio/Background_Ambient_Wind.prefab", new Vector3(30f, 4f, -32f));
            if (wind != null) wind.name = "Ambience_Jungle";
        }

        // ------------------------------------------------------------------ materials

        static bool LoadMaterials()
        {
            Material S(string n) => AssetDatabase.LoadAssetAtPath<Material>($"{Env}/Materials/Surfaces/{n}.mat");
            Material P(string n) => AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/{n}.mat");
            whitewash = S("M_Env_Whitewash"); roofRed = S("M_Env_RoofRed"); rust = S("M_Env_RustMetal"); green = S("M_Env_PaintedGreen");
            planks = S("M_Env_WoodPlanks"); crate = S("M_Env_WoodCrate"); concrete = S("M_Env_ConcreteFloor"); gravel = S("M_Env_Gravel");
            bark = P("M_Prop_Bark"); burlap = P("M_Prop_Burlap");
            var tea = P("M_Prop_TeaBush");
            rock = AssetDatabase.LoadAssetAtPath<Material>($"{Env}/Materials/M_Rock063.mat");
            if (whitewash == null || tea == null || bark == null || burlap == null || rock == null)
            {
                Debug.LogError("[World Builder] Run Build Kirimala Surfaces, Props and Set Dressing first.");
                return false;
            }
            rock = Variant("M_Prop_JungleRock", rock, new Color(0.42f, 0.46f, 0.38f), 1.2f);
            rock.SetFloat("_Smoothness", 0.3f);
            leaf = Variant("M_Prop_JungleLeaf", tea, new Color(0.42f, 0.58f, 0.36f), 1.4f);
            undergrowth = Variant("M_Prop_Undergrowth", tea, new Color(0.5f, 0.68f, 0.42f), 0.9f);
            bamboo = Variant("M_Prop_Bamboo", bark, new Color(0.62f, 0.64f, 0.36f), 1f);
            tarp = Plain("M_Prop_Tarp", new Color(0.13f, 0.27f, 0.42f), 0.35f);
            lorryPaint = Plain("M_Prop_LorryPaint", new Color(0.12f, 0.28f, 0.5f), 0.45f);
            water = Plain("M_Prop_StreamWater", new Color(0.035f, 0.06f, 0.07f), 0.95f);
            sackTag = Plain("M_Prop_ObjectiveTag", new Color(1f, 0.55f, 0.1f), 0.3f);
            sackTag.EnableKeyword("_EMISSION");
            sackTag.SetColor("_EmissionColor", new Color(1f, 0.5f, 0.1f) * 1.5f);
            sackTag.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(sackTag);
            AssetDatabase.SaveAssets();
            return true;
        }

        static Material Variant(string name, Material source, Color tint, float tile)
        {
            string path = $"{MatFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(source); AssetDatabase.CreateAsset(m, path); }
            else { m.CopyPropertiesFromMaterial(source); m.shaderKeywords = source.shaderKeywords; }
            m.SetColor("_BaseColor", tint);
            m.SetTextureScale("_BaseMap", Vector2.one / tile);
            EditorUtility.SetDirty(m);
            return m;
        }

        static Material Plain(string name, Color color, float smooth)
        {
            string path = $"{MatFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", color);
            m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }

        // ------------------------------------------------------------------ building blocks

        /// <summary>Wall along X at a fixed Z. Holes are (startX, endX, sillHeight, headHeight).</summary>
        static void WallX(Transform parent, string name, float z, float x0, float x1, float y, float h, float t, params Vector4[] holes)
        {
            var w = Group(parent, name);
            float c = x0, ht = t * 0.5f;
            foreach (var o in holes.OrderBy(o => o.x))
            {
                if (o.x > c) Box("Segment", w, c, y, z - ht, o.x, y + h, z + ht, whitewash);
                if (o.z > 0f) Box("Segment", w, o.x, y, z - ht, o.y, y + o.z, z + ht, whitewash);
                if (o.w < h) Box("Lintel", w, o.x, y + o.w, z - ht, o.y, y + h, z + ht, whitewash);
                c = o.y;
            }
            if (c < x1) Box("Segment", w, c, y, z - ht, x1, y + h, z + ht, whitewash);
        }

        /// <summary>Wall along Z at a fixed X. Holes are (startZ, endZ, sillHeight, headHeight).</summary>
        static void WallZ(Transform parent, string name, float x, float z0, float z1, float y, float h, float t, params Vector4[] holes)
        {
            var w = Group(parent, name);
            float c = z0, ht = t * 0.5f;
            foreach (var o in holes.OrderBy(o => o.x))
            {
                if (o.x > c) Box("Segment", w, x - ht, y, c, x + ht, y + h, o.x, whitewash);
                if (o.z > 0f) Box("Segment", w, x - ht, y, o.x, x + ht, y + o.z, o.y, whitewash);
                if (o.w < h) Box("Lintel", w, x - ht, y + o.w, o.x, x + ht, y + h, o.y, whitewash);
                c = o.y;
            }
            if (c < z1) Box("Segment", w, x - ht, y, c, x + ht, y + h, z1, whitewash);
        }

        /// <summary>Axis-aligned box between two corners, with metric UVs (textures never stretch).</summary>
        static GameObject Box(string name, Transform parent, float x0, float y0, float z0, float x1, float y1, float z1, Material mat, bool collider = true)
        {
            var c = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
            var s = new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0));
            return BoxR(name, parent, c, s, Quaternion.identity, mat, collider);
        }

        static GameObject BoxR(string name, Transform parent, Vector3 centre, Vector3 size, Quaternion rot, Material mat, bool collider)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(centre, rot);
            go.transform.localScale = size;
            go.GetComponent<MeshFilter>().sharedMesh = boxes.Get(size);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        static GameObject Cyl(string name, Transform parent, Vector3 centre, float radius, float height, Material mat, bool keepCollider = false)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);   // Unity cylinder: 2 m tall, 1 m wide
            c.name = name;
            if (!keepCollider) Object.DestroyImmediate(c.GetComponent<Collider>());
            c.transform.SetParent(parent, true);
            c.transform.position = centre;
            c.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            c.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return c;
        }

        static GameObject Blob(string name, Transform parent, Mesh mesh, Vector3 pos, Vector3 scale, float yaw, Material mat)
        {
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, true);
            go.transform.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));
            go.transform.localScale = scale;
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Transform Pivot(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.position = pos;
            return t;
        }

        static void Setup(Transform t, GreyboxKind kind, string label, bool locked, Vector3 openOffset, float angle)
        {
            var gi = t.gameObject.AddComponent<GreyboxInteractable>();
            gi.kind = kind; gi.label = label; gi.locked = locked; gi.openOffset = openOffset;
            if (angle != 0f) gi.swingAngle = angle;
        }

        static void HideZone(string name, Vector3 centre, Vector3 size)
        {
            var go = new GameObject("HideZone_" + name, typeof(BoxCollider));
            go.transform.SetParent(hideRoot, false);
            go.transform.position = centre;
            var c = go.GetComponent<BoxCollider>();
            c.size = size; c.isTrigger = true;
            go.layer = 2;              // Ignore Raycast: never blocks shots, lamps or the bake tools
            go.tag = "HideSpot";
            hideCount++;
        }

        static void Trigger(string name, Transform parent, Vector3 centre, Vector3 size)
        {
            var go = new GameObject(name, typeof(BoxCollider));
            go.transform.SetParent(parent, false);
            go.transform.position = centre;
            var c = go.GetComponent<BoxCollider>();
            c.size = size; c.isTrigger = true;
            go.layer = 2;
        }

        static void Marker(string name, Transform parent, Vector3 pos, int icon)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var tex = EditorGUIUtility.IconContent($"sv_icon_dot{icon}_pix16_gizmo").image as Texture2D;
            if (tex != null) EditorGUIUtility.SetIconForObject(go, tex);
        }

        static void EnsureTag(string tag)
        {
            var tm = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            var tags = tm.FindProperty("tags");
            for (int i = 0; i < tags.arraySize; i++) if (tags.GetArrayElementAtIndex(i).stringValue == tag) return;
            tags.InsertArrayElementAtIndex(tags.arraySize);
            tags.GetArrayElementAtIndex(tags.arraySize - 1).stringValue = tag;
            tm.ApplyModifiedPropertiesWithoutUndo();
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>Lumpy ball (foliage, bushes, rocks): a UV sphere pushed in and out by noise, flatter underneath.</summary>
        static Mesh BlobMesh(float seed, int lon, int lat, float flatBottom)
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            for (int j = 0; j <= lat; j++)
            for (int i = 0; i <= lon; i++)
            {
                float th = Mathf.PI * j / lat, ph = 2f * Mathf.PI * (i % lon) / lon;
                var d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                float lump = Mathf.PerlinNoise(d.x * 2.2f + seed, d.z * 2.2f + d.y) * 0.35f + Mathf.PerlinNoise(d.y * 5f + seed, d.x * 5f) * 0.12f;
                var p = d * (0.42f + lump * 0.5f);
                if (p.y < 0f) p.y *= flatBottom;
                v.Add(p); uv.Add(new Vector2(ph * 1.5f, th * 1.5f));
            }
            for (int j = 0; j < lat; j++)
            for (int i = 0; i < lon; i++)
            {
                int a = j * (lon + 1) + i, b = a + 1, c = a + lon + 1, d = c + 1;
                tris.AddRange(new[] { a, b, c, b, d, c });
            }
            var m = new Mesh();
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tris, 0);
            m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(m);
            return m;
        }

        static Mesh MeshFor(string name, System.Func<Mesh> make, bool rebuild = false)
        {
            if (meshes.TryGetValue(name, out var m)) return m;
            var existing = AssetDatabase.LoadAllAssetsAtPath(MeshAsset).OfType<Mesh>().FirstOrDefault(x => x.name == name);
            if (existing != null && !rebuild) { meshes[name] = existing; return existing; }
            m = make(); m.name = name;
            if (existing != null) { EditorUtility.CopySerialized(m, existing); Object.DestroyImmediate(m); m = existing; }
            meshes[name] = m;
            return m;
        }

        static void SaveMeshes()
        {
            Object container = AssetDatabase.LoadMainAssetAtPath(MeshAsset);
            if (container == null)
            {
                var holder = new Mesh { name = "Kirimala_Expansion" };
                AssetDatabase.CreateAsset(holder, MeshAsset);
                container = holder;
            }
            foreach (var m in meshes.Values)
                if (!AssetDatabase.Contains(m)) AssetDatabase.AddObjectToAsset(m, container);
            EditorUtility.SetDirty(container);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ helpers

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static Transform Fresh(Transform parent, string name)
        {
            var old = parent.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Kirimala expansion");
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            EnsureFolder(path.Substring(0, slash));
            AssetDatabase.CreateFolder(path.Substring(0, slash), path.Substring(slash + 1));
        }
    }
}
