using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Replaces every old sculpted placeholder tree (JungleTree, UnderstoryTree, ShadeTree: no prefab link) by one of the three custom crown shapes
    /// (Tree_Estate_02 / 02B / 02C) at the same position, parent, name and sibling index. Height follows the old tree (scale clamped), yaw is random from a fixed seed.
    /// Undoable (one step), safe to re-run (replaced trees are prefab instances, so they are skipped), asks first outside Scenes/Sandbox/.
    /// </summary>
    public static class CustomTreeSwapTool
    {
        const string Env = "Assets/_Project/Prefabs/Environment_Custom";
        static readonly string[] OldNames = { "JungleTree", "UnderstoryTree", "ShadeTree" };

        [MenuItem("Tools/Custom Art/Replace Old Trees With Custom")]
        static void Menu() { if (SceneGuard.Confirm("Replace the old placeholder trees")) Debug.Log(ReplaceTrees()); }

        public static string ReplaceTrees(bool ownUndo = true)
        {
            var prefabs = new[] { "Tree_Estate_02", "Tree_Estate_02B", "Tree_Estate_02C" }.Select(n => AssetDatabase.LoadAssetAtPath<GameObject>($"{Env}/{n}.prefab")).ToArray();
            if (prefabs.Any(p => p == null)) return "[Custom Art] trees: custom tree prefabs missing, run Build Custom Environment Prefabs first";
            // native height of each crown shape (all renderers of LOD0)
            var native = prefabs.Select(p => { var b = new Bounds(); bool first = true; foreach (var r in p.GetComponentsInChildren<Renderer>(true)) { if (first) { b = r.bounds; first = false; } else b.Encapsulate(r.bounds); } return Mathf.Max(0.5f, b.size.y); }).ToArray();
            int g = 0;
            if (ownUndo) { Undo.IncrementCurrentGroup(); g = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Replace old trees with custom"); }
            var rng = new System.Random(20261123);
            var old = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include)
                .Where(t => OldNames.Contains(t.name) && PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject) == null)
                .OrderBy(t => t.position.x).ThenBy(t => t.position.z).ToList();
            var counts = new Dictionary<string, int>();
            float minS = 99, maxS = 0;
            foreach (var t in old)
            {
                var rs = t.GetComponentsInChildren<Renderer>(true);
                float h = 6f;
                if (rs.Length > 0) { var b = rs[0].bounds; foreach (var r in rs) b.Encapsulate(r.bounds); h = Mathf.Max(1.5f, b.size.y); }
                int v = rng.Next(3);
                float s = Mathf.Clamp(h / native[v], 0.45f, 1.6f);
                var go = (GameObject)PrefabUtility.InstantiatePrefab(prefabs[v], t.parent);
                Undo.RegisterCreatedObjectUndo(go, "Custom tree");
                go.transform.position = t.position;
                go.transform.rotation = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);
                go.transform.localScale = new Vector3(s, s, s);
                go.name = t.name; go.layer = t.gameObject.layer; go.SetActive(t.gameObject.activeSelf);
                go.transform.SetSiblingIndex(t.GetSiblingIndex());
                string nm = t.name;
                Undo.DestroyObjectImmediate(t.gameObject);
                counts[nm] = (counts.TryGetValue(nm, out var c) ? c : 0) + 1;
                minS = Mathf.Min(minS, s); maxS = Mathf.Max(maxS, s);
            }
            if (ownUndo) Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            var sb = new StringBuilder("[Custom Art] trees replaced: ");
            sb.Append(old.Count == 0 ? "nothing to do (already done)" : string.Join(", ", counts.Select(k => $"{k.Key}={k.Value}")) + $" | total={old.Count}, scale {minS:F2}-{maxS:F2}, native heights {string.Join("/", native.Select(n => n.ToString("F1")))} m");
            return sb.ToString();
        }
    }
}
