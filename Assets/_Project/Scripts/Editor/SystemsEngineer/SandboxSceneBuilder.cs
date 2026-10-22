using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using LastShift.Runtime.Interaction;

namespace LastShift.EditorTools
{
    /// <summary>
    /// Editor tool to build or regenerate the Systems Engineer sandbox test scene.
    /// Access via Tools > Systems Engineer > Setup Sandbox Scene.
    /// Never modifies Level_01.unity or World Builder data.
    /// </summary>
    public static class SandboxSceneBuilder
    {
        private const string ScenePath = "Assets/_Project/Scenes/Sandbox/Sandbox_SystemsEngineer.unity";
        private const string GeneratorPrefabPath = "Assets/_Project/Prefabs/Interactables/Generator_Interactable.prefab";
        private const string DoorPrefabPath = "Assets/_Project/Prefabs/Interactables/Door_Interactable.prefab";
        private const string BarricadePrefabPath = "Assets/_Project/Prefabs/Interactables/Movable_Barricade.prefab";
        private const string PropPrefabPath = "Assets/_Project/Prefabs/Interactables/Carryable_Prop.prefab";
        private const string PlayerPrefabPath = "Assets/Prefabs/PlayerRobot.prefab";

        [MenuItem("Tools/Systems Engineer/Setup Sandbox Scene", priority = 100)]
        public static void BuildSandboxScene()
        {
            if (!System.IO.Directory.Exists("Assets/_Project/Scenes/Sandbox"))
            {
                System.IO.Directory.CreateDirectory("Assets/_Project/Scenes/Sandbox");
                AssetDatabase.Refresh();
            }

            // Create new empty scene
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // 1. Directional Light
            var lightGo = new GameObject("Directional Light");
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.2f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.shadows = LightShadows.Soft;

            // 2. Ground Floor
            var floorGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floorGo.name = "Floor";
            floorGo.transform.position = new Vector3(0f, -0.5f, 0f);
            floorGo.transform.localScale = new Vector3(40f, 1f, 40f);

            // 3. Player setup
            GameObject playerGo = null;
            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerPrefabPath);
            if (playerPrefab != null)
            {
                playerGo = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
                playerGo.name = "PlayerRobot";
                playerGo.transform.position = new Vector3(0f, 0f, 0f);
                playerGo.transform.rotation = Quaternion.identity;

                // Ensure PlayerInteraction and PlayerCarryController components are attached
                if (!playerGo.TryGetComponent<PlayerInteraction>(out _))
                {
                    playerGo.AddComponent<PlayerInteraction>();
                }
                if (!playerGo.TryGetComponent<PlayerCarryController>(out _))
                {
                    playerGo.AddComponent<PlayerCarryController>();
                }
            }
            else
            {
                // Fallback test capsule
                playerGo = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                playerGo.name = "TestPlayer";
                playerGo.transform.position = new Vector3(0f, 1f, 0f);
                playerGo.AddComponent<PlayerInteraction>();
                playerGo.AddComponent<PlayerCarryController>();

                var camGo = new GameObject("MainCamera");
                camGo.tag = "MainCamera";
                camGo.transform.SetParent(playerGo.transform, false);
                camGo.transform.localPosition = new Vector3(0f, 0.6f, 0f);
                camGo.AddComponent<Camera>();
            }

            // 4. Interaction Prompt UI
            var uiGo = new GameObject("InteractionPromptUI");
            uiGo.AddComponent<InteractionPromptUI>();

            // 5. Generator (Direct Aim Test)
            var genPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratorPrefabPath);
            if (genPrefab != null)
            {
                var gen1 = (GameObject)PrefabUtility.InstantiatePrefab(genPrefab);
                gen1.name = "Generator_Target";
                gen1.transform.position = new Vector3(0f, 0.6f, 3.5f);
                gen1.transform.rotation = Quaternion.identity;

                // 6. Wall Obstruction Test (Wall + Obstructed Generator)
                var wallGo = GameObject.CreatePrimitive(PrimitiveType.Cube);
                wallGo.name = "Obstruction_Wall";
                wallGo.transform.position = new Vector3(5f, 1.5f, 3.5f);
                wallGo.transform.localScale = new Vector3(0.3f, 3f, 4f);

                var gen2 = (GameObject)PrefabUtility.InstantiatePrefab(genPrefab);
                gen2.name = "Generator_Behind_Wall";
                gen2.transform.position = new Vector3(7f, 0.6f, 3.5f);
                gen2.transform.rotation = Quaternion.identity;
            }

            // 7. Door Example
            var doorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath);
            if (doorPrefab != null)
            {
                var door = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab);
                door.name = "Door_Interactable";
                door.transform.position = new Vector3(-4f, 0f, 3.5f);
                door.transform.rotation = Quaternion.identity;
            }

            // 8. Movable Barricade Example
            var barricadePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(BarricadePrefabPath);
            if (barricadePrefab != null)
            {
                var barricade = (GameObject)PrefabUtility.InstantiatePrefab(barricadePrefab);
                barricade.name = "Movable_Barricade";
                barricade.transform.position = new Vector3(-2f, 0.6f, 1.5f);
                barricade.transform.rotation = Quaternion.identity;
            }

            // 9. Carryable Prop Example
            var propPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PropPrefabPath);
            if (propPrefab != null)
            {
                var prop = (GameObject)PrefabUtility.InstantiatePrefab(propPrefab);
                prop.name = "Carryable_Prop";
                prop.transform.position = new Vector3(2f, 0.2f, 2f);
                prop.transform.rotation = Quaternion.identity;
            }

            // 10. Objective Coordinator & 3 Estate Generators Test
            if (genPrefab != null && doorPrefab != null)
            {
                var g1 = (GameObject)PrefabUtility.InstantiatePrefab(genPrefab);
                g1.name = "Generator_G1";
                g1.transform.position = new Vector3(-4f, 0.6f, 6.5f);
                g1.transform.rotation = Quaternion.identity;

                var g2 = (GameObject)PrefabUtility.InstantiatePrefab(genPrefab);
                g2.name = "Generator_G2";
                g2.transform.position = new Vector3(0f, 0.6f, 6.5f);
                g2.transform.rotation = Quaternion.identity;

                var g3 = (GameObject)PrefabUtility.InstantiatePrefab(genPrefab);
                g3.name = "Generator_G3";
                g3.transform.position = new Vector3(4f, 0.6f, 6.5f);
                g3.transform.rotation = Quaternion.identity;

                var exitGate = (GameObject)PrefabUtility.InstantiatePrefab(doorPrefab);
                exitGate.name = "Exit_Gate";
                exitGate.transform.position = new Vector3(0f, 0f, 10f);
                exitGate.transform.rotation = Quaternion.identity;

                var exitDoorComp = exitGate.GetComponent<DoorInteractable>();
                if (exitDoorComp != null)
                {
                    exitDoorComp.SetLocked(true);
                }

                var objectiveGo = new GameObject("EstateObjectiveManager");
                var objManager = objectiveGo.AddComponent<EstateObjectiveManager>();

                var g1Comp = g1.GetComponent<GeneratorInteractable>();
                var g2Comp = g2.GetComponent<GeneratorInteractable>();
                var g3Comp = g3.GetComponent<GeneratorInteractable>();

                if (g1Comp != null) objManager.RegisterGenerator(g1Comp);
                if (g2Comp != null) objManager.RegisterGenerator(g2Comp);
                if (g3Comp != null) objManager.RegisterGenerator(g3Comp);
                if (exitDoorComp != null) objManager.SetExitDoor(exitDoorComp);

                var progressUiGo = new GameObject("ObjectiveProgressUI");
                progressUiGo.AddComponent<ObjectiveProgressUI>();
            }

            // Save scene
            EditorSceneManager.SaveScene(scene, ScenePath);
            AssetDatabase.Refresh();
            Debug.Log($"[Systems Engineer] Sandbox scene successfully created at: {ScenePath}");
        }
    }
}
