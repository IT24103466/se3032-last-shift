using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Select the textures of one or more PBR sets in the Project window, right-click,
    /// Assets > World Builder > Build URP Material From Selected Textures.
    ///
    /// For each set it:
    ///  1. Packs metal + AO + smoothness into ONE texture (R = metal, G = AO, A = smoothness).
    ///     URP Lit reads metal from R and smoothness from A of the Metallic map, and reads
    ///     occlusion from G of the Occlusion map, so the same texture fills both slots:
    ///     three or four greyscale maps become one sample and one texture in memory.
    ///     Roughness is inverted on the way in (URP wants smoothness = 1 - roughness).
    ///  2. Creates/updates M_[SetName].mat (URP/Lit) in the environment Materials folder
    ///     with base colour, normal, packed mask and emission assigned.
    ///
    /// Height/displacement maps are intentionally not used: parallax costs extra texture
    /// samples per pixel for little gain at shooter camera distances.
    /// </summary>
    public static class UrpMaterialBuilder
    {
        const string MenuPath = "Assets/World Builder/Build URP Material From Selected Textures";
        const string MaterialFolder = "Assets/_Project/Art/Environment/Materials";

        [MenuItem(MenuPath, true)]
        static bool Validate() => Selection.GetFiltered<Texture2D>(SelectionMode.Assets).Length > 0;

        [MenuItem(MenuPath, false, 1000)]
        static void Build()
        {
            var textures = Selection.GetFiltered<Texture2D>(SelectionMode.Assets);
            var sets = new Dictionary<string, Dictionary<TextureRole, Texture2D>>();

            foreach (var tex in textures)
            {
                string path = AssetDatabase.GetAssetPath(tex);
                TextureRole role = TextureNaming.Classify(path, out string setName);
                if (role == TextureRole.PackedMask) continue; // our own output; rebuilt below
                if (role == TextureRole.Unknown)
                {
                    Debug.LogWarning($"[World Builder] Can't tell what '{tex.name}' is from its name; skipped. " +
                                     "Add a suffix such as _BaseColor, _Normal, _Roughness, _AO, _Metallic.");
                    continue;
                }
                if (!sets.TryGetValue(setName, out var maps))
                    sets[setName] = maps = new Dictionary<TextureRole, Texture2D>();
                maps[role] = tex;
            }

            foreach (var kv in sets) BuildMaterial(kv.Key, kv.Value);
            AssetDatabase.SaveAssets();
        }

        static void BuildMaterial(string setName, Dictionary<TextureRole, Texture2D> maps)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[World Builder] URP Lit shader not found. Is the project using the Universal Render Pipeline?");
                return;
            }

            EnsureFolder(MaterialFolder);
            string matPath = $"{MaterialFolder}/M_{setName}.mat";
            var mat = AssetDatabase.LoadAssetAtPath<Material>(matPath);
            if (mat == null)
            {
                mat = new Material(shader);
                AssetDatabase.CreateAsset(mat, matPath);
            }
            else mat.shader = shader;

            // Base colour
            if (maps.TryGetValue(TextureRole.BaseColor, out var baseMap))
            {
                mat.SetTexture("_BaseMap", baseMap);
                mat.SetColor("_BaseColor", Color.white);
            }

            // Normal (OpenGL convention, which is what Unity expects)
            maps.TryGetValue(TextureRole.Normal, out var normal);
            if (normal == null && maps.TryGetValue(TextureRole.NormalDX, out var normalDX))
            {
                normal = normalDX;
                Debug.LogWarning($"[World Builder] {setName}: only a DirectX normal map was found. Its green channel is " +
                                 "flipped for Unity, so bumps will look inverted. Download the OpenGL version (nor_gl / NormalGL).");
            }
            if (normal != null)
            {
                ForceNormalMapImport(normal);
                mat.SetTexture("_BumpMap", normal);
                mat.SetFloat("_BumpScale", 1f);
                mat.EnableKeyword("_NORMALMAP");
            }

            // Metal / AO / Smoothness packed into one texture
            Texture2D packed = PackMetalAoSmooth(setName, maps, out bool hasAo);
            if (packed != null)
            {
                mat.SetTexture("_MetallicGlossMap", packed);
                mat.SetFloat("_Smoothness", 1f);                // multiplier on the map's alpha
                mat.SetFloat("_SmoothnessTextureChannel", 0f);  // 0 = alpha of the metallic map
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (hasAo)
                {
                    mat.SetTexture("_OcclusionMap", packed);    // URP samples occlusion from G
                    mat.SetFloat("_OcclusionStrength", 1f);
                    mat.EnableKeyword("_OCCLUSIONMAP");
                }
            }
            else
            {
                mat.SetFloat("_Metallic", 0f);
                mat.SetFloat("_Smoothness", 0.3f);
            }

            // Emission: glowing panels/signs also light the level in the lightmap bake
            if (maps.TryGetValue(TextureRole.Emission, out var emission))
            {
                mat.SetTexture("_EmissionMap", emission);
                mat.SetColor("_EmissionColor", Color.white);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
            }

            EditorUtility.SetDirty(mat);
            Debug.Log($"[World Builder] Material ready: {matPath}", mat);
        }

        // ------------------------------------------------------------------ packing

        static Texture2D PackMetalAoSmooth(string setName, Dictionary<TextureRole, Texture2D> maps, out bool hasAo)
        {
            maps.TryGetValue(TextureRole.Metallic, out var metalAsset);
            maps.TryGetValue(TextureRole.Roughness, out var roughAsset);
            maps.TryGetValue(TextureRole.Smoothness, out var smoothAsset);
            maps.TryGetValue(TextureRole.AmbientOcclusion, out var aoAsset);
            maps.TryGetValue(TextureRole.ARM, out var armAsset);

            hasAo = aoAsset != null || armAsset != null;
            Texture2D first = metalAsset ?? roughAsset ?? smoothAsset ?? aoAsset ?? armAsset;
            if (first == null) return null;

            var loaded = new List<Texture2D>();
            Texture2D metal = Load(metalAsset, loaded), rough = Load(roughAsset, loaded),
                      smooth = Load(smoothAsset, loaded), ao = Load(aoAsset, loaded), arm = Load(armAsset, loaded);

            int w = 0, h = 0;
            foreach (var t in loaded) { w = Mathf.Max(w, t.width); h = Mathf.Max(h, t.height); }

            Color32[] pm = Pixels(metal, w, h), pr = Pixels(rough, w, h), ps = Pixels(smooth, w, h),
                      pa = Pixels(ao, w, h), parm = Pixels(arm, w, h);

            var outPixels = new Color32[w * h];
            for (int i = 0; i < outPixels.Length; i++)
            {
                byte m = pm != null ? pm[i].r : parm != null ? parm[i].b : (byte)0;
                byte occ = pa != null ? pa[i].r : parm != null ? parm[i].r : (byte)255;
                byte s;
                if (ps != null) s = ps[i].r;
                else if (pr != null) s = (byte)(255 - pr[i].r);
                else if (parm != null) s = (byte)(255 - parm[i].g);
                else s = 77; // ~0.3 smoothness when no roughness data exists
                outPixels[i] = new Color32(m, occ, 0, s);
            }

            var outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            outTex.SetPixels32(outPixels);
            outTex.Apply();

            string dir = Path.GetDirectoryName(AssetDatabase.GetAssetPath(first)).Replace('\\', '/');
            string outPath = $"{dir}/{setName}_MetalAOSmooth.png";
            File.WriteAllBytes(outPath, outTex.EncodeToPNG());

            Object.DestroyImmediate(outTex);
            foreach (var t in loaded) Object.DestroyImmediate(t);

            AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
            if (AssetImporter.GetAtPath(outPath) is TextureImporter ti)
            {
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.alphaIsTransparency = false;
                ti.mipmapEnabled = true;
                ti.textureCompression = TextureImporterCompression.Compressed;
                ti.SaveAndReimport();
            }
            return AssetDatabase.LoadAssetAtPath<Texture2D>(outPath);
        }

        /// <summary>Reads the original file from disk so we pack full-quality pixels, not the compressed import.</summary>
        static Texture2D Load(Texture2D asset, List<Texture2D> loaded)
        {
            if (asset == null) return null;
            string path = AssetDatabase.GetAssetPath(asset);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            Texture2D tex;

            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg")
            {
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                tex.LoadImage(File.ReadAllBytes(path));
            }
            else
            {
                // TGA/TIF/EXR etc: read the imported texture back through the GPU instead.
                Debug.LogWarning($"[World Builder] {asset.name}: {ext} can't be read directly; using the imported (compressed) copy.");
                var rt = RenderTexture.GetTemporary(asset.width, asset.height, 0,
                                                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                Graphics.Blit(asset, rt);
                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                tex = new Texture2D(asset.width, asset.height, TextureFormat.RGBA32, false, true);
                tex.ReadPixels(new Rect(0, 0, asset.width, asset.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }

            loaded.Add(tex);
            return tex;
        }

        /// <summary>Pixels at the output size, resampling only when a map's size differs.</summary>
        static Color32[] Pixels(Texture2D tex, int w, int h)
        {
            if (tex == null) return null;
            if (tex.width == w && tex.height == h) return tex.GetPixels32();

            var result = new Color32[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                result[y * w + x] = tex.GetPixelBilinear((x + 0.5f) / w, (y + 0.5f) / h);
            return result;
        }

        static void ForceNormalMapImport(Texture2D tex)
        {
            if (AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(tex)) is TextureImporter ti &&
                ti.textureType != TextureImporterType.NormalMap)
            {
                ti.textureType = TextureImporterType.NormalMap;
                ti.SaveAndReimport();
            }
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
