using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using LastShift;
using WorldBuilder.Testing;

namespace CustomArt.EditorTools
{
    /// <summary>
    /// Scene tools that put the custom assets into the OPEN scene. All of them are undoable (one undo step per menu item),
    /// safe to re-run (nothing is added twice) and ask for confirmation unless the open scene is under Scenes/Sandbox/.
    ///   Swap Open Scene Props To Custom      crates, tea chests, oil drums, pallet stacks, fallen logs (visual swap) and the tea-bag objectives (object swap)
    ///   Restore Placeholder Props            removes the custom visuals and switches the placeholder renderers back on (tea bags: use Undo)
    ///   Place Custom Nature In Open Scene    rocks, ferns, hide bushes and the three tree crowns under ONE parent "CustomArt_Nature" (fixed seed)
    ///   Remove Custom Nature                 deletes that parent in one step
    ///   Swap Everything To Custom            enemies + tea rows + props + nature
    /// </summary>
    public static class CustomPlacementTools
    {
        const string Env = "Assets/_Project/Prefabs/Environment_Custom";
        const string VisualName = "CustomArt_Visual";
        public const string NatureName = "CustomArt_Nature";
        public static string LastReport = "";

        // ------------------------------------------------------------------ menu
        [MenuItem("Tools/Custom Art/Swap Open Scene Props To Custom")]
        static void MenuProps() { if (SceneGuard.Confirm("Swap props")) Debug.Log(SwapProps()); }

        [MenuItem("Tools/Custom Art/Restore Placeholder Props")]
        static void MenuRestore() { if (SceneGuard.Confirm("Restore props")) Debug.Log(RestoreProps()); }

        [MenuItem("Tools/Custom Art/Place Custom Nature In Open Scene")]
        static void MenuNature() { if (SceneGuard.Confirm("Place nature")) Debug.Log(PlaceNature()); }

        [MenuItem("Tools/Custom Art/Remove Custom Nature")]
        static void MenuRemoveNature() { if (SceneGuard.Confirm("Remove nature")) Debug.Log(RemoveNature()); }

        [MenuItem("Tools/Custom Art/Swap Everything To Custom")]
        static void MenuAll()
        {
            if (!SceneGuard.Confirm("Swap everything")) return;
            Undo.IncrementCurrentGroup();
            int g = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Swap everything to custom art");
            var sb = new StringBuilder();
            sb.AppendLine($"enemies swapped: {CustomSceneSwapTool.SwapEnemies()}");
            sb.AppendLine($"tea rows swapped: {CustomSceneSwapTool.SwapTeaRows()}");
            sb.AppendLine(SwapProps(false));
            sb.AppendLine(PlaceNature(false));
            sb.AppendLine(CustomHandsTool.SwapHands(false));
            Undo.CollapseUndoOperations(g);
            Debug.Log("[Custom Art] " + sb);
        }

        // ================================================================== 1. PROPS
        class Kit
        {
            public string prefab; public string label; public bool tile = true;
            public Bounds nat;            // native mesh bounds of the custom prefab (metres)
        }

        static Bounds NativeBounds(GameObject prefab)
        {
            var mf = prefab.GetComponentInChildren<MeshFilter>(true);
            return mf != null && mf.sharedMesh != null ? mf.sharedMesh.bounds : new Bounds(Vector3.up * 0.5f, Vector3.one);
        }

        public static string SwapProps(bool ownUndo = true)
        {
            int g = 0;
            if (ownUndo) { Undo.IncrementCurrentGroup(); g = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Swap props to custom"); }
            var counts = new Dictionary<string, int>();
            var skipped = new Dictionary<string, int>();
            void Count(string k, int n = 1) { counts[k] = (counts.TryGetValue(k, out var v) ? v : 0) + n; }
            void Skip(string k) { skipped[k] = (skipped.TryGetValue(k, out var v) ? v : 0) + 1; }

            var prefabs = new Dictionary<string, GameObject>();
            GameObject P(string n) { if (!prefabs.TryGetValue(n, out var p)) prefabs[n] = p = AssetDatabase.LoadAssetAtPath<GameObject>($"{Env}/{n}.prefab"); return p; }

            var all = new List<Transform>();
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects()) all.AddRange(root.GetComponentsInChildren<Transform>(true));

            foreach (var t in all)
            {
                if (t == null || t.name.StartsWith(VisualName)) continue;
                string n = t.name;
                string kind = null;
                if (n == "OilDrum") kind = "Barrel_Custom";
                else if (n == "TeaChest") kind = "TeaChest_Custom";
                else if (n.StartsWith("Cover_Crate") || n == "Crate_Camp" || n == "Crate_Ruin") kind = "Crate_Custom";
                else if (n == "Vault_Pallets") kind = "Pallet_Custom";
                else if (n == "Vault_Log") kind = "Log_Custom";
                if (kind == null) continue;
                if (t.Find(VisualName) != null) { Skip(kind); continue; }          // already swapped
                var prefab = P(kind);
                var mf = t.GetComponent<MeshFilter>(); var mr = t.GetComponent<MeshRenderer>();
                if (prefab == null || mf == null || mf.sharedMesh == null || mr == null) { Skip(kind + " (no mesh)"); continue; }
                int tiles = SwapVisual(t, mf, mr, prefab, kind);
                Count(kind); Count(kind + " tiles", tiles);
            }

            // tea-bag objectives: replace the whole object so that Sack and Tag are direct children again (TeaBag.Search needs them)
            var bagPrefab = P("TeaBag_Custom");
            int bags = 0;
            foreach (var bag in Object.FindObjectsByType<TeaBag>(FindObjectsInactive.Include).ToList())
            {
                var src = PrefabUtility.GetCorrespondingObjectFromSource(bag.gameObject);
                if (src != null && src.name == "TeaBag_Custom") { Skip("TeaBag_Custom"); continue; }
                if (bagPrefab == null) break;
                var old = bag.gameObject;
                var go = (GameObject)PrefabUtility.InstantiatePrefab(bagPrefab, old.transform.parent);
                Undo.RegisterCreatedObjectUndo(go, "Swap tea bag");
                go.transform.SetPositionAndRotation(old.transform.position, old.transform.rotation);
                go.transform.localScale = old.transform.localScale;
                go.name = old.name; go.tag = old.tag; go.layer = old.layer; go.SetActive(old.activeSelf);
                go.transform.SetSiblingIndex(old.transform.GetSiblingIndex());
                EditorUtility.CopySerialized(bag, go.GetComponent<TeaBag>());                 // hasRing, searched, place
                var gi = old.GetComponent<GreyboxInteractable>();
                if (gi != null) { var ngi = go.AddComponent<GreyboxInteractable>(); EditorUtility.CopySerialized(gi, ngi); }
                Undo.DestroyObjectImmediate(old);
                bags++;
            }
            if (bags > 0) Count("TeaBag_Custom (objectives)", bags);

            if (ownUndo) Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            LastReport = Report("props", counts, skipped);
            return LastReport;
        }

        static string Report(string what, Dictionary<string, int> counts, Dictionary<string, int> skipped)
        {
            var sb = new StringBuilder($"[Custom Art] {what}: ");
            sb.Append(counts.Count == 0 ? "nothing to do" : string.Join(", ", counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            if (skipped.Count > 0) sb.Append(" | already done / skipped: " + string.Join(", ", skipped.Select(k => $"{k.Key}={k.Value}")));
            return sb.ToString();
        }

        /// <summary>Hide the placeholder's renderer and add the custom prefab as tiles under a "CustomArt_Visual" child (keeps name, tag, colliders and components of the placeholder).</summary>
        static int SwapVisual(Transform ph, MeshFilter mf, MeshRenderer mr, GameObject prefab, string kind)
        {
            var b = mf.sharedMesh.bounds;                         // placeholder mesh bounds in its own local space
            Vector3 sc = Abs(ph.lossyScale);
            Vector3 ws = Vector3.Scale(b.size, sc);               // size in metres
            var nat = NativeBounds(prefab);

            // child orientation: identity, except logs which are modelled along their local Z
            Quaternion rot = Quaternion.identity;
            int[] perm = { 0, 1, 2 };                             // child axis k -> placeholder-local axis
            int[] n = { 1, 1, 1 };
            if (kind == "Log_Custom")
            {
                int longAxis = ws.x >= ws.y && ws.x >= ws.z ? 0 : ws.y >= ws.z ? 1 : 2;
                Vector3 fwd = Vector3.zero; fwd[longAxis] = 1f;
                Vector3 up = ph.InverseTransformDirection(Vector3.up);           // keep the moss on top
                up[longAxis] = 0f;
                int ua = Mathf.Abs(up[(longAxis + 1) % 3]) >= Mathf.Abs(up[(longAxis + 2) % 3]) ? (longAxis + 1) % 3 : (longAxis + 2) % 3;
                Vector3 upv = Vector3.zero; upv[ua] = up[ua] >= 0f ? 1f : -1f;
                rot = Quaternion.LookRotation(fwd, upv);
                for (int k = 0; k < 3; k++)
                {
                    var d = rot * (k == 0 ? Vector3.right : k == 1 ? Vector3.up : Vector3.forward);
                    perm[k] = Mathf.Abs(d.x) > 0.5f ? 0 : Mathf.Abs(d.y) > 0.5f ? 1 : 2;
                }
                n[longAxis] = Mathf.Max(1, Mathf.RoundToInt(ws[longAxis] / nat.size.z));
            }
            else
            {
                for (int a = 0; a < 3; a++) n[a] = Mathf.Max(1, Mathf.RoundToInt(ws[a] / Mathf.Max(nat.size[a], 0.01f)));
                if (kind == "Barrel_Custom" || kind == "TeaChest_Custom") n = new[] { 1, 1, 1 };
            }

            // each tile: cell of the placeholder box, scaled so the custom mesh exactly fills it
            var wrapper = new GameObject(VisualName);
            Undo.RegisterCreatedObjectUndo(wrapper, "Custom visual");
            wrapper.transform.SetParent(ph, false);
            wrapper.layer = ph.gameObject.layer;
            int tiles = 0;
            for (int i = 0; i < n[0]; i++)
                for (int j = 0; j < n[1]; j++)
                    for (int k = 0; k < n[2]; k++)
                    {
                        Vector3 cell = new Vector3(b.size.x / n[0], b.size.y / n[1], b.size.z / n[2]);
                        Vector3 tileMin = b.min + new Vector3(cell.x * i, cell.y * j, cell.z * k);
                        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, wrapper.transform);
                        Undo.RegisterCreatedObjectUndo(inst, "Custom visual tile");
                        var s = new Vector3(cell[perm[0]] / Mathf.Max(nat.size.x, 0.001f), cell[perm[1]] / Mathf.Max(nat.size.y, 0.001f), cell[perm[2]] / Mathf.Max(nat.size.z, 0.001f));
                        inst.transform.localRotation = rot;
                        inst.transform.localScale = s;
                        // place: the rotated, scaled native bounds must start at tileMin
                        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                        for (int c = 0; c < 8; c++)
                        {
                            var corner = nat.center + Vector3.Scale(nat.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1));
                            min = Vector3.Min(min, rot * Vector3.Scale(corner, s));
                        }
                        inst.transform.localPosition = tileMin - min;
                        inst.layer = ph.gameObject.layer;
                        foreach (var col in inst.GetComponentsInChildren<Collider>(true)) col.enabled = false;     // the placeholder keeps the gameplay collider
                        tiles++;
                    }
            Undo.RecordObject(mr, "Hide placeholder");
            mr.enabled = false;
            return tiles;
        }

        static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        public static string RestoreProps()
        {
            Undo.IncrementCurrentGroup();
            int g = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Restore placeholder props");
            int n = 0;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(x => x.name == VisualName).ToList())
                {
                    var mr = t.parent != null ? t.parent.GetComponent<MeshRenderer>() : null;
                    if (mr != null) { Undo.RecordObject(mr, "Show placeholder"); mr.enabled = true; }
                    Undo.DestroyObjectImmediate(t.gameObject);
                    n++;
                }
            Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            return LastReport = $"[Custom Art] restored {n} placeholder(s) (tea-bag objectives are not restorable here: use Edit > Undo)";
        }

        // ================================================================== 2. NATURE
        static readonly Vector2[] Footpath = { new Vector2(30, 0), new Vector2(30, 5), new Vector2(18.3f, 12.7f), new Vector2(40, 22.3f), new Vector2(30, 32.7f), new Vector2(30, 40) };
        static readonly Vector2[] Trail = { new Vector2(6, -58), new Vector2(16, -48), new Vector2(26, -41), new Vector2(30, -34), new Vector2(24, -22), new Vector2(34, -12), new Vector2(30, -3), new Vector2(30, 0) };
        static float Ravine(float x) => -34f + 5f * Mathf.Sin(0.07f * x + 1f);
        const int Seed = 20261123;

        static float DistToLine(Vector2 p, Vector2[] pts)
        {
            float best = float.MaxValue;
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = pts[i], b = pts[i + 1], ab = b - a;
                float t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude);
                best = Mathf.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }

        class Ctx
        {
            public List<(Vector2 p, float r)> discs = new List<(Vector2, float)>();     // points to keep clear of (doors, spawns, pebbles ...)
            public List<Rect> rects = new List<Rect>();                                   // gameplay trigger footprints (expanded)
            public List<(Vector2 p, float r)> placed = new List<(Vector2, float)>();    // existing vegetation without colliders + our own placements
            public System.Random rng = new System.Random(Seed);
            public Transform parent;
            public Dictionary<string, int> counts = new Dictionary<string, int>();
            public Dictionary<string, int> rejected = new Dictionary<string, int>();
        }

        public static string RemoveNature()
        {
            Undo.IncrementCurrentGroup();
            int g = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Remove custom nature");
            int n = 0;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(x => x.name == NatureName).ToList())
                { Undo.DestroyObjectImmediate(t.gameObject); n++; }
            Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            return LastReport = $"[Custom Art] removed {n} CustomArt_Nature group(s)";
        }

        public static string PlaceNature(bool ownUndo = true)
        {
            int g = 0;
            if (ownUndo) { Undo.IncrementCurrentGroup(); g = Undo.GetCurrentGroup(); Undo.SetCurrentGroupName("Place custom nature"); }
            // re-run: start from a clean slate so the result is identical and nothing is duplicated
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(x => x.name == NatureName).ToList())
                    Undo.DestroyObjectImmediate(t.gameObject);
            Physics.SyncTransforms();

            var ctx = new Ctx();
            var terrains = Terrain.activeTerrains;
            if (terrains.Length == 0) return LastReport = "[Custom Art] no terrain in the open scene: nothing placed";

            // ---- parent
            Transform holder = null;
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
                if (root.name.Contains("ENVIRONMENT")) { var props = root.transform.Find("Props"); holder = props != null ? props : root.transform; }
            var parent = new GameObject(NatureName);
            Undo.RegisterCreatedObjectUndo(parent, "Custom nature");
            if (holder != null) parent.transform.SetParent(holder, false);
            ctx.parent = parent.transform;
            Transform Sub(string n) { var s = new GameObject(n); Undo.RegisterCreatedObjectUndo(s, "Custom nature group"); s.transform.SetParent(parent.transform, false); return s.transform; }
            var gTrees = Sub("Trees"); var gRocks = Sub("Rocks"); var gFerns = Sub("Ferns"); var gBushes = Sub("HideBushes");

            // ---- things to keep clear of
            foreach (var m in Object.FindObjectsByType<GreyboxInteractable>(FindObjectsInactive.Include)) ctx.discs.Add((XZ(m.transform.position), 3.5f));
            foreach (var root in SceneManager.GetActiveScene().GetRootGameObjects())
            {
                if (root.name.Contains("GAMEPLAY"))
                {
                    foreach (var c in root.GetComponentsInChildren<Collider>(true))
                    {
                        if (c.name == "InvisibleWall" || c.GetComponentInParent<PlayerController>() != null) continue;
                        var bb = c.bounds; bb.Expand(new Vector3(3f, 0f, 3f));
                        if (c.isTrigger || c.tag == "HideSpot") ctx.rects.Add(new Rect(bb.min.x, bb.min.z, bb.size.x, bb.size.z));
                        else ctx.discs.Add((XZ(c.bounds.center), 2.2f));                         // ammo crates, pebble piles
                    }
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                        if (t.name.StartsWith("Spawn")) ctx.discs.Add((XZ(t.position), 4.5f));
                }
                if (root.name.Contains("ENVIRONMENT"))
                    foreach (var t in root.GetComponentsInChildren<Transform>(true))
                    {
                        string n = t.name;
                        if (n == "JungleTree" || n == "UnderstoryTree" || n == "ShadeTree") ctx.placed.Add((XZ(t.position), 3.0f));
                        else if (n == "Veg_Bush" || n == "Veg_Rock" || n.StartsWith("Rock") && t.GetComponent<Renderer>() != null) ctx.placed.Add((XZ(t.position), 1.6f));
                        else if (n == "Veg_Fern") ctx.placed.Add((XZ(t.position), 0.7f));
                        else if (n == "BambooClump") ctx.placed.Add((XZ(t.position), 2.2f));
                    }
            }
            Physics.SyncTransforms();

            GameObject L(string n) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Env}/{n}.prefab");
            var trees = new[] { L("Tree_Estate_02"), L("Tree_Estate_02B"), L("Tree_Estate_02C") };
            var rocks = new[] { L("Rock_A_Custom"), L("Rock_B_Custom"), L("Rock_C_Custom"), L("Rock_D_Custom") };
            var fern = L("Fern_Jungle_Custom"); var bush = L("Bush_Hide_Custom");
            if (trees.Any(x => x == null) || rocks.Any(x => x == null) || fern == null || bush == null)
                return LastReport = "[Custom Art] custom environment prefabs are missing: run Tools > Custom Art > Build Custom Environment Prefabs first";

            // ---- 1. trees along the estate boundary (fence line on both sides, west and east edge) ...
            var boundary = new List<Vector2>();
            for (float x = 1.5f; x < 59f; x += 7.3f) boundary.Add(new Vector2(x, -3.6f));      // jungle side of the fence
            for (float x = 4f; x < 58f; x += 9.2f) boundary.Add(new Vector2(x, 2.2f));         // estate side, in front of the first tea row
            for (float z = 7f; z < 38f; z += 8.4f) { boundary.Add(new Vector2(1.2f, z)); boundary.Add(new Vector2(58.8f, z)); }
            int bi = 0;
            foreach (var c in boundary)
            {
                if (!TryPlace(ctx, c, 2.2f, 4.0f, 3.2f, 32f, out var pos, out var tn)) { Rej(ctx, "tree (boundary)"); continue; }
                Spawn(ctx, trees[(bi++ + ctx.rng.Next(3)) % 3], gTrees, pos, "Tree", 0.9f, 1.25f, 3.0f, tilt: 0f, sink: 0f, key: "trees (boundary)");
            }
            // ... and in the jungle
            Scatter(ctx, 20, "trees (jungle)", gTrees, () => trees[ctx.rng.Next(3)], new Rect(-16, -60, 92, 56), 4.0f, 3.5f, 3.2f, 32f, 0.9f, 1.3f, 3.0f, 0f, 0f, 400);
            // ---- 2. rocks: beside the fence and scattered in the jungle (sunk a little, tilted a little)
            Scatter(ctx, 6, "rocks (boundary)", gRocks, () => rocks[ctx.rng.Next(4)], new Rect(2, -5.5f, 56, 3.5f), 2.2f, 3.0f, 1.2f, 55f, 0.8f, 1.5f, 1.8f, 7f, 0.1f, 200);
            Scatter(ctx, 24, "rocks (jungle)", gRocks, () => rocks[ctx.rng.Next(4)], new Rect(-16, -60, 92, 56), 2.4f, 3.0f, 1.2f, 55f, 0.7f, 1.7f, 1.8f, 7f, 0.1f, 400);
            // ---- 3. hide bushes: well away from the trail and every HideSpot trigger
            Scatter(ctx, 14, "hide bushes", gBushes, () => bush, new Rect(-14, -58, 88, 52), 3.0f, 2.4f, 2.2f, 32f, 0.9f, 1.5f, 2.6f, 0f, 0f, 400);
            // ---- 4. ferns: half along the trail edge, half along the stream banks, a few anywhere
            int ferns = 0;
            for (int guard = 0; guard < 4000 && ferns < 80; guard++)
            {
                Vector2 p;
                int mode = ferns % 3;
                if (mode == 0)       // trail edge
                {
                    int seg = ctx.rng.Next(Trail.Length - 1);
                    var a = Trail[seg]; var bb = Trail[seg + 1];
                    p = Vector2.Lerp(a, bb, (float)ctx.rng.NextDouble());
                    var d = (bb - a).normalized; var nrm = new Vector2(-d.y, d.x);
                    p += nrm * ((ctx.rng.Next(2) == 0 ? -1f : 1f) * (1.7f + (float)ctx.rng.NextDouble() * 3f));
                }
                else if (mode == 1)  // stream banks
                {
                    float x = -14f + (float)ctx.rng.NextDouble() * 88f;
                    p = new Vector2(x, Ravine(x) + (ctx.rng.Next(2) == 0 ? -1f : 1f) * (3.2f + (float)ctx.rng.NextDouble() * 3.5f));
                }
                else p = new Vector2(-14f + (float)ctx.rng.NextDouble() * 88f, -58f + (float)ctx.rng.NextDouble() * 54f);
                if (!TryPlace(ctx, p, 1.6f, 0.9f, 1.0f, 45f, out var pos, out var tn)) { Rej(ctx, "ferns"); continue; }
                Spawn(ctx, fern, gFerns, pos, "Fern", 0.8f, 1.5f, 0.8f, tilt: 4f, sink: 0f, key: "ferns");
                ferns++;
            }

            if (ownUndo) Undo.CollapseUndoOperations(g);
            SceneGuard.MarkDirty();
            var sb = new StringBuilder("[Custom Art] nature placed under CustomArt_Nature (seed " + Seed + "): ");
            sb.Append(string.Join(", ", ctx.counts.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            sb.Append($" | total={ctx.counts.Values.Sum()} | rejected candidates: " + string.Join(", ", ctx.rejected.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")));
            return LastReport = sb.ToString();
        }

        static void Rej(Ctx c, string k) { c.rejected[k] = (c.rejected.TryGetValue(k, out var v) ? v : 0) + 1; }
        static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);

        static void Scatter(Ctx ctx, int count, string key, Transform group, System.Func<GameObject> pick, Rect area, float pathClear, float spacing, float ravineClear, float maxSlope,
                            float sMin, float sMax, float radius, float tilt, float sink, int attempts)
        {
            int made = 0;
            for (int i = 0; i < attempts * 10 && made < count; i++)
            {
                var p = new Vector2(area.xMin + (float)ctx.rng.NextDouble() * area.width, area.yMin + (float)ctx.rng.NextDouble() * area.height);
                if (!TryPlace(ctx, p, pathClear, spacing, ravineClear, maxSlope, out var pos, out var tn)) { Rej(ctx, key); continue; }
                var prefab = pick();
                Spawn(ctx, prefab, group, pos, prefab.name.Replace("_Custom", "").Replace("_Estate_02", "").Replace("Tree", "Tree"), sMin, sMax, radius, tilt, sink, key);
                made++;
            }
        }

        /// <summary>Terrain height + all the keep-clear rules for one candidate. spacing = free radius needed around the new object.</summary>
        static bool TryPlace(Ctx ctx, Vector2 p, float pathClear, float spacing, float ravineClear, float maxSlope, out Vector3 pos, out Terrain terrain)
        {
            pos = default; terrain = null;
            foreach (var t in Terrain.activeTerrains)
            {
                var o = t.transform.position; var s = t.terrainData.size;
                if (p.x > o.x + 1f && p.x < o.x + s.x - 1f && p.y > o.z + 1f && p.y < o.z + s.z - 1f) { terrain = t; break; }
            }
            if (terrain == null) return false;
            if (DistToLine(p, Footpath) < pathClear + 0.5f && p.y > -2f) return false;
            if (DistToLine(p, Trail) < pathClear && p.y < 2f) return false;
            if (p.y < 0f && Mathf.Abs(p.y - Ravine(p.x)) < ravineClear + 2.0f) return false;           // stream and its banks
            foreach (var d in ctx.discs) if (Vector2.Distance(p, d.p) < d.r + spacing * 0.3f) return false;
            foreach (var r in ctx.rects) if (r.Contains(p)) return false;
            foreach (var e in ctx.placed) if (Vector2.Distance(p, e.p) < e.r * 0.65f + spacing * 0.55f) return false;
            var td = terrain.terrainData;
            float nx = (p.x - terrain.transform.position.x) / td.size.x, nz = (p.y - terrain.transform.position.z) / td.size.z;
            if (td.GetSteepness(nx, nz) > maxSlope) return false;
            float y = terrain.transform.position.y + td.GetInterpolatedHeight(nx, nz);
            pos = new Vector3(p.x, y, p.y);
            // anything solid already standing there (tea rows, logs, structures, hut walls)
            foreach (var c in Physics.OverlapSphere(pos + Vector3.up * 1.2f, Mathf.Clamp(spacing * 0.3f, 0.5f, 1.0f), ~0, QueryTriggerInteraction.Ignore))
                if (!(c is TerrainCollider) && !c.transform.IsChildOf(ctx.parent)) return false;
            return true;
        }

        static void Spawn(Ctx ctx, GameObject prefab, Transform group, Vector3 pos, string nameBase, float sMin, float sMax, float radius, float tilt, float sink, string key)
        {
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, group);
            Undo.RegisterCreatedObjectUndo(go, "Custom nature");
            float s = sMin + (float)ctx.rng.NextDouble() * (sMax - sMin);
            float sxz = s * (0.92f + (float)ctx.rng.NextDouble() * 0.16f);
            go.transform.position = pos - Vector3.up * (sink * s);
            go.transform.rotation = Quaternion.Euler(((float)ctx.rng.NextDouble() * 2f - 1f) * tilt, (float)ctx.rng.NextDouble() * 360f, ((float)ctx.rng.NextDouble() * 2f - 1f) * tilt);
            go.transform.localScale = new Vector3(sxz, s, sxz);
            go.name = $"{nameBase}_{group.childCount:000}";
            ctx.placed.Add((XZ(pos), radius * s));
            ctx.counts[key] = (ctx.counts.TryGetValue(key, out var v) ? v : 0) + 1;
            Physics.SyncTransforms();
        }
    }

    /// <summary>Asks before a Custom Art tool touches a scene that is not a sandbox copy.</summary>
    public static class SceneGuard
    {
        /// <summary>Marks the open scenes dirty (not allowed in Play Mode, where changes are discarded anyway).</summary>
        public static void MarkDirty() { if (!Application.isPlaying) EditorSceneManager.MarkAllScenesDirty(); }

        public static bool Confirm(string what)
        {
            string path = SceneManager.GetActiveScene().path;
            if (path.Replace('\\', '/').Contains("/Sandbox/")) return true;
            return EditorUtility.DisplayDialog("Custom Art", $"{what} in '{path}'?\n\nThis is not a Sandbox scene. The change is undoable (Ctrl+Z) but the scene is not saved for you.", "Yes, do it", "Cancel");
        }
    }
}
