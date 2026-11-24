using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
namespace LastShift
{
    /// <summary>Interior fittings stay visible and illuminate the factory without requiring a light bake.</summary>
    public sealed class FactoryLampLighting : MonoBehaviour
    {
        readonly List<Material> materials = new List<Material>();
        public static bool IsInterior(Light light) => light.name.StartsWith("Lamp_") &&
            !light.name.StartsWith("Lamp_Yard") && !light.name.StartsWith("Lamp_Gate") && !light.name.StartsWith("Lamp_Path");

        public static void ConfigureLight(Light light)
        {
            if (!IsInterior(light)) return;
            light.enabled = true; light.lightmapBakeType = LightmapBakeType.Realtime;
            // A downlight needs one shadow map rather than six for a point light.
            light.type = LightType.Spot; light.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            light.spotAngle = 115f; light.innerSpotAngle = 80f;
            light.range = Mathf.Max(12f, light.range); light.intensity = Mathf.Max(7f, light.intensity);
            light.shadows = LightShadows.Soft; light.shadowBias = 0.025f; light.shadowNormalBias = 0.15f;
        }
        void Start()
        {
            foreach (var light in FindObjectsByType<Light>(FindObjectsInactive.Exclude))
            {
                if (!IsInterior(light)) continue;
                ConfigureLight(light);
                var bulb = light.transform.Find("Bulb");
                if (bulb == null)
                {
                    var go = GameObject.CreatePrimitive(PrimitiveType.Sphere); go.name = "Bulb";
                    Destroy(go.GetComponent<Collider>()); go.transform.SetParent(light.transform, false);
                    go.transform.localScale = Vector3.one * 0.22f; bulb = go.transform;
                }
                bulb.gameObject.SetActive(true); bulb.gameObject.layer = 2;
                var renderer = bulb.GetComponent<Renderer>();
                if (renderer == null) continue;
                var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Color");
                if (shader == null) continue;
                var mat = new Material(shader) { name = "Factory bulb glow" }; materials.Add(mat);
                mat.SetColor("_Color", light.color * 2f);
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", light.color * 2f);
                mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", light.color * 8f);
                renderer.sharedMaterial = mat; renderer.enabled = true; renderer.shadowCastingMode = ShadowCastingMode.Off;
            }
        }
        void OnDestroy() { foreach (var mat in materials) if (mat != null) Destroy(mat); }
    }
}
