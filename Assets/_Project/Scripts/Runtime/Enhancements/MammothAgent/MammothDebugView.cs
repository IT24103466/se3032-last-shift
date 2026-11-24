using UnityEngine;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Debug visualiser for the mammoth's mind (toggle with F11). On screen: current action, the utility of every action, the
    /// belief state/confidence, alertness, memory counters and the 15-shot / 20-attack counters. In the Scene view (and the Game view
    /// with Gizmos on): the tactical graph, the belief as vertical bars, the planned A* route, the estimated player position and the
    /// territory (camp exclusion circle). Costs nothing while hidden.
    /// </summary>
    public class MammothDebugView : MonoBehaviour
    {
        public bool show;
        MammothAI ai; MammothDecision decision; MammothBelief belief; MammothPerception perc; MammothMemory memory; MammothPlanner planner;
        static readonly string[] Names = { "Patrol", "Investigate", "Track", "Search", "Pursue", "Ambush", "ReturnHome" };

        void Start()
        {
            ai = GetComponent<MammothAI>(); decision = GetComponent<MammothDecision>(); belief = GetComponent<MammothBelief>();
            perc = GetComponent<MammothPerception>(); memory = GetComponent<MammothMemory>(); planner = GetComponent<MammothPlanner>();
        }

        void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f11Key.wasPressedThisFrame) show = !show;
            if (!show || ai == null) return;
            var g = belief != null && belief.Ready ? belief.Graph : null;
            if (g != null)
            {
                for (int i = 0; i < g.Count; i++)
                {
                    if (!g.Reachable(i)) continue;
                    Vector3 p = g.Position(i) + Vector3.up * 0.5f;
                    foreach (int j in g.Neighbours(i)) if (j > i) Debug.DrawLine(p, g.Position(j) + Vector3.up * 0.5f, new Color(0.2f, 0.8f, 0.3f, 0.5f));
                    float pr = belief.Probability(i);
                    Debug.DrawLine(p, p + Vector3.up * (1f + pr * 40f), Color.Lerp(Color.yellow, Color.red, Mathf.Clamp01(pr * 6f)));
                }
                var e = belief.EstimatedPosition + Vector3.up * 1f;
                Debug.DrawLine(e - Vector3.right * 2f, e + Vector3.right * 2f, Color.red); Debug.DrawLine(e - Vector3.forward * 2f, e + Vector3.forward * 2f, Color.red);
            }
            if (planner != null && planner.Ready && planner.Route != null)
                for (int i = 1; i < planner.Route.Count; i++) Debug.DrawLine(planner.Route[i - 1] + Vector3.up, planner.Route[i] + Vector3.up, Color.cyan);
            if (MammothTerritory.Ready)
                for (int i = 0; i < 48; i++)
                {
                    float a0 = i / 48f * Mathf.PI * 2f, a1 = (i + 1) / 48f * Mathf.PI * 2f; float R = MammothTerritory.CampRadius; var c = MammothTerritory.Camp;
                    Debug.DrawLine(c + new Vector3(Mathf.Cos(a0), 0.5f, Mathf.Sin(a0)) * R, c + new Vector3(Mathf.Cos(a1), 0.5f, Mathf.Sin(a1)) * R, Color.magenta);
                }
        }

        void OnGUI()
        {
            if (!show || ai == null || decision == null) return;
            float s = Screen.height / 1080f; GUI.matrix = Matrix4x4.Scale(new Vector3(s, s, 1f));
            var box = new Rect(20, 120, 360, 330);
            GUI.color = new Color(0, 0, 0, 0.72f); GUI.DrawTexture(box, Texture2D.whiteTexture); GUI.color = Color.white;
            var st = new GUIStyle(GUI.skin.label) { fontSize = 14 }; st.normal.textColor = new Color(0.9f, 1f, 0.9f);
            float y = box.y + 6;
            void Line(string t) { GUI.Label(new Rect(box.x + 8, y, box.width - 16, 20), t, st); y += 20; }
            Line($"MAMMOTH MIND (F11)   action: {decision.Current}   {decision.TimeInAction:F1}s");
            for (int i = 0; i < Names.Length; i++)
            {
                float u = decision.Utilities[i];
                GUI.color = i == (int)decision.Current ? new Color(1f, 0.8f, 0.2f) : new Color(0.4f, 0.8f, 1f);
                GUI.DrawTexture(new Rect(box.x + 110, y + 4, 230 * Mathf.Clamp01(u), 10), Texture2D.whiteTexture);
                GUI.color = Color.white; Line($"{Names[i],-11} {u:F2}");
            }
            if (belief != null) Line($"belief {belief.State}  conf {belief.Confidence:F2}  peak {belief.PeakMass:F2}");
            if (perc != null) Line($"alert {(perc.IsAlert ? "YES" : "no")}   visible {perc.PlayerVisible}   noise age {(perc.HasHeardNoise ? perc.NoiseAge.ToString("F0") + "s" : "-")}");
            if (memory != null) Line($"hunt failures {memory.HuntFailures}   ignored repeat clues {memory.IgnoredRepeatClues}");
            if (planner != null) Line($"A* route {(planner.LastPlanFound ? planner.Route.Count + " pts" : "-")}  cost {planner.RouteCost:F0}  expanded {planner.LastExpanded}");
            Line($"enraged {ai.Enraged}   shots {ai.ShotsTaken}/{MammothAI.ShotsToKill}   attacks {ai.AttacksLanded}/{MammothAI.AttacksToKillPlayer}");
            Line($"in territory {MammothTerritory.Contains(transform.position)}   camp {Vector3.Distance(new Vector3(transform.position.x, 0, transform.position.z), new Vector3(MammothTerritory.Camp.x, 0, MammothTerritory.Camp.z)):F0} m");
            GUI.matrix = Matrix4x4.identity;
        }
    }
}
