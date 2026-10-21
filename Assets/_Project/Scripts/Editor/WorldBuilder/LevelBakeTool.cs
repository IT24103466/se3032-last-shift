using System.Collections.Generic;
using System.IO;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// The World Builder's bake pipeline, as numbered steps under Tools > World Builder.
    /// Run them in order (or "Run Full Pipeline") whenever the level layout changes.
    ///
    /// Scene convention (step 0 creates it):
    ///   --- ENVIRONMENT ---   static level geometry (walls, floors, cover, props)  -> baked
    ///   --- LIGHTING ---      lights, probes, post-processing volume
    ///   --- DYNAMIC ---       doors, barricades, throwables (Systems Engineer)     -> NOT baked
    ///   --- AGENTS ---        AI characters (Agent Controller)
    ///   --- GAMEPLAY ---      player spawn, enemy spawn points, triggers
    ///
    /// Requires the "AI Navigation" package (com.unity.ai.navigation).
    /// </summary>
    public static class LevelBakeTool
    {
        public const string EnvironmentRoot = "--- ENVIRONMENT ---";
        public const string LightingRoot = "--- LIGHTING ---";
        public const string DynamicRoot = "--- DYNAMIC ---";
        public const string AgentsRoot = "--- AGENTS ---";
        public const string GameplayRoot = "--- GAMEPLAY ---";

        const string Menu = "Tools/World Builder/";

        // Renderers smaller than this (metres) are lit by light probes instead of lightmaps:
        // a bottle or a pipe clamp would waste lightmap space and bake time for no visible gain.
        const float SmallPropSize = 1.0f;
        // Only large objects (walls, floors, big crates) are worth testing as occluders.
        const float OccluderMinSize = 2.0f;
        // Horizontal distance between light probes placed on the NavMesh.
        const float ProbeSpacing = 3.0f;

        // ------------------------------------------------------------------ 0. hierarchy

        [MenuItem(Menu + "0. Create Scene Hierarchy", priority = 0)]
        public static void CreateHierarchy()
        {
            var env = GetOrCreate(EnvironmentRoot, null);
            GetOrCreate("Structure", env.transform);
            GetOrCreate("Props", env.transform);
            GetOrCreate("Decals", env.transform);

            var lighting = GetOrCreate(LightingRoot, null);
            GetOrCreate("Lights", lighting.transform);
            GetOrCreate("Probes", lighting.transform);
            GetOrCreate("PostProcessing", lighting.transform);

            GetOrCreate(DynamicRoot, null);
            GetOrCreate(AgentsRoot, null);
            var gameplay = GetOrCreate(GameplayRoot, null);
            GetOrCreate("SpawnPoints", gameplay.transform);

            EditorSceneManager.MarkSceneDirty(SceneManager.GetActiveScene());
            Debug.Log("[World Builder] Scene hierarchy ready.");
        }

        // ------------------------------------------------------------------ 1. static flags

        [MenuItem(Menu + "1. Mark Environment Static", priority = 1)]
        public static void MarkEnvironmentStatic()
        {
            var root = RequireRoot(EnvironmentRoot);
            if (root == null) return;

            int lightmapped = 0, probeLit = 0, skipped = 0;
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;
                if (go.GetComponent<Rigidbody>() != null || go.GetComponent<Animator>() != null)
                {
                    skipped++; // anything that moves must not be static
                    continue;
                }

                var flags = StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic |
                            StaticEditorFlags.OccludeeStatic | StaticEditorFlags.ReflectionProbeStatic;

                var r = go.GetComponent<MeshRenderer>();
                if (r != null && go.name.StartsWith("Backdrop_"))
                {
                    // Distant scenery (hundreds of metres across): lit by light probes so it
                    // takes no lightmap space, and never an occluder.
                    Undo.RecordObject(r, "Set GI mode");
                    r.receiveGI = ReceiveGI.LightProbes;
                    probeLit++;
                    Undo.RecordObject(go, "Mark static");
                    GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI | StaticEditorFlags.BatchingStatic);
                    continue;
                }
                if (r != null)
                {
                    float size = MaxExtent(r.bounds);
                    if (size >= OccluderMinSize) flags |= StaticEditorFlags.OccluderStatic;

                    Undo.RecordObject(r, "Set GI mode");
                    if (size < SmallPropSize) { r.receiveGI = ReceiveGI.LightProbes; probeLit++; }
                    else { r.receiveGI = ReceiveGI.Lightmaps; lightmapped++; }
                }

                Undo.RecordObject(go, "Mark static");
                GameObjectUtility.SetStaticEditorFlags(go, flags);
            }

            EditorSceneManager.MarkSceneDirty(root.scene);
            Debug.Log($"[World Builder] Static: {lightmapped} lightmapped renderers, {probeLit} small props on light probes, " +
                      $"{skipped} moving objects skipped.");
        }

        // ------------------------------------------------------------------ 2. lighting settings

        [MenuItem(Menu + "2a. Lighting Settings: Draft (fast preview)", priority = 2)]
        public static void LightingDraft() => ApplyLightingSettings(false);

        [MenuItem(Menu + "2b. Lighting Settings: Final (quality)", priority = 3)]
        public static void LightingFinal() => ApplyLightingSettings(true);

        public static LightingSettings ApplyLightingSettings(bool final)
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[World Builder] Save the scene before applying lighting settings.");
                return null;
            }

            string path = $"{SceneDir(scene)}/{scene.name}_Lighting.lighting";
            var s = AssetDatabase.LoadAssetAtPath<LightingSettings>(path);
            if (s == null)
            {
                s = new LightingSettings();
                AssetDatabase.CreateAsset(s, path);
            }

            s.bakedGI = true;
            s.realtimeGI = false;                                 // realtime GI (Enlighten) is costly and deprecated
            s.mixedBakeMode = MixedLightingMode.Shadowmask;       // static shadows baked, dynamic objects still cast
            s.lightmapper = LightingSettings.Lightmapper.ProgressiveGPU; // falls back to CPU if the GPU can't
            s.directionalityMode = LightmapsMode.CombinedDirectional;    // normal maps still react to baked light
            s.lightmapResolution = final ? 15f : 5f;              // texels per metre
            s.lightmapMaxSize = final ? 2048 : 1024;
            s.lightmapPadding = 2;
            s.lightmapCompression = LightmapCompression.NormalQuality;
            s.ao = true;                                          // baked contact shadows in corners
            s.aoMaxDistance = 1f;
            s.maxBounces = final ? 3 : 1;
            s.directSampleCount = final ? 64 : 16;
            s.indirectSampleCount = final ? 512 : 64;
            s.environmentSampleCount = final ? 256 : 64;
            s.filteringMode = LightingSettings.FilterMode.Auto;   // denoiser cleans up low sample counts

            EditorUtility.SetDirty(s);
            Lightmapping.lightingSettings = s;
            EditorSceneManager.MarkSceneDirty(scene);
            AssetDatabase.SaveAssets();
            Debug.Log($"[World Builder] {(final ? "Final" : "Draft")} lighting settings applied ({path}).");
            return s;
        }

        // ------------------------------------------------------------------ 3. NavMesh

        [MenuItem(Menu + "3. Bake NavMesh", priority = 20)]
        public static void BakeNavMesh()
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[World Builder] Save the scene before baking the NavMesh.");
                return;
            }
            var root = RequireRoot(EnvironmentRoot);
            if (root == null) return;

            var surface = root.GetComponent<NavMeshSurface>();
            if (surface == null)
            {
                surface = Undo.AddComponent<NavMeshSurface>(root);
                // Only static level geometry is baked. Doors and barricades live under
                // --- DYNAMIC --- and use NavMeshObstacle (Carve) so paths update when moved.
                surface.collectObjects = CollectObjects.Children;
                // Colliders = what the player actually collides with; ignores decorative detail.
                surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            }

            surface.BuildNavMesh();
            if (surface.navMeshData == null)
            {
                Debug.LogError("[World Builder] NavMesh build produced nothing. Do the floors have colliders?");
                return;
            }

            // Save the data as an asset next to the scene so it survives reloads and is committed to Git.
            string folder = SceneDir(scene) + "/" + scene.name;
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(SceneDir(scene), scene.name);
            string assetPath = folder + "/NavMesh.asset";
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(surface.navMeshData, assetPath);

            EditorUtility.SetDirty(surface);
            EditorSceneManager.MarkSceneDirty(scene);

            var tri = NavMesh.CalculateTriangulation();
            Debug.Log($"[World Builder] NavMesh baked: {tri.indices.Length / 3} triangles -> {assetPath}");
        }

        // ------------------------------------------------------------------ 4. light probes

        [MenuItem(Menu + "4. Generate Light Probes On NavMesh", priority = 21)]
        public static void GenerateLightProbes()
        {
            var tri = NavMesh.CalculateTriangulation();
            if (tri.vertices == null || tri.vertices.Length == 0)
            {
                Debug.LogError("[World Builder] No NavMesh found. Run step 3 first.");
                return;
            }

            var bounds = new Bounds(tri.vertices[0], Vector3.zero);
            foreach (var v in tri.vertices) bounds.Encapsulate(v);

            // Probes go exactly where the player and agents can walk: that is where
            // moving objects need baked light. Two layers: knee height and head height.
            float half = ProbeSpacing * 0.5f;
            var used = new HashSet<Vector3Int>();
            var world = new List<Vector3>();
            for (float y = bounds.min.y; y <= bounds.max.y + 0.01f; y += 2f)
            for (float x = bounds.min.x; x <= bounds.max.x + 0.01f; x += ProbeSpacing)
            for (float z = bounds.min.z; z <= bounds.max.z + 0.01f; z += ProbeSpacing)
            {
                if (!NavMesh.SamplePosition(new Vector3(x, y, z), out var hit, half, NavMesh.AllAreas)) continue;
                if (!used.Add(Vector3Int.RoundToInt(hit.position / half))) continue;
                world.Add(hit.position + Vector3.up * 0.5f);
                world.Add(hit.position + Vector3.up * 2.0f);
            }

            var lighting = GetOrCreate(LightingRoot, null);
            var probes = GetOrCreate("Probes", lighting.transform);
            var holder = GetOrCreate("LightProbeGroup", probes.transform);

            var group = holder.GetComponent<LightProbeGroup>();
            if (group == null) group = Undo.AddComponent<LightProbeGroup>(holder);

            var local = new Vector3[world.Count];
            for (int i = 0; i < world.Count; i++) local[i] = holder.transform.InverseTransformPoint(world[i]);

            Undo.RecordObject(group, "Generate light probes");
            group.probePositions = local;
            EditorSceneManager.MarkSceneDirty(holder.scene);
            Debug.Log($"[World Builder] {local.Length} light probes placed on the NavMesh. Bake lighting to fill them.");
        }

        // ------------------------------------------------------------------ 5/6. bakes

        [MenuItem(Menu + "5. Bake Lighting", priority = 22)]
        public static void BakeLighting()
        {
            if (Lightmapping.isRunning) { Debug.LogWarning("[World Builder] A bake is already running."); return; }
            if (!Lightmapping.BakeAsync()) Debug.LogError("[World Builder] Lighting bake could not start.");
            else Debug.Log("[World Builder] Lighting bake started (progress bar bottom-right).");
        }

        [MenuItem(Menu + "6. Bake Occlusion Culling", priority = 23)]
        public static void BakeOcclusion()
        {
            if (StaticOcclusionCulling.isRunning) return;
            StaticOcclusionCulling.GenerateInBackground();
            Debug.Log("[World Builder] Occlusion culling bake started. Check it in Window > Rendering > Occlusion Culling > Visualization.");
        }

        [MenuItem(Menu + "Run Full Pipeline (Final quality)", priority = 40)]
        public static void RunAll()
        {
            if (RequireRoot(EnvironmentRoot) == null) return;
            MarkEnvironmentStatic();
            if (ApplyLightingSettings(true) == null) return;
            BakeNavMesh();
            GenerateLightProbes();
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            BakeLighting();
        }

        // ------------------------------------------------------------------ helpers

        static GameObject GetOrCreate(string name, Transform parent)
        {
            GameObject go = null;
            if (parent != null)
            {
                var found = parent.Find(name);
                if (found != null) go = found.gameObject;
            }
            else go = FindRoot(name);
            if (go != null) return go;

            go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        public static GameObject FindRoot(string name)
        {
            foreach (var go in SceneManager.GetActiveScene().GetRootGameObjects())
                if (go.name == name) return go;
            return null;
        }

        static GameObject RequireRoot(string name)
        {
            var go = FindRoot(name);
            if (go == null)
                Debug.LogError($"[World Builder] No root object named \"{name}\". Run Tools > World Builder > " +
                               "0. Create Scene Hierarchy and put your level geometry under it.");
            return go;
        }

        static string SceneDir(Scene scene) => Path.GetDirectoryName(scene.path).Replace('\\', '/');

        static float MaxExtent(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
    }
}
