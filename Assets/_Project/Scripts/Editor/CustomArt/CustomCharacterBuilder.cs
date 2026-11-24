using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LastShift;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Builds the custom (Blender-made) human characters as NEW prefabs next to the originals.
    /// It never edits an existing asset: each prefab is a copy of Characters/Guard|Worker|Hunter
    /// (same collider, NavMeshAgent, Health, EnemyAI and CreatureAnimator settings) in which only the
    /// visual "Model" is swapped for the FBX. Props that belong to the original (the gun on Arm_R, the
    /// lantern on Arm_L, the head lamp / torch on Head) are moved across with the same local offsets,
    /// which works because the FBX pivots sit at exactly the same places.
    /// Menu: Tools > Custom Art > Build Custom Characters.
    /// </summary>
    public static class CustomCharacterBuilder
    {
        const string ArtDir = "Assets/_Project/Art/Characters/Custom";
        const string SrcPrefabs = "Assets/_Project/Prefabs/Characters";
        const string OutPrefabs = "Assets/_Project/Prefabs/Characters_Custom";
        static readonly string[] Kinds = { "Guard", "Worker", "Hunter", "Tiger", "Elephant", "Deer" };

        [MenuItem("Tools/Custom Art/Build Custom Characters")]
        public static void BuildAll()
        {
            EnsureFolder(OutPrefabs);
            EnsureFolder(ArtDir + "/Materials");
            int n = 0;
            foreach (var kind in Kinds)
            {
                if (!File.Exists($"{ArtDir}/{kind}_Custom.fbx")) continue;
                Build(kind);
                n++;
            }
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[Custom Art] Built {n} custom character prefab(s) in {OutPrefabs}");
        }

        public static void Build(string kind)
        {
            string fbxPath = $"{ArtDir}/{kind}_Custom.fbx";
            SetupTextures(kind);
            SetupModel(fbxPath);
            var mat = BuildMaterial(kind);

            var src = AssetDatabase.LoadAssetAtPath<GameObject>($"{SrcPrefabs}/{kind}.prefab");
            var fbx = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            if (src == null || fbx == null) { Debug.LogError($"[Custom Art] Missing source for {kind}"); return; }

            var root = (GameObject)PrefabUtility.InstantiatePrefab(src);
            PrefabUtility.UnpackPrefabInstance(root, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            root.name = kind + "_Custom";

            var oldModel = root.transform.Find("Model");
            var inst = Object.Instantiate(fbx);
            // Unity folds the single top-level empty ("Model") into the FBX root, so the root is the model
            var newModel = inst.transform.Find("Model") ?? inst.transform;
            newModel.name = "Model";
            newModel.SetParent(root.transform, false);
            newModel.localPosition = Vector3.zero; newModel.localRotation = Quaternion.identity; newModel.localScale = Vector3.one;
            if (inst != newModel.gameObject) Object.DestroyImmediate(inst);

            // exported flat (see animals2.py): hang the elephant's trunk under its head again, keeping its world position
            var nTrunk = newModel.Find("Trunk"); var nHead = newModel.Find("Head");
            if (nTrunk != null && nHead != null && nTrunk.parent != nHead) nTrunk.SetParent(nHead, true);

            // carry the originals' props over to the matching new pivots (same local offsets)
            string[] carryNames = { "Gun", "Lantern", "Headlamp", "LampBulb", "Flashlight", "Eye" };
            foreach (var pivot in new[] { "Head", "Arm_L", "Arm_R", "Tail", "Trunk" })
            {
                var oldP = oldModel.Find(pivot);
                var newP = newModel.Find(pivot);
                if (oldP == null || newP == null) continue;
                for (int i = oldP.childCount - 1; i >= 0; i--)
                {
                    var c = oldP.GetChild(i);
                    if (System.Array.IndexOf(carryNames, c.name) >= 0) c.SetParent(newP, false);
                }
            }
            // the hunter's hat is taller than the old cap: keep the head lamp (bulb + beam) on the hat band instead of on the crown
            if (kind == "Hunter")
            {
                var head = newModel.Find("Head");
                if (head != null)
                    foreach (var n in new[] { "LampBulb", "Headlamp" })
                    {
                        var lamp = head.Find(n);
                        if (lamp != null) lamp.localPosition = new Vector3(0f, n == "LampBulb" ? 0.168f : 0.172f, 0.128f);
                    }
            }

            // real guns instead of the blocky primitives: same Gun pivot, same Muzzle, new mesh
            string gunKind = kind == "Guard" ? "Pistol" : kind == "Worker" ? "Shotgun" : kind == "Hunter" ? "Rifle" : null;
            var gun = gunKind != null ? newModel.Find("Arm_R/Gun") : null;
            if (gun != null)
            {
                string gp = CustomEnvironmentBuilder.EnsureGun(gunKind);
                var gunPrefab = gp != null ? AssetDatabase.LoadAssetAtPath<GameObject>(gp) : null;
                if (gunPrefab != null)
                {
                    for (int i = gun.childCount - 1; i >= 0; i--)
                        if (gun.GetChild(i).name != "Muzzle") Object.DestroyImmediate(gun.GetChild(i).gameObject);
                    var gm = Object.Instantiate(gunPrefab);
                    gm.name = "GunMesh";
                    gm.transform.SetParent(gun, false);
                    gm.transform.localPosition = Vector3.zero; gm.transform.localRotation = Quaternion.identity;
                }
            }

            Object.DestroyImmediate(oldModel.gameObject);

            foreach (var r in newModel.GetComponentsInChildren<MeshRenderer>(true))
                if (r.GetComponent<MeshFilter>() != null && r.sharedMaterial != null && r.sharedMaterial.name.StartsWith(kind + "_Custom"))
                    r.sharedMaterial = mat;
            foreach (var r in newModel.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf != null && mf.sharedMesh != null && AssetDatabase.GetAssetPath(mf.sharedMesh) == fbxPath)
                {
                    r.sharedMaterial = mat;
                    r.shadowCastingMode = ShadowCastingMode.On;
                    r.receiveShadows = true;
                }
            }

            if (kind == "Worker")
            {
                var anim = root.GetComponent<CreatureAnimator>();
                if (anim != null) anim.stride = 24f;   // the sarong is a static skirt: shorter strides keep the shins inside it
            }

            string path = $"{OutPrefabs}/{kind}_Custom.prefab";
            PrefabUtility.SaveAsPrefabAsset(root, path);
            Object.DestroyImmediate(root);
            Debug.Log($"[Custom Art] {path}");
        }

        // ---------------------------------------------------------------- import settings
        static void SetupTextures(string kind)
        {
            foreach (var role in new[] { "BaseColor", "Normal", "MetalAOSmooth" })
            {
                string p = $"{ArtDir}/Textures/{kind}_Custom_{role}.png";
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) continue;
                ti.textureType = role == "Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = role == "BaseColor";
                ti.alphaSource = role == "MetalAOSmooth" ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = false;
                ti.mipmapEnabled = true;
                ti.anisoLevel = 8;
                ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
        }

        static void SetupModel(string fbxPath)
        {
            if (!(AssetImporter.GetAtPath(fbxPath) is ModelImporter mi)) return;
            mi.materialImportMode = ModelImporterMaterialImportMode.None;
            mi.useFileScale = true;
            mi.importCameras = false; mi.importLights = false; mi.importBlendShapes = false; mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.addCollider = false;                    // gameplay uses the capsule on the root
            mi.isReadable = false;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.optimizeMeshPolygons = true; mi.optimizeMeshVertices = true;
            mi.weldVertices = true;
            mi.generateSecondaryUV = false;            // characters move: lit by light probes / real-time lights
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;
            mi.SaveAndReimport();
        }

        static Material BuildMaterial(string kind)
        {
            string path = $"{ArtDir}/Materials/M_{kind}_Custom.mat";
            var m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            var bc = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArtDir}/Textures/{kind}_Custom_BaseColor.png");
            var nm = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArtDir}/Textures/{kind}_Custom_Normal.png");
            var ms = AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArtDir}/Textures/{kind}_Custom_MetalAOSmooth.png");
            m.SetTexture("_BaseMap", bc); m.SetColor("_BaseColor", Color.white);
            m.SetTexture("_BumpMap", nm); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP");
            m.SetTexture("_MetallicGlossMap", ms); m.EnableKeyword("_METALLICSPECGLOSSMAP");
            m.SetFloat("_Metallic", 1f); m.SetFloat("_Smoothness", 1f);
            m.SetTexture("_OcclusionMap", ms); m.SetFloat("_OcclusionStrength", 1f); m.EnableKeyword("_OCCLUSIONMAP");
            m.SetFloat("_SmoothnessTextureChannel", 0f);
            EditorUtility.SetDirty(m);
            return m;
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
