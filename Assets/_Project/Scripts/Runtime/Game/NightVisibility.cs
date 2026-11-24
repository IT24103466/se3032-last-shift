using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace LastShift
{
    /// <summary>
    /// Makes the night playable: the level is lit for a moody monsoon blue hour, which on a
    /// normal monitor hides the animals and people coming at you. This lifts the picture at
    /// runtime (exposure, shadows, ambient and moonlight, thinner fog) and hangs a faint cool
    /// "presence" light over every creature so threats read against the dark.
    /// F7 cycles Bright (default) / Normal / Dark (the original look).
    /// Nothing is baked or saved: the scene, lightmaps and shared assets are unchanged.
    /// </summary>
    public class NightVisibility : MonoBehaviour
    {
        enum Level { Bright, Normal, Dark }

        struct Preset { public float exposure, shadowLift, ambient, sun, fog; public bool creatureLights; }
        static readonly Preset[] Presets =
        {
            new Preset { exposure = 1.5f, shadowLift = 0.35f, ambient = 2.4f, sun = 1.9f, fog = 0.45f, creatureLights = true },   // Bright
            new Preset { exposure = 0.9f, shadowLift = 0.18f, ambient = 1.6f, sun = 1.35f, fog = 0.7f, creatureLights = true },   // Normal
            new Preset { exposure = 0f, shadowLift = 0f, ambient = 1f, sun = 1f, fog = 1f, creatureLights = false },              // Dark (original)
        };

        Level level = Level.Bright;
        ColorAdjustments color;
        ShadowsMidtonesHighlights smh;
        Color skyAmb, eqAmb, groundAmb; float ambIntensity, fogDensity;
        Light sun; float sunIntensity;
        float nextScan;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            SceneManager.sceneLoaded -= OnLoaded;
            SceneManager.sceneLoaded += OnLoaded;
            Spawn();
        }

        static void OnLoaded(Scene s, LoadSceneMode m) => Spawn();

        static void Spawn()
        {
            if (FindAnyObjectByType<PlayerController>() == null || FindAnyObjectByType<NightVisibility>() != null) return;
            new GameObject("NightVisibility").AddComponent<NightVisibility>();
        }

        void Awake()
        {
            skyAmb = RenderSettings.ambientSkyColor; eqAmb = RenderSettings.ambientEquatorColor; groundAmb = RenderSettings.ambientGroundColor;
            ambIntensity = RenderSettings.ambientIntensity; fogDensity = RenderSettings.fogDensity;
            sun = RenderSettings.sun;
            if (sun == null) foreach (var l in FindObjectsByType<Light>(FindObjectsInactive.Exclude)) if (l.type == LightType.Directional) { sun = l; break; }
            if (sun != null) sunIntensity = sun.intensity;

            // Our own global volume, above the other post-processing volumes, so its exposure wins
            var vol = gameObject.AddComponent<Volume>();
            vol.isGlobal = true; vol.priority = 60f; vol.weight = 1f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            color = profile.Add<ColorAdjustments>(true);
            smh = profile.Add<ShadowsMidtonesHighlights>(true);
            vol.sharedProfile = profile;
            Apply();
        }

        void OnDestroy()
        {
            // Put the scene's lighting back (e.g. when leaving play mode or reloading)
            RenderSettings.ambientSkyColor = skyAmb; RenderSettings.ambientEquatorColor = eqAmb; RenderSettings.ambientGroundColor = groundAmb;
            RenderSettings.ambientIntensity = ambIntensity; RenderSettings.fogDensity = fogDensity;
            if (sun != null) sun.intensity = sunIntensity;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f7Key.wasPressedThisFrame)
            {
                level = (Level)(((int)level + 1) % Presets.Length);
                Apply();
                GameManager.Instance?.Toast($"Brightness: {level}  (F7)", 1.5f);
            }
            // Creatures spawned later (the dog, the mammoth) get their light too
            if (Time.time >= nextScan) { nextScan = Time.time + 2f; UpdateCreatureLights(); }
        }

        void Apply()
        {
            var p = Presets[(int)level];
            color.postExposure.Override(p.exposure);
            color.active = p.exposure > 0f;
            smh.shadows.Override(new Vector4(1f, 1f, 1f, p.shadowLift));       // lift the shadows (w = brightness offset)
            smh.active = p.shadowLift > 0f;
            RenderSettings.ambientSkyColor = skyAmb * p.ambient;
            RenderSettings.ambientEquatorColor = eqAmb * p.ambient;
            RenderSettings.ambientGroundColor = groundAmb * p.ambient;
            RenderSettings.ambientIntensity = ambIntensity * p.ambient;
            RenderSettings.fogDensity = fogDensity * p.fog;
            if (sun != null) sun.intensity = sunIntensity * p.sun;
            UpdateCreatureLights();
        }

        /// <summary>A soft, shadowless cool light above each living creature (not the player).</summary>
        void UpdateCreatureLights()
        {
            bool on = Presets[(int)level].creatureLights;
            foreach (var h in FindObjectsByType<Health>(FindObjectsInactive.Exclude))
            {
                if (h.GetComponent<PlayerController>() != null) continue;
                var t = h.transform.Find("PresenceLight");
                if (t == null)
                {
                    if (!on) continue;
                    var col = h.GetComponent<Collider>();
                    float height = col != null ? col.bounds.size.y : 1.5f;
                    var l = new GameObject("PresenceLight").AddComponent<Light>();
                    l.transform.SetParent(h.transform, false);
                    l.transform.localPosition = new Vector3(0f, height + 1.2f, 0f);
                    l.type = LightType.Point;
                    l.range = Mathf.Clamp(height * 3f, 4f, 11f);
                    l.intensity = 1.3f;
                    l.color = new Color(0.78f, 0.86f, 1f);
                    l.shadows = LightShadows.None;
                    t = l.transform;
                }
                var light = t.GetComponent<Light>();
                light.enabled = on && !h.Dead;
            }
        }
    }
}
