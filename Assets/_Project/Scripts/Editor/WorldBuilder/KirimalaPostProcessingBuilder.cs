using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Post-Processing.
    ///
    /// Adds one global URP Volume under --- LIGHTING --- / PostProcessing with a profile
    /// saved next to the scene (Level_01/Level_01_PostFX.asset). The grade supports the
    /// "blue hour in monsoon mist" art direction:
    ///   Tonemapping (ACES)   bright sodium lamps roll off to orange instead of clipping to white
    ///   Bloom                lamps glow softly through the mist (only pixels brighter than 1.1)
    ///   Color Adjustments    small exposure lift (the baked level reads dark), a bit more
    ///                        contrast, slightly desaturated for a damp night
    ///   Split Toning         blue shadows, warm highlights: cold moonlight vs sodium lamps
    ///   Vignette             dark edges focus the eye and add tension
    ///
    /// Also switches on Post Processing on the Main Camera (URP cameras have it off by default).
    /// Running it again overwrites the profile values and reuses the same Volume.
    /// </summary>
    public static class KirimalaPostProcessingBuilder
    {
        [MenuItem("Tools/World Builder/Build Kirimala Post-Processing", priority = 82)]
        public static void Build()
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[World Builder] Save the scene before building post-processing.");
                return;
            }

            LevelBakeTool.CreateHierarchy();
            var lighting = LevelBakeTool.FindRoot(LevelBakeTool.LightingRoot).transform;
            var parent = lighting.Find("PostProcessing");

            // ---- profile asset next to the scene, so it is committed with the level
            string dir = System.IO.Path.GetDirectoryName(scene.path).Replace('\\', '/');
            string folder = dir + "/" + scene.name;
            if (!AssetDatabase.IsValidFolder(folder)) AssetDatabase.CreateFolder(dir, scene.name);
            string path = $"{folder}/{scene.name}_PostFX.asset";

            var profile = AssetDatabase.LoadAssetAtPath<VolumeProfile>(path);
            if (profile == null)
            {
                profile = ScriptableObject.CreateInstance<VolumeProfile>();
                AssetDatabase.CreateAsset(profile, path);
            }

            var tone = Get<Tonemapping>(profile);
            tone.mode.Override(TonemappingMode.ACES);

            var bloom = Get<Bloom>(profile);
            bloom.threshold.Override(1.1f);   // only the lamps and bright highlights bloom
            bloom.intensity.Override(0.6f);
            bloom.scatter.Override(0.75f);    // wide, soft halo = light scattering in mist
            bloom.tint.Override(new Color(1f, 0.85f, 0.7f));

            var color = Get<ColorAdjustments>(profile);
            color.postExposure.Override(0.7f);
            color.contrast.Override(12f);
            color.saturation.Override(-10f);

            var split = Get<SplitToning>(profile);
            split.shadows.Override(new Color(0.42f, 0.48f, 0.60f));    // moonlit blue, kept subtle: ambient and fog are already blue
            split.highlights.Override(new Color(0.75f, 0.58f, 0.40f)); // sodium orange
            split.balance.Override(0f);

            var vignette = Get<Vignette>(profile);
            vignette.intensity.Override(0.25f);
            vignette.smoothness.Override(0.45f);
            vignette.color.Override(new Color(0.02f, 0.03f, 0.06f));

            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssets();

            // ---- global volume
            var volumeGo = parent.Find("Global Volume")?.gameObject;
            if (volumeGo == null)
            {
                volumeGo = new GameObject("Global Volume");
                Undo.RegisterCreatedObjectUndo(volumeGo, "Create Global Volume");
                volumeGo.transform.SetParent(parent, false);
            }
            var volume = volumeGo.GetComponent<Volume>();
            if (volume == null) volume = Undo.AddComponent<Volume>(volumeGo);
            Undo.RecordObject(volume, "Global Volume");
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.sharedProfile = profile;

            // ---- camera: URP renders post-processing only on cameras that opt in
            var cam = Camera.main;
            if (cam == null)
            {
                var go = LevelBakeTool.FindRoot("Main Camera");
                if (go != null) cam = go.GetComponent<Camera>();
            }
            if (cam != null)
            {
                var data = cam.GetUniversalAdditionalCameraData();
                Undo.RecordObject(data, "Enable post-processing");
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                EditorUtility.SetDirty(data);
            }
            else Debug.LogWarning("[World Builder] No Main Camera found: enable Post Processing on your camera by hand.");

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[World Builder] Post-processing ready: ACES, bloom, colour grade, split toning, vignette ({path}).");
        }

        static T Get<T>(VolumeProfile profile) where T : VolumeComponent
        {
            if (!profile.TryGet(out T c))
            {
                c = profile.Add<T>(false);
                c.name = typeof(T).Name;
                AssetDatabase.AddObjectToAsset(c, profile); // sub-asset, or it is lost on reload
            }
            c.active = true;
            return c;
        }
    }
}
