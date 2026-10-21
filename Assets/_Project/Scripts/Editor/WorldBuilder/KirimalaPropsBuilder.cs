using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Props.
    ///
    ///  1. Tea bushes: every TeaRow_ placeholder box becomes a rounded, lumpy, flat-topped hedge
    ///     (the "plucking table" of a pruned tea bush) with the TeaLeaves texture. Three mesh
    ///     variants plus a random 180° turn stop the rows looking copy-pasted. The box collider
    ///     is kept, so rows still give the same cover and NavMesh result.
    ///  2. Tea chests: the TeaChests_ blocks in the storeroom become stacks of single chests
    ///     (0.48 x 0.58 x 0.48 m) stencilled "CEYLON TEA", each with its own collider.
    ///  3. Signs: six trilingual (English / Sinhala / Tamil) boards at the estate entrance,
    ///     factory front, storeroom, boiler room, leaf shed and main gate.
    ///
    /// Textures come from Tools/ArtGen/kirimala_art.py (the signs need proper Sinhala/Tamil
    /// text shaping, so they are drawn as images rather than with TextMeshPro).
    /// The blockout builder calls ApplyIfBuilt(), so rebuilding the blockout keeps the props.
    /// </summary>
    public static class KirimalaPropsBuilder
    {
        const string EnvArt = "Assets/_Project/Art/Environment";
        const string TeaLeaves = EnvArt + "/Textures/TeaLeaves";
        const string SignFolder = EnvArt + "/Signs";
        const string MatFolder = EnvArt + "/Materials/Props";
        const string MeshAsset = EnvArt + "/Meshes/Kirimala_Props.asset";

        const float Ground = 8f, LoftY = 12.5f;

        [MenuItem("Tools/World Builder/Build Kirimala Props", priority = 84)]
        public static void Build()
        {
            if (LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot) == null)
            {
                Debug.LogError("[World Builder] Build the blockout first (Tools > World Builder > Build Kirimala Blockout).");
                return;
            }
            MakeMaterials();
            Apply(true);
        }

        /// <summary>Called by the blockout builder: re-applies props if their materials exist.</summary>
        public static void ApplyIfBuilt()
        {
            if (AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/M_Prop_TeaBush.mat") != null) Apply(false);
        }

        static void Apply(bool markStatic)
        {
            var env = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot).transform;
            var store = new MeshStore(MeshAsset);

            int bushes = BuildTeaBushes(env, store);
            int chests = BuildTeaChests(env, store);
            int signs = BuildSigns(env, store);
            store.Save();

            if (markStatic) LevelBakeTool.MarkEnvironmentStatic();   // new chests and signs need static flags
            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log($"[World Builder] Props: {bushes} tea bushes, {chests} tea chests, {signs} signs. " +
                      "Next: 3. Bake NavMesh, 4. Light Probes, 5. Bake Lighting.");
        }

        // ------------------------------------------------------------------ materials

        static Material Mat(string name) => AssetDatabase.LoadAssetAtPath<Material>($"{MatFolder}/{name}.mat");

        static void MakeMaterials()
        {
            EnsureFolder(MatFolder);

            // Tea bush: PBR set built like the other surfaces, tiled every 0.8 m
            var leaves = UrpMaterialBuilder.BuildSetFromFolder(TeaLeaves);
            var bush = Upsert("M_Prop_TeaBush", leaves);
            bush.SetTextureScale("_BaseMap", Vector2.one / 0.8f);
            bush.SetColor("_BaseColor", Color.white);
            EditorUtility.SetDirty(bush);

            // Tea chest: one texture per face (Unity's cube maps 0-1 on every face)
            var chest = Upsert("M_Prop_TeaChest", null);
            chest.SetTexture("_BaseMap", Load($"{EnvArt}/Props/TeaChest_Color.png", clamp: true));
            chest.SetFloat("_Smoothness", 0.15f);
            EditorUtility.SetDirty(chest);

            // One material per sign
            foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { SignFolder }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var m = Upsert("M_" + System.IO.Path.GetFileNameWithoutExtension(path), null);
                m.SetTexture("_BaseMap", Load(path, clamp: true));
                m.SetFloat("_Smoothness", 0.25f);   // painted metal sheet
                m.SetFloat("_Metallic", 0f);
                EditorUtility.SetDirty(m);
            }
            AssetDatabase.SaveAssets();
        }

        static Material Upsert(string name, Material source)
        {
            string path = $"{MatFolder}/{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null)
            {
                m = source != null ? new Material(source) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(m, path);
            }
            else if (source != null) { m.CopyPropertiesFromMaterial(source); m.shaderKeywords = source.shaderKeywords; }
            return m;
        }

        static Texture2D Load(string path, bool clamp)
        {
            if (clamp && AssetImporter.GetAtPath(path) is TextureImporter ti && ti.wrapMode != TextureWrapMode.Clamp)
            {
                ti.wrapMode = TextureWrapMode.Clamp;   // signs and stencils must not bleed from the opposite edge
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
        }

        // ------------------------------------------------------------------ tea bushes

        static int BuildTeaBushes(Transform env, MeshStore store)
        {
            var mat = Mat("M_Prop_TeaBush");
            var rows = env.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("TeaRow_")).ToList();
            if (rows.Count == 0 || mat == null) return 0;

            Vector3 size = rows[0].localScale;                       // all rows share one size (4 x 1.4 x 0.9 m)
            var variants = new Mesh[3];
            for (int v = 0; v < variants.Length; v++)
                variants[v] = store.Get($"TeaBush_{v}_{size.x:0.0}x{size.y:0.0}x{size.z:0.0}", () => TeaBushMesh(size, 17.3f * (v + 1)));

            for (int i = 0; i < rows.Count; i++)
            {
                var t = rows[i];
                var mf = t.GetComponent<MeshFilter>();
                var r = t.GetComponent<MeshRenderer>();
                if (mf == null || r == null) continue;
                Undo.RecordObject(mf, "Tea bush"); Undo.RecordObject(r, "Tea bush"); Undo.RecordObject(t, "Tea bush");
                int h = Hash(t.position);
                mf.sharedMesh = variants[h % variants.Length];
                r.sharedMaterial = mat;
                if ((h / 3) % 2 == 1) t.localRotation = Quaternion.Euler(0f, 180f, 0f);  // symmetric box, so safe to turn
            }
            return rows.Count;
        }

        /// <summary>
        /// A pruned tea hedge segment: a box with 0.35 m rounded edges, pushed in and out by
        /// noise (leaf clumps), with a gently wavy flat top. Built in metres, then divided by
        /// the row's scale so the object's transform still sizes it.
        /// </summary>
        static Mesh TeaBushMesh(Vector3 size, float seed)
        {
            var verts = new List<Vector3>(); var tris = new List<int>();
            var index = new Dictionary<Vector3Int, int>();
            int sx = Mathf.CeilToInt(size.x * 8), sy = Mathf.CeilToInt(size.y * 8), sz = Mathf.CeilToInt(size.z * 8);
            Vector3[] n = { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back };

            foreach (var normal in n)
            {
                Vector3 u = Mathf.Abs(normal.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, normal);
                Vector3 v = Vector3.Cross(normal, u);
                int nu = Seg(u, sx, sy, sz), nv = Seg(v, sx, sy, sz);
                var grid = new int[nu + 1, nv + 1];
                for (int i = 0; i <= nu; i++)
                for (int j = 0; j <= nv; j++)
                {
                    Vector3 p = normal * 0.5f + u * (i / (float)nu - 0.5f) + v * (j / (float)nv - 0.5f);
                    var key = Vector3Int.RoundToInt(p * 1000f);          // weld shared edge vertices
                    if (!index.TryGetValue(key, out int id)) { id = verts.Count; verts.Add(p); index[key] = id; }
                    grid[i, j] = id;
                }
                for (int i = 0; i < nu; i++)
                for (int j = 0; j < nv; j++)
                {
                    int a = grid[i, j], b = grid[i + 1, j], c = grid[i + 1, j + 1], d = grid[i, j + 1];
                    bool front = Vector3.Dot(Vector3.Cross(verts[d] - verts[a], verts[c] - verts[a]), normal) > 0f;
                    if (front) tris.AddRange(new[] { a, d, c, a, c, b }); else tris.AddRange(new[] { a, c, d, a, b, c });
                }
            }

            const float radius = 0.35f;
            Vector3 inner = Vector3.Max(size * 0.5f - Vector3.one * radius, Vector3.zero);
            var uvs = new Vector2[verts.Count];
            for (int k = 0; k < verts.Count; k++)
            {
                Vector3 q = Vector3.Scale(verts[k], size);              // metres
                Vector3 c = Vector3.Max(Vector3.Min(q, inner), -inner);
                Vector3 dir = (q - c).sqrMagnitude > 1e-8f ? (q - c).normalized : Vector3.up;
                q = c + dir * radius;                                   // rounded box

                float lumps = Noise3(q * 1.6f, seed) - 0.5f + 0.5f * (Noise3(q * 4.2f, seed + 5f) - 0.5f);
                q += dir * lumps * (0.10f + 0.06f * Mathf.Max(0f, dir.y));
                if (dir.y > 0.2f) q.y += (Mathf.PerlinNoise(q.x * 0.5f + seed, seed) - 0.5f) * 0.18f;  // uneven plucking table

                Vector3 a = new Vector3(Mathf.Abs(dir.x), Mathf.Abs(dir.y), Mathf.Abs(dir.z));
                uvs[k] = a.y >= a.x && a.y >= a.z ? new Vector2(q.x, q.z)
                       : a.x >= a.z ? new Vector2(q.z, q.y) : new Vector2(q.x, q.y);
                verts[k] = new Vector3(q.x / size.x, q.y / size.y, q.z / size.z);
            }

            var mesh = new Mesh { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts); mesh.SetTriangles(tris, 0); mesh.uv = uvs;
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(mesh);
            return mesh;
        }

        static int Seg(Vector3 axis, int sx, int sy, int sz) =>
            Mathf.Abs(axis.x) > 0.5f ? sx : Mathf.Abs(axis.y) > 0.5f ? sy : sz;

        static float Noise3(Vector3 p, float s) =>
            (Mathf.PerlinNoise(p.x + s, p.y) + Mathf.PerlinNoise(p.y + s, p.z) + Mathf.PerlinNoise(p.z + s, p.x)) / 3f;

        static int Hash(Vector3 p) => Mathf.Abs(Mathf.RoundToInt(p.x * 73.1f) * 31 + Mathf.RoundToInt(p.z * 19.7f) * 17);

        // ------------------------------------------------------------------ tea chests

        static int BuildTeaChests(Transform env, MeshStore store)
        {
            var mat = Mat("M_Prop_TeaChest");
            if (mat == null) return 0;
            var chestMesh = store.Get("TeaChestBox", ChestMesh);
            var blocks = env.GetComponentsInChildren<Transform>(true).Where(t => t.name.StartsWith("TeaChests_")).ToList();
            var chest = new Vector3(0.48f, 0.58f, 0.48f);
            const float gap = 0.03f;
            int count = 0;

            foreach (var block in blocks)
            {
                var r = block.GetComponent<Renderer>();
                if (r == null) continue;
                Bounds b = r.bounds;
                Transform parent = block.parent;
                string stackName = block.name.Replace("TeaChests_", "TeaChestStack_");
                var old = parent.Find(stackName);
                if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

                var stack = new GameObject(stackName).transform;
                Undo.RegisterCreatedObjectUndo(stack.gameObject, "Tea chests");
                stack.SetParent(parent, true);
                stack.position = b.center;

                int nx = Mathf.Max(1, Mathf.FloorToInt(b.size.x / (chest.x + gap)));
                int ny = Mathf.Max(1, Mathf.FloorToInt(b.size.y / (chest.y + gap)));
                int nz = Mathf.Max(1, Mathf.FloorToInt(b.size.z / (chest.z + gap)));
                var rng = new System.Random(stackName.GetHashCode());

                for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                for (int z = 0; z < nz; z++)
                {
                    if (y == ny - 1 && ny > 1 && rng.NextDouble() < 0.3) continue;  // a few missing on top
                    var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    c.name = "TeaChest";
                    c.transform.SetParent(stack, false);
                    c.transform.position = new Vector3(
                        b.min.x + (x + 0.5f) * (b.size.x / nx),
                        b.min.y + chest.y * 0.5f + y * (chest.y + 0.005f),
                        b.min.z + (z + 0.5f) * (b.size.z / nz));
                    c.transform.rotation = Quaternion.Euler(0f, (float)(rng.NextDouble() * 8 - 4) + (rng.Next(4) * 90), 0f);
                    c.transform.localScale = chest;
                    c.GetComponent<MeshFilter>().sharedMesh = chestMesh;   // stencil reads correctly on every side
                    c.GetComponent<MeshRenderer>().sharedMaterial = mat;
                    count++;
                }
                Undo.DestroyObjectImmediate(block.gameObject);   // the stack replaces the placeholder block
            }
            if (blocks.Count == 0)   // already converted: refresh existing chests
                foreach (var t in env.GetComponentsInChildren<Transform>(true).Where(t => t.name == "TeaChest"))
                {
                    t.GetComponent<MeshFilter>().sharedMesh = chestMesh;
                    t.GetComponent<MeshRenderer>().sharedMaterial = mat;
                    count++;
                }
            return count;
        }

        // ------------------------------------------------------------------ signs

        struct SignDef
        {
            public string tex; public Vector3 pos; public float yaw, w, h; public bool posts;
            public SignDef(string tex, Vector3 pos, float yaw, float w, float h, bool posts = false)
            { this.tex = tex; this.pos = pos; this.yaw = yaw; this.w = w; this.h = h; this.posts = posts; }
        }

        static int BuildSigns(Transform env, MeshStore store)
        {
            var propsRoot = env.Find("Props") ?? env;
            var old = propsRoot.Find("Kirimala_Signs");
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var root = new GameObject("Kirimala_Signs").transform;
            Undo.RegisterCreatedObjectUndo(root.gameObject, "Signs");
            root.SetParent(propsRoot, false);

            float T(float x, float z) { var t = Terrain.activeTerrain; return t != null ? t.SampleHeight(new Vector3(x, 0, z)) + t.transform.position.y : Ground; }
            float shed = T(45f, 23f);
            const float wallFace = 0.15f + 0.03f;   // half wall thickness + sign standoff

            // yaw 0: the sign's text faces south (-Z); -90: faces east (+X)
            var defs = new[]
            {
                new SignDef("Sign_Estate",   new Vector3(34.5f, T(34.5f, 39.2f) + 1.75f, 39.2f), 0f, 3.0f, 1.5f, posts: true),
                new SignDef("Sign_Factory",  new Vector3(30f, 13.8f, 52f - wallFace), 0f, 6.0f, 1.5f),
                new SignDef("Sign_Store",    new Vector3(44f, Ground + 3.6f, 52f - wallFace), 0f, 2.4f, 1.0f),
                new SignDef("Sign_Boiler",   new Vector3(41f, Ground + 2.8f, 62f - wallFace), 0f, 2.0f, 1.0f),
                new SignDef("Sign_LeafShed", new Vector3(45f, shed + 2.15f, 19.55f), 0f, 2.8f, 0.9f),
                new SignDef("Sign_MainGate", new Vector3(10.3f, Ground + 3.1f, 46f), -90f, 2.4f, 1.0f, posts: true),
            };

            var board = store.Get("SignBoard", SignMesh);
            var wood = AssetDatabase.LoadAssetAtPath<Material>($"{EnvArt}/Materials/Surfaces/M_Env_WoodPlanks.mat");
            int count = 0;
            foreach (var d in defs)
            {
                var mat = Mat("M_" + d.tex);
                if (mat == null) { Debug.LogWarning($"[World Builder] Missing sign texture {d.tex}"); continue; }

                var go = new GameObject(d.tex, typeof(MeshFilter), typeof(MeshRenderer), typeof(BoxCollider));
                go.transform.SetParent(root, false);
                go.transform.SetPositionAndRotation(d.pos, Quaternion.Euler(0f, d.yaw, 0f));
                go.transform.localScale = new Vector3(d.w, d.h, 0.05f);
                go.GetComponent<MeshFilter>().sharedMesh = board;
                go.GetComponent<MeshRenderer>().sharedMaterial = mat;
                count++;

                if (d.posts)   // two wooden posts down to the ground
                {
                    Vector3 side = go.transform.right * (d.w * 0.5f - 0.15f);
                    foreach (var s in new[] { -side, side })
                    {
                        Vector3 top = d.pos + s + Vector3.up * (d.h * 0.5f + 0.1f);
                        float bottom = T(top.x, top.z) - 0.3f;
                        if (d.tex == "Sign_MainGate") bottom = Ground;
                        var post = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        post.name = "Post";
                        post.transform.SetParent(go.transform.parent, false);
                        post.transform.position = new Vector3(top.x, (top.y + bottom) * 0.5f, top.z) + go.transform.forward * 0.06f;
                        post.transform.rotation = go.transform.rotation;
                        post.transform.localScale = new Vector3(0.12f, top.y - bottom, 0.12f);
                        if (wood != null) post.GetComponent<MeshRenderer>().sharedMaterial = wood;
                    }
                }
            }
            return count;
        }

        /// <summary>
        /// Thin board: the -Z face shows the whole texture, readable when you stand in front of
        /// it (local -Z side). Edges and back sample the texture's border colour.
        /// </summary>
        static Mesh SignMesh()
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            Vector3[] dirs = { Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.up, Vector3.down };
            foreach (var n in dirs)
            {
                Vector3 u = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.Cross(Vector3.up, n) * -1f;
                if (n == Vector3.back) u = Vector3.right;            // text reads left to right from the front
                Vector3 w = Vector3.Cross(n, u) * -1f;
                if (Vector3.Dot(w, Vector3.up) < 0f && Mathf.Abs(n.y) < 0.5f) w = -w;
                Vector3 c = n * 0.5f;
                Vector3[] q = { c - u * 0.5f - w * 0.5f, c - u * 0.5f + w * 0.5f, c + u * 0.5f + w * 0.5f, c + u * 0.5f - w * 0.5f };
                int b = v.Count;
                for (int i = 0; i < 4; i++)
                {
                    v.Add(q[i]);
                    uv.Add(n == Vector3.back ? new Vector2(Vector3.Dot(q[i], u) + 0.5f, Vector3.Dot(q[i], w) + 0.5f)
                                             : new Vector2(0.004f, 0.004f));
                }
                bool front = Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), n) > 0f;
                if (front) tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(mesh);
            return mesh;
        }

        /// <summary>
        /// Box with the whole texture on every face, oriented so text reads left to right and
        /// upright when you look at any side (Unity's built-in cube mirrors some faces).
        /// </summary>
        static Mesh ChestMesh()
        {
            var v = new List<Vector3>(); var uv = new List<Vector2>(); var tris = new List<int>();
            foreach (var n in new[] { Vector3.back, Vector3.forward, Vector3.left, Vector3.right, Vector3.up, Vector3.down })
            {
                bool side = Mathf.Abs(n.y) < 0.5f;
                Vector3 u = side ? Vector3.Cross(Vector3.up, -n) : Vector3.right;   // viewer's right
                Vector3 w = side ? Vector3.up : Vector3.Cross(u, n) * (n.y > 0 ? -1f : 1f);
                Vector3 c = n * 0.5f;
                Vector3[] q = { c - u * 0.5f - w * 0.5f, c - u * 0.5f + w * 0.5f, c + u * 0.5f + w * 0.5f, c + u * 0.5f - w * 0.5f };
                int b = v.Count;
                foreach (var p in q) { v.Add(p); uv.Add(new Vector2(Vector3.Dot(p, u) + 0.5f, Vector3.Dot(p, w) + 0.5f)); }
                bool front = Vector3.Dot(Vector3.Cross(q[1] - q[0], q[2] - q[0]), n) > 0f;
                if (front) tris.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                else tris.AddRange(new[] { b, b + 2, b + 1, b, b + 3, b + 2 });
            }
            var mesh = new Mesh();
            mesh.SetVertices(v); mesh.SetUVs(0, uv); mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals(); mesh.RecalculateTangents(); mesh.RecalculateBounds();
            Unwrapping.GenerateSecondaryUVSet(mesh);
            return mesh;
        }

        // ------------------------------------------------------------------ helpers

        class MeshStore
        {
            readonly string path; Object container;
            readonly Dictionary<string, Mesh> meshes = new Dictionary<string, Mesh>();
            public MeshStore(string path)
            {
                this.path = path;
                container = AssetDatabase.LoadMainAssetAtPath(path);
                if (container != null)
                    foreach (var m in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Mesh>()) meshes[m.name] = m;
            }
            public Mesh Get(string name, System.Func<Mesh> make)
            {
                if (meshes.TryGetValue(name, out var m)) return m;
                m = make(); m.name = name;
                if (container == null)
                {
                    EnsureFolder(System.IO.Path.GetDirectoryName(path).Replace('\\', '/'));
                    var holder = new Mesh { name = "Kirimala_Props" };
                    AssetDatabase.CreateAsset(holder, path);
                    container = holder;
                }
                AssetDatabase.AddObjectToAsset(m, container);
                meshes[name] = m;
                return m;
            }
            public void Save() { if (container != null) { EditorUtility.SetDirty(container); AssetDatabase.SaveAssets(); } }
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
