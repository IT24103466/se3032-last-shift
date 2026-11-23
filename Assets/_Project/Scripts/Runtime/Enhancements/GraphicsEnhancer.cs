using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace LastShift
{
    /// <summary>Runtime graphics upgrade. Works on a CLONE of the URP asset and a runtime Volume, so shared project assets are never modified.</summary>
    public class GraphicsEnhancer : MonoBehaviour
    {
        static UniversalRenderPipelineAsset clone;
        Vignette vignette;
        ColorAdjustments color;
        ChromaticAberration chroma;
        PlayerVitality vitality;
        float baseVig = 0.30f;

        static void Set(object o, string prop, object val)
        {
            try
            {
                var p = o.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null && p.CanWrite) p.SetValue(o, val);
            }
            catch (Exception) { }
        }

        void Start()
        {
            try { UpgradePipeline(); } catch (Exception e) { Debug.LogWarning("[Sanjula] pipeline upgrade skipped: " + e.Message); }
            try { BuildVolume(); } catch (Exception e) { Debug.LogWarning("[Sanjula] volume skipped: " + e.Message); }
            try { UpgradeCamera(); } catch (Exception e) { Debug.LogWarning("[Sanjula] camera skipped: " + e.Message); }
            try { UpgradeWorld(); } catch (Exception e) { Debug.LogWarning("[Sanjula] world skipped: " + e.Message); }
            vitality = GetComponent<PlayerVitality>();
        }

        void UpgradePipeline()
        {
            var cur = QualitySettings.renderPipeline as UniversalRenderPipelineAsset
                      ?? GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (cur == null) return;
            if (clone == null)
            {
                clone = Instantiate(cur);
                clone.name = cur.name + "_SanjulaHQ";
                Set(clone, "msaaSampleCount", 4);
                Set(clone, "supportsHDR", true);
                Set(clone, "renderScale", 1f);
                Set(clone, "shadowDistance", 90f);
                Set(clone, "shadowCascadeCount", 4);
                Set(clone, "mainLightShadowmapResolution", 4096);
                Set(clone, "supportsSoftShadows", true);
                Set(clone, "softShadowQuality", Enum.ToObject(typeof(SoftShadowQuality), 3));
                Set(clone, "supportsMainLightShadows", true);
                Set(clone, "supportsAdditionalLightShadows", true);
                Set(clone, "additionalLightsShadowmapResolution", 4096);
            }
            QualitySettings.renderPipeline = clone;
            QualitySettings.lodBias = Mathf.Max(QualitySettings.lodBias, 2.5f);
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
            QualitySettings.globalTextureMipmapLimit = 0;
        }

        void BuildVolume()
        {
            var go = new GameObject("Sanjula_PostFX");
            go.transform.SetParent(transform, false);
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 50f; vol.weight = 1f;
            var p = ScriptableObject.CreateInstance<VolumeProfile>();
            var tone = p.Add<Tonemapping>(true); tone.mode.Override(TonemappingMode.ACES);
            var bloom = p.Add<Bloom>(true);
            bloom.threshold.Override(0.95f); bloom.intensity.Override(0.9f); bloom.scatter.Override(0.72f);
            bloom.tint.Override(new Color(1f, 0.88f, 0.75f));
            color = p.Add<ColorAdjustments>(true);
            color.postExposure.Override(0.55f); color.contrast.Override(18f); color.saturation.Override(6f);
            color.colorFilter.Override(new Color(1f, 0.97f, 0.92f));
            var split = p.Add<SplitToning>(true);
            split.shadows.Override(new Color(0.40f, 0.50f, 0.62f)); split.highlights.Override(new Color(0.85f, 0.65f, 0.42f));
            vignette = p.Add<Vignette>(true);
            vignette.intensity.Override(baseVig); vignette.smoothness.Override(0.5f);
            vignette.color.Override(new Color(0.02f, 0.02f, 0.04f));
            var grain = p.Add<FilmGrain>(true);
            grain.type.Override(FilmGrainLookup.Thin1); grain.intensity.Override(0.22f); grain.response.Override(0.8f);
            chroma = p.Add<ChromaticAberration>(true); chroma.intensity.Override(0.08f);
            var mb = p.Add<MotionBlur>(true); mb.intensity.Override(0.18f); mb.quality.Override(MotionBlurQuality.Medium);
            var dof = p.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Gaussian);
            dof.gaussianStart.Override(70f); dof.gaussianEnd.Override(260f); dof.gaussianMaxRadius.Override(0.9f);
            vol.sharedProfile = p;
        }

        void UpgradeCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var d = cam.GetUniversalAdditionalCameraData();
            d.renderPostProcessing = true;
            d.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            d.antialiasingQuality = AntialiasingQuality.High;
            d.dithering = true;
            d.stopNaN = true;
            d.renderShadows = true;
            cam.allowHDR = true;
            cam.allowMSAA = true;
        }

        void UpgradeWorld()
        {
            foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            {
                if (l.type == LightType.Directional) { l.shadows = LightShadows.Soft; l.shadowStrength = Mathf.Max(l.shadowStrength, 0.95f); l.shadowNormalBias = 0.8f; }
                else if (l.GetComponentInParent<PlayerController>() != null) { l.shadows = LightShadows.Soft; }
            }
            foreach (var t in Terrain.activeTerrains)
            {
                t.heightmapPixelError = 1f;
                t.basemapDistance = 400f;
                t.detailObjectDistance = 130f;
                t.detailObjectDensity = 1f;
                t.treeDistance = 600f;
                t.treeBillboardDistance = 120f;
                t.drawInstanced = true;
            }
        }

        void Update()
        {
            if (vignette == null || vitality == null) return;
            float hurt = 1f - vitality.Fraction;
            float pulse = vitality.Fraction < 0.4f ? (Mathf.Sin(Time.time * (4f + hurt * 4f)) * 0.5f + 0.5f) * 0.12f * hurt : 0f;
            vignette.intensity.value = baseVig + hurt * 0.38f + pulse;
            vignette.color.value = Color.Lerp(new Color(0.02f, 0.02f, 0.04f), new Color(0.35f, 0.0f, 0.02f), hurt);
            color.saturation.value = Mathf.Lerp(6f, -45f, hurt * hurt);
            chroma.intensity.value = 0.08f + hurt * 0.5f;
        }
    }
}
