using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LastShift;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Builds prefabs for the custom environment models (trees, tea bushes, rocks, props) into
    /// Assets/_Project/Prefabs/Environment_Custom. Everything created here is NEW: it never changes an existing asset.
    /// Menu: Tools > Custom Art > Build Custom Environment Prefabs.
    /// Notes: the project's import rules (EnvironmentImportRules) add MESH COLLIDERS to every model under Art/Environment;
    /// this builder switches that off for the custom models and adds simple primitive colliders to the prefab instead
    /// (a leaf card must never block the player or the NavMesh).
    /// </summary>
    public static class CustomEnvironmentBuilder
    {
        const string ModelDir = "Assets/_Project/Art/Environment/Models";
        const string PrefabDir = "Assets/_Project/Prefabs/Environment_Custom";

        [MenuItem("Tools/Custom Art/Build Custom Environment Prefabs")]
        public static void BuildAll()
        {
            EnsureFolder(PrefabDir);
            EnsureFolder(ModelDir + "/Materials");
            if (File.Exists($"{ModelDir}/Tree_Estate_02.fbx")) BuildTree();
            if (File.Exists($"{ModelDir}/TeaRow_Estate_01.fbx")) BuildTeaBush();
            if (File.Exists($"{ModelDir}/Rocks_Custom.fbx")) BuildRocks();
            BuildProps();
            BuildVegetation();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        // ------------------------------------------------------------------ tree
        static void BuildTree()
        {
            string tdir = $"{ModelDir}/Textures/Tree_Estate_02";
            SetupLeafTexture($"{tdir}/Tree_Estate_02_Leaves_BaseColor.png");
            var bark = PbrMaterial("Tree_Estate_02_Bark", $"{tdir}/Tree_Estate_02_Bark_BaseColor.png", $"{tdir}/Tree_Estate_02_Bark_Normal.png", $"{tdir}/Tree_Estate_02_Bark_MetalAOSmooth.png");
            var leaves = LeafMaterial("Tree_Estate_02_Leaves", $"{tdir}/Tree_Estate_02_Leaves_BaseColor.png");
            foreach (var name in new[] { "Tree_Estate_02", "Tree_Estate_02B", "Tree_Estate_02C" })
            {
                string fbx = $"{ModelDir}/{name}.fbx";
                if (!File.Exists(fbx)) continue;
                SetupModel(fbx);
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                var root = Object.Instantiate(src);
                root.name = name;
                AssignOrdered(root, bark, leaves);
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                    r.lightProbeUsage = LightProbeUsage.BlendProbes;
                }
                AddLods(root, 0.18f, 0.025f);
                // trunk collider: a capsule, not the leaf cards
                var col = root.AddComponent<CapsuleCollider>();
                col.radius = 0.32f; col.height = 8.5f; col.center = new Vector3(0.35f, 4.25f, -0.1f); col.direction = 1;
                Save(root, name);
            }
        }

        static void AddLods(GameObject root, float lod0, float lod1)
        {
            var lg = root.GetComponent<LODGroup>() ?? root.AddComponent<LODGroup>();
            var lods = new List<LOD>();
            var l0 = FindRenderers(root, "_LOD0"); var l1 = FindRenderers(root, "_LOD1");
            if (l0.Length > 0) lods.Add(new LOD(lod0, l0));
            if (l1.Length > 0) lods.Add(new LOD(lod1, l1));
            lg.SetLODs(lods.ToArray());
            lg.fadeMode = LODFadeMode.CrossFade; lg.animateCrossFading = true;
            lg.RecalculateBounds();
        }

        // ------------------------------------------------------------------ hide bush and fern
        static void BuildVegetation()
        {
            if (File.Exists($"{ModelDir}/Bush_Hide_Custom.fbx"))
            {
                string tdir = $"{ModelDir}/Textures/Bush_Hide_Custom";
                SetupLeafTexture($"{tdir}/Bush_Hide_Custom_Leaves_BaseColor.png");
                SetupModel($"{ModelDir}/Bush_Hide_Custom.fbx");
                var shell = PbrMaterial("Bush_Hide_Custom_Shell", $"{tdir}/Bush_Hide_Custom_Shell_BaseColor.png", $"{tdir}/Bush_Hide_Custom_Shell_Normal.png", $"{tdir}/Bush_Hide_Custom_Shell_MetalAOSmooth.png");
                var leaves = LeafMaterial("Bush_Hide_Custom_Leaves", $"{tdir}/Bush_Hide_Custom_Leaves_BaseColor.png");
                leaves.SetFloat("_Smoothness", 0.2f);
                var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelDir}/Bush_Hide_Custom.fbx"));
                root.name = "Bush_Hide_Custom";
                AssignOrdered(root, shell, leaves);
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>()) { r.shadowCastingMode = ShadowCastingMode.On; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
                AddLods(root, 0.10f, 0.02f);     // no collider on purpose: the player crawls inside (the level's HideZone trigger does the hiding)
                Save(root, "Bush_Hide_Custom");
            }
            if (File.Exists($"{ModelDir}/Fern_Jungle_Custom.fbx"))
            {
                string tdir = $"{ModelDir}/Textures/Fern_Jungle_Custom";
                SetupLeafTexture($"{tdir}/Fern_Jungle_Custom_Leaves_BaseColor.png");
                SetupModel($"{ModelDir}/Fern_Jungle_Custom.fbx");
                var leaves = LeafMaterial("Fern_Jungle_Custom_Leaves", $"{tdir}/Fern_Jungle_Custom_Leaves_BaseColor.png");
                leaves.SetFloat("_Smoothness", 0.18f);
                var root = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>($"{ModelDir}/Fern_Jungle_Custom.fbx"));
                root.name = "Fern_Jungle_Custom";
                AssignOrdered(root, leaves);
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>()) { r.shadowCastingMode = ShadowCastingMode.Off; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
                AddLods(root, 0.06f, 0.012f);
                Save(root, "Fern_Jungle_Custom");
            }
        }

        // ------------------------------------------------------------------ tea bush
        static void BuildTeaBush()
        {
            string fbx = $"{ModelDir}/TeaRow_Estate_01.fbx";
            string tdir = $"{ModelDir}/Textures/TeaRow_Estate_01";
            SetupLeafTexture($"{tdir}/TeaRow_Estate_01_Leaves_BaseColor.png");
            SetupModel(fbx);
            var shell = PbrMaterial("TeaRow_Estate_01_Shell", $"{tdir}/TeaRow_Estate_01_Shell_BaseColor.png", $"{tdir}/TeaRow_Estate_01_Shell_Normal.png", $"{tdir}/TeaRow_Estate_01_Shell_MetalAOSmooth.png");
            var leaves = LeafMaterial("TeaRow_Estate_01_Leaves", $"{tdir}/TeaRow_Estate_01_Leaves_BaseColor.png");
            leaves.SetFloat("_Smoothness", 0.22f);
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            var root = Object.Instantiate(src);
            root.name = "TeaRow_Estate_01";
            AssignOrdered(root, shell, leaves);
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>())
            {
                r.shadowCastingMode = ShadowCastingMode.On;
                r.lightProbeUsage = LightProbeUsage.BlendProbes;
            }
            var lg = root.GetComponent<LODGroup>() ?? root.AddComponent<LODGroup>();
            var lods = new List<LOD>();
            var l0 = FindRenderers(root, "_LOD0"); var l1 = FindRenderers(root, "_LOD1");
            if (l0.Length > 0) lods.Add(new LOD(0.10f, l0));
            if (l1.Length > 0) lods.Add(new LOD(0.015f, l1));
            lg.SetLODs(lods.ToArray());
            lg.fadeMode = LODFadeMode.CrossFade; lg.animateCrossFading = true;
            lg.RecalculateBounds();
            var col = root.AddComponent<BoxCollider>();      // same footprint as the level's placeholder TeaRow boxes (4 x 0.9 m)
            col.center = new Vector3(0f, 0.45f, 0f); col.size = new Vector3(4.0f, 0.95f, 0.9f);
            Save(root, "TeaRow_Estate_01");
        }

        static void BuildRocks() { BuildRockKit(); }

        // ------------------------------------------------------------------ props: barrel, tea sack, crate, tea chest
        static void BuildProps()
        {
            foreach (var kind in new[] { "Barrel", "TeaBag", "Crate", "TeaChest", "Pallet", "Log" })
            {
                string fbx = $"{ModelDir}/{kind}_Custom.fbx";
                if (!File.Exists(fbx)) continue;
                string tdir = $"{ModelDir}/Textures/{kind}_Custom";
                SetupModel(fbx);
                var mat = PbrMaterial($"{kind}_Custom", $"{tdir}/{kind}_Custom_BaseColor.png", $"{tdir}/{kind}_Custom_Normal.png", $"{tdir}/{kind}_Custom_MetalAOSmooth.png");
                var src = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
                var mesh = Object.Instantiate(src);
                AssignOrdered(mesh, mat);
                foreach (var r in mesh.GetComponentsInChildren<MeshRenderer>()) { r.shadowCastingMode = ShadowCastingMode.On; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
                GameObject root;
                switch (kind)
                {
                    case "Barrel":
                        root = mesh; root.name = "Barrel_Custom";
                        var cc = root.AddComponent<CapsuleCollider>(); cc.radius = 0.29f; cc.height = 0.88f; cc.center = new Vector3(0f, 0.44f, 0f);
                        break;
                    case "Crate":
                        root = mesh; root.name = "Crate_Custom";
                        var bc = root.AddComponent<BoxCollider>(); bc.size = new Vector3(0.9f, 0.9f, 0.9f); bc.center = new Vector3(0f, 0.45f, 0f);
                        break;
                    case "TeaChest":
                        root = mesh; root.name = "TeaChest_Custom";
                        var tc = root.AddComponent<BoxCollider>(); tc.size = new Vector3(0.52f, 0.6f, 0.52f); tc.center = new Vector3(0f, 0.3f, 0f);
                        break;
                    case "Pallet":
                        root = mesh; root.name = "Pallet_Custom";
                        var pc = root.AddComponent<BoxCollider>(); pc.size = new Vector3(1.2f, 0.15f, 0.8f); pc.center = new Vector3(0f, 0.075f, 0f);
                        TrySetTag(root, "Vaultable");
                        break;
                    case "Log":
                        root = mesh; root.name = "Log_Custom";
                        var lc = root.AddComponent<CapsuleCollider>(); lc.radius = 0.44f; lc.height = 3.2f; lc.direction = 2; lc.center = new Vector3(0f, 0.44f, 0f);
                        TrySetTag(root, "Vaultable");
                        break;
                    default: // TeaBag: same structure as the greybox bag (root pivot on the floor, "Sack" + "Tag" children) so TeaBag.Search() still works
                        root = new GameObject("TeaBag_Custom");
                        mesh.name = "Sack"; mesh.transform.SetParent(root.transform, false);
                        var sc = mesh.AddComponent<CapsuleCollider>(); sc.radius = 0.27f; sc.height = 0.78f; sc.center = new Vector3(0f, 0.38f, 0f);
                        var tag = GameObject.CreatePrimitive(PrimitiveType.Cube); tag.name = "Tag";
                        Object.DestroyImmediate(tag.GetComponent<Collider>());
                        tag.transform.SetParent(root.transform, false);
                        tag.transform.localPosition = new Vector3(0f, 0.50f, -0.215f); tag.transform.localScale = new Vector3(0.20f, 0.12f, 0.012f);
                        var tr = tag.GetComponent<MeshRenderer>(); tr.sharedMaterial = TagMaterial(); tr.shadowCastingMode = ShadowCastingMode.Off;
                        root.AddComponent<TeaBag>();
                        break;
                }
                Save(root, $"{kind}_Custom");
            }
        }

        /// <summary>Builds Pistol_Custom / Shotgun_Custom / Rifle_Custom (used by the enemy prefabs) and returns the asset path of a gun prefab.</summary>
        public static string EnsureGun(string kind)
        {
            string prefabPath = $"{PrefabDir}/{kind}_Custom.prefab";
            string fbx = $"{ModelDir}/{kind}_Custom.fbx";
            if (!File.Exists(fbx)) return null;
            EnsureFolder(PrefabDir); EnsureFolder(ModelDir + "/Materials");
            string tdir = $"{ModelDir}/Textures/{kind}_Custom";
            SetupModel(fbx);
            var mat = PbrMaterial($"{kind}_Custom", $"{tdir}/{kind}_Custom_BaseColor.png", $"{tdir}/{kind}_Custom_Normal.png", $"{tdir}/{kind}_Custom_MetalAOSmooth.png");
            var mesh = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(fbx));
            mesh.name = kind;
            AssignOrdered(mesh, mat);
            foreach (var r in mesh.GetComponentsInChildren<MeshRenderer>()) { r.shadowCastingMode = ShadowCastingMode.On; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
            // the gun is modelled in the original primitives' local space (barrel -Y, up +Z); the FBX import turns Blender (x,y,z) into (-x,z,-y): +90 deg about X puts it back
            var root = new GameObject(kind + "_Custom");
            mesh.transform.SetParent(root.transform, false);
            mesh.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            return prefabPath;
        }

        static void TrySetTag(GameObject go, string tag)
        {
            try { go.tag = tag; } catch (UnityException) { Debug.LogWarning($"[Custom Art] Tag '{tag}' is not defined in this project."); }
        }

        static Material TagMaterial()
        {
            string path = $"{ModelDir}/Materials/M_TeaBag_Tag.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", new Color(1f, 0.55f, 0.1f));
            m.EnableKeyword("_EMISSION"); m.SetColor("_EmissionColor", new Color(1f, 0.5f, 0.1f) * 1.5f);
            m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            EditorUtility.SetDirty(m);
            return m;
        }

        static void BuildRockKit()
        {
            string fbx = $"{ModelDir}/Rocks_Custom.fbx";
            string tdir = $"{ModelDir}/Textures/Rocks_Custom";
            SetupModel(fbx);
            var mat = PbrMaterial("Rocks_Custom", $"{tdir}/Rocks_Custom_BaseColor.png", $"{tdir}/Rocks_Custom_Normal.png", $"{tdir}/Rocks_Custom_MetalAOSmooth.png");
            var src = AssetDatabase.LoadAssetAtPath<GameObject>(fbx);
            foreach (var name in new[] { "Rock_A", "Rock_B", "Rock_C", "Rock_D" })
            {
                var inst = Object.Instantiate(src);
                for (int i = inst.transform.childCount - 1; i >= 0; i--)
                    if (inst.transform.GetChild(i).name != name) Object.DestroyImmediate(inst.transform.GetChild(i).gameObject);
                var child = inst.transform.Find(name);
                if (child == null) { Object.DestroyImmediate(inst); continue; }
                inst.name = name + "_Custom";
                AssignOrdered(inst, mat);
                foreach (var r in inst.GetComponentsInChildren<MeshRenderer>()) { r.shadowCastingMode = ShadowCastingMode.On; r.lightProbeUsage = LightProbeUsage.BlendProbes; }
                var mf = child.GetComponent<MeshFilter>();
                var b = mf.sharedMesh.bounds;
                var col = inst.AddComponent<BoxCollider>();          // simple collider: the game mesh is only ~600 triangles but a box is cheaper and never snags
                col.center = b.center; col.size = b.size * 0.92f;
                Save(inst, name + "_Custom");
            }
        }

        // ------------------------------------------------------------------ helpers
        public static void SetupModel(string fbxPath)
        {
            if (!(AssetImporter.GetAtPath(fbxPath) is ModelImporter mi)) return;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.useFileScale = true;
            mi.importCameras = false; mi.importLights = false; mi.importBlendShapes = false; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.addCollider = false;                    // the project rule would add mesh colliders (leaf cards!)
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Low;
            mi.optimizeMeshPolygons = true; mi.optimizeMeshVertices = true;
            mi.weldVertices = true;
            mi.generateSecondaryUV = false;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.SaveAndReimport();
        }

        static void SetupLeafTexture(string path)
        {
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) return;
            ti.textureType = TextureImporterType.Default;
            ti.sRGBTexture = true;
            ti.alphaSource = TextureImporterAlphaSource.FromInput;
            ti.alphaIsTransparency = true;            // dilates colour under transparent pixels: no dark halos
            ti.mipMapsPreserveCoverage = true;        // keeps the alpha-cutout coverage when the tree is far away
            ti.alphaTestReferenceValue = 0.45f;
            ti.mipmapEnabled = true;
            ti.wrapMode = TextureWrapMode.Clamp;
            ti.maxTextureSize = 2048;
            ti.textureCompression = TextureImporterCompression.Compressed;
            ti.SaveAndReimport();
        }

        public static Material PbrMaterial(string name, string basePath, string normalPath, string packedPath, float smoothnessScale = 1f)
        {
            string path = $"{ModelDir}/Materials/M_{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(basePath)); m.SetColor("_BaseColor", Color.white);
            if (!string.IsNullOrEmpty(normalPath)) { m.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(normalPath)); m.EnableKeyword("_NORMALMAP"); }
            if (!string.IsNullOrEmpty(packedPath))
            {
                var ms = AssetDatabase.LoadAssetAtPath<Texture2D>(packedPath);
                m.SetTexture("_MetallicGlossMap", ms); m.EnableKeyword("_METALLICSPECGLOSSMAP");
                m.SetTexture("_OcclusionMap", ms); m.EnableKeyword("_OCCLUSIONMAP");
                m.SetFloat("_Metallic", 1f); m.SetFloat("_Smoothness", smoothnessScale);
            }
            EditorUtility.SetDirty(m);
            return m;
        }

        public static Material LeafMaterial(string name, string basePath)
        {
            string path = $"{ModelDir}/Materials/M_{name}.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(basePath)); m.SetColor("_BaseColor", Color.white);
            m.SetFloat("_AlphaClip", 1f); m.SetFloat("_Cutoff", 0.45f); m.EnableKeyword("_ALPHATEST_ON");
            m.SetFloat("_Cull", 0f);                  // two-sided
            m.SetFloat("_Smoothness", 0.06f); m.SetFloat("_Metallic", 0f);
            m.SetFloat("_SpecularHighlights", 0f); m.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            m.SetFloat("_EnvironmentReflections", 0f); m.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");
            m.renderQueue = (int)RenderQueue.AlphaTest;
            m.SetOverrideTag("RenderType", "TransparentCutout");
            EditorUtility.SetDirty(m);
            return m;
        }

        /// <summary>The FBX is imported without materials, so slots are filled by order: slot 0 = ordered[0], slot 1 = ordered[1] ...</summary>
        public static void AssignOrdered(GameObject root, params Material[] ordered)
        {
            foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                int n = Mathf.Max(r.sharedMaterials.Length, 1);
                var mats = new Material[n];
                for (int i = 0; i < n; i++) mats[i] = ordered[Mathf.Min(i, ordered.Length - 1)];
                r.sharedMaterials = mats;
            }
        }

        static Renderer[] FindRenderers(GameObject root, string suffix)
        {
            var list = new List<Renderer>();
            foreach (var r in root.GetComponentsInChildren<Renderer>(true)) if (r.name.EndsWith(suffix)) list.Add(r);
            return list.ToArray();
        }

        public static void Save(GameObject root, string name)
        {
            string path = $"{PrefabDir}/{name}.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log("[Custom Art] " + path);
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
