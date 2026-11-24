using LastShift.AI.Core;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastShift.GuardDog
{
    /// <summary>
    /// F12: shows what Kalu is thinking, for testing and the viva.
    ///  - a map of the grid: walls, water (blue), cover (lighter), the scent trail (orange, by
    ///    strength), the cells the last A* search expanded (purple), the planned path (red), the
    ///    dog (white), the player (cyan) and the last noise it heard (yellow);
    ///  - the behaviour tree's active branch, the planner's numbers and the senses;
    ///  - the current action floating above the dog, and the path drawn in the world.
    /// </summary>
    public class GuardDogDebugView : MonoBehaviour
    {
        GuardDogBrain brain;
        ScentTracker world;
        PlayerController player;
        Texture2D tex;
        Color32[] pixels;
        LineRenderer line;
        bool visible;
        float nextRefresh;
        GUIStyle text, label;

        public void Bind(GuardDogBrain b, ScentTracker w, PlayerController p)
        {
            brain = b; world = w; player = p;
            var g = w.Grid;
            tex = new Texture2D(g.Width, g.Height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            pixels = new Color32[g.Count];
            line = new GameObject("GuardDog_PathLine").AddComponent<LineRenderer>();
            line.transform.SetParent(transform, false);
            line.widthMultiplier = 0.12f;
            line.material = new Material(Shader.Find("Sprites/Default"));
            line.startColor = line.endColor = new Color(1f, 0.2f, 0.15f, 0.9f);
            line.enabled = false;
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.f12Key.wasPressedThisFrame) { visible = !visible; line.enabled = visible; }
            if (!visible || brain == null) return;
            if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + 0.25f; Paint(); }
            DrawPathInWorld();
        }

        void Paint()
        {
            var g = world.Grid; var scent = world.Scent; var nav = brain.Navigator; float now = Time.time;
            for (int i = 0; i < g.Count; i++)
            {
                Color c;
                if (!g.Walkable[i]) c = new Color(0.05f, 0.05f, 0.06f);
                else if (g.Has(i, GridMap.CellFlags.Water)) c = new Color(0.1f, 0.22f, 0.5f);
                else c = g.Has(i, GridMap.CellFlags.Covered) ? new Color(0.2f, 0.24f, 0.2f) : new Color(0.12f, 0.16f, 0.12f);
                if (nav.Search.WasExpanded(i)) c = Color.Lerp(c, new Color(0.55f, 0.25f, 0.7f), 0.55f);
                float s = Mathf.Clamp01(scent.Strength(i, now));
                if (s > 0.01f) c = Color.Lerp(c, new Color(1f, 0.55f, 0.1f), 0.25f + 0.75f * s);
                pixels[i] = c;
            }
            var path = nav.Path;
            for (int k = 0; k < path.Count; k++) pixels[path[k]] = new Color(1f, 0.15f, 0.1f);
            Mark(brain.Blackboard.NoiseHandled ? -1 : world.CellOf(brain.Blackboard.NoisePos), new Color(1f, 0.95f, 0.2f), 1);
            Mark(world.CellOf(player.transform.position), new Color(0.2f, 1f, 1f), 1);
            Mark(world.CellOf(brain.transform.position), Color.white, 1);
            tex.SetPixels32(pixels);
            tex.Apply(false);
        }

        void Mark(int cell, Color c, int r)
        {
            if (cell < 0) return;
            var g = world.Grid; int cx = g.X(cell), cz = g.Z(cell);
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                    if (g.InBounds(cx + dx, cz + dz)) pixels[g.Index(cx + dx, cz + dz)] = c;
        }

        void DrawPathInWorld()
        {
            var path = brain.Navigator.Path;
            line.positionCount = path.Count;
            for (int k = 0; k < path.Count; k++) line.SetPosition(k, world.WorldOf(path[k]) + Vector3.up * 0.15f);
        }

        void OnGUI()
        {
            if (!visible || brain == null || tex == null) return;
            text ??= new GUIStyle(GUI.skin.label) { fontSize = 13, normal = { textColor = Color.white } };
            label ??= new GUIStyle(GUI.skin.box) { fontSize = 14, normal = { textColor = new Color(1f, 0.85f, 0.4f) } };

            float scale = Screen.height / 1080f;
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = 250f, h = w * world.Grid.Height / world.Grid.Width, x = 16f, y = 150f;
            GUI.Box(new Rect(x - 6, y - 26, w + 12, h + 250), "KALU – guard dog AI (F12)");
            GUI.DrawTexture(new Rect(x, y, w, h), tex, ScaleMode.StretchToFill, false);

            var bb = brain.Blackboard; var nav = brain.Navigator; var r = nav.LastResult; float now = Time.time;
            string trail = bb.TrailAhead.Valid ? $"{now - bb.TrailAhead.Time:F1}s old, {bb.TrailAhead.Strength:F2}" : "none";
            GUI.Label(new Rect(x, y + h + 6, w + 260, 220),
                $"BT: {brain.ActiveBranch}\n" +
                $"A* [{nav.CurrentMode}] expanded {r.Expanded}, {nav.LastPlanMs:F2} ms, cost {r.Cost:F1}{(r.Partial ? " (partial)" : "")}, plans {nav.PlansMade}\n" +
                $"Tick {brain.TickMs:F2} ms   HP {brain.GetComponent<Health>().current:F0} (hits {brain.GetComponent<DogBody>().HitsTaken}/{brain.GetComponent<DogBody>().shotsToKill})   stuck events {brain.StuckEvents}\n" +
                $"Body: {brain.GetComponent<DogBody>().SteeringInfo}\n" +
                $"Sees player: {bb.SeesPlayer}  dist {bb.DistToPlayer:F1} m  hidden: {bb.PlayerHidden}  gun on dog: {bb.PlayerAimingAtMe}\n" +
                $"Scent here {bb.ScentHere:F2}   trail ahead: {trail}\n" +
                $"Noise: {(bb.NoiseHandled ? "-" : $"{bb.NoiseLoudness:F0} m, {now - bb.NoiseTime:F1}s ago")}   interest {bb.Interest:F2}   alerted {bb.Alerted(now)}\n" +
                "Map: orange scent, purple A* search, red path, blue water, cyan you, white dog, yellow noise", text);
            GUI.matrix = old;

            // What it's doing, above its head
            var cam = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();
            if (cam == null) return;
            Vector3 sp = cam.WorldToScreenPoint(brain.transform.position + Vector3.up * 1.3f);
            if (sp.z <= 0f) return;
            string leaf = brain.ActiveBranch; int cut = leaf.LastIndexOf('>');
            if (cut >= 0) leaf = leaf.Substring(cut + 1).Trim();
            var size = label.CalcSize(new GUIContent(leaf));
            GUI.Box(new Rect(sp.x - size.x / 2f, Screen.height - sp.y - size.y, size.x, size.y), leaf, label);
        }
    }
}
