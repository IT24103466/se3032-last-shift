using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Kirimala Lighting.
    ///
    /// Places the first-pass lighting for the art direction "blue hour in monsoon mist":
    ///   - Moonlight: weak, cold blue directional light (the old "Directional Light" is reused).
    ///   - Ambient: blue trilight + exponential fog for the mist.
    ///   - Sodium lamps: warm orange point lights on the yard, gate, footpath, leaf shed and
    ///     inside the factory (rolling floor, loft, storeroom, boiler room).
    ///   - Reflection probes in the yard and the three interiors so metal and wet floors
    ///     reflect the right surroundings.
    ///
    /// Light modes follow the Shadowmask plan:
    ///   Mixed  = moonlight and 3 yard lamps: baked onto the level, but still cast
    ///            realtime shadows from robots and the player.
    ///   Baked  = all other lamps: cheapest; moving objects pick their light up from the
    ///            light probes (step 4). Shadowmask allows only 4 overlapping Mixed lights,
    ///            so the many interior lamps stay Baked.
    ///
    /// Running it again deletes the previous Kirimala lights and rebuilds them.
    /// Next: 2a. Lighting Settings, 4. Light Probes, 5. Bake Lighting.
    /// </summary>
    public static class KirimalaLightingBuilder
    {
        const float Ground = 8f;
        const float LoftY = 12.5f;
        const float RoofY = 17f;

        // Low-pressure sodium street lamps are about 1800-2000 K: deep orange.
        static readonly Color Sodium = new Color(1.00f, 0.62f, 0.28f);
        // The boiler room gets a hotter, redder tint so it reads as a different space.
        static readonly Color BoilerGlow = new Color(1.00f, 0.45f, 0.22f);
        static readonly Color Moon = new Color(0.55f, 0.66f, 1.00f);

        [MenuItem("Tools/World Builder/Build Kirimala Lighting", priority = 81)]
        public static void Build()
        {
            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[World Builder] Save the scene before building the lighting.");
                return;
            }
            if (LevelBakeTool.FindRoot(LevelBakeTool.EnvironmentRoot) == null)
            {
                Debug.LogError("[World Builder] Build the blockout first (Tools > World Builder > Build Kirimala Blockout).");
                return;
            }

            LevelBakeTool.CreateHierarchy();
            var lighting = LevelBakeTool.FindRoot(LevelBakeTool.LightingRoot).transform;
            var lights = Fresh(lighting.Find("Lights"), "Kirimala_Lights");
            var probes = Fresh(lighting.Find("Probes"), "Kirimala_ReflectionProbes");

            SetupMoonlight(lights);
            SetupAmbientAndFog();

            int lamps = 0;

            // Factory yard: lamp posts along the south edge of the yard (Z 41), 4.5 m tall.
            // Only every other lamp (X 14, 30, 46: 16 m apart) is Mixed, so moonlight plus these
            // three stay within Shadowmask's limit of 4 Mixed lights; the rest are Baked.
            var yard = Group(lights, "Yard");
            for (float x = 14f; x <= 46f; x += 8f)
            {
                var mode = Mathf.RoundToInt(x - 14f) % 16 == 0 ? LightmapBakeType.Mixed : LightmapBakeType.Baked;
                lamps += Lamp(yard, $"Lamp_Yard_{x:0}", new Vector3(x, Ground + 4.5f, 41f), 14f, 4f, Sodium, mode);
            }

            // Estate road and main gate.
            var gate = Group(lights, "Gate");
            lamps += Lamp(gate, "Lamp_Gate", new Vector3(8f, Ground + 4f, 46f), 12f, 4f, Sodium, LightmapBakeType.Baked);

            // Footpath through the tea: low lamps at the zigzag corners, on the terrain.
            var path = Group(lights, "Footpath");
            Vector2[] corners = { new Vector2(18.3f, 12.7f), new Vector2(40f, 22.3f), new Vector2(30f, 32.7f) };
            foreach (var c in corners)
                lamps += Lamp(path, $"Lamp_Path_{c.x:0}_{c.y:0}", OnTerrain(c.x, c.y, 3f), 10f, 2.5f, Sodium, LightmapBakeType.Baked);

            // Leaf collection shed (X 42-48, Z 20-26).
            lamps += Lamp(path, "Lamp_LeafShed", OnTerrain(45f, 23f, 2.8f), 8f, 2.5f, Sodium, LightmapBakeType.Baked);

            // Rolling floor (X 10-36, Z 52-76). Open part hangs from the roof; under the loft
            // (Z 66-76) the lamps hang just below the loft floor.
            var rolling = Group(lights, "RollingFloor");
            foreach (float x in new[] { 15f, 23f, 31f })
            {
                lamps += Lamp(rolling, $"Lamp_Rolling_{x:0}_56", new Vector3(x, RoofY - 1.5f, 56f), 12f, 3f, Sodium, LightmapBakeType.Baked);
                lamps += Lamp(rolling, $"Lamp_Rolling_{x:0}_62", new Vector3(x, RoofY - 1.5f, 62f), 12f, 3f, Sodium, LightmapBakeType.Baked);
                lamps += Lamp(rolling, $"Lamp_UnderLoft_{x:0}", new Vector3(x, LoftY - 0.6f, 71f), 9f, 2.5f, Sodium, LightmapBakeType.Baked);
            }

            // Withering loft (upper level, Y 12.5) and the catwalk.
            var loft = Group(lights, "Loft");
            foreach (float x in new[] { 14f, 22f, 30f })
                lamps += Lamp(loft, $"Lamp_Loft_{x:0}", new Vector3(x, RoofY - 1.2f, 71f), 9f, 2.5f, Sodium, LightmapBakeType.Baked);
            lamps += Lamp(loft, "Lamp_Catwalk", new Vector3(34f, LoftY + 2.8f, 62f), 7f, 2f, Sodium, LightmapBakeType.Baked);

            // Storeroom (X 36-50, Z 52-62).
            var store = Group(lights, "Storeroom");
            lamps += Lamp(store, "Lamp_Storeroom", new Vector3(43f, Ground + 3.2f, 57f), 9f, 2.5f, Sodium, LightmapBakeType.Baked);

            // Boiler room (X 36-50, Z 62-76, ceiling 11.5): hotter tint, low ceiling.
            var boiler = Group(lights, "BoilerRoom");
            lamps += Lamp(boiler, "Lamp_Boiler_South", new Vector3(41f, 11.0f, 66f), 9f, 3f, BoilerGlow, LightmapBakeType.Baked);
            lamps += Lamp(boiler, "Lamp_Boiler_North", new Vector3(45f, 11.0f, 72f), 9f, 3f, BoilerGlow, LightmapBakeType.Baked);

            // Reflection probes: one per space, box size = the room.
            ReflectionProbe(probes, "RP_Yard", new Vector3(30f, Ground + 3f, 46f), new Vector3(40f, 8f, 12f));
            ReflectionProbe(probes, "RP_RollingFloor", new Vector3(23f, Ground + 4f, 64f), new Vector3(26f, 9f, 24f));
            ReflectionProbe(probes, "RP_Storeroom", new Vector3(43f, Ground + 1.75f, 57f), new Vector3(14f, 3.5f, 10f));
            ReflectionProbe(probes, "RP_BoilerRoom", new Vector3(43f, Ground + 1.75f, 69f), new Vector3(14f, 3.5f, 14f));

            EditorSceneManager.MarkSceneDirty(scene);
            Debug.Log($"[World Builder] Kirimala lighting built: moonlight, {lamps} sodium lamps, 4 reflection probes, " +
                      "blue ambient and fog. Next: 2a. Lighting Settings, 4. Light Probes, 5. Bake Lighting.");
        }

        // ------------------------------------------------------------------ pieces

        static void SetupMoonlight(Transform parent)
        {
            // Reuse the scene's default directional light instead of adding a second sun.
            // On a re-run it is already at --- LIGHTING ---/Lights/Moonlight.
            var existing = parent.parent.Find("Moonlight");
            var go = existing != null ? existing.gameObject : LevelBakeTool.FindRoot("Directional Light");
            Light light = go != null ? go.GetComponent<Light>() : null;
            if (light == null)
            {
                go = new GameObject("Moonlight");
                Undo.RegisterCreatedObjectUndo(go, "Create Moonlight");
                light = Undo.AddComponent<Light>(go);
            }
            Undo.RecordObject(go, "Moonlight");
            Undo.RecordObject(go.transform, "Moonlight");
            Undo.RecordObject(light, "Moonlight");
            Undo.SetTransformParent(go.transform, parent.parent, "Moonlight"); // under --- LIGHTING ---/Lights
            go.name = "Moonlight";
            // Low in the sky from the north-east, so the factory throws long shadows over the yard.
            go.transform.rotation = Quaternion.Euler(28f, 215f, 0f);
            light.type = LightType.Directional;
            light.color = Moon;
            light.intensity = 0.35f;
            light.shadows = LightShadows.Soft;
            light.lightmapBakeType = LightmapBakeType.Mixed;
            RenderSettings.sun = light;
        }

        static void SetupAmbientAndFog()
        {
            // Blue hour: the sky is the main light source, so ambient carries most of the mood.
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.20f, 0.27f, 0.42f);
            RenderSettings.ambientEquatorColor = new Color(0.12f, 0.16f, 0.25f);
            RenderSettings.ambientGroundColor = new Color(0.05f, 0.06f, 0.08f);

            // Monsoon mist: exponential-squared fog in a cold grey-blue. At 0.015 about 80% of the
            // scene is still visible at 30 m and about 45% at 60 m, so it hides the map edges
            // without hiding enemies the player needs to see coming.
            RenderSettings.fog = true;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = new Color(0.30f, 0.36f, 0.46f);
            RenderSettings.fogDensity = 0.015f;
        }

        static int Lamp(Transform parent, string name, Vector3 pos, float range, float intensity,
                        Color color, LightmapBakeType mode)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.color = color;
            light.range = range;
            light.intensity = intensity;
            light.shadows = LightShadows.Soft;
            light.lightmapBakeType = mode;
            if (mode == LightmapBakeType.Mixed)
            {
                // Mixed point lights keep realtime shadows for moving objects. Each one needs
                // 6 shadow maps (a cube), so use URP's Low tier to fit them all in the shadow atlas.
                var urp = go.GetComponent<UniversalAdditionalLightData>();
                if (urp == null) urp = go.AddComponent<UniversalAdditionalLightData>(); // Unity's "null" breaks ??
                // The C# property only works in Play mode, so set the serialized field like the Inspector does.
                var so = new SerializedObject(urp);
                so.FindProperty("m_AdditionalLightsShadowResolutionTier").intValue =
                    UniversalAdditionalLightData.AdditionalLightsShadowResolutionTierLow;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
            return 1;
        }

        static void ReflectionProbe(Transform parent, string name, Vector3 center, Vector3 size)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            go.transform.position = center;
            var p = go.AddComponent<ReflectionProbe>();
            p.mode = ReflectionProbeMode.Baked;
            p.size = size;
            p.boxProjection = true;   // interiors: reflections line up with the walls
            p.resolution = 128;
        }

        // Point on the estate terrain, raised by 'height' metres.
        static Vector3 OnTerrain(float x, float z, float height)
        {
            var p = new Vector3(x, 0f, z);
            var t = Terrain.activeTerrain;
            if (t != null) p.y = t.SampleHeight(p) + t.transform.position.y;
            p.y += height;
            return p;
        }

        static Transform Group(Transform parent, string name)
        {
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        /// <summary>Deletes an old child with this name (undoable) and creates an empty one.</summary>
        static Transform Fresh(Transform parent, string name)
        {
            var old = parent.Find(name);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }
    }
}
