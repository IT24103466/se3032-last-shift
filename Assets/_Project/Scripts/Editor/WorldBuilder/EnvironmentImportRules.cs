using UnityEditor;
using UnityEngine;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Import rules for environment art. Runs automatically the first time a texture or
    /// model is imported under <see cref="EnvRoot"/>, so every environment asset gets the
    /// same compression, mipmap and lightmap-UV settings without anyone having to remember.
    ///
    /// Settings you change by hand afterwards are left alone. To force the rules again on
    /// everything, use Tools > World Builder > Re-apply Import Rules to Environment Art.
    ///
    /// Only this folder is affected, so teammates' characters, props and animations keep
    /// whatever settings they choose.
    /// </summary>
    public class EnvironmentImportRules : AssetPostprocessor
    {
        public const string EnvRoot = "Assets/_Project/Art/Environment/";

        // Texture budget: large tiling surfaces (walls, floors) get 2048,
        // data maps (roughness, AO, packed masks, height) are less visible and get 1024.
        const int ColorAndNormalMax = 2048;
        const int DataMapMax = 1024;

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(EnvRoot) || !assetImporter.importSettingsMissing) return;
            ApplyTextureRules((TextureImporter)assetImporter, assetPath);
        }

        void OnPreprocessModel()
        {
            if (!assetPath.StartsWith(EnvRoot) || !assetImporter.importSettingsMissing) return;
            ApplyModelRules((ModelImporter)assetImporter);
        }

        public static void ApplyTextureRules(TextureImporter ti, string path)
        {
            TextureRole role = TextureNaming.Classify(path, out _);

            ti.mipmapEnabled = true;          // avoids shimmering on distant floors and saves bandwidth
            ti.streamingMipmaps = true;       // only the mip levels the camera needs are loaded
            ti.anisoLevel = 4;                // floors seen at grazing angles stay sharp
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.textureCompression = TextureImporterCompression.Compressed; // BC1/BC3/BC5 on PC

            if (role == TextureRole.Normal || role == TextureRole.NormalDX)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.maxTextureSize = ColorAndNormalMax;
            }
            else if (TextureNaming.IsColorData(role))
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = true;
                ti.maxTextureSize = ColorAndNormalMax;
            }
            else
            {
                // Roughness, metal, AO, height and packed masks hold numbers, not colours:
                // they must be linear or the lighting maths comes out wrong.
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                ti.alphaIsTransparency = false;
                ti.maxTextureSize = DataMapMax;
            }
        }

        public static void ApplyModelRules(ModelImporter mi)
        {
            mi.bakeAxisConversion = true;      // Blender Z-up -> Unity Y-up baked into the mesh (no -90° X rotation)
            mi.useFileScale = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.importBlendShapes = false;
            mi.importAnimation = false;        // environment pieces never animate
            mi.animationType = ModelImporterAnimationType.None;
            mi.isReadable = false;             // no CPU-side copy of the mesh: less memory
            mi.meshCompression = ModelImporterMeshCompression.Low;
            mi.optimizeMeshPolygons = true;    // reorders triangles for the GPU vertex cache
            mi.optimizeMeshVertices = true;
            mi.weldVertices = true;
            mi.addCollider = true;             // static mesh colliders so the player and NavMesh see walls/floors
            mi.generateSecondaryUV = true;     // lightmap UVs (UV2) for baked lighting
            mi.secondaryUVPackMargin = 4;      // padding between UV islands to stop light bleeding
        }

        [MenuItem("Tools/World Builder/Re-apply Import Rules to Environment Art", priority = 100)]
        static void ReapplyAll()
        {
            string root = EnvRoot.TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(root))
            {
                Debug.LogWarning($"[World Builder] Folder {root} does not exist yet.");
                return;
            }

            string[] folders = { root };
            var textureGuids = AssetDatabase.FindAssets("t:Texture2D", folders);
            var modelGuids = AssetDatabase.FindAssets("t:Model", folders);
            int count = 0;

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string guid in textureGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetImporter.GetAtPath(path) is TextureImporter ti)
                    {
                        ApplyTextureRules(ti, path);
                        ti.SaveAndReimport();
                        count++;
                    }
                }
                foreach (string guid in modelGuids)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guid);
                    if (AssetImporter.GetAtPath(path) is ModelImporter mi)
                    {
                        ApplyModelRules(mi);
                        mi.SaveAndReimport();
                        count++;
                    }
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }

            Debug.Log($"[World Builder] Re-applied import rules to {count} assets in {root}.");
        }
    }
}
