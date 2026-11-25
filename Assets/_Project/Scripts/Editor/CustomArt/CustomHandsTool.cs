using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// The player's first-person hands. The builder gives every weapon the same blob mesh ("Player_Fist" + "Player_Sleeve");
    /// these tools put real hands in instead: fingers wrapped round the grip, thumb, knuckles, nails, forearm and a rolled shirt sleeve.
    ///   Build Custom Hands                          imports FPHands_Custom.fbx (textures, material). Run once after the Blender export.
    ///   Swap Open Scene Player Hands To Custom      club (right hand), pistol grip (right), shotgun wrist (right), pump (left), slingshot (left)
    ///   Restore Placeholder Hands                   removes the custom hands and switches the old blobs back on
    /// Undoable, safe to re-run (a hand that already has its custom mesh is skipped), asks first outside Scenes/Sandbox/.
    /// The meshes are modelled in the local frame of the thing they hold (see BlenderSource/human/scripts/fphands.py).
    /// </summary>
    public static class CustomHandsTool
    {
        const string ArtDir = "Assets/_Project/Art/Characters/Custom";
        const string Fbx = ArtDir + "/FPHands_Custom.fbx";
        const string MatPath = ArtDir + "/Materials/M_FPHands_Custom.mat";
        public const string Marker = "CustomArt_Hand";

        [MenuItem("Tools/Custom Art/Build Custom Hands")]
        public static void BuildAssets()
        {
            if (!File.Exists(Fbx)) { Debug.LogError("[Custom Art] FPHands_Custom.fbx not found: run BlenderSource/human/scripts/run_fphands.sh first."); return; }
            foreach (var role in new[] { "BaseColor", "Normal", "MetalAOSmooth" })
            {
                string p = $"{ArtDir}/Textures/FPHands_Custom_{role}.png";
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) continue;
                ti.textureType = role == "Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = role == "BaseColor";
                ti.alphaSource = role == "MetalAOSmooth" ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = false; ti.mipmapEnabled = true; ti.anisoLevel = 8; ti.maxTextureSize = 2048;
                ti.textureCompression = TextureImporterCompression.Compressed; ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            if (AssetImporter.GetAtPath(Fbx) is ModelImporter mi)
            {
                mi.materialImportMode = ModelImporterMaterialImportMode.None; mi.useFileScale = true;
                mi.importCameras = false; mi.importLights = false; mi.importBlendShapes = false; mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None; mi.addCollider = false; mi.isReadable = true;
                mi.meshCompression = ModelImporterMeshCompression.Off; mi.optimizeMeshPolygons = true; mi.optimizeMeshVertices = true; mi.weldVertices = true;
                mi.generateSecondaryUV = false; mi.importNormals = ModelImporterNormals.Import; mi.importTangents = ModelImporterTangents.CalculateMikk;
                mi.SaveAndReimport();
            }
            var m = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, MatPath); }
            Tex(m, "_BaseMap", "BaseColor"); m.SetColor("_BaseColor", Color.white);
            Tex(m, "_BumpMap", "Normal"); m.SetFloat("_BumpScale", 1f); m.EnableKeyword("_NORMALMAP");
            Tex(m, "_MetallicGlossMap", "MetalAOSmooth"); m.EnableKeyword("_METALLICSPECGLOSSMAP"); m.SetFloat("_Metallic", 1f); m.SetFloat("_Smoothness", 1f);
            Tex(m, "_OcclusionMap", "MetalAOSmooth"); m.SetFloat("_OcclusionStrength", 1f); m.EnableKeyword("_OCCLUSIONMAP");
            m.SetFloat("_SmoothnessTextureChannel", 0f);
            EditorUtility.SetDirty(m);
            AssetDatabase.SaveAssets();
            FirstPersonArtBuilder.Build();
            Debug.Log("[Custom Art] Hands: model, textures and material ready (" + MatPath + ")");
        }

        static void Tex(Material m, string prop, string role) => m.SetTexture(prop, AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArtDir}/Textures/FPHands_Custom_{role}.png"));

        static Mesh MeshOf(string part) =>
            AssetDatabase.LoadAllAssetsAtPath(Fbx).OfType<Mesh>().FirstOrDefault(x => x.name == part || x.name == "LP_" + part || x.name.EndsWith(part));

        [MenuItem("Tools/Custom Art/Swap Open Scene Player Hands To Custom")]
        static void MenuSwap() { if (SceneGuard.Confirm("Swap the player's hands")) Debug.Log(SwapHands()); }

        [MenuItem("Tools/Custom Art/Restore Placeholder Hands")]
        static void MenuRestore() { if (SceneGuard.Confirm("Restore the player's hands")) Debug.Log(RestoreHands()); }

        // part, path of the parent that gets the new mesh (relative to Eyes), frame the mesh was modelled in (relative to frameRoot)
        struct Spec { public string path, mesh, frameRoot; public Vector3 pos; public Vector3 euler; public float scale; }

        static Spec[] Specs() => new[]
        {
            new Spec { path = "Viewmodel",             mesh = "GripR",  frameRoot = "Viewmodel",         pos = new Vector3(0f, 0.04f, 0.063f),   euler = new Vector3(-15f, 0f, 0f), scale = 1f },     // club
            new Spec { path = "Pistol/Hand",           mesh = "GripR",  frameRoot = "Pistol",            pos = new Vector3(0f, -0.04f, -0.01f),  euler = new Vector3(-14f, 0f, 0f), scale = 1f },
            new Spec { path = "Shotgun/Hand",          mesh = "GripR",  frameRoot = "Shotgun",           pos = new Vector3(0f, -0.025f, -0.02f), euler = new Vector3(-20f, 0f, 0f), scale = 0.92f },
            new Spec { path = "Shotgun/Pump/Hand",     mesh = "ForeL",  frameRoot = "Shotgun/Pump",      pos = Vector3.zero,                      euler = Vector3.zero,               scale = 1f },
            new Spec { path = "Slingshot/Hand",        mesh = "SlingL", frameRoot = "Slingshot",         pos = new Vector3(0f, -0.07f, 0f),       euler = Vector3.zero,               scale = 1f },
        };

        static Transform FindEyes()
        {
            foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsInactive.Include))
                if (t.name == "Eyes" && t.Find("Viewmodel") != null && t.Find("Pistol") != null) return t;
            return null;
        }

        public static string SwapHands(bool ownUndo = true)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mat == null || !File.Exists(Fbx)) return "[Custom Art] hands: run Tools > Custom Art > Build Custom Hands first (needs FPHands_Custom.fbx)";
            var eyes = FindEyes();
            if (eyes == null) return "[Custom Art] hands: no player viewmodel (Eyes/Viewmodel/Pistol) in the open scene";
            int g = 0;
            if (ownUndo) { Undo.IncrementCurrentGroup(); g = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Swap player hands to custom"); }
            var sb = new StringBuilder("[Custom Art] hands: ");
            int done = 0, skipped = 0;
            foreach (var s in Specs())
            {
                var parent = eyes.Find(s.path); var frameRoot = eyes.Find(s.frameRoot); var mesh = MeshOf(s.mesh);
                if (parent == null || frameRoot == null || mesh == null) { sb.Append($"[{s.path} missing] "); continue; }
                if (parent.Find(Marker) != null) { skipped++; continue; }
                var go = new GameObject(Marker, typeof(MeshFilter), typeof(MeshRenderer));
                Undo.RegisterCreatedObjectUndo(go, "Custom hand");
                go.layer = parent.gameObject.layer;
                go.transform.SetParent(parent, false);
                // the mesh was modelled in the frame (frameRoot, pos, euler): put it there in world space, then keep it under the hand pivot so it moves with it
                var wp = frameRoot.TransformPoint(s.pos);
                var wr = frameRoot.rotation * Quaternion.Euler(s.euler);
                go.transform.SetPositionAndRotation(wp, wr);
                go.transform.localScale = Vector3.one * s.scale / Mathf.Max(0.0001f, parent.lossyScale.x);
                go.GetComponent<MeshFilter>().sharedMesh = mesh;
                var r = go.GetComponent<MeshRenderer>();
                r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
                // switch the old blobs off (kept, so Restore can bring them back)
                foreach (var old in parent.GetComponentsInChildren<MeshRenderer>(true))
                {
                    if (old.transform == go.transform) continue;
                    bool blob = (old.name == "Fist" || old.name == "Sleeve") && old.GetComponent<MeshFilter>()?.sharedMesh != null && old.GetComponent<MeshFilter>().sharedMesh.name.StartsWith("Player_");
                    bool sling = s.mesh == "SlingL" && old.transform == parent;
                    if (blob || sling) { Undo.RecordObject(old, "Hide placeholder hand"); old.enabled = false; }
                }
                done++;
                sb.Append($"{s.path}={s.mesh} ");
            }
            if (ownUndo) Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            sb.Append($"| swapped {done}, already done {skipped}");
            return sb.ToString();
        }

        public static string RestoreHands()
        {
            Undo.IncrementCurrentGroup();
            int g = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore placeholder hands");
            int n = 0;
            var eyes = FindEyes();
            if (eyes != null)
                foreach (var t in eyes.GetComponentsInChildren<Transform>(true).Where(x => x.name == Marker).ToList())
                {
                    var parent = t.parent;
                    foreach (var old in parent.GetComponentsInChildren<MeshRenderer>(true))
                    {
                        if (old.transform == t || old.enabled) continue;
                        var mf = old.GetComponent<MeshFilter>();
                        if (mf != null && mf.sharedMesh != null && (mf.sharedMesh.name.StartsWith("Player_") || mf.sharedMesh.name == "Slingshot_Hand")) { Undo.RecordObject(old, "Show placeholder hand"); old.enabled = true; }
                    }
                    Undo.DestroyObjectImmediate(t.gameObject);
                    n++;
                }
            Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            return $"[Custom Art] hands restored: {n}";
        }
    }
}
