using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Render Character Previews.
    /// Renders each character prefab (side, three-quarter and front) in a neutral studio light
    /// and saves the images to Docs/CharacterPreviews/, e.g. for the report and the viva.
    /// </summary>
    public static class CharacterPreviewRenderer
    {
        [MenuItem("Tools/World Builder/Render Character Previews", priority = 88)]
        public static void Render()
        {
            string outDir = Path.Combine(Directory.GetCurrentDirectory(), "Docs", "CharacterPreviews");
            Directory.CreateDirectory(outDir);
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var lightGo = new GameObject("Key");
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional; light.intensity = 1.3f;
                lightGo.transform.rotation = Quaternion.Euler(40f, -30f, 0f);
                EditorSceneManager.MoveGameObjectToScene(lightGo, scene);
                var fillGo = new GameObject("Fill");
                var fill = fillGo.AddComponent<Light>();
                fill.type = LightType.Directional; fill.intensity = 0.5f; fill.color = new Color(0.75f, 0.85f, 1f);
                fillGo.transform.rotation = Quaternion.Euler(20f, 150f, 0f);
                EditorSceneManager.MoveGameObjectToScene(fillGo, scene);

                var camGo = new GameObject("Camera");
                var cam = camGo.AddComponent<Camera>();
                cam.scene = scene;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0.32f, 0.36f, 0.42f);
                cam.fieldOfView = 30f;
                cam.GetUniversalAdditionalCameraData().renderPostProcessing = false;
                EditorSceneManager.MoveGameObjectToScene(camGo, scene);
                var rt = new RenderTexture(900, 700, 24);
                cam.targetTexture = rt;

                foreach (var name in new[] { "Tiger", "Deer", "Elephant", "Guard", "Worker" })
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"Assets/_Project/Prefabs/Characters/{name}.prefab");
                    if (prefab == null) continue;
                    var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                    var b = new Bounds(go.transform.position, Vector3.zero);
                    foreach (var r in go.GetComponentsInChildren<Renderer>()) b.Encapsulate(r.bounds);
                    float dist = b.extents.magnitude / Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.15f;
                    foreach (var (view, yaw) in new[] { ("threequarter", 35f), ("side", 90f), ("back", 215f) })
                    {
                        var dir = Quaternion.Euler(12f, yaw, 0f) * Vector3.back;
                        cam.transform.position = b.center - dir * dist;
                        cam.transform.LookAt(b.center);
                        cam.Render();
                        RenderTexture.active = rt;
                        var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                        tex.Apply();
                        RenderTexture.active = null;
                        File.WriteAllBytes(Path.Combine(outDir, $"{name}_{view}.png"), tex.EncodeToPNG());
                        Object.DestroyImmediate(tex);
                    }
                    Object.DestroyImmediate(go);
                }
                cam.targetTexture = null;
                Object.DestroyImmediate(rt);
                Debug.Log($"[World Builder] Character previews saved to {outDir}");
            }
            finally { EditorSceneManager.ClosePreviewScene(scene); }
        }
    }
}
