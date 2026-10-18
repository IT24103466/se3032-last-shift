using UnityEditor;
using UnityEngine;
using WorldBuilder.Testing;

namespace WorldBuilder.EditorTools
{
    /// <summary>
    /// Tools > World Builder > Walk Test (First Person).
    ///
    /// Enters Play mode and drops a first-person test player (FirstPersonWalker) on the
    /// Spawn_Player marker. Everything it creates exists only during Play mode, so the scene
    /// file is never changed. Other cameras (the overview Main Camera) are switched off
    /// while walking.
    /// </summary>
    [InitializeOnLoad]
    public static class WalkTestLauncher
    {
        const string Flag = "WorldBuilder.WalkTest";

        static WalkTestLauncher()
        {
            // Entering Play mode reloads scripts; SessionState survives that reload.
            EditorApplication.playModeStateChanged += state =>
            {
                if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(Flag, false)) return;
                SessionState.SetBool(Flag, false);
                SpawnWalker();
            };
        }

        [MenuItem("Tools/World Builder/Walk Test (First Person)", priority = 90)]
        static void Start()
        {
            if (EditorApplication.isPlaying) { SpawnWalker(); return; }
            SessionState.SetBool(Flag, true);
            EditorApplication.EnterPlaymode();
        }

        static void SpawnWalker()
        {
            var marker = GameObject.Find("Spawn_Player");
            Vector3 pos = marker != null ? marker.transform.position : new Vector3(30f, 2f, 5f);
            if (marker == null) Debug.LogWarning("[World Builder] No Spawn_Player marker found; starting at (30, 2, 5).");

            foreach (var cam in Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude))
                cam.gameObject.SetActive(false);

            FirstPersonWalker.Spawn(pos + Vector3.up * 0.2f, 0f);
            Debug.Log("[World Builder] Walk test started. Press H for controls, Esc to free the mouse, Play to stop.");
        }
    }
}
