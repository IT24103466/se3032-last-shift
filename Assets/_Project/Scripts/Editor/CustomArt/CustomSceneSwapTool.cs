using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using LastShift;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Opt-in helpers for the World Builder: they act on the scene that is OPEN when you run them (never on a file by themselves)
    /// and are fully undoable (Ctrl+Z). Nothing runs automatically.
    ///   Tools > Custom Art > Swap Open Scene Enemies To Custom
    ///       every EnemyAI character (Guard, Worker, Hunter, Tiger, Elephant, Deer) is replaced by its *_Custom prefab at the same
    ///       position / rotation / parent / name, and the whole EnemyAI component (patrol route, leash, wander radius ...) is copied across.
    ///   Tools > Custom Art > Swap Open Scene Tea Rows To Custom
    ///       every box under "TeaRows_Placeholder" is replaced by a TeaRow_Estate_01 prefab (4 m along X, same footprint).
    /// </summary>
    public static class CustomSceneSwapTool
    {
        const string CharPrefabs = "Assets/_Project/Prefabs/Characters_Custom";
        const string EnvPrefabs = "Assets/_Project/Prefabs/Environment_Custom";

        [MenuItem("Tools/Custom Art/Swap Open Scene Enemies To Custom")]
        static void MenuEnemies()
        {
            if (!SceneGuard.Confirm("Swap enemies")) return;
            int n = SwapEnemies();
            Debug.Log($"[Custom Art] Swapped {n} enemies to their *_Custom prefabs in the open scene(s). Save the scene yourself if you want to keep it.");
        }

        [MenuItem("Tools/Custom Art/Swap Open Scene Tea Rows To Custom")]
        static void MenuTeaRows()
        {
            if (!SceneGuard.Confirm("Swap tea rows")) return;
            Debug.Log($"[Custom Art] Swapped {SwapTeaRows()} tea row placeholders.");
        }

        public static int SwapEnemies()
        {
            int n = 0;
            var list = new List<EnemyAI>(Object.FindObjectsByType<EnemyAI>(FindObjectsInactive.Include));
            foreach (var ai in list)
            {
                string kind = KindName(ai);
                if (kind == null) continue;
                var custom = AssetDatabase.LoadAssetAtPath<GameObject>($"{CharPrefabs}/{kind}_Custom.prefab");
                if (custom == null) continue;
                var old = ai.gameObject;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(custom, old.transform.parent);
                Undo.RegisterCreatedObjectUndo(go, "Swap enemy to custom");
                go.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
                go.transform.localScale = old.transform.localScale;
                go.name = old.name;
                go.SetActive(old.activeSelf);
                go.layer = old.layer; go.tag = old.tag;
                var newAI = go.GetComponent<EnemyAI>();
                // the agent must be disabled while the copy lands on a possibly different NavMesh height
                EditorUtility.CopySerialized(ai, newAI);
                Undo.DestroyObjectImmediate(old);
                n++;
            }
            SceneGuard.MarkDirty();
            return n;
        }

        static string KindName(EnemyAI ai)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(ai.gameObject);
            string n = src != null ? src.name : ai.name;
            if (n.EndsWith("_Custom")) return null;
            foreach (var k in new[] { "Guard", "Worker", "Hunter", "Tiger", "Elephant", "Deer" })
                if (n.StartsWith(k)) return k;
            return null;
        }

        public static int SwapTeaRows()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{EnvPrefabs}/TeaRow_Estate_01.prefab");
            var holder = GameObject.Find("TeaRows_Placeholder");
            if (prefab == null || holder == null) { Debug.LogWarning("[Custom Art] TeaRows_Placeholder or the tea row prefab was not found."); return 0; }
            var kids = new List<Transform>();
            foreach (Transform t in holder.transform) kids.Add(t);
            int n = 0;
            foreach (var t in kids)
            {
                var r = t.GetComponent<Renderer>();
                if (r == null) continue;
                var b = r.bounds;                                   // placeholder box: 4 x 1.4 x 0.9, bottom 0.4 below the terrain
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, holder.transform);
                Undo.RegisterCreatedObjectUndo(go, "Swap tea row");
                go.transform.position = new Vector3(b.center.x, b.min.y + 0.4f, b.center.z);
                go.transform.rotation = Quaternion.identity;
                go.name = t.name;
                Undo.DestroyObjectImmediate(t.gameObject);
                n++;
            }
            SceneGuard.MarkDirty();
            return n;
        }
    }
}
