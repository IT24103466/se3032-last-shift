using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.AI;
using WorldBuilder.Testing;

namespace LastShift
{
    /// <summary>
    /// Plugs the Python brain (GuardAgent/, "Sentinel") into one guard.
    ///
    /// Every <see cref="decisionInterval"/> seconds this component sends what the guard PERCEIVES to the Python server over a local TCP socket
    /// (his position, ammo, health, whether he can see the player and where, noises, the alarm, damage he took) and executes the answer:
    ///   goto   walk or run to a point (the NavMesh does the footsteps, doors are opened on the way)
    ///   engage hold position, face the player, aim, fire (wind-up, clear-shot check, hit chance by range and by what the player is doing)
    ///   cover  step to a nearby spot the player cannot see and hold it, shooting back at half rate
    ///   reload reload in place
    /// While the brain is connected the guard's built-in <see cref="EnemyAI"/> is switched off; if the connection drops, it is switched back on, so the game never breaks.
    ///
    /// Score = damage dealt to the player (+1 per hit point), minus time the player spends inside the factory building, and the breach / escape / death terms
    /// defined in GuardAgent/guard_agent/config.py. It is shown top-right (F8 hides it).
    /// Start the brain first:   cd GuardAgent ; .venv\Scripts\python -m guard_agent.server
    /// </summary>
    [RequireComponent(typeof(EnemyAI), typeof(NavMeshAgent), typeof(Health))]
    public class GuardAgentBridge : MonoBehaviour
    {
        [Header("Brain connection")]
        public string host = "127.0.0.1";
        public int port = 5555;
        public float decisionInterval = 0.5f;

        [Header("Agent-mode guard (must match GuardAgent/guard_agent/config.py)")]
        public float hp = 100f;
        public float walkSpeed = 1.8f, runSpeed = 5.2f, sight = 22f, fov = 120f, hearing = 12f;
        public float gunRange = 22f, gunDamage = 8f, gunInterval = 1.0f, gunAim = 0.5f, gunAccuracy = 0.65f, gunReload = 1.8f;
        public int gunMag = 12;
        public float eyeHeight = 1.7f;

        [Header("Score (read-only: the brain keeps the score and sends it back)")]
        public float damageDealt;
        public float score;
        public bool showHud = true;

        // ------------------------------------------------------------------ state
        EnemyAI ai; NavMeshAgent agent; Health health; CreatureAnimator anim; PlayerController player; Transform muzzle;
        GuardLink link;
        AgentAction action;
        readonly object actionLock = new object();
        AgentAction pending;
        float nextTick, aimUntil = -1f, nextShot, reloadUntil, nextDoorCheck, coverUntil, lastDamageTime = -99f;
        int ammo;
        float damageSinceTick, damageTakenSinceTick;
        bool alarmSent, wasPlaying, ended, hadConnection;
        Vector3 lastKnown, coverSpot;
        bool coverValid;
        readonly List<NoiseEvent> noises = new List<NoiseEvent>();
        string lastWhy = "", lastMode = "", lastZone = ""; string[] belief = new string[0];
        float lastLatencyMs;

        // Factory building = protected zones of the zone graph (x0, x1, z0, z1)
        static readonly float[][] Factory = { new[] { 10f, 36f, 52f, 66f }, new[] { 10f, 36f, 66f, 76f }, new[] { 36f, 50f, 52f, 62f }, new[] { 36f, 50f, 62f, 76f } };

        public bool Connected => link != null && link.Connected;

        // ------------------------------------------------------------------ setup
        void Awake()
        {
            ai = GetComponent<EnemyAI>(); agent = GetComponent<NavMeshAgent>(); health = GetComponent<Health>(); anim = GetComponent<CreatureAnimator>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Muzzle") { muzzle = t; break; }
        }

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            health.max = hp; health.current = hp;
            ammo = gunMag;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
            link = new GuardLink(host, port) { OnMessage = OnMessage };
            link.Start();
            nextTick = Time.time + 0.5f;
        }

        void OnDestroy() { link?.Stop(); if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; } }

        void OnDamaged(float amount, Vector3 from) { damageTakenSinceTick += amount; lastDamageTime = Time.time; }

        void OnDied()
        {
            if (Connected && !ended) { ended = true; link.Send("{\"type\":\"episode_end\",\"outcome\":\"guard_down\"}"); }
            agent.enabled = false;
        }

        // ------------------------------------------------------------------ main loop
        void Update()
        {
            if (health.Dead || !agent.isOnNavMesh) return;
            var gm = GameManager.Instance;
            bool playing = gm == null || gm.IsPlaying;
            agent.isStopped = !playing;

            // hand control to the brain while it is connected, back to the stock AI when it is not
            if (Connected != hadConnection)
            {
                hadConnection = Connected;
                ai.enabled = !Connected;
                if (Connected) { ApplyStats(); link.Send("{\"type\":\"episode_start\"}"); ended = false; }
                else agent.speed = ai.enabled ? agent.speed : walkSpeed;
            }
            // episode bookkeeping
            if (gm != null)
            {
                if (playing && !wasPlaying && Connected) { link.Send("{\"type\":\"episode_start\"}"); ended = false; alarmSent = false; damageDealt = 0f; score = 0f; ammo = gunMag; }
                if (!playing && wasPlaying && Connected && !ended)
                {
                    ended = true;
                    string outcome = gm.phase == GameManager.Phase.Won ? "escape" : (player != null && player.Health.Dead ? "kill" : "timeout");
                    link.Send("{\"type\":\"episode_end\",\"outcome\":\"" + outcome + "\"}");
                }
                wasPlaying = playing;
            }
            if (!Connected || !playing || player == null) return;

            // pick up the newest answer
            lock (actionLock) { if (pending != null) { action = pending; pending = null; } }
            if (Time.time >= nextDoorCheck) { nextDoorCheck = Time.time + 0.25f; OpenDoors(); }
            if (reloadUntil > 0f && Time.time >= reloadUntil) { reloadUntil = -1f; ammo = gunMag; }
            if (ammo <= 0 && reloadUntil < 0f) { reloadUntil = Time.time + gunReload; GunFX.Play(GunSound.Reload, transform.position, 0.6f); }

            Execute();

            if (Time.time >= nextTick) { nextTick = Time.time + decisionInterval; SendTick(); }
        }

        void ApplyStats()
        {
            health.max = hp; if (health.current > hp || health.current <= 0f) health.current = hp;
            agent.speed = walkSpeed; agent.angularSpeed = 540f; agent.acceleration = 14f; agent.stoppingDistance = 0.3f;
        }

        // ------------------------------------------------------------------ perception -> JSON
        bool CanSee(float dist)
        {
            if (player.Hidden && dist > 1.3f) return false;
            float range = sight * (player.Crouched ? 0.6f : 1f) * (player.TorchOn ? 1.4f : 1f) * (alarmSent ? 1.4f : 1f);
            if (dist > range) return false;
            Vector3 eye = transform.position + Vector3.up * eyeHeight;
            Vector3 to = player.Chest - eye;
            if (dist > 2f && Vector3.Angle(transform.forward, to) > fov * 0.5f) return false;
            if (Physics.Raycast(eye, to.normalized, out var hit, to.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(transform)) return true;
                return hit.collider.GetComponentInParent<PlayerController>() != null;
            }
            return true;
        }

        Vector3 MuzzlePos => muzzle != null ? muzzle.position : transform.position + Vector3.up * 1.4f + transform.forward * 0.5f;

        bool ClearShot()
        {
            Vector3 from = MuzzlePos, to = player.Chest - from;
            foreach (var h in Physics.RaycastAll(from, to.normalized, to.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && h.collider.GetComponentInParent<PlayerController>() == null) return false;
            return true;
        }

        static bool InFactory(Vector3 p)
        {
            foreach (var r in Factory) if (p.x >= r[0] && p.x <= r[1] && p.z >= r[2] && p.z <= r[3]) return true;
            return false;
        }

        void SendTick()
        {
            float dist = Vector3.Distance(transform.position, player.transform.position);
            bool sees = !player.Health.Dead && CanSee(dist);
            var gm = GameManager.Instance;
            var t = new Tick
            {
                type = "tick",
                dt = decisionInterval,
                guard = new GuardInfo { pos = new[] { transform.position.x, transform.position.z }, hp = health.current, ammo = ammo, reloading = reloadUntil > 0f },
                player_visible = sees,
                player_pos = new[] { player.transform.position.x, player.transform.position.z },
                player_hp_frac = player.Health.Fraction,
                clear_shot = sees && dist <= gunRange && ClearShot(),
                alarm = gm != null && gm.AlarmRaised,
                damage_taken = damageTakenSinceTick,
                score_info = new ScoreInfo { damage_dealt_now = damageSinceTick, intruder_in_factory = !player.Health.Dead && InFactory(player.transform.position) },
            };
            if (sees) lastKnown = player.transform.position;
            if (t.alarm && !alarmSent) { alarmSent = true; t.alarm_event_pos = new[] { player.transform.position.x, player.transform.position.z }; }
            else t.alarm_event_pos = new float[0];
            // noises: sprinting or rummaging within earshot, and gunfire
            noises.Clear();
            if (!player.Health.Dead && !player.Hidden)
            {
                if (player.Sprinting && dist < hearing) noises.Add(new NoiseEvent { pos = new[] { player.transform.position.x, player.transform.position.z }, strength = 0.7f });
                else if (Time.time - player.LastNoiseTime < 0.6f && dist < hearing * 0.8f) noises.Add(new NoiseEvent { pos = new[] { player.transform.position.x, player.transform.position.z }, strength = 0.5f });
            }
            t.noises = noises.ToArray();
            damageSinceTick = 0f; damageTakenSinceTick = 0f;
            link.Send(JsonUtility.ToJson(t));
        }

        // ------------------------------------------------------------------ execute the brain's decision
        void Execute()
        {
            if (action == null) return;
            if (anim != null) anim.aiming = action.mode == "engage" || action.mode == "cover";
            switch (action.mode)
            {
                case "goto":
                    aimUntil = -1f;
                    if (action.target != null && action.target.Length == 2) Go(new Vector3(action.target[0], transform.position.y, action.target[1]), action.run ? runSpeed : walkSpeed * 1.4f);
                    break;
                case "engage":
                    Hold();
                    FaceAndShoot(1f);
                    break;
                case "cover":
                    if (!coverValid || Time.time > coverUntil) FindCover();
                    if (coverValid && (transform.position - coverSpot).sqrMagnitude > 0.6f) Go(coverSpot, runSpeed);
                    else Hold();
                    FaceAndShoot(0.5f);
                    break;
                case "reload":
                    Hold();
                    if (reloadUntil < 0f && ammo < gunMag) { reloadUntil = Time.time + gunReload; GunFX.Play(GunSound.Reload, transform.position, 0.6f); }
                    break;
            }
        }

        void Go(Vector3 target, float speed)
        {
            agent.speed = speed;
            if (NavMesh.SamplePosition(target, out var hit, 4f, NavMesh.AllAreas)) target = hit.position;
            if ((agent.destination - target).sqrMagnitude > 1f || !agent.hasPath) agent.SetDestination(target);
        }

        void Hold() { if (agent.hasPath) agent.ResetPath(); }

        void Face(Vector3 target, float speed)
        {
            Vector3 to = target - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), speed * Time.deltaTime);
        }

        void FaceAndShoot(float rateScale)
        {
            float dist = Vector3.Distance(transform.position, player.transform.position);
            bool sees = !player.Health.Dead && CanSee(dist);
            if (!sees) { aimUntil = -1f; if (lastKnown != Vector3.zero) Face(lastKnown, 5f); return; }
            lastKnown = player.transform.position;
            Face(player.transform.position, 8f);
            if (dist > gunRange || player.Hidden || reloadUntil > 0f || ammo <= 0 || Time.time < nextShot) return;
            if (aimUntil < 0f) { aimUntil = Time.time + gunAim + (Time.time - nextShot > 3f ? 0.5f : 0f); return; }
            if (Time.time < aimUntil) return;
            aimUntil = -1f;
            if (!ClearShot()) { nextShot = Time.time + 0.3f; return; }
            Fire(dist);
            nextShot = Time.time + gunInterval / Mathf.Max(0.1f, rateScale);
        }

        void Fire(float dist)
        {
            Vector3 from = MuzzlePos;
            float chance = gunAccuracy * (1f - 0.5f * dist / gunRange) * (player.Sprinting ? 0.6f : 1f) * (player.Crouched ? 0.75f : 1f) * (alarmSent ? 1.15f : 1f);
            chance = Mathf.Clamp(chance, 0.05f, 0.95f);
            ammo--;
            bool hit = UnityEngine.Random.value < chance;
            if (hit) GunFX.Tracer(from, player.Chest + UnityEngine.Random.insideUnitSphere * 0.25f, 0.02f, 0.05f);
            else
            {
                Vector3 aim = player.Chest + UnityEngine.Random.insideUnitSphere * 1.3f - from;
                if (Physics.Raycast(from, aim.normalized, out var h, gunRange * 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore) && !h.collider.transform.IsChildOf(transform))
                { GunFX.Tracer(from, h.point, 0.02f, 0.05f); GunFX.Impact(h.point, h.normal, false); }
                else GunFX.Tracer(from, from + aim.normalized * gunRange, 0.02f, 0.05f);
            }
            GunFX.Flash(from, 5f);
            GunFX.Play(GunSound.Pistol, from, 1f);
            anim?.Shoot();
            if (hit)
            {
                float before = player.Health.current;
                player.Health.Damage(gunDamage, transform.position);
                float dealt = before - player.Health.current;
                damageDealt += dealt; damageSinceTick += dealt;
                GameManager.Instance?.OnPlayerHit(ai);
            }
            EnemyAI.MakeNoise(transform.position, 20f);
        }

        /// <summary>A NavMesh spot within a few metres that the player cannot see (a ray from his chest is blocked) and that is not closer to him than we are.</summary>
        void FindCover()
        {
            coverValid = false; coverUntil = Time.time + 4f;
            Vector3 best = Vector3.zero; float bestScore = float.MaxValue;
            for (int i = 0; i < 16; i++)
            {
                Vector3 p = transform.position + UnityEngine.Random.insideUnitSphere * 7f; p.y = transform.position.y;
                if (!NavMesh.SamplePosition(p, out var hit, 2f, NavMesh.AllAreas)) continue;
                Vector3 chest = hit.position + Vector3.up * 1.3f;
                if (!Physics.Linecast(player.Chest, chest, ~(1 << 2), QueryTriggerInteraction.Ignore)) continue;      // still visible
                float cost = Vector3.Distance(transform.position, hit.position) + 0.3f * Vector3.Distance(hit.position, player.transform.position) * -1f;
                if (cost < bestScore) { bestScore = cost; best = hit.position; coverValid = true; }
            }
            coverSpot = best;
        }

        // Closed doors are not on the NavMesh's mind: open them on the way (guards have keys)
        void OpenDoors()
        {
            if (!agent.hasPath || agent.velocity.sqrMagnitude < 0.05f) return;
            foreach (var c in Physics.OverlapSphere(transform.position + transform.forward * 0.8f + Vector3.up, 1.2f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                var gi = c.GetComponentInParent<GreyboxInteractable>();
                if (gi == null || gi.IsOpen) continue;
                if (gi.kind == GreyboxKind.SwingDoor || gi.kind == GreyboxKind.SlideDoor) gi.ForceOpen();
                else if (gi.kind == GreyboxKind.Barricade) gi.Interact();
            }
        }

        // ------------------------------------------------------------------ messages from the brain (called on the network thread)
        void OnMessage(string line)
        {
            if (line.IndexOf("\"type\": \"action\"", StringComparison.Ordinal) < 0 && line.IndexOf("\"type\":\"action\"", StringComparison.Ordinal) < 0) return;
            var a = JsonUtility.FromJson<AgentAction>(line);
            lock (actionLock) pending = a;
            lastWhy = a.why; lastMode = a.mode; lastZone = a.zone; belief = a.belief_top ?? belief; score = a.score;
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || !Connected) return;
            var gm = GameManager.Instance;
            if (gm != null && !gm.IsPlaying) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = true, alignment = TextAnchor.UpperRight };
            float w = 380f;
            var r = new Rect(Screen.width - w - 12f, 10f, w, 120f);
            string b = belief != null && belief.Length > 0 ? string.Join("  ", belief) : "";
            GUI.Label(r, $"<b>SENTINEL</b>  guard agent\nscore <b>{score:F0}</b>   damage dealt <b>{damageDealt:F0}</b>\n{lastMode} → {lastZone}\n<i>{lastWhy}</i>\nbelief: {b}", style);
        }

        // ------------------------------------------------------------------ wire format (JsonUtility)
        [Serializable] class GuardInfo { public float[] pos; public float hp; public int ammo; public bool reloading; }
        [Serializable] class ScoreInfo { public float damage_dealt_now; public bool intruder_in_factory; }
        [Serializable] class NoiseEvent { public float[] pos; public float strength; }
        [Serializable] class Tick
        {
            public string type; public float dt; public GuardInfo guard; public bool player_visible; public float[] player_pos; public float player_hp_frac;
            public bool clear_shot; public bool alarm; public float[] alarm_event_pos; public NoiseEvent[] noises; public float damage_taken; public ScoreInfo score_info;
        }
        [Serializable] class AgentAction { public string type, mode, zone, why; public float[] target; public bool run; public float score, eta; public string[] belief_top, route; }

        /// <summary>Line-based JSON over TCP on background threads; reconnects every 3 s.</summary>
        class GuardLink
        {
            readonly string host; readonly int port;
            Thread thread; volatile bool stop; volatile bool connected;
            readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
            public Action<string> OnMessage;
            public bool Connected => connected;
            public GuardLink(string h, int p) { host = h; port = p; }
            public void Send(string json) { if (connected) outbox.Enqueue(json); }
            public void Start() { thread = new Thread(Run) { IsBackground = true, Name = "GuardAgentLink" }; thread.Start(); }
            public void Stop() { stop = true; }

            void Run()
            {
                while (!stop)
                {
                    try
                    {
                        using (var c = new TcpClient { NoDelay = true })
                        {
                            c.Connect(host, port);
                            using (var s = c.GetStream())
                            {
                                s.ReadTimeout = 20; s.WriteTimeout = 1000;
                                var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                                var rd = new StreamReader(s, Encoding.UTF8);
                                w.WriteLine("{\"type\":\"hello\",\"version\":1}");
                                var sb = new StringBuilder();
                                bool gotReady = false;
                                var buf = new char[4096];
                                while (!stop && c.Connected)
                                {
                                    while (outbox.TryDequeue(out var msg)) w.WriteLine(msg);
                                    int n = 0;
                                    try { n = rd.Read(buf, 0, buf.Length); }
                                    catch (IOException) { continue; }          // read timeout: nothing to read yet
                                    if (n == 0) break;
                                    sb.Append(buf, 0, n);
                                    string all = sb.ToString(); int nl;
                                    while ((nl = all.IndexOf('\n')) >= 0)
                                    {
                                        string line = all.Substring(0, nl); all = all.Substring(nl + 1);
                                        if (!gotReady && line.Contains("\"ready\"")) { gotReady = true; connected = true; continue; }
                                        OnMessage?.Invoke(line);
                                    }
                                    sb.Clear(); sb.Append(all);
                                }
                            }
                        }
                    }
                    catch (Exception) { }
                    connected = false;
                    while (outbox.TryDequeue(out _)) { }
                    for (int i = 0; i < 30 && !stop; i++) Thread.Sleep(100);
                }
            }
        }
    }
}
