using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Blockout.
    ///
    /// Builds the greybox of the agreed level plan (1 unit = 1 m, origin = south-west
    /// corner of the estate, X = east, Z = north):
    ///   Tea estate terrain   X 0-60,  Z 0-40,  rises from Y 0 to Y 8
    ///   Factory yard         X 10-50, Z 40-52, Y 8
    ///   Rolling floor        X 10-36, Z 52-76  (withering loft at Y 12.5)
    ///   Storeroom            X 36-50, Z 52-62
    ///   Boiler room          X 36-50, Z 62-76
    ///   Estate road + gate   X 0-10,  Z 44-48
    ///
    /// Everything is plain cubes with box colliders so the NavMesh and the player work
    /// straight away. This is a starting point: move, resize and replace pieces by hand.
    /// Running it again deletes the previous blockout and rebuilds it.
    /// </summary>
    public static class KirimalaBlockoutBuilder
    {
        const string MaterialFolder = "Assets/_Project/Art/Environment/Materials/Blockout";
        const string TerrainFolder = "Assets/_Project/Art/Environment/Terrain";

        const float Ground = 8f;        // factory and yard floor height
        const float LoftY = 12.5f;      // withering loft floor (+4.5 m)
        const float RoofY = 17f;        // factory roof (+9 m)
        const float Wall = 0.3f;        // wall thickness
        const float DoorH = 2.2f;       // standard door height

        static Material floorMat, wallMat, accentMat, teaMat, coverMat;

        // Footpath through the estate (X, Z), bottom to top
        internal static readonly Vector2[] Path =
        {
            new Vector2(30f, 5f), new Vector2(18.3f, 12.7f), new Vector2(40f, 22.3f),
            new Vector2(30f, 32.7f), new Vector2(30f, 40f)
        };

        [MenuItem("Tools/World Builder/Build Kirimala Blockout", priority = 80)]
        public static void Build()
        {
            if (string.IsNullOrEmpty(UnityEngine.SceneManagement.SceneManager.GetActiveScene().path))
            {
                EditorUtility.DisplayDialog("Kirimala Blockout", "Save the scene first (e.g. Level_01).", "OK");
                return;
            }
            if (!EditorUtility.DisplayDialog("Kirimala Blockout",
                    "Build the greybox level in the open scene? An existing Kirimala blockout will be replaced.",
                    "Build", "Cancel"))
                return;

            LevelBakeTool.CreateHierarchy();
            CreateMaterials();

            var env = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot).transform;
            var dyn = LevelBakeTool.FindRoot(LevelBakeTool.DynamicRoot).transform;
            var gameplay = LevelBakeTool.FindRoot(LevelBakeTool.GameplayRoot).transform;

            var structure = Fresh(Child(env, "Structure"), "Blockout_Kirimala");
            var props = Fresh(Child(env, "Props"), "Blockout_Props");
            var dynamic = Fresh(dyn, "Blockout_Interactables");
            var spawns = Fresh(Child(gameplay, "SpawnPoints"), "Blockout_Spawns");

            var terrain = BuildTerrain(structure);
            BuildYardAndRoad(structure, dynamic);
            BuildFactory(structure, props);
            BuildEstate(props, terrain);
            BuildGameplay(dynamic, spawns, terrain);

            // Re-apply textured surfaces if they have been built before (rebuilding the
            // blockout would otherwise put the grey MB_ materials back).
            KirimalaSurfaceBuilder.ApplyIfBuilt();
            KirimalaPropsBuilder.ApplyIfBuilt();

            EditorSceneManager.MarkSceneDirty(UnityEngine.SceneManagement.SceneManager.GetActiveScene());
            LevelBakeTool.MarkEnvironmentStatic();
            Debug.Log("[World Builder] Kirimala blockout built. Next: Tools > World Builder > 3. Bake NavMesh.");
        }

        // ------------------------------------------------------------------ terrain

        static TerrainData BuildTerrain(Transform parent)
        {
            EnsureFolder(TerrainFolder);
            string path = TerrainFolder + "/Kirimala_Estate.asset";
            AssetDatabase.DeleteAsset(path);

            var data = new TerrainData { heightmapResolution = 129 };
            data.size = new Vector3(60f, 20f, 40f);

            int res = data.heightmapResolution;
            var h = new float[res, res];
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
            {
                float u = xi / (float)(res - 1), v = zi / (float)(res - 1);
                float slope = Mathf.SmoothStep(0f, Ground, v);
                // Gentle rolling bumps; sin() keeps them at zero along both edges so the
                // top edge meets the yard at exactly Y = 8.
                float bumps = Mathf.PerlinNoise(u * 4f + 3.1f, v * 3f + 7.7f) * 1.2f * Mathf.Sin(v * Mathf.PI);
                h[zi, xi] = (slope + bumps) / data.size.y;
            }
            data.SetHeights(0, 0, h);
            AssetDatabase.CreateAsset(data, path);

            var go = Terrain.CreateTerrainGameObject(data);
            go.name = "Terrain_Estate";
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            return data;
        }

        // ------------------------------------------------------------------ yard & road

        static void BuildYardAndRoad(Transform parent, Transform dynamic)
        {
            var yard = Child(parent, "Yard");
            // Solid foundation under the yard and the whole factory (top at Y = 8)
            Box("Foundation", yard, 10, 0, 40, 50, Ground, 76, floorMat);
            Box("EstateRoad", yard, 0, 0, 44, 10, Ground, 48, floorMat);

            // 2 m fence. South side has the pedestrian gate (X 28-32), west side the main gate (Z 44-48).
            WallX("Fence_South", yard, 40f, 10f, 50f, Ground, 2f, 0.2f, new[] { new Vector3(28f, 32f, 2f) });
            WallZ("Fence_West", yard, 10f, 40f, 52f, Ground, 2f, 0.2f, new[] { new Vector3(44f, 48f, 2f) });
            WallZ("Fence_East", yard, 50f, 40f, 52f, Ground, 2f, 0.2f, null);

            // Main gate: closed until all three generators run (Systems Engineer makes it open)
            Box("Gate_Main", dynamic, 9.85f, Ground, 44, 10.15f, Ground + 2.5f, 48, accentMat);

            // A little cover in the yard for the final wave
            Box("Cover_Crate_A", yard, 20, Ground, 45, 22, Ground + 1.2f, 46.5f, coverMat);
            Box("Cover_Crate_B", yard, 38, Ground, 47, 40, Ground + 1.2f, 48.5f, coverMat);
        }

        // ------------------------------------------------------------------ factory

        static void BuildFactory(Transform parent, Transform props)
        {
            var f = Child(parent, "Factory");
            float h = RoofY - Ground;

            // Outer walls. South wall: loading door X 16-22 (4 m tall), storeroom door X 42-46 (3 m tall).
            WallX("Wall_South", f, 52f, 10f, 50f, Ground, h, Wall,
                  new[] { new Vector3(16f, 22f, 4f), new Vector3(42f, 46f, 3f) });
            WallX("Wall_North", f, 76f, 10f, 50f, Ground, h, Wall, null);
            WallZ("Wall_West", f, 10f, 52f, 76f, Ground, h, Wall, null);
            WallZ("Wall_East", f, 50f, 52f, 76f, Ground, h, Wall, null);

            // Inner wall between rolling floor and storeroom/boiler: two doors
            WallZ("Wall_Inner_X36", f, 36f, 52f, 76f, Ground, h, Wall,
                  new[] { new Vector3(59f, 61f, DoorH), new Vector3(70f, 72f, DoorH) });
            // Storeroom / boiler room divider with one door
            WallX("Wall_Inner_Z62", f, 62f, 36f, 50f, Ground, 4.5f, Wall, new[] { new Vector3(40f, 42f, DoorH) });

            // Factory floor slab on top of the foundation, so the inside (concrete) and the
            // yard (gravel) can have different surfaces. 2 cm thick: no step for the player.
            Box("Floor_Factory", f, 10, Ground, 52, 50, Ground + 0.02f, 76, floorMat);

            // Ceilings and roof
            Box("Ceiling_Boiler", f, 36, Ground + 3.5f, 62, 50, Ground + 3.8f, 76, wallMat);   // low and oppressive
            Box("Ceiling_Storeroom", f, 36, LoftY - 0.3f, 52, 50, LoftY, 62, wallMat);
            Box("Roof", f, 10, RoofY, 52, 50, RoofY + 0.3f, 76, wallMat);

            // Withering loft (upper floor) and catwalk
            Box("Loft_Floor", f, 10, LoftY - 0.3f, 66, 36, LoftY, 76, floorMat);
            Box("Catwalk", f, 32, LoftY - 0.2f, 58, 36, LoftY, 66, floorMat);
            Box("Railing_LoftEdge", f, 14, LoftY, 65.95f, 32, LoftY + 1f, 66.05f, wallMat);
            Box("Railing_Catwalk", f, 31.95f, LoftY, 58, 32.05f, LoftY + 1f, 66, wallMat);

            // Stairs (ramps: smooth for the NavMesh and for physics)
            Ramp("Stairs_West", f, 10.2f, 14f, 55f, 66f, Ground, LoftY, floorMat);
            Ramp("Stairs_East", f, 32f, 35.8f, 52.3f, 58f, Ground, LoftY, floorMat);

            // Rolling machines: waist-high cover on the ground floor
            var cover = Child(props, "Factory_Cover");
            Box("RollingMachine_1", cover, 16, Ground, 60, 19, Ground + 1.2f, 64, coverMat);
            Box("RollingMachine_2", cover, 22, Ground, 60, 25, Ground + 1.2f, 64, coverMat);
            Box("RollingMachine_3", cover, 28, Ground, 60, 31, Ground + 1.2f, 64, coverMat);

            // Boiler (big cylinder) and tea-chest stacks in the storeroom
            var boiler = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            boiler.name = "Boiler";
            boiler.transform.SetParent(cover, false);
            boiler.transform.localPosition = new Vector3(42f, Ground + 1.5f, 69f);
            boiler.transform.localScale = new Vector3(3f, 1.5f, 3f);
            boiler.GetComponent<MeshRenderer>().sharedMaterial = coverMat;

            Box("TeaChests_A", cover, 38, Ground, 54, 39.5f, Ground + 2f, 55.5f, coverMat);
            Box("TeaChests_B", cover, 44, Ground, 57, 46, Ground + 1f, 58.5f, coverMat);
            Box("TeaChests_C", cover, 47.5f, Ground, 59, 49.5f, Ground + 1.5f, 61, coverMat);
        }

        // ------------------------------------------------------------------ estate

        static void BuildEstate(Transform props, TerrainData terrain)
        {
            var rows = Child(props, "TeaRows_Placeholder");

            // Rows run across the slope (along the contours), every 3 m, in 4 m segments
            // with 1.2 m gaps. Segments near the footpath or the leaf shed are left out.
            int count = 0;
            for (float z = 4f; z <= 37f; z += 3f)
            for (float x = 2f; x + 4f <= 58f; x += 5.2f)
            {
                var c = new Vector2(x + 2f, z);
                if (DistanceToPath(c) < 2.5f) continue;
                if (c.x > 40f && c.x < 50f && c.y > 18f && c.y < 28f) continue; // leaf shed clearing
                float y = Height(terrain, c.x, c.y);
                Box($"TeaRow_{count++:000}", rows, x, y - 0.4f, z - 0.45f, x + 4f, y + 1.0f, z + 0.45f, teaMat);
            }

            // Leaf collection shed: four posts, a roof and a low wall for cover
            var shed = Child(props, "LeafShed");
            float sy = Height(terrain, 45f, 23f);
            foreach (var p in new[] { new Vector2(42, 20), new Vector2(48, 20), new Vector2(42, 26), new Vector2(48, 26) })
                Box("Post", shed, p.x - 0.1f, Height(terrain, p.x, p.y) - 0.5f, p.y - 0.1f, p.x + 0.1f, sy + 2.6f, p.y + 0.1f, wallMat);
            Box("Roof", shed, 41.5f, sy + 2.6f, 19.5f, 48.5f, sy + 2.8f, 26.5f, wallMat);
            Box("LowWall", shed, 42, Height(terrain, 45f, 26f) - 0.5f, 25.8f, 48, sy + 1.1f, 26.1f, wallMat);
            Box("WeighingTable", shed, 44, sy - 0.5f, 22, 46, sy + 0.9f, 23, coverMat);
        }

        // ------------------------------------------------------------------ gameplay markers

        static void BuildGameplay(Transform dynamic, Transform spawns, TerrainData terrain)
        {
            // Generator placeholders: the Core Developer's model replaces these,
            // the Systems Engineer adds the hold-to-interact script.
            Box("Generator_G1", dynamic, 20.25f, Ground, 56.5f, 21.75f, Ground + 1.2f, 57.5f, accentMat);
            Box("Generator_G2", dynamic, 14.25f, LoftY, 72.5f, 15.75f, LoftY + 1.2f, 73.5f, accentMat);
            Box("Generator_G3", dynamic, 45.25f, Ground, 71.5f, 46.75f, Ground + 1.2f, 72.5f, accentMat);

            Marker("Spawn_Player", spawns, new Vector3(30f, Height(terrain, 30f, 5f) + 0.1f, 5f), 3);
            Marker("Spawn_Enemy_Loft", spawns, new Vector3(12f, LoftY + 0.1f, 74f), 6);
            Marker("Spawn_Enemy_Boiler", spawns, new Vector3(48f, Ground + 0.1f, 64f), 6);
            Marker("Spawn_Enemy_Storeroom", spawns, new Vector3(48f, Ground + 0.1f, 54f), 6);
            Marker("Spawn_Enemy_Yard", spawns, new Vector3(48f, Ground + 0.1f, 42f), 6);
            Marker("Spawn_Enemy_EstateWest", spawns, new Vector3(4f, Height(terrain, 4f, 34f) + 0.1f, 34f), 6);
            Marker("Spawn_Enemy_EstateEast", spawns, new Vector3(56f, Height(terrain, 56f, 30f) + 0.1f, 30f), 6);
        }

        // ------------------------------------------------------------------ building blocks

        /// <summary>Axis-aligned box between two corners (min X/Y/Z, max X/Y/Z), in metres.</summary>
        static GameObject Box(string name, Transform parent, float x0, float y0, float z0,
                              float x1, float y1, float z1, Material mat)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localPosition = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
            go.transform.localScale = new Vector3(Mathf.Abs(x1 - x0), Mathf.Abs(y1 - y0), Mathf.Abs(z1 - z0));
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        /// <summary>Wall running along X at a fixed Z. Each gap is (startX, endX, gapHeight).</summary>
        static void WallX(string name, Transform parent, float z, float x0, float x1,
                          float y, float height, float thick, Vector3[] gaps)
        {
            var wall = Child(parent, name);
            float t = thick * 0.5f, cursor = x0;
            foreach (var g in Sorted(gaps))
            {
                if (g.x > cursor) Box("Segment", wall, cursor, y, z - t, g.x, y + height, z + t, wallMat);
                if (g.z < height) Box("Lintel", wall, g.x, y + g.z, z - t, g.y, y + height, z + t, wallMat);
                cursor = g.y;
            }
            if (cursor < x1) Box("Segment", wall, cursor, y, z - t, x1, y + height, z + t, wallMat);
        }

        /// <summary>Wall running along Z at a fixed X. Each gap is (startZ, endZ, gapHeight).</summary>
        static void WallZ(string name, Transform parent, float x, float z0, float z1,
                          float y, float height, float thick, Vector3[] gaps)
        {
            var wall = Child(parent, name);
            float t = thick * 0.5f, cursor = z0;
            foreach (var g in Sorted(gaps))
            {
                if (g.x > cursor) Box("Segment", wall, x - t, y, cursor, x + t, y + height, g.x, wallMat);
                if (g.z < height) Box("Lintel", wall, x - t, y + g.z, g.x, x + t, y + height, g.y, wallMat);
                cursor = g.y;
            }
            if (cursor < z1) Box("Segment", wall, x - t, y, cursor, x + t, y + height, z1, wallMat);
        }

        /// <summary>Sloped slab rising along +Z from (z0, y0) to (z1, y1).</summary>
        static void Ramp(string name, Transform parent, float x0, float x1, float z0, float z1,
                         float y0, float y1, Material mat)
        {
            const float thick = 0.3f;
            float run = z1 - z0, rise = y1 - y0;
            float length = Mathf.Sqrt(run * run + rise * rise);
            float angle = Mathf.Atan2(rise, run) * Mathf.Rad2Deg;

            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = name;
            go.transform.SetParent(parent, false);
            go.transform.localRotation = Quaternion.Euler(-angle, 0f, 0f); // negative pitch lifts the +Z end
            Vector3 up = go.transform.localRotation * Vector3.up;
            go.transform.localPosition = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f) - up * (thick * 0.5f);
            go.transform.localScale = new Vector3(x1 - x0, thick, length);
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        }

        static void Marker(string name, Transform parent, Vector3 pos, int iconIndex)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var icon = EditorGUIUtility.IconContent($"sv_icon_dot{iconIndex}_pix16_gizmo").image as Texture2D;
            if (icon != null) EditorGUIUtility.SetIconForObject(go, icon);
        }

        // ------------------------------------------------------------------ helpers

        static void CreateMaterials()
        {
            EnsureFolder(MaterialFolder);
            floorMat = Mat("MB_Floor", new Color(0.45f, 0.45f, 0.45f));
            wallMat = Mat("MB_Wall", new Color(0.72f, 0.72f, 0.70f));
            coverMat = Mat("MB_Cover", new Color(0.55f, 0.50f, 0.45f));
            teaMat = Mat("MB_Tea", new Color(0.25f, 0.45f, 0.20f));
            accentMat = Mat("MB_Interactable", new Color(0.95f, 0.50f, 0.10f));
        }

        static Material Mat(string name, Color color)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, path);
            }
            mat.SetColor("_BaseColor", color);
            mat.color = color;
            EditorUtility.SetDirty(mat);
            return mat;
        }

        static float Height(TerrainData t, float x, float z) =>
            t.GetInterpolatedHeight(Mathf.Clamp01(x / t.size.x), Mathf.Clamp01(z / t.size.z));

        internal static float DistanceToPath(Vector2 p)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Path.Length - 1; i++)
            {
                Vector2 a = Path[i], b = Path[i + 1], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        static IEnumerable<Vector3> Sorted(Vector3[] gaps)
        {
            if (gaps == null) yield break;
            var list = new List<Vector3>(gaps);
            list.Sort((a, b) => a.x.CompareTo(b.x));
            foreach (var g in list) yield return g;
        }

        static Transform Child(Transform parent, string name)
        {
            var found = parent.Find(name);
            if (found != null) return found;
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>Deletes an old child with this name (undoable) and creates an empty one.</summary>
        static Transform Fresh(Transform parent, string name)
        {
            var old = parent.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Build Kirimala blockout");
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            int slash = path.LastIndexOf('/');
            string parent = path.Substring(0, slash);
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, path.Substring(slash + 1));
        }
    }
}
