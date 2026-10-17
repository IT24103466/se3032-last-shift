using System;
using System.IO;

namespace WorldBuilder.EditorTools
{
    /// <summary>What a texture is used for, worked out from its file name.</summary>
    public enum TextureRole
    {
        Unknown,
        BaseColor,
        Normal,
        NormalDX,
        Roughness,
        Smoothness,
        Metallic,
        AmbientOcclusion,
        ARM,          // Poly Haven packed map: R = AO, G = Roughness, B = Metal
        Height,
        Emission,
        PackedMask    // our own output: R = Metal, G = AO, A = Smoothness
    }

    /// <summary>
    /// Reads the role of a texture from its name, so texture sets downloaded from
    /// Poly Haven ("red_brick_diff_2k.png", "red_brick_nor_gl_2k.png") or ambientCG
    /// ("Bricks076_2K-PNG_Color.png", "Bricks076_2K-PNG_NormalGL.png") are recognised
    /// without renaming. The set name is everything in front of the role word.
    /// </summary>
    public static class TextureNaming
    {
        static readonly char[] Separators = { '_', '-', ' ', '.' };

        static readonly (TextureRole role, string[] words)[] Rules =
        {
            (TextureRole.PackedMask,       new[] { "metalaosmooth" }),
            (TextureRole.NormalDX,         new[] { "normaldx", "nrmdx" }),
            (TextureRole.Normal,           new[] { "normal", "normalgl", "nor", "nrm", "norm" }),
            (TextureRole.ARM,              new[] { "arm" }),
            (TextureRole.Roughness,        new[] { "rough", "roughness" }),
            (TextureRole.Smoothness,       new[] { "smoothness", "gloss", "glossiness" }),
            (TextureRole.Metallic,         new[] { "metal", "metallic", "metalness" }),
            (TextureRole.AmbientOcclusion, new[] { "ao", "ambientocclusion", "occlusion" }),
            (TextureRole.Height,           new[] { "height", "disp", "displacement" }),
            (TextureRole.Emission,         new[] { "emission", "emissive", "emit" }),
            (TextureRole.BaseColor,        new[] { "basecolor", "albedo", "diffuse", "diff", "color", "col", "base" }),
        };

        public static TextureRole Classify(string assetPath, out string setName)
        {
            string file = Path.GetFileNameWithoutExtension(assetPath);
            string[] tokens = file.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

            // Scan from the end: the role word comes after the material name
            // ("metal_plate_rough_2k" is a roughness map, not a metal map).
            for (int i = tokens.Length - 1; i >= 0; i--)
            {
                string t = tokens[i].ToLowerInvariant();
                foreach (var (role, words) in Rules)
                {
                    if (Array.IndexOf(words, t) < 0) continue;

                    TextureRole result = role;
                    // Poly Haven DirectX normals: "..._nor_dx_2k"
                    if (result == TextureRole.Normal && i + 1 < tokens.Length &&
                        tokens[i + 1].Equals("dx", StringComparison.OrdinalIgnoreCase))
                        result = TextureRole.NormalDX;

                    setName = i > 0 ? string.Join("_", tokens, 0, i) : file;
                    return result;
                }
            }

            setName = file;
            return TextureRole.Unknown;
        }

        /// <summary>Colour textures are stored in sRGB; everything else is linear data.</summary>
        public static bool IsColorData(TextureRole role) =>
            role == TextureRole.BaseColor || role == TextureRole.Emission || role == TextureRole.Unknown;
    }
}
