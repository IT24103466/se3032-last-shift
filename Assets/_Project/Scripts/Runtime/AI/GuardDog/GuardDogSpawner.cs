using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace LastShift.GuardDog
{
    /// <summary>
    /// Puts Kalu in the level at runtime, so the agent needs no edits to Level_01 (no scene
    /// merge conflicts with the rest of the team): builds the shared world model
    /// (<see cref="ScentTracker"/>), loads the dog prefab from Resources/GuardDog/Kalu (made by
    /// Tools > World Builder > Build Guard Dog), places it at its kennel beside the leaf collection
    /// shed and gives it its rounds of the tea estate.
    /// </summary>
    public static class GuardDogSpawner
    {
        const string PrefabPath = "GuardDog/Kalu";

        // Kennel beside the leaf collection shed (X 42-48, Z 20-26) and the estate rounds:
        // footpath zigzag, the jungle fence gap, the factory fence (see CLAUDE.md level plan)
        static readonly Vector2 Kennel = new Vector2(49.5f, 22f);
        static readonly Vector2[] Rounds =
        {
            new Vector2(40f, 22.3f), new Vector2(30f, 32.7f), new Vector2(46f, 37f), new Vector2(55f, 30f),
            new Vector2(52f, 12f), new Vector2(30f, 5f), new Vector2(30f, 1.5f), new Vector2(18.3f, 12.7f), new Vector2(12f, 28f),
        };

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
            var player = Object.FindAnyObjectByType<PlayerController>();
            if (player == null || Object.FindAnyObjectByType<ScentTracker>() != null) return;
            var prefab = Resources.Load<GameObject>(PrefabPath);
            if (prefab == null)
            {
                Debug.LogWarning("[GuardDog] No dog prefab yet: run Tools > World Builder > Build Guard Dog (Kalu).");
                return;
            }

            var worldGo = new GameObject("GuardDog_World");
            var world = worldGo.AddComponent<ScentTracker>();
            world.Init(player);

            if (!OnNavMesh(Kennel, out var kennel)) { Debug.LogWarning("[GuardDog] Kennel isn't on the NavMesh."); return; }
            var rounds = new List<Vector3>();
            foreach (var r in Rounds) if (OnNavMesh(r, out var p)) rounds.Add(p);

            var dog = Object.Instantiate(prefab, kennel, Quaternion.Euler(0f, 270f, 0f));
            dog.name = "Kalu (Guard Dog)";
            var agent = dog.GetComponent<NavMeshAgent>();
            if (agent != null) agent.Warp(kennel);
            var brain = dog.GetComponent<GuardDogBrain>();
            brain.Init(player, world, kennel, rounds.ToArray());
            worldGo.AddComponent<GuardDogDebugView>().Bind(brain, world, player);
            Debug.Log($"[GuardDog] Kalu is at the kennel {kennel} with {rounds.Count} patrol points. F12 shows the AI debug view.");
        }

        static bool OnNavMesh(Vector2 xz, out Vector3 p)
        {
            p = default;
            if (!Physics.Raycast(new Vector3(xz.x, 80f, xz.y), Vector3.down, out var hit, 200f, ~(1 << 2), QueryTriggerInteraction.Ignore)) return false;
            if (!NavMesh.SamplePosition(hit.point, out var nh, 4f, NavMesh.AllAreas)) return false;
            p = nh.position;
            return true;
        }
    }
}
