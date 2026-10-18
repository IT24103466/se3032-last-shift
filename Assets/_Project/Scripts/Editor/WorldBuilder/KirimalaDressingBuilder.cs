using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Set Dressing.
    ///
    /// The "make it feel lived in" pass, on top of the blockout, surfaces, lights and props:
    ///  - Lamp hardware: posts and arms for outdoor lamps, drop rods and shades for indoor ones
    ///    (found from the lights themselves, so moving a light moves its fitting next run).
    ///  - Factory: rows of lit windows, a boiler chimney, steam pipes, oil drums, sacks of
    ///    withered leaf on the loft, and a watchman's guard hut by the main gate.
    ///  - Estate: shade trees between the tea rows (as on real Sri Lankan estates).
    ///  - Backdrop: rolling tea hills all around, so the map no longer ends at a cliff.
    ///    Lit by light probes only, so it costs no lightmap space.
    ///  - Bounds: invisible walls at the map edge (players can't walk onto the backdrop).
    ///  - Atmosphere: monsoon rain over the outdoor areas, fireflies over the tea, wind ambience.
    /// Everything is regenerated on each run. Re-bake afterwards (3, 4, 5, 6).
    /// </summary>
    public static class KirimalaDressingBuilder
    {
        const string Env = "Assets/_Project/Art/Environment";
        const string MatFolder = Env + "/Materials/Props";
        const string MeshAsset = Env + "/Meshes/Kirimala_Dressing.asset";
        const float Ground = 8f, LoftY = 12.5f, RoofY = 17f;

        static Material whitewash, roofRed, rust, green, wood, crate, tea, canopy, bark, burlap, window, distant;

        [MenuItem("Tools/World Builder/Build Kirimala Set Dressing", priority = 85)]
        public static void Build()
        {
            var envRoot = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot);
            if (envRoot == null) { Debug.LogError("[World Builder] Build the blockout first."); return; }
            if (!LoadMaterials()) return;

            Physics.SyncTransforms();   // raycasts below need up-to-date colliders
            var env = envRoot.transform;
            var props = env.Find("Props") ?? env;
            var root = Fresh(props, "Kirimala_Dressing");
            var meshes = new Dictionary<string, Mesh>();

            int lamps = LampHardware(root);
            int windows = Windows(Group(root, "Windows"), meshes);
            Chimney(Group(root, "Chimney"));
            Pipes(Group(root, "BoilerPipes"));
            int drums = Drums(Group(root, "OilDrums"));
            int sacks = Sacks(Group(root, "LeafSacks"));
            GuardHut(Group(root, "GuardHut"), meshes);
            int trees = Trees(Group(root, "ShadeTrees"), meshes);
            Backdrop(env, meshes);
            Bounds();
            Atmosphere();
            SaveMeshes(meshes);

            KirimalaSurfaceBuilder.ApplyIfBuilt();     // whitewash/roof on the guard hut
            LevelBakeTool.MarkEnvironmentStatic();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[World Builder] Set dressing: {lamps} lamp fittings, {windows} windows, {drums} drums, {sacks} leaf sacks, " +
                      $"{trees} shade trees, guard hut, chimney, pipes, backdrop hills, rain, fireflies. " +
                      "Next: 3. Bake NavMesh, 4. Light Probes, 5. Bake Lighting, 6. Occlusion.");
        }

        // ------------------------------------------------------------------ materials

        static bool LoadMaterials()
        {
            Material S(string n) => AssetDatabase.LoadAssetAtPath<Material>($"{Env}/Materials/Surfaces/{n}.mat");
            whitewash = S("M_Env_Whitewash"); roofRed = S("M_Env_RoofRed"); rust = S("M_Env_RustMetal");
            green = S("M_Env_PaintedGreen"); wood = S("M_Env_WoodPlanks"); crate = S("M_Env_WoodCrate");
            tea = AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/M_Prop_TeaBush.mat");
            if (whitewash == null || tea == null)
            {
                Debug.LogError("[World Builder] Run Build Kirimala Surfaces and Build Kirimala Props first.");
                return false;
            }
            canopy = Variant("M_Prop_Canopy", tea, new Color(0.8f, 0.95f, 0.72f), 1.2f);       // grevillea foliage
            distant = Variant("M_Prop_DistantTea", tea, new Color(0.75f, 0.9f, 0.7f), 3f);      // tea on far hills
            bark = Variant("M_Prop_Bark", wood, new Color(0.38f, 0.32f, 0.27f), 1f);
            burlap = TextureMaterial("M_Prop_Burlap", "Burlap_Color", BurlapTexture, 0.1f);
            window = TextureMaterial("M_Prop_WindowGlow", "Window_Color", WindowTexture, 0.6f);
            window.EnableKeyword("_EMISSION");
            window.SetTexture("_EmissionMap", window.GetTexture("_BaseMap"));
            // Warm light from inside the factory; glass only (the frame is dark, so it barely emits)
            window.SetColor("_EmissionColor", new Color(1f, 0.62f, 0.3f) * 1.6f);
            window.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(window);
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

        static Material TextureMaterial(string name, string texName, System.Func<Texture2D> make, float smooth)
        {
            string texPath = $"{Env}/Props/{texName}.png";
            if (!File.Exists(texPath))
            {
                var t = make();
                File.WriteAllBytes(texPath, t.EncodeToPNG());
                Object.DestroyImmediate(t);
                AssetDatabase.ImportAsset(texPath);
            }
            string path = $"{MatFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(texPath));
            m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_Smoothness", smooth);
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>Jute sacking: a coarse basket weave.</summary>
        static Texture2D BurlapTexture()
        {
            const int s = 256; var t = new Texture2D(s, s, TextureFormat.RGB24, true);
            var rng = new System.Random(5);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                bool over = ((x / 8) + (y / 8)) % 2 == 0;
                float thread = over ? Mathf.Sin((x % 8) / 8f * Mathf.PI) : Mathf.Sin((y % 8) / 8f * Mathf.PI);
                float v = 0.55f + 0.35f * thread + (float)rng.NextDouble() * 0.12f;
                t.SetPixel(x, y, new Color(0.56f, 0.45f, 0.30f) * v);
            }
            t.Apply(); return t;
        }

        /// <summary>Factory window: dark green frame and glazing bars around warm, slightly dirty glass.</summary>
        static Texture2D WindowTexture()
        {
            const int w = 160, h = 120; var t = new Texture2D(w, h, TextureFormat.RGB24, true);
            var frame = new Color(0.10f, 0.20f, 0.14f);
            var rng = new System.Random(9);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool bar = x < 10 || x >= w - 10 || y < 10 || y >= h - 10 || Mathf.Abs(x - w / 3) < 3 ||
                           Mathf.Abs(x - 2 * w / 3) < 3 || Mathf.Abs(y - h / 2) < 3;
                float dirt = 0.75f + 0.25f * Mathf.PerlinNoise(x * 0.05f, y * 0.05f) + (float)rng.NextDouble() * 0.05f;
                t.SetPixel(x, y, bar ? frame : new Color(0.95f, 0.72f, 0.42f) * dirt);
            }
            t.Apply(); return t;
        }

        // ------------------------------------------------------------------ lamp hardware

        static int LampHardware(Transform root)
        {
            var lightsRoot = LevelBakeTool.FindRoot(LevelBakeTool.LightingRoot);
            if (lightsRoot == null) return 0;
            var g = Group(root, "LampFittings");
            int n = 0;
            foreach (var light in lightsRoot.GetComponentsInChildren<Light>(true))
            {
                if (light.type != LightType.Point || !light.name.StartsWith("Lamp_")) continue;
                Vector3 p = light.transform.position;
                bool outdoor = light.name.StartsWith("Lamp_Yard") || light.name.StartsWith("Lamp_Gate") || light.name.StartsWith("Lamp_Path");

                if (outdoor && Physics.Raycast(p + Vector3.back * 0.6f, Vector3.down, out var hit, 30f))
                {
                    // Pole behind the lamp, an arm out over it, and a shade
                    Vector3 basePt = hit.point + Vector3.down * 0.3f, top = new Vector3(basePt.x, p.y + 0.35f, basePt.z);
                    Cyl("Pole", g, (basePt + top) * 0.5f, 0.07f, top.y - basePt.y, rust, keepCollider: true);
                    Cube("Arm", g, (top + new Vector3(p.x, top.y, p.z)) * 0.5f, new Vector3(0.05f, 0.05f, 0.65f), rust);
                }
                else if (!outdoor && Physics.Raycast(p + Vector3.up * 0.2f, Vector3.up, out var ceil, 10f))
                {
                    // Drop rod from the ceiling
                    float len = ceil.point.y - (p.y + 0.15f);
                    if (len > 0.05f) Cyl("Rod", g, new Vector3(p.x, p.y + 0.15f + len * 0.5f, p.z), 0.015f, len, rust);
                }
                Cyl("Shade", g, p + Vector3.up * 0.17f, 0.26f, 0.08f, green);   // conical shade, simplified
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ factory

        static int Windows(Transform g, Dictionary<string, Mesh> meshes)
        {
            var board = MeshFor(meshes, "Board", BoardMesh);
            int n = 0;
            const float off = 0.15f + 0.03f;
            void W(float x, float y, float z, float yaw)
            {
                var go = new GameObject("Window", typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(g, false);
                go.transform.SetPositionAndRotation(new Vector3(x, y, z), Quaternion.Euler(0, yaw, 0));
                go.transform.localScale = new Vector3(1.6f, 1.2f, 0.06f);
                go.GetComponent<MeshFilter>().sharedMesh = board;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = window;
                r.shadowCastingMode = ShadowCastingMode.Off;
                n++;
            }
            foreach (float y in new[] { Ground + 2.6f, Ground + 6.8f })
            {
                for (float x = 12f; x <= 48f; x += 4f)
                {
                    bool door = (x > 14.5f && x < 23.5f) || (x > 40.5f && x < 47.5f);
                    bool sign = y > Ground + 5f && x > 25.5f && x < 34.5f;
                    if (!door && !sign) W(x, y, 52f - off, 0f);               // south (yard) wall
                    W(x, y, 76f + off, 180f);                                 // north wall
                }
                for (float z = 54f; z <= 74f; z += 4f)
                {
                    W(10f - off, y, z, 90f);                                  // west wall
                    W(50f + off, y, z, -90f);                                 // east wall
                }
            }
            return n;
        }

        static void Chimney(Transform g)
        {
            // Boiler flue: through the boiler-room ceiling and the roof, 10 m above it
            Cyl("Chimney", g, new Vector3(46f, RoofY + 5f, 72f), 0.55f, 10f, rust, keepCollider: true);
            Cyl("Chimney_Cap", g, new Vector3(46f, RoofY + 10.1f, 72f), 0.7f, 0.2f, rust);
            for (int i = 0; i < 3; i++)   // guy-wire anchors ring
                Cyl("Chimney_Band", g, new Vector3(46f, RoofY + 2f + i * 3f, 72f), 0.6f, 0.08f, green);
        }

        static void Pipes(Transform g)
        {
            // Steam main along the boiler-room north wall, and a riser from the boiler
            foreach (float y in new[] { Ground + 2.8f, Ground + 3.2f })
                CylAxis("SteamPipe", g, new Vector3(43f, y, 75.6f), 0.09f, 12.8f, Vector3.right, rust);
            CylAxis("Riser", g, new Vector3(42f, Ground + 3.1f, 69f), 0.12f, 0.6f, Vector3.up, rust);
            CylAxis("Header", g, new Vector3(42f, Ground + 3.25f, 72.3f), 0.11f, 6.6f, Vector3.forward, rust);
            // Pipe from the steam main through the inner wall to the rolling floor dryers
            CylAxis("Feed", g, new Vector3(30f, Ground + 3.2f, 75.6f), 0.09f, 13.2f, Vector3.right, rust);
        }

        static int Drums(Transform g)
        {
            var rng = new System.Random(21);
            Material[] paints = { roofRed, green, rust, rust };
            int n = 0;
            void Cluster(Vector3 c, int count)
            {
                for (int i = 0; i < count; i++)
                {
                    float ang = i * 2.39996f, rad = 0.35f * Mathf.Sqrt(i + 0.5f) * 1.4f;   // sunflower packing
                    Vector3 p = c + new Vector3(Mathf.Cos(ang) * rad, 0.45f, Mathf.Sin(ang) * rad);
                    bool tipped = i == count - 1;
                    var d = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                    d.name = "OilDrum";
                    d.transform.SetParent(g, false);
                    d.transform.position = tipped ? p + new Vector3(0.7f, -0.15f, 0.3f) : p;
                    d.transform.rotation = tipped ? Quaternion.Euler(90f, rng.Next(360), 0f) : Quaternion.Euler(0, rng.Next(360), 0);
                    d.transform.localScale = new Vector3(0.58f, 0.45f, 0.58f);
                    d.GetComponent<MeshRenderer>().sharedMaterial = paints[rng.Next(paints.Length)];
                    n++;
                }
            }
            Cluster(new Vector3(48.3f, Ground, 49.7f), 5);   // yard, by the storeroom door
            Cluster(new Vector3(48.4f, Ground, 74.2f), 4);   // boiler room corner
            Cluster(new Vector3(11.5f, Ground, 53.4f), 3);   // rolling floor corner
            return n;
        }

        static int Sacks(Transform g)
        {
            var rng = new System.Random(4);
            int n = 0;
            for (float x = 19.5f; x <= 34.5f; x += 0.75f)
            {
                int stack = rng.Next(3);                         // 0-2 sacks high
                for (int s = 0; s <= stack; s++)
                {
                    var sack = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                    sack.name = "LeafSack";
                    Object.DestroyImmediate(sack.GetComponent<Collider>());
                    sack.AddComponent<BoxCollider>();            // stable, cheap cover
                    sack.transform.SetParent(g, false);
                    sack.transform.position = new Vector3(x + (float)rng.NextDouble() * 0.1f, LoftY + 0.2f + s * 0.36f, 75.3f - (s % 2) * 0.1f);
                    sack.transform.rotation = Quaternion.Euler(0, (float)rng.NextDouble() * 20 - 10, (float)rng.NextDouble() * 10 - 5);
                    sack.transform.localScale = new Vector3(0.72f, 0.42f, 0.55f);
                    sack.GetComponent<MeshRenderer>().sharedMaterial = burlap;
                    n++;
                }
            }
            return n;
        }

        /// <summary>Watchman's hut inside the main gate. Named like factory walls so the surface pass whitewashes it.</summary>
        static void GuardHut(Transform g, Dictionary<string, Mesh> meshes)
        {
            const float x0 = 11f, x1 = 13.6f, z0 = 48.4f, z1 = 50.6f, t = 0.15f, h = 2.6f;
            var walls = Group(g, "Wall_GuardHut");
            void B(string n, float ax, float ay, float az, float bx, float by, float bz)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                c.name = n; c.transform.SetParent(walls, false);
                c.transform.position = new Vector3((ax + bx) / 2, (ay + by) / 2, (az + bz) / 2);
                c.transform.localScale = new Vector3(bx - ax, by - ay, bz - az);
                c.GetComponent<MeshRenderer>().sharedMaterial = whitewash;
            }
            B("Segment", x0, Ground, z0, x1, Ground + h, z0 + t);           // south
            B("Segment", x0, Ground, z1 - t, x1, Ground + h, z1);           // north
            B("Segment", x0, Ground, z0, x0 + t, Ground + h, z1);           // west
            B("Segment", x1 - t, Ground, z0, x1, Ground + h, 49.0f);        // east, door 49.0-49.9 faces the yard
            B("Segment", x1 - t, Ground, 49.9f, x1, Ground + h, z1);
            B("Lintel", x1 - t, Ground + 2.1f, 49.0f, x1, Ground + h, 49.9f);
            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "Roof"; roof.transform.SetParent(g, false);
            roof.transform.position = new Vector3((x0 + x1) / 2, Ground + h + 0.08f, (z0 + z1) / 2);
            roof.transform.localScale = new Vector3(x1 - x0 + 0.5f, 0.16f, z1 - z0 + 0.5f);
            roof.GetComponent<MeshRenderer>().sharedMaterial = roofRed;

            var board = MeshFor(meshes, "Board", BoardMesh);
            var win = new GameObject("Window", typeof(MeshFilter), typeof(MeshRenderer));
            win.transform.SetParent(g, false);
            win.transform.SetPositionAndRotation(new Vector3(12.3f, Ground + 1.5f, z0 - 0.02f), Quaternion.identity);
            win.transform.localScale = new Vector3(0.9f, 0.7f, 0.04f);
            win.GetComponent<MeshFilter>().sharedMesh = board;
            win.GetComponent<MeshRenderer>().sharedMaterial = window;
        }

        // ------------------------------------------------------------------ estate

        static int Trees(Transform g, Dictionary<string, Mesh> meshes)
        {
            var t = KirimalaBlockoutBuilder.EstateTerrain();
            if (t == null) return 0;
            Vector2[] spots = { new Vector2(6, 5.5f), new Vector2(14, 11.5f), new Vector2(25, 8.5f), new Vector2(48, 8.5f),
                                new Vector2(55, 14.5f), new Vector2(8, 20.5f), new Vector2(22, 23.5f), new Vector2(53, 26.5f),
                                new Vector2(12, 32.5f), new Vector2(40, 32.5f), new Vector2(51, 35.5f), new Vector2(3, 35.5f),
                                new Vector2(36, 14.5f), new Vector2(18, 29.5f) };
            var crowns = Enumerable.Range(0, 3).Select(i => MeshFor(meshes, $"Crown_{i}", () => BlobMesh(31f * (i + 1)))).ToArray();
            int n = 0;
            foreach (var s in spots)
            {
                if (KirimalaBlockoutBuilder.DistanceToPath(s) < 3.5f) continue;
                if (s.x > 40 && s.x < 50 && s.y > 18 && s.y < 28) continue;   // leaf shed clearing
                float y = t.SampleHeight(new Vector3(s.x, 0, s.y)) + t.transform.position.y;
                float height = 5.2f + (n % 3) * 0.7f;           // grevillea: tall trunk, crown above head height
                var rng = new System.Random(n * 7919);
                var tree = new GameObject("ShadeTree").transform;
                tree.SetParent(g, false);
                tree.position = new Vector3(s.x, y, s.y);
                Cyl("Trunk", tree, new Vector3(s.x, y + height * 0.5f - 0.3f, s.y), 0.2f, height, bark, keepCollider: true);

                // Crown: one big clump and two smaller ones around it, so it reads as foliage, not a disc
                void Clump(Vector3 offset, Vector3 scale, int k)
                {
                    var crown = new GameObject("Crown", typeof(MeshFilter), typeof(MeshRenderer));
                    crown.transform.SetParent(tree, false);
                    crown.transform.position = new Vector3(s.x, y + height, s.y) + offset;
                    crown.transform.rotation = Quaternion.Euler(0, rng.Next(360), 0);
                    crown.transform.localScale = scale;
                    crown.GetComponent<MeshFilter>().sharedMesh = crowns[(n + k) % 3];
                    crown.GetComponent<MeshRenderer>().sharedMaterial = canopy;
                }
                Clump(new Vector3(0, 0.9f, 0), new Vector3(5f, 3.6f, 5f), 0);
                float a = rng.Next(360) * Mathf.Deg2Rad;
                Clump(new Vector3(Mathf.Cos(a) * 1.9f, 0.1f, Mathf.Sin(a) * 1.9f), new Vector3(3.4f, 2.6f, 3.4f), 1);
                Clump(new Vector3(-Mathf.Cos(a + 0.8f) * 1.7f, 1.6f, -Mathf.Sin(a + 0.8f) * 1.7f), new Vector3(3f, 2.4f, 3f), 2);
                n++;
            }
            return n;
        }

        /// <summary>
        /// Rolling tea hills around the playable area, out to 200 m. Height follows the edge of the
        /// level (so there is no step) and rises with distance; under the level it drops out of sight.
        /// </summary>
        static void Backdrop(Transform env, Dictionary<string, Mesh> meshes)
        {
            var estate = KirimalaBlockoutBuilder.EstateTerrain();
            var jungle = KirimalaExpansionBuilder.JungleTerrain();
            float Sample(Terrain t, float x, float z) => t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y;

            // Playable areas: the hills sink 3 m under these and rise from their edges
            var areas = new List<Rect> { Rect.MinMaxRect(0, 0, 60, 40), Rect.MinMaxRect(10, 39.5f, 50, 76), KirimalaExpansionBuilder.RoadArea };
            if (jungle != null) { areas.Add(KirimalaExpansionBuilder.JungleArea); areas.Add(KirimalaExpansionBuilder.CompoundArea); }
            float Surface(float x, float z)
            {
                if (jungle != null && z < 0f) return Sample(jungle, Mathf.Clamp(x, -20f, 80f), Mathf.Max(z, -64f));
                if (z <= 40f && x >= 0f && x <= 60f && estate != null) return Sample(estate, x, Mathf.Max(z, 0f));
                return Ground;
            }

            var mesh = MeshFor(meshes, "Backdrop_Hills", () =>
            {
                const float half = 200f, step = 4.5f;   // ~15.8k triangles: under the audit limit, fog hides the facets
                int n = Mathf.RoundToInt(2 * half / step);
                var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
                for (int j = 0; j <= n; j++)
                for (int i = 0; i <= n; i++)
                {
                    float x = 30f - half + i * step, z = 8f - half + j * step;
                    // Nearest playable area: inside it the hills drop out of sight, outside they rise with distance
                    float d = float.MaxValue; Vector2 c = Vector2.zero; bool inside = false;
                    foreach (var a in areas)
                    {
                        var p = new Vector2(Mathf.Clamp(x, a.xMin, a.xMax), Mathf.Clamp(z, a.yMin, a.yMax));
                        float dist = Vector2.Distance(new Vector2(x, z), p);
                        if (x > a.xMin + 0.5f && x < a.xMax - 0.5f && z > a.yMin + 0.5f && z < a.yMax - 0.5f) inside = true;
                        if (dist < d) { d = dist; c = p; }
                    }
                    float h;
                    if (inside) h = Surface(x, z) - 3f;
                    else
                    {
                        float rise = (1f - Mathf.Exp(-d / 35f)) * 32f;
                        float hills = Mathf.PerlinNoise(x * 0.012f + 3f, z * 0.012f + 9f) * 1.6f - 0.3f;
                        float ripple = (Mathf.PerlinNoise(x * 0.05f, z * 0.05f) - 0.5f) * 3f * Mathf.Clamp01(d / 20f);
                        h = Surface(c.x, c.y) - 0.05f + rise * Mathf.Max(0.2f, hills) + ripple;
                    }
                    v.Add(new Vector3(x, h, z)); uv.Add(new Vector2(x, z));
                }
                for (int j = 0; j < n; j++)
                for (int i = 0; i < n; i++)
                {
                    int a = j * (n + 1) + i, b = a + 1, c = a + n + 1, d2 = c + 1;
                    tris.AddRange(new[] { a, c, b, b, c, d2 });
                }
                var m = new Mesh { indexFormat = IndexFormat.UInt32 };
                m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tris, 0);
                m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
                return m;
            });
            var old = env.Find("Backdrop_Hills");
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var go = new GameObject("Backdrop_Hills", typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(go, "Backdrop");
            go.transform.SetParent(env, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = distant;
            r.shadowCastingMode = ShadowCastingMode.Off;   // fog hides it; no shadow cost
        }

        static void Bounds()
        {
            var gameplay = LevelBakeTool.FindRoot(LevelBakeTool.GameplayRoot);
            if (gameplay == null) return;
            var b = Fresh(gameplay.transform, "Kirimala_Bounds");
            void Wall(Vector3 c, Vector3 s)
            {
                var w = new GameObject("InvisibleWall", typeof(BoxCollider));
                w.transform.SetParent(b, false);
                w.transform.position = c;
                w.GetComponent<BoxCollider>().size = s;
            }
            if (KirimalaExpansionBuilder.JungleTerrain() != null)
            {
                foreach (var (c, size) in KirimalaExpansionBuilder.BoundWalls()) Wall(c, size);
                return;
            }
            Wall(new Vector3(30, 15, -0.5f), new Vector3(62, 40, 1));
            Wall(new Vector3(30, 15, 76.5f), new Vector3(62, 40, 1));
            Wall(new Vector3(-0.5f, 15, 38), new Vector3(1, 40, 78));
            Wall(new Vector3(60.5f, 15, 38), new Vector3(1, 40, 78));
        }

        /// <summary>Called by the expansion builder: the hills and bounds follow the new playable areas.</summary>
        internal static void RebuildBackdropAndBounds()
        {
            var envRoot = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot);
            if (envRoot == null || !LoadMaterials()) return;
            var meshes = new Dictionary<string, Mesh>();
            Backdrop(envRoot.transform, meshes);
            Bounds();
            SaveMeshes(meshes);
        }

        static void Atmosphere()
        {
            var lighting = LevelBakeTool.FindRoot(LevelBakeTool.LightingRoot);
            if (lighting == null) return;
            var a = Fresh(lighting.transform, "Atmosphere");

            GameObject Spawn(string prefab, Vector3 pos)
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>(prefab);
                if (asset == null) return null;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(asset, a);
                go.transform.position = pos;
                return go;
            }
            // Monsoon rain only over the open areas (particles don't stop at roofs)
            void Rain(Vector3 centre, Vector2 size)
            {
                var go = Spawn("Assets/VFX/VFX_Rain.prefab", centre);
                if (go == null) return;
                go.name = "Rain";
                var ps = go.GetComponent<ParticleSystem>();
                var shape = ps.shape; shape.scale = new Vector3(size.x, size.y, 1f);   // emitter box is rotated: local Y = world -Z
                float areaFactor = size.x * size.y / 900f;
                // The template's drops are big cartoon teardrops: make them small, faint and many
                // so they read as fine monsoon rain streaking through the lamp light.
                var em = ps.emission; em.rateOverTimeMultiplier *= areaFactor * 3f;
                var main = ps.main;
                main.maxParticles = Mathf.RoundToInt(main.maxParticles * areaFactor * 3f);
                main.startSizeMultiplier *= 0.3f;
                main.startColor = new Color(0.75f, 0.82f, 0.9f, 0.35f);
            }
            Rain(new Vector3(30f, 4f, 20f), new Vector2(64f, 44f));
            Rain(new Vector3(30f, 12f, 46f), new Vector2(42f, 12f));

            foreach (var p in new[] { new Vector3(15f, 5f, 15f), new Vector3(42f, 6f, 28f) })
            {
                var f = Spawn("Assets/VFX/VFX_Fireflies.prefab", p);
                if (f != null) f.name = "Fireflies";
            }
            var wind = Spawn("Assets/Audio/Background_Ambient_Wind.prefab", new Vector3(30f, 10f, 40f));
            if (wind != null) wind.name = "Ambience_Wind";
        }

        // ------------------------------------------------------------------ meshes

        /// <summary>Lumpy ball for a tree crown: a UV sphere pushed in and out by noise, flatter underneath.</summary>
        static Mesh BlobMesh(float seed)
        {
            const int lon = 24, lat = 14;
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            for (int j = 0; j <= lat; j++)
            for (int i = 0; i <= lon; i++)
            {
                float th = Mathf.PI * j / lat, ph = 2 * Mathf.PI * (i % lon) / lon;
                var d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                float lump = Mathf.PerlinNoise(d.x * 2.2f + seed, d.z * 2.2f + d.y) * 0.35f +
                             Mathf.PerlinNoise(d.y * 5f + seed, d.x * 5f) * 0.12f;
                var p = d * (0.42f + lump * 0.5f);
                if (p.y < 0) p.y *= 0.55f;                       // flat-bottomed crown
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

        /// <summary>Thin box showing the whole texture on its -Z face (same layout as the signs).</summary>
        static Mesh BoardMesh()
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            foreach (var n in new[] { Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.up, Vector3.down })
            {
                bool side = Mathf.Abs(n.y) < 0.5f;
                Vector3 u = side ? Vector3.Cross(Vector3.up, -n) : Vector3.right;
                Vector3 w = side ? Vector3.up : Vector3.forward;
                Vector3 c = n * 0.5f;
                Vector3[] q = { c - u * 0.5f - w * 0.5f, c - u * 0.5f + w * 0.5f, c + u * 0.5f + w * 0.5f, c + u * 0.5f - w * 0.5f };
                int b = v.Count;
                foreach (var p in q)
                    uv.Add(n == Vector3.back ? new Vector2(Vector3.Dot(p, u) + 0.5f, Vector3.Dot(p, w) + 0.5f) : new Vector2(0.02f, 0.02f));
                v.AddRange(q);
                bool front = Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), n) > 0f;
                if (front) tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            var m = new Mesh();
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tris, 0);
            m.RecalculateNormals(); m.RecalculateTangents(); m.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(m);
            return m;
        }

        static Mesh MeshFor(Dictionary<string, Mesh> cache, string name, System.Func<Mesh> make)
        {
            if (cache.TryGetValue(name, out var m)) return m;
            var existing = AssetDatabase.LoadAllAssetsAtPath(MeshAsset).OfType<Mesh>().FirstOrDefault(x => x.name == name);
            if (existing != null && name != "Backdrop_Hills") { cache[name] = existing; return existing; }
            m = make(); m.name = name;
            if (existing != null) { EditorUtility.CopySerialized(m, existing); Object.DestroyImmediate(m); m = existing; }
            cache[name] = m;
            return m;
        }

        static void SaveMeshes(Dictionary<string, Mesh> meshes)
        {
            Object container = AssetDatabase.LoadMainAssetAtPath(MeshAsset);
            if (container == null)
            {
                var holder = new Mesh { name = "Kirimala_Dressing" };
                AssetDatabase.CreateAsset(holder, MeshAsset);
                container = holder;
            }
            foreach (var m in meshes.Values)
                if (!AssetDatabase.Contains(m)) AssetDatabase.AddObjectToAsset(m, container);
            EditorUtility.SetDirty(container);
            AssetDatabase.SaveAssets();
        }

        // ------------------------------------------------------------------ building blocks

        static GameObject Cyl(string name, Transform parent, Vector3 centre, float radius, float height, Material mat, bool keepCollider = false)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cylinder);   // Unity cylinder: 2 m tall, 1 m wide
            c.name = name;
            if (!keepCollider) Object.DestroyImmediate(c.GetComponent<Collider>());
            c.transform.SetParent(parent, false);
            c.transform.position = centre;
            c.transform.localScale = new Vector3(radius * 2f, height * 0.5f, radius * 2f);
            c.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return c;
        }

        static void CylAxis(string name, Transform parent, Vector3 centre, float radius, float length, Vector3 axis, Material mat)
        {
            var c = Cyl(name, parent, centre, radius, length, mat);
            c.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
        }

        static void Cube(string name, Transform parent, Vector3 centre, Vector3 size, Material mat)
        {
            var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
            c.name = name;
            Object.DestroyImmediate(c.GetComponent<Collider>());
            c.transform.SetParent(parent, false);
            c.transform.position = centre;
            c.transform.localScale = size;
            c.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

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
            Undo.RegisterCreatedObjectUndo(go, "Set dressing");
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
