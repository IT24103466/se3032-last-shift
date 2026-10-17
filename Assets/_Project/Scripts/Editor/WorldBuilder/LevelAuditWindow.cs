using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Level Audit.
    /// Counts what the level costs (triangles, materials, textures, lights, lightmaps, probes)
    /// and lists problems that break the look or the build: pink materials, missing lightmap
    /// UVs, uncompressed or oversized textures, too many realtime lights, structure with no
    /// collider, non-convex mesh colliders on rigidbodies.
    ///
    /// "Export report" writes a Markdown file to Docs/ in the project root. Export one before
    /// and one after optimising: the difference is your evidence for the optimisation marks.
    /// </summary>
    public class LevelAuditWindow : EditorWindow
    {
        class Issue
        {
            public string message;
            public Object target;
            public bool severe;
        }

        readonly List<(string label, string value)> stats = new List<(string, string)>();
        readonly List<Issue> issues = new List<Issue>();
        Vector2 scroll;

        const int MaxTextureSize = 2048;
        const int HeavyMeshTris = 20000;
        const int MaxRealtimeLights = 4;

        [MenuItem("Tools/World Builder/Level Audit", priority = 60)]
        static void Open()
        {
            var w = GetWindow<LevelAuditWindow>("Level Audit");
            w.Run();
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Run audit")) Run();
                GUI.enabled = stats.Count > 0;
                if (GUILayout.Button("Export report (.md)")) Export();
                GUI.enabled = true;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("Scene statistics", EditorStyles.boldLabel);
            foreach (var (label, value) in stats) EditorGUILayout.LabelField(label, value);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Issues ({issues.Count})", EditorStyles.boldLabel);
            if (issues.Count == 0 && stats.Count > 0) EditorGUILayout.HelpBox("No issues found.", MessageType.Info);
            foreach (var issue in issues)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.HelpBox(issue.message, issue.severe ? MessageType.Error : MessageType.Warning);
                    if (issue.target != null && GUILayout.Button("Select", GUILayout.Width(60), GUILayout.Height(38)))
                    {
                        Selection.activeObject = issue.target;
                        EditorGUIUtility.PingObject(issue.target);
                    }
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void Add(string msg, Object target, bool severe = false) =>
            issues.Add(new Issue { message = msg, target = target, severe = severe });

        void Run()
        {
            stats.Clear();
            issues.Clear();

            GameObject envRoot = LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot);
            Transform structure = envRoot != null ? envRoot.transform.Find("Structure") : null;

            // ---- renderers, meshes, materials
            var renderers = Object.FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude);
            long totalTris = 0;
            var meshes = new HashSet<Mesh>();
            var materials = new HashSet<Material>();
            var textures = new HashSet<Texture>();

            foreach (var r in renderers)
            {
                var mf = r.GetComponent<MeshFilter>();
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                bool inEnv = envRoot != null && r.transform.IsChildOf(envRoot.transform);

                if (mesh != null)
                {
                    meshes.Add(mesh);
                    long tris = 0;
                    for (int s = 0; s < mesh.subMeshCount; s++) tris += mesh.GetIndexCount(s) / 3;
                    totalTris += tris;

                    if (tris > HeavyMeshTris)
                        Add($"{r.name}: {tris:N0} triangles in one mesh. Consider a lower-poly version or LOD Group.", r);

                    var flags = GameObjectUtility.GetStaticEditorFlags(r.gameObject);
                    bool lightmapped = (flags & StaticEditorFlags.ContributeGI) != 0 && r.receiveGI == ReceiveGI.Lightmaps;
                    if (lightmapped && !mesh.HasVertexAttribute(VertexAttribute.TexCoord1))
                        Add($"{r.name}: lightmapped but mesh '{mesh.name}' has no lightmap UVs (UV2). " +
                            "Tick 'Generate Lightmap UVs' in its import settings.", r, true);
                }

                if (inEnv && !GameObjectUtility.AreStaticEditorFlagsSet(r.gameObject, StaticEditorFlags.BatchingStatic)
                    && r.GetComponentInParent<Rigidbody>() == null)
                    Add($"{r.name}: environment object is not static. Run '1. Mark Environment Static'.", r);

                if (structure != null && r.transform.IsChildOf(structure) && r.GetComponent<Collider>() == null)
                    Add($"{r.name}: structure piece has no collider. The player and agents can walk through it.", r, true);

                foreach (var m in r.sharedMaterials)
                {
                    if (m == null) { Add($"{r.name}: empty material slot (renders pink).", r, true); continue; }
                    materials.Add(m);
                    foreach (int id in m.GetTexturePropertyNameIDs())
                    {
                        var t = m.GetTexture(id);
                        if (t != null) textures.Add(t);
                    }
                }
            }

            foreach (var m in materials)
            {
                string shader = m.shader != null ? m.shader.name : "";
                if (shader == "Standard" || shader.StartsWith("Legacy Shaders") || shader == "Hidden/InternalErrorShader")
                    Add($"Material '{m.name}' uses '{shader}', which renders pink in URP. " +
                        "Use Edit > Rendering > Materials > Convert Selected Built-in Materials to URP.", m, true);
            }

            // ---- textures
            long texBytes = 0;
            foreach (var t in textures)
            {
                texBytes += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(t);
                string path = AssetDatabase.GetAssetPath(t);
                if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;

                if (ti.textureCompression == TextureImporterCompression.Uncompressed)
                    Add($"Texture '{t.name}' is uncompressed (about 4-8x the memory of a compressed one).", t);
                if (t.width > MaxTextureSize || t.height > MaxTextureSize)
                    Add($"Texture '{t.name}' is {t.width}x{t.height}. Cap it at {MaxTextureSize} unless it covers a huge area.", t);
                if (!ti.mipmapEnabled)
                    Add($"Texture '{t.name}' has no mipmaps: it will shimmer at a distance and cost more bandwidth.", t);
            }

            // ---- lights
            var lights = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude);
            int baked = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Baked);
            int mixed = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Mixed);
            int realtime = lights.Count(l => l.lightmapBakeType == LightmapBakeType.Realtime);
            int shadowCasters = lights.Count(l => l.lightmapBakeType != LightmapBakeType.Baked && l.shadows != LightShadows.None);
            if (realtime > MaxRealtimeLights)
                Add($"{realtime} realtime lights. Each one is recalculated every frame; bake the ones that never change.", null);
            if (mixed > 4)
                Add($"{mixed} mixed lights. Shadowmask supports at most 4 overlapping mixed lights per area; " +
                    "extra overlapping ones fall back to fully baked.", null);

            // ---- rigidbodies with non-convex mesh colliders (physics errors)
            foreach (var rb in Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude))
            {
                if (rb.isKinematic) continue;
                foreach (var mc in rb.GetComponentsInChildren<MeshCollider>())
                    if (!mc.convex)
                        Add($"{mc.name}: non-convex MeshCollider on a moving Rigidbody. Tick 'Convex' or use Box/Capsule colliders.", mc, true);
            }

            // ---- baked data
            int lightmaps = LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0;
            int probeCount = LightmapSettings.lightProbes != null ? LightmapSettings.lightProbes.count : 0;
            int reflectionProbes = Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Exclude).Length;
            var nav = NavMesh.CalculateTriangulation();
            bool hasNavMesh = nav.vertices != null && nav.vertices.Length > 0;

            if (!hasNavMesh) Add("No NavMesh baked. Run '3. Bake NavMesh'.", null, true);
            if (lightmaps == 0) Add("No lightmaps baked yet. Run '5. Bake Lighting'.", null);
            if (probeCount == 0) Add("No baked light probes: moving characters will not pick up baked light.", null);

            // ---- stats
            var scene = SceneManager.GetActiveScene();
            stats.Add(("Scene", scene.name));
            stats.Add(("Mesh renderers", renderers.Length.ToString("N0")));
            stats.Add(("Triangles (all renderers)", totalTris.ToString("N0")));
            stats.Add(("Unique meshes", meshes.Count.ToString("N0")));
            stats.Add(("Unique materials", materials.Count.ToString("N0")));
            stats.Add(("Unique textures", textures.Count.ToString("N0")));
            stats.Add(("Texture memory (editor estimate)", $"{texBytes / (1024f * 1024f):F1} MB"));
            stats.Add(("Lights baked / mixed / realtime", $"{baked} / {mixed} / {realtime}"));
            stats.Add(("Lights casting realtime shadows", shadowCasters.ToString()));
            stats.Add(("Lightmaps", lightmaps.ToString()));
            stats.Add(("Baked light probes", probeCount.ToString("N0")));
            stats.Add(("Reflection probes", reflectionProbes.ToString()));
            stats.Add(("NavMesh", hasNavMesh ? $"{nav.indices.Length / 3:N0} triangles" : "not baked"));

            Repaint();
        }

        void Export()
        {
            string sceneName = SceneManager.GetActiveScene().name;
            var sb = new StringBuilder();
            sb.AppendLine($"# Level audit - {sceneName}");
            sb.AppendLine();
            sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
            sb.AppendLine("| Metric | Value |");
            sb.AppendLine("|---|---|");
            foreach (var (label, value) in stats) sb.AppendLine($"| {label} | {value} |");
            sb.AppendLine();
            sb.AppendLine($"## Issues ({issues.Count})");
            sb.AppendLine();
            if (issues.Count == 0) sb.AppendLine("No issues found.");
            foreach (var i in issues) sb.AppendLine($"- {(i.severe ? "**Error**" : "Warning")}: {i.message}");

            string dir = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "Docs");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"LevelAudit_{sceneName}_{DateTime.Now:yyyyMMdd_HHmm}.md");
            File.WriteAllText(file, sb.ToString());
            EditorUtility.RevealInFinder(file);
            Debug.Log($"[World Builder] Audit report written to {file}");
        }
    }
}
