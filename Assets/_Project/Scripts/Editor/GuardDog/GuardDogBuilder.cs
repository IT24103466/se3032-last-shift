using System.Collections.Generic;
using LastShift;
using LastShift.GuardDog;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Build Guard Dog (Kalu).
    /// Sculpts Kalu, a black Sri Lankan estate dog (pricked ears, curled tail, red collar, glowing
    /// eyes at night) with the same SDF sculpting as the other characters, and saves the prefab
    /// to Resources/GuardDog/Kalu so <see cref="GuardDogSpawner"/> can place it at runtime.
    /// Nothing in Level_01 is changed.
    /// </summary>
    public static class GuardDogBuilder
    {
        const string PrefabFolder = "Assets/_Project/Resources/GuardDog";
        const string MeshAsset = "Assets/_Project/Art/Characters/Meshes/GuardDog_Kalu.asset";
        const string MatFolder = "Assets/_Project/Art/Characters/Materials";

        static MeshBank bank;
        static readonly Dictionary<string, Mesh> built = new Dictionary<string, Mesh>();
        static readonly Dictionary<string, Material> mats = new Dictionary<string, Material>();

        [MenuItem("Tools/World Builder/Build Guard Dog (Kalu)", priority = 89)]
        public static void Build()
        {
            Folder("Assets/_Project", "Resources");
            Folder("Assets/_Project/Resources", "GuardDog");
            Folder("Assets/_Project/Art/Characters", "Meshes");
            Folder("Assets/_Project/Art/Characters", "Materials");
            built.Clear(); mats.Clear();
            bank = new MeshBank(MeshAsset);

            var root = BuildDog();
            bank.Save();
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, $"{PrefabFolder}/Kalu.prefab");
            Object.DestroyImmediate(root);
            AssetDatabase.SaveAssets();
            Selection.activeObject = prefab;
            Debug.Log("[GuardDog] Built Kalu → Resources/GuardDog/Kalu.prefab. Press Play: the dog spawns at its kennel by the leaf collection shed.");
        }

        static GameObject BuildDog()
        {
            var root = new GameObject("Kalu");
            var col = root.AddComponent<CapsuleCollider>();
            col.direction = 2; col.center = new Vector3(0f, 0.52f, 0f); col.radius = 0.22f; col.height = 1.0f;
            var agent = root.AddComponent<NavMeshAgent>();
            agent.radius = 0.3f; agent.height = 0.75f; agent.baseOffset = 0f;
            agent.obstacleAvoidanceType = ObstacleAvoidanceType.MedQualityObstacleAvoidance;
            var hp = root.AddComponent<Health>(); hp.max = 70f;
            var anim = root.AddComponent<CreatureAnimator>(); anim.stride = 42f; anim.cadence = 3.4f; anim.lunge = 0.55f;
            root.AddComponent<DogBody>();
            root.AddComponent<GuardDogBrain>();

            var m = new GameObject("Model").transform; m.SetParent(root.transform, false);
            Material coat = Fur("DogCoat", new Color(0.2f, 0.17f, 0.15f)), dark = M("DogDark", new Color(0.03f, 0.03f, 0.03f)),
                     eye = M("DogEye", new Color(1f, 0.7f, 0.2f), 1.6f), collar = M("DogCollar", new Color(0.55f, 0.06f, 0.05f)),
                     tongue = M("DogTongue", new Color(0.7f, 0.25f, 0.3f));

            // Lean body: deep chest, tucked belly, narrow hips, arched neck
            S(m, "Body", new Sculpt()
                .Ell(V(0f, 0.56f, 0.22f), V(0.15f, 0.19f, 0.22f))
                .Cone(V(0f, 0.57f, 0.2f), V(0f, 0.56f, -0.25f), 0.15f, 0.12f, 0.1f)
                .Ell(V(0f, 0.57f, -0.3f), V(0.13f, 0.15f, 0.16f), 0.1f)
                .Cone(V(0f, 0.62f, 0.3f), V(0f, 0.74f, 0.42f), 0.095f, 0.075f, 0.08f)
                .Axis(V(0f, 0.56f, -0.46f), V(0f, 0.56f, 0.5f), Vector3.up), coat, 0.02f);
            P(m, "Collar", PrimitiveType.Cylinder, new Vector3(0f, 0.68f, 0.37f), new Vector3(0.19f, 0.014f, 0.19f), collar, new Vector3(-40f, 0f, 0f));

            // Head: CreatureAnimator turns "Head"; DogBody tilts "HeadTilt" down to sniff
            var head = Pivot(m, "Head", new Vector3(0f, 0.74f, 0.42f));
            var tilt = Pivot(head, "HeadTilt", Vector3.zero);
            S(tilt, "Head", new Sculpt()
                .Ell(V(0f, 0.05f, 0.05f), V(0.09f, 0.085f, 0.1f))
                .Cone(V(0f, 0.02f, 0.1f), V(0f, 0f, 0.26f), 0.055f, 0.035f, 0.04f)
                .Cone(V(-0.055f, 0.1f, 0.02f), V(-0.078f, 0.21f, 0f), 0.035f, 0.006f, 0.02f)
                .Cone(V(0.055f, 0.1f, 0.02f), V(0.078f, 0.21f, 0f), 0.035f, 0.006f, 0.02f)
                .Axis(V(0f, 0f, -0.06f), V(0f, 0f, 0.3f), Vector3.up), coat, 0.011f);
            S(tilt, "Nose", new Sculpt().Ell(V(0f, 0.012f, 0.28f), V(0.026f, 0.02f, 0.018f)), dark, 0.005f);
            S(tilt, "Tongue", new Sculpt().Ell(V(0f, -0.035f, 0.2f), V(0.022f, 0.008f, 0.04f)), tongue, 0.005f);
            foreach (float sx in new[] { -1f, 1f })
                P(tilt, "Eye", PrimitiveType.Sphere, new Vector3(sx * 0.046f, 0.075f, 0.135f), Vector3.one * 0.022f, eye, shadows: false);

            foreach (var (n, x, z, front) in new[] { ("FL", -0.085f, 0.24f, true), ("FR", 0.085f, 0.24f, true), ("BL", -0.085f, -0.3f, false), ("BR", 0.085f, -0.3f, false) })
            {
                var leg = Pivot(m, "Leg_" + n, new Vector3(x, 0.48f, z));
                var sc = front
                    ? new Sculpt().Chain(0.03f, Pt(0f, 0.02f, 0f, 0.055f), Pt(0f, -0.22f, 0.01f, 0.032f), Pt(0f, -0.45f, 0.01f, 0.026f))
                    : new Sculpt().Chain(0.03f, Pt(0f, 0.03f, 0f, 0.065f), Pt(0f, -0.18f, -0.07f, 0.038f), Pt(0f, -0.45f, -0.02f, 0.026f));
                sc.Ell(V(0f, -0.465f, 0.035f), V(0.03f, 0.02f, 0.045f), 0.02f).Axis(V(0f, 0.05f, 0f), V(0f, -0.48f, 0f), Vector3.forward);
                S(leg, front ? "LegFront" : "LegBack", sc, coat, 0.011f);
            }

            var tail = Pivot(m, "Tail", new Vector3(0f, 0.62f, -0.43f));
            S(tail, "Tail", new Sculpt().Chain(0.02f, Pt(0f, 0f, 0f, 0.03f), Pt(0f, 0.08f, -0.12f, 0.028f), Pt(0f, 0.18f, -0.17f, 0.024f), Pt(0f, 0.26f, -0.12f, 0.016f))
                .Axis(V(0f, 0f, 0f), V(0f, 0.2f, -0.15f), Vector3.up), coat, 0.009f);
            return root;
        }

        // ------------------------------------------------------------------ helpers (same conventions as the gameplay builder)

        static Vector3 V(float x, float y, float z) => new Vector3(x, y, z);
        static (Vector3, float) Pt(float x, float y, float z, float r) => (new Vector3(x, y, z), r);

        static GameObject S(Transform parent, string name, Sculpt sculpt, Material mat, float cell)
        {
            string key = "Kalu_" + name;
            if (!built.TryGetValue(key, out var mesh)) built[key] = mesh = bank.Add(sculpt.Build(key, cell));
            var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
            go.transform.SetParent(parent, false);
            go.GetComponent<MeshFilter>().sharedMesh = mesh;
            go.GetComponent<MeshRenderer>().sharedMaterial = mat;
            return go;
        }

        static Transform Pivot(Transform parent, string name, Vector3 pos)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            return t;
        }

        static GameObject P(Transform parent, string name, PrimitiveType type, Vector3 pos, Vector3 scale, Material mat, Vector3 euler = default, bool shadows = true)
        {
            var go = GameObject.CreatePrimitive(type);
            go.name = name;
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(euler);
            go.transform.localScale = scale;
            var r = go.GetComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            if (!shadows) r.shadowCastingMode = ShadowCastingMode.Off;
            return go;
        }

        static Material M(string name, Color c, float emission = 0f)
        {
            if (mats.TryGetValue(name, out var m)) return m;
            string path = $"{MatFolder}/M_{name}.mat";
            m = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (m == null) { m = new Material(Shader.Find("Universal Render Pipeline/Lit")); AssetDatabase.CreateAsset(m, path); }
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.2f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            EditorUtility.SetDirty(m);
            mats[name] = m;
            return m;
        }

        /// <summary>Short coat: the generated deer-coat texture (subtle hair streaks) tinted near-black.</summary>
        static Material Fur(string name, Color tint)
        {
            var m = M(name, tint);
            var tex = CreatureTextures.Get("Deer_Coat");
            if (tex != null) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", Vector2.one * 2f); }
            m.SetFloat("_Smoothness", 0.25f);
            return m;
        }

        static void Folder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
        }
    }
}
