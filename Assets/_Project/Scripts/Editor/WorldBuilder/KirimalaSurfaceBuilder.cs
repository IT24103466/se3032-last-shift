using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Surfaces.
    ///
    /// Turns the grey blockout into the art direction using the ambientCG texture sets in
    /// Assets/_Project/Art/Environment/Textures/[Set]/:
    ///
    ///  1. Base materials: one URP/Lit material per texture set (UrpMaterialBuilder packs
    ///     metal + AO + smoothness into one texture).
    ///  2. Surface materials (M_Env_*): copies of the base materials with a tint and a tile
    ///     size in metres, e.g. the same corrugated steel becomes the red roof and rusty metal.
    ///  3. Metric UVs: a Unity cube has 0-1 UVs on every face, so a texture is stretched over
    ///     a 40 m wall exactly as over a 1 m crate. Each blockout box gets a box mesh whose UVs
    ///     are in metres (one shared mesh per box size, stored in Meshes/Kirimala_BlockoutBoxes.asset),
    ///     so every surface tiles at the same real-world scale. Lightmap UVs are generated too.
    ///  4. Assigns surfaces by object name (whitewashed walls, green-painted machines,
    ///     railings and fence, red corrugated roof, concrete factory floor, wooden loft,
    ///     gravel yard, rusty boiler, wooden tea chests).
    ///  5. Paints the estate terrain: red laterite soil, mossy rock on slopes steeper than ~20°,
    ///     gravel along the footpath and at the leaf shed.
    ///
    /// Generators and the main gate keep the orange interactable placeholder material: those
    /// belong to teammates. The blockout builder calls ApplyIfBuilt(), so rebuilding the
    /// blockout keeps the surfaces. Re-bake lighting afterwards (step 5).
    /// </summary>
    public static class KirimalaSurfaceBuilder
    {
        const string TextureRoot = "Assets/_Project/Art/Environment/Textures";
        const string MaterialFolder = "Assets/_Project/Art/Environment/Materials/Surfaces";
        const string MeshAsset = "Assets/_Project/Art/Environment/Meshes/Kirimala_BlockoutBoxes.asset";
        const string TerrainFolder = "Assets/_Project/Art/Environment/Terrain";

        // Texture sets (folder names under TextureRoot)
        const string Plaster = "PaintedPlaster013_Whitewash";
        const string Steel = "CorrugatedSteel009";
        const string Concrete = "Concrete047A";
        const string Gravel = "Ground108";
        const string Soil = "Ground111";
        const string Rock = "Rock063";
        const string Wood = "WoodFloor064";

        enum Finish { AsScanned, Painted }   // Painted: drop the metal map (paint and rust are not bare metal)

        class Surface
        {
            public string name, set; public Color tint; public float tile; public Finish finish; public float smooth = 1f;
            public float bump = 1f, occlusion = 1f;   // how strongly the normal and AO maps show
            public Surface(string name, string set, Color tint, float tile, Finish finish = Finish.AsScanned, float smooth = 1f)
            { this.name = name; this.set = set; this.tint = tint; this.tile = tile; this.finish = finish; this.smooth = smooth; }
        }

        // Lime wash over old plaster: the scanned peeling is toned down (normal and AO) so it
        // reads as an aged wall from across the yard instead of a repeating camouflage pattern.
        static readonly Surface Whitewash = new Surface("M_Env_Whitewash", Plaster, Color.white, 2.5f) { bump = 0.35f, occlusion = 0.3f };
        // Green-painted steel: tea-factory machinery, railings and the estate fence
        static readonly Surface PaintedGreen = new Surface("M_Env_PaintedGreen", Steel, new Color(0.20f, 0.42f, 0.28f), 2f, Finish.Painted, 0.4f);
        static readonly Surface RoofRed   = new Surface("M_Env_RoofRed", Steel, new Color(0.62f, 0.16f, 0.10f), 2f, Finish.Painted, 0.45f);
        static readonly Surface RustMetal = new Surface("M_Env_RustMetal", Steel, new Color(0.55f, 0.34f, 0.22f), 2f, Finish.Painted, 0.25f);
        static readonly Surface FloorConcrete = new Surface("M_Env_ConcreteFloor", Concrete, Color.white, 3f);
        static readonly Surface YardGravel = new Surface("M_Env_Gravel", Gravel, Color.white, 3f);
        static readonly Surface WoodPlanks = new Surface("M_Env_WoodPlanks", Wood, new Color(0.85f, 0.85f, 0.85f), 2f);
        static readonly Surface WoodCrate  = new Surface("M_Env_WoodCrate", Wood, new Color(1.0f, 0.88f, 0.72f), 1f);

        static readonly Surface[] All = { Whitewash, PaintedGreen, RoofRed, RustMetal, FloorConcrete, YardGravel, WoodPlanks, WoodCrate };

        [MenuItem("Tools/World Builder/Build Kirimala Surfaces", priority = 83)]
        public static void Build()
        {
            if (LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot) == null)
            {
                Debug.LogError("[World Builder] Build the blockout first (Tools > World Builder > Build Kirimala Blockout).");
                return;
            }

            var baseMats = new Dictionary<string, Material>();
            foreach (string set in new[] { Plaster, Steel, Concrete, Gravel, Soil, Rock, Wood })
            {
                string folder = $"{TextureRoot}/{set}";
                if (!AssetDatabase.IsValidFolder(folder))
                {
                    Debug.LogError($"[World Builder] Texture set folder missing: {folder}");
                    return;
                }
                baseMats[set] = UrpMaterialBuilder.BuildSetFromFolder(folder);
            }

            EnsureFolder(MaterialFolder);
            foreach (var s in All) MakeSurface(s, baseMats[s.set]);
            MakeTerrainLayers(baseMats);
            AssetDatabase.SaveAssets();

            Apply();
        }

        /// <summary>Box meshes with metric UVs, shared with the other builders (call Save() when done).</summary>
        internal static MeshCache BoxCache() => new MeshCache(MeshAsset);

        /// <summary>Called by the blockout builder: re-applies surfaces only if they exist already.</summary>
        public static void ApplyIfBuilt()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{Whitewash.name}.mat") != null) Apply();
        }

        static void Apply()
        {
            var env = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot);
            if (env == null) return;

            var mats = All.ToDictionary(s => s, s => AssetDatabase.LoadAssetAtPath<Material>($"{MaterialFolder}/{s.name}.mat"));
            var meshes = new MeshCache(MeshAsset);
            int count = 0;

            foreach (var r in env.GetComponentsInChildren<MeshRenderer>(true))
            {
                var surface = Pick(r.transform);
                // Anything else already using one of the surfaces still gets metric-UV boxes
                if (surface == null) surface = mats.FirstOrDefault(kv => kv.Value != null && kv.Value == r.sharedMaterial).Key;
                if (surface == null || mats[surface] == null) continue;

                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null &&
                    (mf.sharedMesh.name == "Cube" || mf.sharedMesh.name.StartsWith("KBox_")))
                {
                    Undo.RecordObject(mf, "Metric UV box");
                    mf.sharedMesh = meshes.Get(r.transform.localScale);
                }
                Undo.RecordObject(r, "Assign surface");
                r.sharedMaterial = mats[surface];
                count++;
            }
            meshes.Save();

            int painted = PaintTerrain();
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[World Builder] Surfaces applied to {count} objects, {meshes.Count} box meshes with metric UVs, " +
                      $"terrain painted ({painted} layers). Next: 5. Bake Lighting.");
        }

        // ------------------------------------------------------------------ which surface goes where

        static Surface Pick(Transform t)
        {
            string n = t.name, parent = t.parent != null ? t.parent.name : "";

            if (n == "Foundation" || n == "EstateRoad") return YardGravel;
            if (n == "Floor_Factory" || n == "Ceiling_Boiler") return FloorConcrete;
            if (n == "Roof") return RoofRed;                                   // factory and leaf shed
            if (n == "Loft_Floor" || n == "Catwalk" || n == "Ceiling_Storeroom" || n == "Post" ||
                n.StartsWith("Stairs_")) return WoodPlanks;
            if (n.StartsWith("Railing_") || n.StartsWith("RollingMachine_")) return PaintedGreen;
            if (n == "Boiler") return RustMetal;
            if (n.StartsWith("TeaChests_") || n.StartsWith("Cover_Crate") || n == "WeighingTable") return WoodCrate;
            if (n == "LowWall") return Whitewash;

            if (n == "Segment" || n == "Lintel")
            {
                if (parent.StartsWith("Fence_")) return PaintedGreen;
                if (parent.StartsWith("Wall_")) return Whitewash; // lintels run up to the roof, so they are wall too
            }
            return null; // tea rows, generators, gate: leave as they are
        }

        // ------------------------------------------------------------------ materials

        static void MakeSurface(Surface s, Material source)
        {
            string path = $"{MaterialFolder}/{s.name}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null)
            {
                mat = new Material(source);
                AssetDatabase.CreateAsset(mat, path);
            }
            else mat.CopyPropertiesFromMaterial(source);
            mat.shaderKeywords = source.shaderKeywords;

            mat.SetColor("_BaseColor", s.tint);
            // Mesh UVs are in metres, so tiling = 1 / tile size. URP/Lit uses the base map's
            // tiling for every map (normal, mask), so one value scales the whole set.
            mat.SetTextureScale("_BaseMap", Vector2.one / s.tile);
            mat.SetFloat("_BumpScale", s.bump);
            mat.SetFloat("_OcclusionStrength", s.occlusion);

            if (s.finish == Finish.Painted)
            {
                // Keep the packed texture as the occlusion map (G channel), but stop it driving
                // metal/smoothness: paint and rust are dielectric and fairly rough.
                mat.SetTexture("_MetallicGlossMap", null);
                mat.DisableKeyword("_METALLICSPECGLOSSMAP");
                mat.SetFloat("_Metallic", 0.1f);
                mat.SetFloat("_Smoothness", s.smooth);
            }
            else mat.SetFloat("_Smoothness", s.smooth);

            EditorUtility.SetDirty(mat);
        }

        static TerrainLayer[] layers;

        static void MakeTerrainLayers(Dictionary<string, Material> baseMats)
        {
            layers = new[]
            {
                Layer("TL_Soil", baseMats[Soil], 4f),     // red laterite between the tea rows
                Layer("TL_Rock", baseMats[Rock], 5f),     // mossy rock on steep banks
                Layer("TL_Path", baseMats[Gravel], 3f),   // trodden footpath
            };
        }

        static TerrainLayer Layer(string name, Material m, float tile)
        {
            string path = $"{TerrainFolder}/{name}.terrainlayer";
            var l = AssetDatabase.LoadAssetAtPath<TerrainLayer>(path);
            if (l == null)
            {
                l = new TerrainLayer();
                AssetDatabase.CreateAsset(l, path);
            }
            l.diffuseTexture = m.GetTexture("_BaseMap") as Texture2D;
            l.normalMapTexture = m.GetTexture("_BumpMap") as Texture2D;
            // URP terrain mask map = R metal, G AO, B height, A smoothness: the packed
            // MetalAOSmooth texture already uses that layout.
            l.maskMapTexture = m.GetTexture("_MetallicGlossMap") as Texture2D;
            l.tileSize = new Vector2(tile, tile);
            l.normalScale = 1f;
            EditorUtility.SetDirty(l);
            return l;
        }

        static int PaintTerrain()
        {
            var terrain = KirimalaBlockoutBuilder.EstateTerrain();
            if (terrain == null) return 0;
            if (layers == null)
            {
                layers = new[] { "TL_Soil", "TL_Rock", "TL_Path" }
                    .Select(n => AssetDatabase.LoadAssetAtPath<TerrainLayer>($"{TerrainFolder}/{n}.terrainlayer")).ToArray();
                if (layers.Any(l => l == null)) return 0;
            }

            var d = terrain.terrainData;
            Undo.RegisterCompleteObjectUndo(d, "Paint terrain");
            d.terrainLayers = layers;

            int res = d.alphamapResolution;
            var map = new float[res, res, 3];
            Vector3 origin = terrain.transform.position;
            for (int zi = 0; zi < res; zi++)
            for (int xi = 0; xi < res; xi++)
            {
                float u = xi / (float)(res - 1), v = zi / (float)(res - 1);
                var world = new Vector2(origin.x + u * d.size.x, origin.z + v * d.size.z);

                float rock = Smooth(16f, 28f, d.GetSteepness(u, v));
                float path = 1f - Smooth(1.0f, 1.8f, KirimalaBlockoutBuilder.DistanceToPath(world));
                if (world.x > 41f && world.x < 49f && world.y > 19f && world.y < 27f) path = 1f; // leaf shed yard
                path *= 1f - rock;
                float soil = Mathf.Max(0f, 1f - rock - path);

                map[zi, xi, 0] = soil; map[zi, xi, 1] = rock; map[zi, xi, 2] = path;
            }
            d.SetAlphamaps(0, 0, map);
            EditorUtility.SetDirty(d);
            return layers.Length;
        }

        static float Smooth(float a, float b, float x) { float t = Mathf.Clamp01((x - a) / (b - a)); return t * t * (3f - 2f * t); }

        // ------------------------------------------------------------------ metric-UV box meshes

        internal class MeshCache
        {
            readonly string path;
            readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
            Object container;
            public int Count => meshes.Count;

            public MeshCache(string path)
            {
                this.path = path;
                container = AssetDatabase.LoadMainAssetAtPath(path);
                if (container != null)
                    foreach (var m in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>())
                        if (m.name.StartsWith("KBox_")) meshes[m.name] = m;
            }

            public Mesh Get(Vector3 scale)
            {
                // One mesh per box size, rounded to the centimetre.
                var s = new Vector3(Round(scale.x), Round(scale.y), Round(scale.z));
                string key = $"KBox_{s.x * 100:0}x{s.y * 100:0}x{s.z * 100:0}";
                if (meshes.TryGetValue(key, out var mesh)) return mesh;

                mesh = BoxMesh(s);
                mesh.name = key;
                if (container == null)
                {
                    EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                    var holder = new Mesh { name = "Kirimala_BlockoutBoxes" };
                    AssetDatabase.CreateAsset(holder, path);
                    container = holder;
                }
                AssetDatabase.AddObjectToAsset(mesh, container);
                meshes[key] = mesh;
                return mesh;
            }

            public void Save() { if (container != null) { EditorUtility.SetDirty(container); AssetDatabase.SaveAssets(); } }

            static float Round(float v) => Mathf.Max(0.01f, Mathf.Round(Mathf.Abs(v) * 100f) / 100f);
        }

        /// <summary>
        /// Unit cube (-0.5..0.5, the object's scale still makes it the right size) whose UVs are
        /// in metres of the scaled box: side faces map U along the wall and V upwards,
        /// top and bottom map X and Z, so textures stay upright and never stretch.
        /// </summary>
        static Mesh BoxMesh(Vector3 size)
        {
            var verts = new List<Vector3>(); var normals = new List<Vector3>();
            var uvs = new List<Vector2>(); var tris = new List<int>();
            Vector3[] dirs = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

            foreach (var n in dirs)
            {
                Vector3 uAxis = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, n);
                Vector3 vAxis = Mathf.Abs(n.y) > 0.5f ? Vector3.forward : Vector3.up;
                float su = Vector3.Scale(uAxis, size).magnitude, sv = Vector3.Scale(vAxis, size).magnitude;

                Vector3 c = n * 0.5f;
                Vector3[] q = { c - uAxis * 0.5f - vAxis * 0.5f, c - uAxis * 0.5f + vAxis * 0.5f,
                                c + uAxis * 0.5f + vAxis * 0.5f, c + uAxis * 0.5f - vAxis * 0.5f };
                int b = verts.Count;
                foreach (var p in q)
                {
                    verts.Add(p); normals.Add(n);
                    uvs.Add(new Vector2(Vector3.Dot(p, uAxis) * su, Vector3.Dot(p, vAxis) * sv));
                }
                // Unity front faces: Cross(p1 - p0, p2 - p0) points out of the face.
                bool front = Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), n) > 0f;
                if (front) tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }

            var mesh = new Mesh();
            mesh.SetVertices(verts); mesh.SetNormals(normals); mesh.SetUVs(0, uvs); mesh.SetTriangles(tris, 0);
            mesh.RecalculateTangents();   // needed for normal maps
            mesh.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(mesh); // UV2 for the lightmapper
            return mesh;
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
