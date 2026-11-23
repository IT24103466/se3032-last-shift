using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using LastShift;
using LastShift.CustomArtSandbox;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Builds Assets/_Project/Scenes/Sandbox/Sandbox_Models.unity: a night test bench that shows the original
    /// primitive characters beside the custom ones under the game's blue-hour lighting (moon, cold ambient,
    /// mist, sodium lamp, the Level_01 post-processing profile referenced read-only).
    /// It only ever creates/overwrites its own scene file and refuses to run over an unsaved open scene.
    /// Menu: Tools > Custom Art > Build Sandbox Scene.
    /// </summary>
    public static class CustomSandboxBuilder
    {
        const string SceneDir = "Assets/_Project/Scenes/Sandbox";
        const string ScenePath = SceneDir + "/Sandbox_Models.unity";

        [MenuItem("Tools/Custom Art/Build Sandbox Scene")]
        public static void Build()
        {
            if (EditorSceneManager.GetActiveScene().isDirty && EditorSceneManager.GetActiveScene().path != ScenePath)
            {
                Debug.LogWarning("[Custom Art] The open scene has unsaved changes: save or close it first. Sandbox not built.");
                return;
            }
            if (!AssetDatabase.IsValidFolder(SceneDir)) AssetDatabase.CreateFolder("Assets/_Project/Scenes", "Sandbox");

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // lighting: same numbers as KirimalaLightingBuilder (blue hour + mist), real-time only
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.24f, 0.30f, 0.44f);
            RenderSettings.ambientEquatorColor = new Color(0.15f, 0.19f, 0.28f);
            RenderSettings.ambientGroundColor = new Color(0.05f, 0.06f, 0.08f);
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.30f, 0.36f, 0.46f);
            RenderSettings.fogDensity = 0.015f;

            var moon = new GameObject("Moonlight").AddComponent<Light>();
            moon.type = LightType.Directional; moon.color = new Color(0.62f, 0.72f, 1f); moon.intensity = 0.55f; moon.shadows = LightShadows.Soft;
            moon.transform.rotation = Quaternion.Euler(35f, -35f, 0f);
            RenderSettings.sun = moon;

            var lamp = new GameObject("SodiumLamp").AddComponent<Light>();
            lamp.type = LightType.Point; lamp.color = new Color(1f, 0.62f, 0.28f); lamp.range = 12f; lamp.intensity = 12f; lamp.shadows = LightShadows.Soft;
            lamp.transform.position = new Vector3(0f, 3.4f, -3.2f);

            // ground
            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground"; ground.transform.localScale = new Vector3(6f, 1f, 6f);
            var gm = new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "M_SandboxGround" };
            gm.SetColor("_BaseColor", new Color(0.10f, 0.095f, 0.08f)); gm.SetFloat("_Smoothness", 0.1f);
            AssetDatabase.CreateAsset(gm, SceneDir + "/M_SandboxGround.mat");
            ground.GetComponent<MeshRenderer>().sharedMaterial = gm;

            // post-processing: the game's own profile, only referenced
            var postFx = AssetDatabase.LoadAssetAtPath<VolumeProfile>("Assets/_Project/Scenes/Main/Level_01/Level_01_PostFX.asset");
            if (postFx != null)
            {
                var v = new GameObject("PostFX").AddComponent<Volume>();
                v.isGlobal = true; v.sharedProfile = postFx;
            }

            // camera
            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            camGo.AddComponent<AudioListener>();
            cam.fieldOfView = 42f; cam.nearClipPlane = 0.05f; cam.farClipPlane = 200f;
            cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = RenderSettings.fogColor;
            camGo.transform.position = new Vector3(0f, 1.6f, -7.5f);
            camGo.transform.rotation = Quaternion.Euler(4f, 0f, 0f);
            var cd = camGo.AddComponent<UnityEngine.Rendering.Universal.UniversalAdditionalCameraData>();
            cd.renderPostProcessing = true;

            // characters: originals on the left, custom on the right (facing the camera = -Z)
            string[] kinds = { "Guard", "Worker", "Hunter" };
            for (int i = 0; i < kinds.Length; i++)
            {
                Place($"Assets/_Project/Prefabs/Characters/{kinds[i]}.prefab", new Vector3(-5f + i * 1.8f, 0f, 0f));
                Place($"Assets/_Project/Prefabs/Characters_Custom/{kinds[i]}_Custom.prefab", new Vector3(1.4f + i * 1.8f, 0f, 0f));
            }

            // animals: originals left, custom right, a row further back (only if the custom prefab exists)
            string[] animals = { "Tiger", "Deer", "Elephant" };
            float[] ax = { 0f, 4f, 9f };
            for (int i = 0; i < animals.Length; i++)
            {
                string cp = $"Assets/_Project/Prefabs/Characters_Custom/{animals[i]}_Custom.prefab";
                if (AssetDatabase.LoadAssetAtPath<GameObject>(cp) == null) continue;
                Place($"Assets/_Project/Prefabs/Characters/{animals[i]}.prefab", new Vector3(-6f - ax[i], 0f, 7f));
                Place(cp, new Vector3(-1f + ax[i] + 6f, 0f, 7f));
            }

            // environment models (trees, bushes, rocks, props) in a row behind the characters
            float ex = -14f;
            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/_Project/Prefabs/Environment_Custom" }))
            {
                var ep = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
                if (ep == null) continue;
                var eg = (GameObject)PrefabUtility.InstantiatePrefab(ep);
                var b = new Bounds(eg.transform.position, Vector3.zero);
                foreach (var r in eg.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                float w = Mathf.Max(b.size.x, b.size.z, 1f);
                eg.transform.position = new Vector3(ex + w * 0.5f, 0f, 16f);
                eg.name = ep.name;
                ex += w + 3f;
            }

            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.SaveAssets();
            Debug.Log("[Custom Art] Sandbox scene saved: " + ScenePath);
        }

        /// <summary>
        /// Test helper: renders the sandbox "Main Camera" from the poses listed in a text file
        /// (one per line: name px py pz rx ry rz fov) into PNGs in outDir. Works in edit and play mode.
        /// </summary>
        public static void CaptureShots(string shotsFile, string outDir, int w = 1600, int h = 900)
        {
            var camGo = GameObject.Find("Main Camera");
            if (camGo == null) { Debug.LogError("[Custom Art] No Main Camera"); return; }
            var cam = camGo.GetComponent<Camera>();
            Directory.CreateDirectory(outDir);
            var savedPos = camGo.transform.position; var savedRot = camGo.transform.rotation; var savedFov = cam.fieldOfView;
            foreach (var line in File.ReadAllLines(shotsFile))
            {
                var p = line.Trim().Split(' ');
                if (p.Length < 8) continue;
                float F(int i) => float.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
                camGo.transform.position = new Vector3(F(1), F(2), F(3));
                camGo.transform.rotation = Quaternion.Euler(F(4), F(5), F(6));
                cam.fieldOfView = F(7);
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                var old = cam.targetTexture; cam.targetTexture = rt; cam.Render(); cam.targetTexture = old;
                RenderTexture.active = rt;
                var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
                File.WriteAllBytes(Path.Combine(outDir, p[0] + ".png"), tex.EncodeToPNG());
                RenderTexture.active = null; Object.DestroyImmediate(rt); Object.DestroyImmediate(tex);
            }
            camGo.transform.position = savedPos; camGo.transform.rotation = savedRot; cam.fieldOfView = savedFov;
        }

        static void Place(string prefabPath, Vector3 pos)
        {
            var p = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (p == null) return;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(p);
            go.transform.position = pos;
            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            // no NavMesh in the sandbox: the AI and the agent stay off, the walker drives the animation instead
            var ai = go.GetComponent<EnemyAI>(); if (ai != null) ai.enabled = false;
            var ag = go.GetComponent<NavMeshAgent>(); if (ag != null) ag.enabled = false;
            var w = go.AddComponent<SandboxWalker>();
            w.cycleStates = false; w.forcedState = 0;
        }
    }
}
