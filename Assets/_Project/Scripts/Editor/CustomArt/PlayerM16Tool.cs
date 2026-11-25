using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using LastShift;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// The player's M16A2: a fully automatic 5.56 rifle, the most powerful weapon in the game (weapon 4: hold the left button to fire).
    ///   Build M16 Assets          import settings, material (run once after the Blender export)
    ///   Add M16 To Player         adds a fourth gun to PlayerGun and mounts the model, with the custom hands on the grip and the hand guard, in the player's view
    ///   Remove M16 From Player    takes it out again
    /// Undoable, safe to re-run (no second M16), asks first outside Scenes/Sandbox/. The gameplay builder rebuilds the player on every run: add the M16 again afterwards.
    /// Numbers: 34 damage a bullet (pistol 25), 650 rounds a minute, 30-round magazine, 90 spare (180 max), 120 m range, loud like every gun.
    /// </summary>
    public static class PlayerM16Tool
    {
        const string ArtDir = "Assets/_Project/Art/Characters/Custom";
        const string Fbx = ArtDir + "/M16_Custom.fbx";
        const string MatPath = ArtDir + "/Materials/M_M16_Custom.mat";
        const string HandsFbx = ArtDir + "/FPHands_Custom.fbx";
        const string HandsMat = ArtDir + "/Materials/M_FPHands_Custom.mat";
        const string Marker = "M16";

        [MenuItem("Tools/Custom Art/Build M16 Assets")]
        public static string BuildAssets()
        {
            if (!File.Exists(Fbx)) return "[Custom Art] M16_Custom.fbx not found: run BlenderSource/human/scripts/run_m16.sh first";
            foreach (var role in new[] { "BaseColor", "Normal", "MetalAOSmooth" })
            {
                string p = $"{ArtDir}/Textures/M16_Custom_{role}.png";
                if (!(AssetImporter.GetAtPath(p) is TextureImporter ti)) continue;
                ti.textureType = role == "Normal" ? TextureImporterType.NormalMap : TextureImporterType.Default;
                ti.sRGBTexture = role == "BaseColor";
                ti.alphaSource = role == "MetalAOSmooth" ? TextureImporterAlphaSource.FromInput : TextureImporterAlphaSource.None;
                ti.alphaIsTransparency = false; ti.mipmapEnabled = true; ti.anisoLevel = 8; ti.maxTextureSize = 1024;
                ti.textureCompression = TextureImporterCompression.Compressed; ti.wrapMode = TextureWrapMode.Clamp;
                ti.SaveAndReimport();
            }
            if (AssetImporter.GetAtPath(Fbx) is ModelImporter mi)
            {
                mi.materialImportMode = ModelImporterMaterialImportMode.None; mi.useFileScale = true;
                mi.importCameras = false; mi.importLights = false; mi.importBlendShapes = false; mi.importAnimation = false;
                mi.animationType = ModelImporterAnimationType.None; mi.addCollider = false; mi.isReadable = false;
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
            return "[Custom Art] M16: model, textures and material ready";
        }

        static void Tex(Material m, string prop, string role) => m.SetTexture(prop, AssetDatabase.LoadAssetAtPath<Texture2D>($"{ArtDir}/Textures/M16_Custom_{role}.png"));

        static Mesh Mesh(string fbx, string part) =>
            AssetDatabase.LoadAllAssetsAtPath(fbx).OfType<Mesh>().FirstOrDefault(x => x.name == part || x.name == "LP_" + part || x.name.EndsWith(part));

        [MenuItem("Tools/Custom Art/Add M16 To Player")]
        static void MenuAdd() { if (SceneGuard.Confirm("Add the M16")) Debug.Log(AddM16()); }

        [MenuItem("Tools/Custom Art/Remove M16 From Player")]
        static void MenuRemove() { if (SceneGuard.Confirm("Remove the M16")) Debug.Log(RemoveM16()); }

        public static string AddM16()
        {
            var gun = Object.FindAnyObjectByType<PlayerGun>();
            var pc = gun != null ? gun.GetComponent<PlayerController>() : null;
            if (gun == null || pc == null || pc.eyes == null) return "[Custom Art] M16: no player (PlayerGun + eyes) in the open scene";
            var mesh = Mesh(Fbx, "M16"); var mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
            if (mesh == null || mat == null) return "[Custom Art] M16: run Tools > Custom Art > Build M16 Assets first";
            if (gun.guns.Any(x => x.name == Marker) && pc.eyes.Find(Marker) != null) return "[Custom Art] M16: already on the player (nothing to do)";

            Undo.IncrementCurrentGroup();
            int grp = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Add M16 to player");

            var go = new GameObject(Marker, typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(go, "M16");
            go.layer = pc.eyes.gameObject.layer;
            go.transform.SetParent(pc.eyes, false);
            go.transform.localPosition = new Vector3(0.13f, -0.17f, 0.34f);          // the origin of the model is the pistol grip: this is where the right hand holds it
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;

            var muzzle = new GameObject("Muzzle").transform;
            muzzle.SetParent(go.transform, false);
            muzzle.localPosition = new Vector3(0f, 0.058f, 0.625f);

            // the custom hands: right hand round the pistol grip, left hand under the hand guard
            var hm = AssetDatabase.LoadAssetAtPath<Material>(HandsMat);
            var grip = Mesh(HandsFbx, "GripR"); var fore = Mesh(HandsFbx, "ForeL");
            if (hm != null && grip != null && fore != null)
            {
                MakeHand(go.transform, grip, hm, new Vector3(0f, -0.029f, -0.032f), Quaternion.Euler(20f, 0f, 0f), 1f);
                MakeHand(go.transform, fore, hm, new Vector3(0f, 0.058f, 0.27f), Quaternion.identity, 1.25f);
            }

            var g = new Gun
            {
                name = Marker, sound = GunSound.M16, magSize = 30, mag = 30, reserve = 90, maxReserve = 180,
                damage = 34f, pellets = 1, spread = 1.0f, interval = 0.092f, reloadTime = 2.0f, range = 120f, recoil = 1.6f,
                automatic = true, model = go.transform, muzzle = muzzle,
            };
            Undo.RecordObject(gun, "Add M16 to player");
            gun.guns = gun.guns.Concat(new[] { g }).ToArray();
            EditorUtility.SetDirty(gun);
            Undo.CollapseUndoOperations(grp);
            SceneGuard.MarkDirty();
            return $"[Custom Art] M16 added: weapon 4 (hold to fire), {g.damage} damage, {Mathf.RoundToInt(60f / g.interval)} rounds a minute, {g.magSize}+{g.reserve} rounds";
        }

        static void MakeHand(Transform parent, Mesh mesh, Material mat, Vector3 pos, Quaternion rot, float scale)
        {
            var h = new GameObject("CustomArt_Hand", typeof(MeshFilter), typeof(MeshRenderer));
            Undo.RegisterCreatedObjectUndo(h, "M16 hand");
            h.layer = parent.gameObject.layer;
            h.transform.SetParent(parent, false);
            h.transform.localPosition = pos; h.transform.localRotation = rot; h.transform.localScale = Vector3.one * scale;
            h.GetComponent<MeshFilter>().sharedMesh = mesh;
            var r = h.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat; r.shadowCastingMode = ShadowCastingMode.Off; r.receiveShadows = false;
        }

        public static string RemoveM16()
        {
            var gun = Object.FindAnyObjectByType<PlayerGun>();
            if (gun == null) return "[Custom Art] M16: no player in the open scene";
            Undo.IncrementCurrentGroup();
            int grp = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove M16 from player");
            int removed = 0;
            var keep = gun.guns.Where(x => x.name != Marker).ToArray();
            removed = gun.guns.Length - keep.Length;
            foreach (var g in gun.guns.Where(x => x.name == Marker)) if (g.model != null) Undo.DestroyObjectImmediate(g.model.gameObject);
            Undo.RecordObject(gun, "Remove M16");
            gun.guns = keep;
            EditorUtility.SetDirty(gun);
            Undo.CollapseUndoOperations(grp);
            SceneGuard.MarkDirty();
            return $"[Custom Art] M16 removed ({removed})";
        }
    }
}
