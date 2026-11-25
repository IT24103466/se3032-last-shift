using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Linq;
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
    /// <summary>The job of this person. Same agent architecture for everyone; the role sets the weapon, the goal (score weights) and the zones he looks after.</summary>
    public enum AgentRole { Sentinel, Gatekeeper, TeaKeeper, Lookout }

    /// <summary>Embedded: the brain runs inside this component (isolated agent, no server, nothing to connect). Remote: the Python server (GuardAgent/) decides, over TCP.</summary>
    public enum BrainMode { Embedded, Remote }

    [RequireComponent(typeof(EnemyAI), typeof(NavMeshAgent), typeof(Health))]
    public class GuardAgentBridge : MonoBehaviour
    {
        [Header("Brain")]
        [Tooltip("Embedded = an isolated agent: the brain (belief + trained policy + reflexes) runs inside Unity, no server needed. Remote = ask the Python server on host:port.")]
        public BrainMode brainMode = BrainMode.Embedded;
        public string host = "127.0.0.1";
        public int port = 5555;
        public float decisionInterval = 0.5f;

        [Header("Role")]
        [Tooltip("Sentinel: keep him out of the factory. Gatekeeper: stop the escape at the gate. TeaKeeper: protect the tea bags (shotgun ambush). Lookout: spot, report, survive.")]
        public AgentRole role = AgentRole.Sentinel;
        [Tooltip("Fill the numbers below from the role (guards: pistol, workers: double-barrel). Untick to tune them by hand.")]
        public bool useRolePreset = true;
        [Tooltip("Optional: zone names this person looks after instead of the role default (e.g. north_lane, loft)")]
        public string[] focusZones;
        [Tooltip("Interior garrison: he never leaves the factory building by his own choice. He waits at his post, takes the ambush spot behind the door the intruder is likely to use, and shoots from inside.")]
        public bool stayInside;
        [Tooltip("Zone he waits in (zone-graph name: rolling, loft, storeroom, boiler ...)")]
        public string postZone = "";

        [Header("Toughness")]
        [Tooltip("Multiplier on hit points. The brain still sees his health as a fraction of the normal body, so the policy behaves as before, but it takes this many times more damage to put him down.")]
        public float toughness = 1f;

        [Header("Weapon and body (the presets match GuardAgent/guard_agent/config.py)")]
        public float hp = 100f;
        public float walkSpeed = 1.8f, runSpeed = 5.2f, sight = 22f, fov = 120f, hearing = 12f;
        public float gunRange = 22f, gunDamage = 8f, gunInterval = 1.0f, gunAim = 0.5f, gunAccuracy = 0.65f, gunReload = 1.8f;
        public int gunMag = 12;
        [Tooltip("Pellets per shot: 1 for the pistol, 5 for the worker shotgun")] public int pellets = 1;
        public float eyeHeight = 1.7f;
        [Tooltip("Inside this distance (metres) he notices the player whichever way he is facing, if nothing solid is in between")] public float closeAwareness = 4.5f;

        [Header("Gunshot hearing")]
        [Tooltip("The guard hears the player's gunshots within this many metres, through walls (the game's own noise radius for a shot is 40)")]
        public float gunshotRadius = 40f;

        [Header("Score (read-only: the brain keeps the score and sends it back)")]
        public float damageDealt;
        public float score;
        public bool showHud = true;

        // ------------------------------------------------------------------ state
        PlayerGun playerGun; int lastMag = -1, lastSlot = -1; float lastShotHeard = -99f;
        readonly List<NoiseEvent> shotEvents = new List<NoiseEvent>();
        EnemyAI ai; NavMeshAgent agent; Health health; CreatureAnimator anim; PlayerController player; Transform muzzle;
        GuardLink link;
        EmbeddedBrain brain;
        AgentAction action;
        readonly object actionLock = new object();
        string pendingLine;
        float nextTick, aimUntil = -1f, nextShot, reloadUntil = -1f, nextDoorCheck, coverUntil, lastDamageTime = -99f;
        int ammo;
        float damageSinceTick, damageTakenSinceTick, lastHitOnPlayer = -99f;
        bool alarmSent, wasPlaying, ended, hadConnection;
        Vector3 lastKnown, coverSpot;
        bool coverValid;
        readonly List<NoiseEvent> noises = new List<NoiseEvent>();
        string lastWhy = "", lastMode = "", lastZone = ""; string[] belief = new string[0];
        static int hudCount; int hudIndex = -1; float lastSeenTime = -99f;
        float lastLatencyMs, bagsCountedAt = -99f; int bagsSearched;

        // Factory building = protected zones of the zone graph (x0, x1, z0, z1)
        static readonly float[][] Factory = { new[] { 10f, 36f, 52f, 66f }, new[] { 10f, 36f, 66f, 76f }, new[] { 36f, 50f, 52f, 62f }, new[] { 36f, 50f, 62f, 76f } };

        public bool Connected => brainMode == BrainMode.Embedded ? (brain != null && brain.Ready) : (link != null && link.Connected);
        [Tooltip("What the socket thread is doing (read-only)")] public string linkStatus = "";
        string lastLoggedStatus = "";
        [Header("Diagnostics (read-only)")] public int ticksSent, actionsReceived; public string debug = "";

        // ------------------------------------------------------------------ setup
        float MaxHp => hp * toughness;
        public static string RoleKey(AgentRole r) => r == AgentRole.Sentinel ? "sentinel" : r == AgentRole.Gatekeeper ? "gatekeeper" : r == AgentRole.TeaKeeper ? "tea_keeper" : "lookout";
        public static string RoleTitle(AgentRole r) => r == AgentRole.Sentinel ? "SENTINEL" : r == AgentRole.Gatekeeper ? "GATEKEEPER" : r == AgentRole.TeaKeeper ? "TEA KEEPER" : "LOOKOUT";

        /// <summary>Guards carry the pistol, workers the double-barrel shotgun (numbers = config.py).</summary>
        void ApplyPreset()
        {
            if (!useRolePreset) return;
            bool worker = role == AgentRole.TeaKeeper || role == AgentRole.Lookout;
            if (worker) { hp = 80f; walkSpeed = 1.4f; runSpeed = 4.6f; sight = 14f; fov = 110f; hearing = 9f; gunRange = 10f; gunDamage = 9f; pellets = 5; gunInterval = 1.6f; gunAim = 0.6f; gunAccuracy = 0.5f; gunReload = 2.2f; gunMag = 2; }
            else { hp = 100f; walkSpeed = 1.8f; runSpeed = 5.2f; sight = 22f; fov = 120f; hearing = 12f; gunRange = 22f; gunDamage = 8f; pellets = 1; gunInterval = 1.0f; gunAim = 0.5f; gunAccuracy = 0.65f; gunReload = 1.8f; gunMag = 12; }
        }

        string HelloJson()
        {
            var sb = new StringBuilder("{\"type\":\"hello\",\"version\":2,\"role\":\"").Append(RoleKey(role)).Append("\",\"id\":\"").Append(name).Append("\"");
            if (stayInside) sb.Append(",\"confined\":true");
            if (!string.IsNullOrEmpty(postZone)) sb.Append(",\"post\":\"").Append(postZone).Append("\"");
            if (focusZones != null && focusZones.Length > 0) sb.Append(",\"focus\":[").Append(string.Join(",", focusZones.Select(z => "\"" + z + "\""))).Append("]");
            return sb.Append("}").ToString();
        }

        void Awake()
        {
            ai = GetComponent<EnemyAI>(); agent = GetComponent<NavMeshAgent>(); health = GetComponent<Health>(); anim = GetComponent<CreatureAnimator>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Muzzle") { muzzle = t; break; }
        }

        GuardCombatVoice guardVoice;

        void Start()
        {
            player = FindAnyObjectByType<PlayerController>();
            playerGun = FindAnyObjectByType<PlayerGun>();
            guardVoice = GuardCombatVoice.For(ai);
            ApplyPreset();
            health.max = MaxHp; health.current = MaxHp;
            ammo = gunMag;
            health.Damaged += OnDamaged;
            health.Died += OnDied;
            if (brainMode == BrainMode.Embedded)
            {
                brain = new EmbeddedBrain(RoleKey(role), name, stayInside, string.IsNullOrEmpty(postZone) ? null : postZone, focusZones);
                linkStatus = brain.Ready ? (brain.HasPolicy ? "embedded brain (isolated, no server)" : "embedded brain, no policy file: belief planner") : "embedded brain failed: zone graph missing";
                if (!brain.Ready) Debug.LogError("[Guard Agent] " + name + ": " + linkStatus);
            }
            else
            {
                link = new GuardLink(host, port, HelloJson()) { OnMessage = OnMessage };
                link.Start();
            }
            nextTick = Time.time + 0.5f;
        }

        void OnDestroy() { link?.Stop(); if (health != null) { health.Damaged -= OnDamaged; health.Died -= OnDied; } }

        void OnDamaged(float amount, Vector3 from) { damageTakenSinceTick += amount; lastDamageTime = Time.time; }

        void OnDied()
        {
            if (Connected && !ended) { ended = true; SendEpisodeEnd("guard_down"); }
            agent.enabled = false;
        }

        // ------------------------------------------------------------------ main loop
        void Update()
        {
            if (link != null && brainMode == BrainMode.Remote) { linkStatus = link.Status; if (linkStatus != lastLoggedStatus && (linkStatus.StartsWith("error") || linkStatus == "connected")) Debug.Log("[Guard Agent] " + linkStatus); lastLoggedStatus = linkStatus; }
            if (health.Dead || !agent.isOnNavMesh) { debug = health.Dead ? "guard dead" : "guard not on NavMesh"; return; }
            var gm = GameManager.Instance;
            bool playing = gm == null || gm.IsPlaying;
            agent.isStopped = !playing;

            // hand control to the brain while it is connected, back to the stock AI when it is not
            if (Connected != hadConnection)
            {
                hadConnection = Connected;
                ai.enabled = !Connected;
                if (Connected) { ApplyStats(); SendEpisodeStart(); ended = false; }
                else agent.speed = ai.enabled ? agent.speed : walkSpeed;
            }
            // episode bookkeeping
            if (gm != null)
            {
                if (playing && !wasPlaying && Connected) { SendEpisodeStart(); ended = false; alarmSent = false; damageDealt = 0f; score = 0f; ammo = gunMag; reloadUntil = -1f; }
                if (!playing && wasPlaying && Connected && !ended)
                {
                    ended = true;
                    bool iKilledHim = player != null && player.Health.Dead && Time.time - lastHitOnPlayer < 6f;     // only counts if the guard's own shots were the last to hurt him
                    string outcome = gm.phase == GameManager.Phase.Won ? "escape" : (iKilledHim ? "kill" : "timeout");
                    SendEpisodeEnd(outcome);
                }
                wasPlaying = playing;
            }
            if (!Connected || !playing || player == null) { debug = !Connected ? (brainMode == BrainMode.Embedded ? "embedded brain not ready" : "not connected") : !playing ? "game not in Playing phase" : "no player"; return; }
            debug = "running";

            // pick up the newest answer
            ParsePending();
            if (Time.time >= nextDoorCheck) { nextDoorCheck = Time.time + 0.25f; OpenDoors(); }
            if (reloadUntil > 0f && Time.time >= reloadUntil) { reloadUntil = -1f; ammo = gunMag; }
            if (ammo <= 0 && reloadUntil < 0f) { reloadUntil = Time.time + gunReload; GunFX.Play(GunSound.Reload, transform.position, 0.6f); }

            PollGunshots();
            Execute();
            ScanWhileWaiting();

            if (Time.time >= nextTick) { nextTick = Time.time + decisionInterval; SendTick(); }
        }

        void SendEpisodeStart() { if (brainMode == BrainMode.Embedded) brain?.NewGame(); else link?.Send("{\"type\":\"episode_start\"}"); }
        void SendEpisodeEnd(string outcome) { if (brainMode == BrainMode.Embedded) brain?.EndGame(outcome); else link?.Send("{\"type\":\"episode_end\",\"outcome\":\"" + outcome + "\"}"); }

        /// <summary>The game has no 'shot fired' event for other scripts, so watch the player's magazine: it drops by one on every shot.
        /// A shot within <see cref="gunshotRadius"/> is heard whether or not the guard can see the player; louder when closer.</summary>
        void PollGunshots()
        {
            if (playerGun == null || player == null || player.Health.Dead) return;
            var g = playerGun.Current;
            if (g == null) { lastMag = -1; lastSlot = playerGun.Slot; return; }
            if (playerGun.Slot != lastSlot || lastMag < 0) { lastSlot = playerGun.Slot; lastMag = g.mag; return; }
            if (g.mag < lastMag)
            {
                float dist = Vector3.Distance(transform.position, player.transform.position);
                if (dist <= gunshotRadius)
                {
                    shotEvents.Add(new NoiseEvent { pos = new[] { player.transform.position.x, player.transform.position.z }, strength = Mathf.Lerp(1f, 0.4f, dist / gunshotRadius), kind = "gunshot" });
                    lastShotHeard = Time.time;
                    nextTick = Time.time;                       // react now, not at the next tick
                }
            }
            lastMag = g.mag;
        }

        /// <summary>While the alarm is on and he has not seen the player for a moment, a person standing at his post turns slowly on the spot instead of staring at a wall.</summary>
        void ScanWhileWaiting()
        {
            if (!alarmSent || agent.hasPath || action == null || action.mode == "reload") return;
            if (Time.time - lastSeenTime < 1.5f) return;
            transform.Rotate(0f, 55f * Time.deltaTime, 0f);
        }

        void ApplyStats()
        {
            health.max = MaxHp; if (health.current > MaxHp || health.current <= 0f) health.current = MaxHp;
            agent.speed = walkSpeed; agent.angularSpeed = 540f; agent.acceleration = 14f; agent.stoppingDistance = 0.3f;
        }

        // ------------------------------------------------------------------ perception -> JSON
        bool CanSee(float dist)
        {
            if (player.Hidden && dist > 1.3f) return false;
            float range = sight * (player.Crouched ? 0.6f : 1f) * (player.TorchOn ? 1.4f : 1f) * (alarmSent ? 1.4f : 1f);
            if (dist > range) return false;
            Vector3 eye = anim != null ? anim.SightOrigin : transform.position + Vector3.up * eyeHeight;
            Vector3 to = player.Chest - eye;
            if (dist > closeAwareness && Vector3.Angle(transform.forward, to) > fov * 0.5f) return false;
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

        int CountSearchedBags()
        {
            if (Time.time - bagsCountedAt > 1f)
            {
                bagsCountedAt = Time.time; bagsSearched = 0;
                foreach (var b in FindObjectsByType<TeaBag>(FindObjectsInactive.Exclude)) if (b.searched) bagsSearched++;
            }
            return bagsSearched;
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
                guard = new GuardInfo { pos = new[] { transform.position.x, transform.position.z }, hp = health.current / toughness, ammo = ammo, reloading = reloadUntil > 0f },
                player_visible = sees,
                player_pos = new[] { player.transform.position.x, player.transform.position.z },
                player_hp_frac = player.Health.Fraction,
                clear_shot = sees && dist <= gunRange && ClearShot(),
                alarm = gm != null && gm.AlarmRaised,
                damage_taken = damageTakenSinceTick / toughness,
                score_info = new ScoreInfo { damage_dealt_now = damageSinceTick, intruder_in_factory = !player.Health.Dead && InFactory(player.transform.position), intruder_searching_bag = player.SearchProgress > 0.01f, bags_searched_total = CountSearchedBags() },
            };
            if (sees) { lastKnown = player.transform.position; lastSeenTime = Time.time; }
            if (t.alarm && !alarmSent) { alarmSent = true; t.alarm_event_pos = new[] { player.transform.position.x, player.transform.position.z }; }
            else t.alarm_event_pos = new float[0];
            // noises: sprinting or rummaging within earshot, and gunfire
            noises.Clear();
            if (!player.Health.Dead && !player.Hidden)
            {
                if (player.Sprinting && dist < hearing) noises.Add(new NoiseEvent { pos = new[] { player.transform.position.x, player.transform.position.z }, strength = 0.7f, kind = "footsteps" });
                else if (Time.time - player.LastNoiseTime < 0.6f && dist < hearing * 0.8f) noises.Add(new NoiseEvent { pos = new[] { player.transform.position.x, player.transform.position.z }, strength = 0.5f, kind = "rummaging" });
            }
            noises.AddRange(shotEvents); shotEvents.Clear();
            t.noises = noises.ToArray();
            damageSinceTick = 0f; damageTakenSinceTick = 0f;
            ticksSent++;
            if (brainMode == BrainMode.Embedded) { DecideEmbedded(t); return; }
            link.Send(JsonUtility.ToJson(t));
        }

        /// <summary>Isolated agent: hand the percept to the brain inside this component and use the answer at once (no network, no latency).</summary>
        void DecideEmbedded(Tick t)
        {
            var p = new AgentPercept
            {
                dt = t.dt, guardPos = new Vector2(t.guard.pos[0], t.guard.pos[1]), hp = t.guard.hp, ammo = t.guard.ammo, reloading = t.guard.reloading,
                playerVisible = t.player_visible, playerPos = new Vector2(t.player_pos[0], t.player_pos[1]), playerHpFrac = t.player_hp_frac, clearShot = t.clear_shot,
                alarm = t.alarm, hasAlarmEvent = t.alarm_event_pos != null && t.alarm_event_pos.Length == 2,
                damageTaken = t.damage_taken, damageDealtNow = t.score_info.damage_dealt_now, intruderInFactory = t.score_info.intruder_in_factory,
                intruderSearchingBag = t.score_info.intruder_searching_bag, bagsSearchedTotal = t.score_info.bags_searched_total,
            };
            if (p.hasAlarmEvent) p.alarmEventPos = new Vector2(t.alarm_event_pos[0], t.alarm_event_pos[1]);
            foreach (var n in t.noises) p.noises.Add(new AgentNoise { pos = new Vector2(n.pos[0], n.pos[1]), strength = n.strength, gunshot = n.kind == "gunshot" });
            var c = brain.Decide(p);
            var a = new AgentAction { type = "action", mode = c.mode, zone = c.zone, why = c.why, run = c.run, score = c.score, eta = c.eta, belief_top = c.beliefTop, route = c.route };
            a.target = c.hasTarget ? new[] { c.target.x, c.target.y } : null;
            action = a; actionsReceived++;
            lastWhy = a.why; lastMode = a.mode; lastZone = a.zone; belief = a.belief_top ?? belief; score = a.score;
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
            if (anim != null && anim.FaceTarget(target, speed)) return;
            Vector3 to = target - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), speed * Time.deltaTime);
        }

        void FaceAndShoot(float rateScale)
        {
            float dist = Vector3.Distance(transform.position, player.transform.position);
            bool sees = !player.Health.Dead && CanSee(dist);
            if (!sees) { aimUntil = -1f; if (lastKnown != Vector3.zero) Face(lastKnown + Vector3.up * 1.3f, 5f); return; }
            lastKnown = player.transform.position;
            Face(player.Chest, 8f);
            if (dist > gunRange || player.Hidden || reloadUntil > 0f || ammo <= 0 || Time.time < nextShot) return;
            if (aimUntil < 0f) { guardVoice?.Threaten(); aimUntil = Time.time + gunAim + (Time.time - nextShot > 3f ? 0.5f : 0f); return; }
            if (Time.time < aimUntil || (anim != null && !anim.CanAimAt(player.Chest))) return;
            aimUntil = -1f;
            if (!ClearShot()) { nextShot = Time.time + 0.3f; return; }
            Fire(dist);
            nextShot = Time.time + gunInterval / Mathf.Max(0.1f, rateScale);
        }

        void Fire(float dist)
        {
            guardVoice?.FireCallout();
            Vector3 from = MuzzlePos;
            float chance = gunAccuracy * (1f - 0.5f * dist / gunRange) * (player.Sprinting ? 0.6f : 1f) * (player.Crouched ? 0.75f : 1f) * (alarmSent ? 1.15f : 1f);
            chance = Mathf.Clamp(chance, 0.05f, 0.95f);
            ammo--;
            int hits = 0;
            for (int i = 0; i < Mathf.Max(1, pellets); i++)
            {
                bool hit = UnityEngine.Random.value < chance;
                if (hit) hits++;
                if (i >= 3) continue;                                   // only a few tracers for a shotgun blast
                if (hit) GunFX.Tracer(from, player.Chest + UnityEngine.Random.insideUnitSphere * 0.25f, 0.02f, 0.05f);
                else
                {
                    Vector3 aim = player.Chest + UnityEngine.Random.insideUnitSphere * 1.3f - from;
                    if (Physics.Raycast(from, aim.normalized, out var h, gunRange * 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore) && !h.collider.transform.IsChildOf(transform))
                    { GunFX.Tracer(from, h.point, 0.02f, 0.05f); GunFX.Impact(h.point, h.normal, false); }
                    else GunFX.Tracer(from, from + aim.normalized * gunRange, 0.02f, 0.05f);
                }
            }
            GunFX.Flash(from, 5f);
            GunFX.Play(pellets > 1 ? GunSound.Shotgun : GunSound.Pistol, from, 1f);
            anim?.Shoot();
            if (hits > 0)
            {
                float before = player.Health.current;
                player.Health.Damage(gunDamage * hits, transform.position);
                float dealt = before - player.Health.current;
                damageDealt += dealt; damageSinceTick += dealt; lastHitOnPlayer = Time.time;
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
            // network thread: JsonUtility only works on the main thread, so just keep the newest raw line; Update() parses it
            if (line.IndexOf("\"action\"", StringComparison.Ordinal) < 0) return;
            lock (actionLock) pendingLine = line;
        }

        void ParsePending()
        {
            string line;
            lock (actionLock) { line = pendingLine; pendingLine = null; }
            if (line == null) return;
            try
            {
                var a = JsonUtility.FromJson<AgentAction>(line);
                action = a; actionsReceived++;
                lastWhy = a.why; lastMode = a.mode; lastZone = a.zone; belief = a.belief_top ?? belief; score = a.score;
            }
            catch (Exception e) { Debug.LogWarning("[Guard Agent] could not read the brain's answer: " + e.Message); }
        }

        // ------------------------------------------------------------------ HUD
        void OnGUI()
        {
            if (!showHud || !Connected) return;
            var gm = GameManager.Instance;
            if (gm != null && !gm.IsPlaying) return;
            var style = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = true, alignment = TextAnchor.UpperRight };
            float w = 380f;
            if (hudIndex < 0) hudIndex = hudCount++;
            var r = new Rect(Screen.width - w - 12f, 10f + hudIndex * 40f, w, 40f);
            string b = belief != null && belief.Length > 0 ? string.Join("  ", belief) : "";
            if (Time.time - lastShotHeard < 3f) b = "<color=#ff5050>GUNSHOT HEARD</color>  " + b;
            GUI.Label(r, $"<b>{RoleTitle(role)}</b> {name}  score <b>{score:F0}</b>  dmg <b>{damageDealt:F0}</b>  {lastMode} → {lastZone}\n<i>{lastWhy}</i>  {b}", style);
        }

        // ------------------------------------------------------------------ wire format (JsonUtility)
        [Serializable] class GuardInfo { public float[] pos; public float hp; public int ammo; public bool reloading; }
        [Serializable] class ScoreInfo { public float damage_dealt_now; public bool intruder_in_factory; public bool intruder_searching_bag; public int bags_searched_total; }
        [Serializable] class NoiseEvent { public float[] pos; public float strength; public string kind; }
        [Serializable] class Tick
        {
            public string type; public float dt; public GuardInfo guard; public bool player_visible; public float[] player_pos; public float player_hp_frac;
            public bool clear_shot; public bool alarm; public float[] alarm_event_pos; public NoiseEvent[] noises; public float damage_taken; public ScoreInfo score_info;
        }
        [Serializable] class AgentAction { public string type, mode, zone, why; public float[] target; public bool run; public float score, eta; public string[] belief_top, route; }

        /// <summary>Line-based JSON over TCP on background threads; reconnects every 3 s.</summary>
        class GuardLink
        {
            readonly string host, hello; readonly int port;
            Thread thread; volatile bool stop; volatile bool connected;
            readonly ConcurrentQueue<string> outbox = new ConcurrentQueue<string>();
            public Action<string> OnMessage;
            public bool Connected => connected;
            public GuardLink(string h, int p, string helloJson) { host = h; port = p; hello = helloJson; }
            public void Send(string json) { if (connected) outbox.Enqueue(json); }
            public void Start() { thread = new Thread(Run) { IsBackground = true, Name = "GuardAgentLink" }; thread.Start(); }
            public void Stop() { stop = true; }

            public volatile string Status = "starting";

            void Run()
            {
                while (!stop)
                {
                    try
                    {
                        Status = "connecting to " + host + ":" + port;
                        using (var c = new TcpClient { NoDelay = true })
                        {
                            c.Connect(host, port);
                            using (var s = c.GetStream())
                            {
                                var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                                var rd = new StreamReader(s, Encoding.UTF8);
                                w.WriteLine(hello);
                                Status = "hello sent";
                                var sb = new StringBuilder();
                                bool gotReady = false;
                                var buf = new char[4096];
                                while (!stop)
                                {
                                    while (outbox.TryDequeue(out var msg)) w.WriteLine(msg);
                                    if (!s.DataAvailable)
                                    {
                                        // the server closed the connection: readable with nothing to read
                                        if (c.Client.Poll(0, SelectMode.SelectRead) && c.Client.Available == 0) break;
                                        Thread.Sleep(5);
                                        continue;
                                    }
                                    int n = rd.Read(buf, 0, buf.Length);
                                    if (n == 0) break;
                                    sb.Append(buf, 0, n);
                                    string all = sb.ToString(); int nl;
                                    while ((nl = all.IndexOf('\n')) >= 0)
                                    {
                                        string line = all.Substring(0, nl); all = all.Substring(nl + 1);
                                        if (!gotReady && line.Contains("\"ready\"")) { gotReady = true; connected = true; Status = "connected"; continue; }
                                        OnMessage?.Invoke(line);
                                    }
                                    sb.Clear(); sb.Append(all);
                                }
                            }
                        }
                    }
                    catch (Exception e) { Status = "error: " + e.Message; }
                    connected = false;
                    while (outbox.TryDequeue(out _)) { }
                    for (int i = 0; i < 30 && !stop; i++) Thread.Sleep(100);
                }
            }
        }
    }
}
