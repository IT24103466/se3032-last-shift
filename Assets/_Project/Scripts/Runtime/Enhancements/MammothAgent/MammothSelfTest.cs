using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;

namespace LastShift
{
    /// <summary>
    /// Built-in verification for the mammoth rules (results go to the Console as PASS / FAIL lines, see Docs/MammothAI.md):
    ///   F9  territory + spawn checks (non-destructive)
    ///   F8  fires 15 shots at the mammoth, one per frame (alive after 14, dead exactly on the 15th)   - kills the mammoth
    ///   F7  makes the mammoth land 20 attacks on the player (alive after 19, dead exactly on the 20th) - ends the run
    /// </summary>
    public class MammothSelfTest : MonoBehaviour
    {
        System.IO.StreamWriter log; float nextStatus; public bool statusLog = true;

        void Awake()
        {
            try   // mirror the mammoth log lines (+ a 5 s status line) to a text file next to the project, for the test report
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Application.dataPath), "MammothTestLog.txt");
                log = new System.IO.StreamWriter(path, true) { AutoFlush = true };
                log.WriteLine($"==== session start {System.DateTime.Now:HH:mm:ss}");
                Application.logMessageReceived += OnLog;
            }
            catch { log = null; }
        }
        void OnDestroy() { Application.logMessageReceived -= OnLog; log?.Dispose(); }
        void OnLog(string msg, string st, LogType t) { if (log != null && (msg.StartsWith("[Mammoth") || msg.StartsWith("[Sanjula"))) log.WriteLine($"{Time.time,7:F1} {msg}"); }

        void Status()
        {
            var ai = GetComponent<MammothAI>(); var pl = FindAnyObjectByType<PlayerController>(); if (ai == null || pl == null || log == null) return;
            var mp = ai.transform.position;
            log.WriteLine($"{Time.time,7:F1} STATUS mammoth=({mp.x:F0},{mp.z:F0}) act={ai.CurrentAction} inTerritory={MammothTerritory.Contains(mp)} campDist={Vector3.Distance(Flat(mp), Flat(MammothTerritory.Camp)):F0} dPlayer={Vector3.Distance(Flat(mp), Flat(pl.transform.position)):F0} playerHp={pl.Health.current:F0} shots={ai.ShotsTaken} attacks={ai.AttacksLanded}");
        }

        void Update()
        {
            if (statusLog && Time.time > nextStatus) { nextStatus = Time.time + 5f; Status(); }
            var k = Keyboard.current; if (k == null) return;
            if (k.f9Key.wasPressedThisFrame) StartCoroutine(TerritoryCheck());
            if (k.f8Key.wasPressedThisFrame) StartCoroutine(ShotsCheck());
            if (k.f7Key.wasPressedThisFrame) StartCoroutine(AttacksCheck());
        }

        static void Report(string name, bool ok, string detail) => Debug.Log($"[MammothTest] {(ok ? "PASS" : "FAIL")}  {name}  -  {detail}");

        IEnumerator TerritoryCheck()
        {
            var ai = FindAnyObjectByType<MammothAI>();
            Report("mammoth exists (exactly one)", FindObjectsByType<MammothAI>().Length == 1, "count=" + FindObjectsByType<MammothAI>().Length);
            if (ai != null)
                Report("mammoth is inside the jungle territory", MammothTerritory.Contains(ai.transform.position),
                       $"pos={ai.transform.position:F0}, campDist={Vector3.Distance(Flat(ai.transform.position), Flat(MammothTerritory.Camp)):F0} (camp radius {MammothTerritory.CampRadius})");
            int bad = 0, n = 0; var rnd = new System.Random(7);
            for (int i = 0; i < 500; i++)
            {
                var p = new Vector3(MammothTerritory.Camp.x + (float)(rnd.NextDouble() - 0.5) * 80f, 0f, MammothTerritory.Camp.z + (float)(rnd.NextDouble() - 0.5) * 80f);
                n++; if (!MammothTerritory.Contains(MammothTerritory.Clamp(p))) bad++;
            }
            Report("Clamp() always lands in legal ground (500 random points around the camp)", bad == 0, $"{bad} illegal of {n}");
            Report("camp centre is outside the territory", !MammothTerritory.Contains(MammothTerritory.Camp), "");
            yield break;
        }

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }

        IEnumerator ShotsCheck()
        {
            var ai = FindAnyObjectByType<MammothAI>(); if (ai == null) { Report("15 shots", false, "no mammoth"); yield break; }
            var hp = ai.GetComponent<Health>(); var pl = FindAnyObjectByType<PlayerController>();
            bool deadEarly = false;
            for (int i = 1; i <= 15; i++)
            {
                hp.Damage(20f, pl.transform.position);
                hp.Damage(20f, pl.transform.position);          // second "pellet" in the same frame: must NOT count again
                if (i < 15 && (hp.Dead || ai.ShotsTaken != i)) deadEarly = true;
                yield return null;
            }
            Report("14 shots leave it alive, counted once per shot (pellets in one frame = 1 shot)", !deadEarly, "");
            Report("the 15th shot kills it", hp.Dead && ai.ShotsTaken == 15, $"shots={ai.ShotsTaken}, dead={hp.Dead}");
        }

        IEnumerator AttacksCheck()
        {
            var ai = FindAnyObjectByType<MammothAI>(); var pl = FindAnyObjectByType<PlayerController>();
            if (ai == null || pl == null) { Report("20 attacks", false, "no mammoth/player"); yield break; }
            bool diedEarly = false;
            for (int i = 1; i <= 20; i++)
            {
                ai.TestAttack();
                if (i < 20 && pl.Health.Dead) diedEarly = true;
                yield return null;
            }
            Report("player survives 19 attacks", !diedEarly && ai.AttacksLanded == 20, "");
            Report("the 20th attack kills the player", pl.Health.Dead, $"attacks={ai.AttacksLanded}, hp={pl.Health.current:F1}");
        }
    }
}
