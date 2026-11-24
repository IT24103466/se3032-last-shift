using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace LastShift
{
    /// <summary>
    /// Runs one night at Kirimala: intro, objectives (jungle -> estate -> premises -> find the ring
    /// in one of the tea bags -> back through the jungle to the camp), the alarm, win/lose, pause, and the HUD.
    /// </summary>
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance { get; private set; }
        public enum Phase { Intro, Playing, Paused, Won, Lost }

        public Phase phase = Phase.Intro;
        [Tooltip("Spawned when the alarm goes off")]
        public GameObject alarmMusic;

        public bool IsPlaying => phase == Phase.Playing;
        public bool AlarmRaised { get; private set; }
        public bool HasRing { get; private set; }

        PlayerController player;
        TeaBag[] bags = new TeaBag[0];
        bool reachedEstate;
        int searched, knockedOut;
        float startTime, endTime, flash, toastUntil, lastEscapeNag;
        string toast, loseReason;
        GUIStyle box, big, title, body, small, center;
        bool debugAI;
        PlayerSlingshot sling;
        PlayerGun gun;
        Vector3 lastHitFrom;
        float hitDirUntil;
        bool hitWasHead;
        EnemyAI[] enemies = new EnemyAI[0];
        Texture2D white;

        void Awake()
        {
            Instance = this;
            Time.timeScale = 0f;
            SetCursor(false);
        }

        void OnDestroy() { if (Instance == this) Instance = null; Time.timeScale = 1f; }

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            bags = FindObjectsByType<TeaBag>(FindObjectsInactive.Exclude);
            enemies = FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude);
            if (bags.Length > 0)
            {
                // The ring is in a different bag every night (never the first one the player is likely to find)
                var pick = bags[Random.Range(0, bags.Length)];
                pick.hasRing = true;
            }
            // The finish: back at the camp in the jungle where the game started (by the rocks)
            var spawn = GameObject.Find("Spawn_Player");
            camp = spawn != null ? spawn.transform.position : player != null ? player.transform.position : Vector3.zero;
            if (player != null)
            {
                player.Health.Damaged += (a, f) => flash = Mathf.Min(0.7f, flash + a / 40f);
                player.Health.Died += () => Lose("Nimal was caught. The ring is lost.");
            }
        }

        void Update()
        {
            var kb = Keyboard.current;
            if (kb == null) return;
            flash = Mathf.Max(0f, flash - Time.unscaledDeltaTime * 1.2f);
            if (kb.f9Key.wasPressedThisFrame) debugAI = !debugAI;
            if (IsPlaying && alarmQueue.Count > 0) DispatchGuards();

            switch (phase)
            {
                case Phase.Intro:
                    if (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame) Begin();
                    break;
                case Phase.Playing:
                    if (kb.escapeKey.wasPressedThisFrame) { phase = Phase.Paused; Time.timeScale = 0f; SetCursor(false); }
                    if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked) SetCursor(true);
                    if (HasRing && player != null && !player.Health.Dead && CampDistance() < CampFinishRadius) { Win(); break; }
                    if (!reachedEstate && player != null && player.transform.position.z > 1f)
                    {
                        reachedEstate = true;
                        Toast("You made it out of the jungle. The factory is up the hill.");
                    }
                    break;
                case Phase.Paused:
                    if (kb.escapeKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) { phase = Phase.Playing; Time.timeScale = 1f; SetCursor(true); }
                    if (kb.rKey.wasPressedThisFrame) Restart();
                    break;
                case Phase.Won:
                case Phase.Lost:
                    if (kb.rKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame) Restart();
                    break;
            }
        }

        void Begin()
        {
            phase = Phase.Playing;
            Time.timeScale = 1f;
            startTime = Time.time;
            SetCursor(true);
            Toast("Find the tea bag with your ring. Stay quiet in the jungle.");
        }

        void Restart()
        {
            Time.timeScale = 1f;
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex >= 0 ? SceneManager.GetActiveScene().buildIndex : 0);
        }

        static void SetCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        // ------------------------------------------------------------------ game events

        public void Toast(string text, float seconds = 3.5f)
        {
            toast = text;
            toastUntil = Time.unscaledTime + seconds;
        }

        public void RaiseAlarm()
        {
            if (AlarmRaised || !IsPlaying) return;
            AlarmRaised = true;
            Toast("ALARM! Security knows someone is inside the premises!", 4f);
            // Security comes in waves, not all at once: the nearest guards go first (at most
            // EnemyAI.MaxResponders out at a time); the rest are sent one by one as slots free up.
            // Factory officers (workers) close by join in too.
            foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude))
                if (!e.Dead && !alarmQueue.Contains(e) && (e.kind == EnemyKind.Guard || e.kind == EnemyKind.Worker && Vector3.Distance(e.transform.position, player.transform.position) < 40f))
                    alarmQueue.Add(e);
            nextDispatch = 0f;
            DispatchGuards();
            AlarmSiren.Sound(new Vector3(30f, 18f, 64f));   // the factory siren: everyone on the estate hears it
            if (alarmMusic != null) Instantiate(alarmMusic, transform);
        }

        // ---- Alarm waves
        readonly System.Collections.Generic.List<EnemyAI> alarmQueue = new System.Collections.Generic.List<EnemyAI>();
        float nextDispatch;
        const float DispatchGap = 5f;   // seconds between sending one guard and the next

        /// <summary>Sends the nearest waiting guard to where the player is, if a responder slot is free.</summary>
        void DispatchGuards()
        {
            alarmQueue.RemoveAll(e => e == null || e.Dead);
            if (alarmQueue.Count == 0 || player == null || Time.time < nextDispatch) return;
            Vector3 p = player.transform.position;
            alarmQueue.Sort((a, b) => (a.transform.position - p).sqrMagnitude.CompareTo((b.transform.position - p).sqrMagnitude));
            var g = alarmQueue[0];
            if (!g.Alert(p)) return;   // two already out: try again later
            alarmQueue.RemoveAt(0);
            nextDispatch = Time.time + DispatchGap;
        }

        public void OnBagSearched(TeaBag bag)
        {
            searched++;
            if (bag.hasRing)
            {
                HasRing = true;
                Toast("Your wedding ring! It was in this bag. Now get back through the jungle to your camp!", 5f);
                // Everyone hears the commotion
                foreach (var e in FindObjectsByType<EnemyAI>(FindObjectsInactive.Exclude))
                    if (e.kind == EnemyKind.Guard && !e.Dead && !alarmQueue.Contains(e)) alarmQueue.Add(e);
                nextDispatch = 0f;
            }
            else Toast($"Just tea leaves... ({searched}/{bags.Length} bags searched)");
        }

        public void TryEscape()
        {
            if (!IsPlaying) return;
            if (Time.time - lastEscapeNag < 5f) return;
            lastEscapeNag = Time.time;
            Toast(HasRing ? "The estate road is watched. Go back down through the tea and the jungle to your camp!" : "You can't leave without your ring!");
        }

        // ---- The finish line: Nimal's camp in the jungle (where the game started)
        Vector3 camp;
        const float CampFinishRadius = 7f;
        float CampDistance() { Vector3 d = player.transform.position - camp; d.y = 0f; return d.magnitude; }

        /// <summary>The player was hurt by someone: remember where from (the HUD points that way).</summary>
        public void OnPlayerHit(EnemyAI by)
        {
            if (by == null) return;
            lastHitFrom = by.transform.position;
            hitDirUntil = Time.unscaledTime + 1.2f;
        }

        /// <summary>Gunshots inside the factory premises give the game away.</summary>
        public void OnPlayerGunshot()
        {
            if (player != null && player.transform.position.z > 39f) RaiseAlarm();
        }

        float hitMarkerUntil;
        public void HitMarker(bool headshot = false) { hitMarkerUntil = Time.unscaledTime + 0.25f; hitWasHead = headshot; }

        public void OnEnemyDown(EnemyAI e)
        {
            knockedOut++;
            if (e.kind == EnemyKind.Guard || e.kind == EnemyKind.Worker) Toast($"{e.kind} down. He dropped some ammo.", 2f);
            else if (e.kind == EnemyKind.Hunter) Toast("The hunter is down. Grab his ammo.", 2.5f);
            else if (e.kind == EnemyKind.Tiger) Toast("The tiger slinks away, beaten.", 2f);
        }

        void Win()
        {
            phase = Phase.Won;
            endTime = Time.time;
            SetCursor(false);
        }

        public void Lose(string reason)
        {
            if (phase == Phase.Won || phase == Phase.Lost) return;
            phase = Phase.Lost;
            loseReason = reason;
            endTime = Time.time;
            SetCursor(false);
        }

        string Objective()
        {
            if (!reachedEstate) return "Get through the jungle to the tea estate (follow the trail north)";
            if (!AlarmRaised && !HasRing) return "Sneak into the factory premises: part of the fence has collapsed near the east end";
            if (!HasRing) return $"Search the tea bags for your ring ({searched}/{bags.Length})\nBags: dispatch warehouse, withering loft, storeroom, workers' line house";
            return $"Get back to your camp in the jungle where you started, by the rocks ({CampDistance():F0} m)\nOut through the fence, down through the tea, over the stream";
        }

        // ------------------------------------------------------------------ HUD

        GUISkin stylesSkin;

        void Styles()
        {
            // Rebuild the styles whenever the GUI skin (and so its font) changes. If Play is pressed
            // while the Game view is hidden, the first OnGUI can come from a different skin whose font
            // later goes away: the panels then draw their dark background but no text at all.
            if (box != null && stylesSkin == GUI.skin && box.font == GUI.skin.font) return;
            stylesSkin = GUI.skin;
            box = new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 16, wordWrap = true, padding = new RectOffset(12, 12, 8, 8) };
            box.normal.textColor = Color.white;
            big = new GUIStyle(GUI.skin.label) { fontSize = 22, alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
            big.normal.textColor = Color.white;
            title = new GUIStyle(big) { fontSize = 46 };
            title.normal.textColor = new Color(1f, 0.78f, 0.4f);
            body = new GUIStyle(GUI.skin.label) { fontSize = 18, wordWrap = true, alignment = TextAnchor.UpperCenter };
            body.normal.textColor = new Color(0.92f, 0.92f, 0.92f);
            small = new GUIStyle(body) { fontSize = 14 };
            center = new GUIStyle(big) { fontSize = 18, fontStyle = FontStyle.Normal };
        }

        void Fill(Rect r, Color c)
        {
            if (white == null) { white = new Texture2D(1, 1); white.SetPixel(0, 0, Color.white); white.Apply(); }
            var old = GUI.color; GUI.color = c; GUI.DrawTexture(r, white); GUI.color = old;
        }

        void OnGUI()
        {
            Styles();
            // Lay the HUD out on a 1080p canvas and scale it to the real screen
            float scale = Screen.height / 1080f;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = 1080f;

            if (player != null && player.InLocker)
            {
                // Peeking through the locker vents
                Fill(new Rect(0, 0, w, h * 0.36f), new Color(0f, 0f, 0f, 0.92f));
                Fill(new Rect(0, h * 0.64f, w, h * 0.36f), new Color(0f, 0f, 0f, 0.92f));
                for (int i = 0; i < 6; i++) Fill(new Rect(0, h * 0.36f + i * h * 0.05f, w, h * 0.02f), new Color(0.05f, 0.07f, 0.05f, 0.95f));
            }
            if (flash > 0f) Fill(new Rect(0, 0, w, h), new Color(0.6f, 0f, 0f, flash * 0.5f));

            switch (phase)
            {
                case Phase.Intro: DrawIntro(w, h); return;
                case Phase.Paused: DrawHud(w, h); DrawPanel(w, h, "PAUSED", "Esc / Enter  resume        R  restart", null); return;
                case Phase.Won:
                    DrawPanel(w, h, "YOU ESCAPED", $"Nimal slips back into the jungle and reaches his camp with the ring safe in his pocket.\nTime: {Format(endTime - startTime)}    Bags searched: {searched}/{bags.Length}    Knocked out: {knockedOut}", "Press R to play again");
                    return;
                case Phase.Lost:
                    DrawPanel(w, h, "CAUGHT", loseReason, "Press R to try again");
                    return;
            }
            DrawHud(w, h);
        }

        void DrawHud(float w, float h)
        {
            if (player == null) return;
            // Objective
            GUI.Box(new Rect(16, 16, 470, 70), "<b>OBJECTIVE</b>\n" + Objective(), new GUIStyle(box) { richText = true, fontSize = 15 });
            // Health and stamina
            float bw = 260f;
            Fill(new Rect(16, h - 62, bw + 4, 20), new Color(0, 0, 0, 0.6f));
            Fill(new Rect(18, h - 60, bw * player.Health.Fraction, 16), new Color(0.8f, 0.15f, 0.12f));
            Fill(new Rect(16, h - 36, bw + 4, 14), new Color(0, 0, 0, 0.6f));
            Fill(new Rect(18, h - 34, bw * player.Stamina / player.maxStamina, 10), new Color(0.95f, 0.8f, 0.3f));
            GUI.Label(new Rect(bw + 28, h - 64, 200, 22), $"{Mathf.CeilToInt(player.Health.current)} HP", small);
            // Status chips
            float x = w - 16;
            void Chip(string text, Color c) { x -= 130; Fill(new Rect(x, 16, 120, 30), c); GUI.Label(new Rect(x, 16, 120, 30), text, center); x -= 6; }
            if (HasRing) Chip("RING ✓", new Color(0.75f, 0.6f, 0.1f, 0.85f));
            if (AlarmRaised) Chip("ALARM", new Color(0.7f, 0.05f, 0.05f, 0.4f + 0.4f * Mathf.PingPong(Time.time * 2f, 1f)));
            if (player.Hidden) Chip("HIDDEN", new Color(0.1f, 0.35f, 0.2f, 0.85f));
            else if (player.Crouched) Chip("CROUCHING", new Color(0.2f, 0.2f, 0.2f, 0.75f));
            // Crosshair: four ticks that open up with the weapon's spread and recoil
            if (sling == null) sling = player.GetComponent<PlayerSlingshot>();
            if (gun == null) gun = player.GetComponent<PlayerGun>();
            if (!player.InLocker)
            {
                float gap = 5f + (gun != null && gun.OnGun ? gun.Spread * 9f : 3f);
                Color ch = new Color(1f, 1f, 1f, 0.85f);
                Fill(new Rect(w / 2 - gap - 9, h / 2 - 1, 9, 2), ch); Fill(new Rect(w / 2 + gap, h / 2 - 1, 9, 2), ch);
                Fill(new Rect(w / 2 - 1, h / 2 - gap - 9, 2, 9), ch); Fill(new Rect(w / 2 - 1, h / 2 + gap, 2, 9), ch);
                Fill(new Rect(w / 2 - 1, h / 2 - 1, 2, 2), ch);
            }
            if (Time.unscaledTime < hitMarkerUntil)
            {
                Color hc = hitWasHead ? new Color(1f, 0.25f, 0.2f) : new Color(1f, 1f, 1f);
                var old = GUI.matrix;
                GUIUtility.RotateAroundPivot(45f, new Vector2(Screen.width / 2f, Screen.height / 2f));   // pivot in screen pixels
                Fill(new Rect(w / 2 - 14, h / 2 - 1.5f, 28, 3), hc); Fill(new Rect(w / 2 - 1.5f, h / 2 - 14, 3, 28), hc);
                GUI.matrix = old;
            }
            // One fight at a time: who's after you; and how close the others are to noticing you
            string threat = ThreatDirector.HolderName;
            if (threat != null)
                GUI.Label(new Rect(w / 2 - 300, h * 0.14f, 600, 30), $"{threat} is after you!", new GUIStyle(center) { normal = { textColor = new Color(1f, 0.45f, 0.35f) } });
            else
            {
                float sus = 0f;
                foreach (var e in EnemyAI.All) if (e != null && e.Suspicion > sus && e.Suspicion < 1f) sus = e.Suspicion;
                if (sus > 0.03f)
                {
                    // Detection meter: fills while someone is starting to notice you; get out of sight or crouch
                    Color mc = Color.Lerp(new Color(1f, 0.9f, 0.3f), new Color(1f, 0.35f, 0.2f), sus);
                    Fill(new Rect(w / 2 - 42, h / 2 - 46, 84, 7), new Color(0, 0, 0, 0.55f));
                    Fill(new Rect(w / 2 - 40, h / 2 - 44, 80 * sus, 3), mc);
                    GUI.Label(new Rect(w / 2 - 100, h / 2 - 70, 200, 22), sus > 0.6f ? "!  BEING SPOTTED" : "?  someone noticed something", new GUIStyle(small) { normal = { textColor = mc } });
                }
            }

            // Which way the last hit came from
            if (Time.unscaledTime < hitDirUntil)
            {
                Vector3 to = lastHitFrom - player.transform.position; to.y = 0f;
                float ang = Vector3.SignedAngle(player.transform.forward, to, Vector3.up);
                var old = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, new Vector2(Screen.width / 2f, Screen.height / 2f));
                float a = Mathf.Clamp01((hitDirUntil - Time.unscaledTime) / 0.6f);
                Fill(new Rect(w / 2 - 40, h / 2 - 150, 80, 8), new Color(0.9f, 0.1f, 0.05f, 0.85f * a));
                Fill(new Rect(w / 2 - 22, h / 2 - 160, 44, 10), new Color(0.9f, 0.1f, 0.05f, 0.85f * a));
                GUI.matrix = old;
            }
            // Weapon panel: what's in hand, ammo, reloading
            Fill(new Rect(w - 276, h - 106, 260, 90), new Color(0, 0, 0, 0.55f));
            var ammoBig = new GUIStyle(big) { fontSize = 34, alignment = TextAnchor.MiddleRight };
            var label = new GUIStyle(small) { alignment = TextAnchor.UpperLeft };
            if (gun != null && gun.OnGun)
            {
                var g = gun.Current;
                GUI.Label(new Rect(w - 264, h - 102, 160, 24), g.name.ToUpper(), label);
                ammoBig.normal.textColor = g.mag == 0 ? new Color(1f, 0.35f, 0.3f) : Color.white;
                GUI.Label(new Rect(w - 270, h - 84, 150, 50), g.mag.ToString(), ammoBig);
                GUI.Label(new Rect(w - 116, h - 72, 100, 30), "/ " + g.reserve, new GUIStyle(center) { alignment = TextAnchor.MiddleLeft });
                if (gun.Reloading)
                {
                    Fill(new Rect(w / 2 - 60, h / 2 + 34, 120, 8), new Color(0, 0, 0, 0.6f));
                    Fill(new Rect(w / 2 - 58, h / 2 + 36, 116 * gun.ReloadProgress, 4), new Color(0.9f, 0.85f, 0.6f));
                    GUI.Label(new Rect(w / 2 - 100, h / 2 + 44, 200, 24), "RELOADING", small);
                }
                else if (g.mag == 0) GUI.Label(new Rect(w / 2 - 150, h / 2 + 34, 300, 24), g.reserve > 0 ? "R  Reload" : "No ammo: switch weapon (1-3) or find an ammo box", small);
                else if (g.mag <= g.magSize / 4) GUI.Label(new Rect(w / 2 - 100, h / 2 + 34, 200, 24), "Low ammo  (R)", small);
            }
            if (sling != null && gun != null && !gun.OnGun)
            {
                GUI.Label(new Rect(w - 264, h - 102, 160, 24), "SLINGSHOT", label);
                GUI.Label(new Rect(w - 270, h - 84, 150, 50), sling.ammo.ToString(), ammoBig);
                GUI.Label(new Rect(w - 116, h - 72, 100, 30), "/ " + sling.maxAmmo, new GUIStyle(center) { alignment = TextAnchor.MiddleLeft });
            }
            if (gun != null)
                GUI.Label(new Rect(w - 264, h - 40, 250, 22), "1 Pistol   2 Shotgun   3 Slingshot", new GUIStyle(small) { fontSize = 12, alignment = TextAnchor.UpperLeft });
            if (sling != null && sling.active)
            {
                if (sling.Drawing)
                {
                    Fill(new Rect(w / 2 - 40, h / 2 + 22, 80, 6), new Color(0, 0, 0, 0.6f));
                    Fill(new Rect(w / 2 - 39, h / 2 + 23, 78 * sling.DrawAmount, 4), new Color(1f, 0.85f, 0.4f));
                }
            }
            if (!string.IsNullOrEmpty(player.Prompt)) GUI.Label(new Rect(w / 2 - 300, h * 0.58f, 600, 30), player.Prompt, center);
            if (player.SearchProgress > 0f)
            {
                Fill(new Rect(w / 2 - 100, h * 0.58f + 34, 200, 10), new Color(0, 0, 0, 0.6f));
                Fill(new Rect(w / 2 - 98, h * 0.58f + 36, 196 * player.SearchProgress, 6), new Color(0.9f, 0.75f, 0.4f));
            }
            // AI debug view (F9): every character's state and distance
            if (debugAI)
            {
                var sb = new System.Text.StringBuilder("<b>AI (F9)</b>\n");
                foreach (var e in enemies)
                    if (e != null) sb.AppendLine($"{e.name,-12} {e.StateName,-8} {Vector3.Distance(e.transform.position, player.transform.position),5:0} m");
                GUI.Box(new Rect(16, 100, 300, 24 + enemies.Length * 19), sb.ToString(), new GUIStyle(box) { richText = true, fontSize = 14 });
            }
            // Toast
            if (toast != null && Time.unscaledTime < toastUntil)
            {
                Fill(new Rect(w / 2 - 330, 90, 660, 44), new Color(0, 0, 0, 0.55f));
                GUI.Label(new Rect(w / 2 - 330, 90, 660, 44), toast, center);
            }
        }

        void DrawIntro(float w, float h)
        {
            Fill(new Rect(0, 0, w, h), new Color(0.02f, 0.03f, 0.06f, 0.88f));
            float cw = Mathf.Min(900f, w - 40f), x = (w - cw) / 2f;
            GUI.Label(new Rect(x, h * 0.12f, cw, 60), "LAST SHIFT", title);
            GUI.Label(new Rect(x, h * 0.12f + 58, cw, 30), "Kirimala Estate · Sri Lanka", center);
            GUI.Label(new Rect(x, h * 0.26f, cw, 200),
                "This afternoon Nimal helped load the day's tea at the Kirimala factory. Tonight he noticed his wedding ring was gone: it slipped " +
                "into one of the tea bags, and the bags leave on the lorry at dawn.\n\n" +
                "He has to get it back before morning: through the jungle (tigers and the old elephant: the camp fire keeps them away), " +
                "up through the tea, over the factory fence, past the armed security guards and night-shift workers, and then all the way back to his camp.", body);
            GUI.Label(new Rect(x, h * 0.58f, cw, 120),
                "WASD move  ·  Shift sprint  ·  Space jump  ·  C crouch (hide in bushes)  ·  Left click shoot  ·  R reload  ·  1 pistol  2 shotgun  3 slingshot  ·  Right click stick\n" +
                "E use doors, search bags (hold), take ammo, hide in lockers  ·  F torch (helps you see, but helps them see you)  ·  Esc pause\nTip: gunshots are loud (shooting inside the premises sets off the alarm); a slingshot pebble lures a guard away quietly", small);
            GUI.Label(new Rect(x, h * 0.70f, cw, 30), "Demo checkpoints: F1 jungle camp · F2 stream · F3 estate · F4 fence breach · F5 warehouse · F6 main gate", small);
            GUI.Label(new Rect(x, h * 0.78f, cw, 40), "Press ENTER to begin", big);
        }

        void DrawPanel(float w, float h, string head, string text, string foot)
        {
            Fill(new Rect(0, 0, w, h), new Color(0f, 0f, 0f, 0.7f));
            float cw = Mathf.Min(820f, w - 40f), x = (w - cw) / 2f;
            GUI.Label(new Rect(x, h * 0.3f, cw, 60), head, title);
            GUI.Label(new Rect(x, h * 0.3f + 80, cw, 100), text, body);
            if (foot != null) GUI.Label(new Rect(x, h * 0.3f + 190, cw, 40), foot, big);
        }

        static string Format(float s) => $"{(int)(s / 60f)}:{(int)(s % 60f):00}";
    }
}
