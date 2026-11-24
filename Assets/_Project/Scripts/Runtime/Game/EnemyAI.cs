using UnityEngine;
using UnityEngine.AI;
using WorldBuilder.Testing;

namespace LastShift
{
    public enum EnemyKind { Tiger, Deer, Elephant, Guard, Worker, Hunter }

    /// <summary>
    /// One brain for every character on the NavMesh, tuned by kind:
    ///   Tiger    - prowls the jungle, hunts the player on sight or sound, gives up far from its territory
    ///   Deer     - harmless, grazes and bolts away from the player
    ///   Elephant - wanders by the stream; charges anyone who comes too close, then walks off
    ///   Guard    - patrols the premises with a pistol; the alarm sends every guard after the player; opens locked doors
    ///   Worker   - night-shift worker inside the factory with a double-barrel shotgun; chases intruders he sees
    ///   Hunter   - a poacher in the jungle with a scoped rifle and a headlamp; shoots from long range,
    ///              never follows the player out of the jungle
    /// People with guns stop to aim (a short, visible wind-up), fire, and reload after a few shots.
    /// Whether a shot hits depends on range and on what the player is doing (sprinting and
    /// crouching make him harder to hit); every shot needs a clear line from the muzzle.
    /// Sight is a cone + line of sight (crouching, darkness and hiding make the player harder to see;
    /// the torch makes him easier to see). Sprinting, fighting and rummaging make noise.
    /// </summary>
    [RequireComponent(typeof(NavMeshAgent), typeof(Health))]
    public class EnemyAI : MonoBehaviour
    {
        public EnemyKind kind;
        [Tooltip("Optional: a parent whose children are patrol waypoints, in order")]
        public Transform patrolRoute;
        public int startWaypoint;
        public float wanderRadius = 10f;
        [Tooltip("Animals give up a chase this far from home (0 = never)")]
        public float leash;

        enum State { Wander, Patrol, Chase, Search, Flee, Charge, Recover, Stunned, Dead }

        // Tuning (set from kind in Configure)
        float sight, fov, hearing, walkSpeed, runSpeed, attackRange, attackDamage, attackCooldown, hitKnock, eyeHeight;
        bool harmless;

        // Firearm (guards, workers, the hunter)
        bool hasGun;
        float gunRange, gunDamage, gunInterval, gunAim, gunAccuracy, gunReload;
        int gunPellets, gunMag, shotsLeft;
        GunSound gunSound;
        Transform muzzle;
        float aimUntil = -1f, nextShot, reloadUntil;
        bool spotted;

        State state, resumeState;
        NavMeshAgent agent;
        Health health;
        CreatureAnimator anim;
        PlayerController player;
        Vector3 home, lastKnown, knock;
        float stateTimer, lostTimer, nextAttack, nextDoorCheck, idleUntil, attackAt = -1f;
        int waypoint;
        bool alerted;

        public bool Chasing => state == State.Chase || state == State.Charge;

        static readonly System.Collections.Generic.List<EnemyAI> all = new System.Collections.Generic.List<EnemyAI>();
        void OnEnable() => all.Add(this);
        void OnDisable() => all.Remove(this);

        /// <summary>A sound at a point (a pebble landing): enemies in range go to look; deer bolt.</summary>
        public static void MakeNoise(Vector3 pos, float radius)
        {
            foreach (var e in all.ToArray())
                if (e != null && Vector3.Distance(e.transform.position, pos) < radius) e.HearNoise(pos);
        }

        void HearNoise(Vector3 pos)
        {
            if (state == State.Dead || player == null) return;
            switch (kind)
            {
                case EnemyKind.Deer: StartFlee(); break;
                case EnemyKind.Elephant: break;   // unbothered
                default:
                    if (state == State.Chase || state == State.Stunned) return;
                    lastKnown = pos; state = State.Search; stateTimer = 5f;
                    break;
            }
        }
        public string DisplayName => kind.ToString();
        public bool IsHuman => kind == EnemyKind.Guard || kind == EnemyKind.Worker || kind == EnemyKind.Hunter;
        public bool Aiming => aimUntil > 0f;
        public string StateName => state.ToString();

        void Awake()
        {
            agent = GetComponent<NavMeshAgent>();
            health = GetComponent<Health>();
            anim = GetComponent<CreatureAnimator>();
            foreach (var t in GetComponentsInChildren<Transform>(true)) if (t.name == "Muzzle") { muzzle = t; break; }
            Configure();
            health.Damaged += OnDamaged;
            health.Died += OnDied;
        }

        void Configure()
        {
            switch (kind)
            {
                case EnemyKind.Tiger:
                    Set(18f, 150f, 14f, 2.2f, 6.6f, 2.0f, 11f, 1.6f, 4f, 1.0f); health.max = 70f; if (leash == 0f) leash = 38f; break;
                case EnemyKind.Deer:
                    Set(16f, 300f, 16f, 1.4f, 7.5f, 0f, 0f, 99f, 0f, 1.7f); health.max = 30f; harmless = true; break;
                case EnemyKind.Elephant:
                    Set(10f, 360f, 10f, 1.2f, 6.2f, 3.4f, 35f, 3f, 11f, 2.8f); health.max = 600f; wanderRadius = 7f; break;
                case EnemyKind.Guard:
                    Set(22f, 120f, 12f, 1.8f, 5.2f, 1.8f, 8f, 1.6f, 3f, 1.7f); health.max = 80f;
                    SetGun(GunSound.Pistol, 22f, 5f, 1, 1.3f, 0.6f, 0.4f, 8, 2.2f); break;
                case EnemyKind.Worker:
                    Set(14f, 110f, 9f, 1.4f, 4.6f, 1.7f, 6f, 1.8f, 2.5f, 1.7f); health.max = 55f;
                    SetGun(GunSound.Shotgun, 10f, 3f, 5, 1.9f, 0.7f, 0.4f, 2, 2.6f); break;
                case EnemyKind.Hunter:
                    Set(30f, 130f, 18f, 1.7f, 4.8f, 1.8f, 8f, 1.6f, 3f, 1.7f); health.max = 90f; if (leash == 0f) leash = 45f;
                    SetGun(GunSound.Rifle, 45f, 16f, 1, 2.6f, 1f, 0.55f, 5, 3f); break;
            }
            health.current = health.max;
            agent.speed = walkSpeed;
            agent.angularSpeed = 540f;
            agent.acceleration = 14f;
            agent.stoppingDistance = 0.2f;
        }

        void SetGun(GunSound sound, float range, float damage, int pellets, float interval, float aim, float accuracy, int mag, float reload)
        {
            hasGun = true; gunSound = sound; gunRange = range; gunDamage = damage; gunPellets = pellets; gunInterval = interval;
            gunAim = aim; gunAccuracy = accuracy; gunMag = shotsLeft = mag; gunReload = reload;
        }

        void Set(float s, float f, float h, float walk, float run, float range, float dmg, float cd, float kb, float eye)
        { sight = s; fov = f; hearing = h; walkSpeed = walk; runSpeed = run; attackRange = range; attackDamage = dmg; attackCooldown = cd; hitKnock = kb; eyeHeight = eye; }

        void Start()
        {
            home = transform.position;
            if (NavMesh.SamplePosition(transform.position, out var hit, 4f, NavMesh.AllAreas)) { agent.Warp(hit.position); home = hit.position; }
            player = FindAnyObjectByType<PlayerController>();
            waypoint = startWaypoint;
            state = patrolRoute != null && patrolRoute.childCount > 0 ? State.Patrol : State.Wander;
            idleUntil = Time.time + Random.Range(0f, 3f);
        }

        // ------------------------------------------------------------------ main loop

        void Update()
        {
            if (state == State.Dead || !agent.isOnNavMesh) return;
            bool playing = GameManager.Instance == null || GameManager.Instance.IsPlaying;
            agent.isStopped = !playing;
            if (!playing || player == null) return;

            if (knock.sqrMagnitude > 0.01f)
            {
                agent.Move(knock * Time.deltaTime);
                knock = Vector3.MoveTowards(knock, Vector3.zero, 14f * Time.deltaTime);
            }
            if (Time.time >= nextDoorCheck) { nextDoorCheck = Time.time + 0.25f; OpenDoors(); }
            if (attackAt > 0f && Time.time >= attackAt) { attackAt = -1f; LandAttack(); }
            if (anim != null) anim.aiming = hasGun && state == State.Chase;

            float dist = Vector3.Distance(transform.position, player.transform.position);
            bool sees = !player.Health.Dead && CanSee(dist);
            bool hears = !player.Health.Dead && Hears(dist);

            if (state == State.Stunned)
            {
                aimUntil = -1f;
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f) state = resumeState;
                return;
            }

            switch (kind)
            {
                case EnemyKind.Deer: DeerLogic(dist, sees || hears); break;
                case EnemyKind.Elephant: ElephantLogic(dist, sees); break;
                default: HunterLogic(dist, sees, hears); break;
            }
        }

        void HunterLogic(float dist, bool sees, bool hears)
        {
            // The hunter keeps to the jungle: once the player is up in the tea, he lets him go
            if (kind == EnemyKind.Hunter && player.transform.position.z > -1f) { sees = false; hears = false; if (state == State.Chase) { state = State.Search; stateTimer = 3f; } }
            if (sees)
            {
                lastKnown = player.transform.position; lostTimer = 0f;
                if (state != State.Chase) { Bark(); if (kind == EnemyKind.Hunter && !spotted) { spotted = true; GameManager.Instance?.Toast("A hunter has you in his sights! Get to cover!", 3f); } }
                state = State.Chase;
            }
            else if (hears && state != State.Chase) { lastKnown = player.transform.position; state = State.Search; stateTimer = 6f; }

            // Animals don't follow you out of their territory
            if (leash > 0f && Vector3.Distance(transform.position, home) > leash && (state == State.Chase || state == State.Search))
            { state = State.Wander; Go(home, walkSpeed); return; }

            switch (state)
            {
                case State.Chase:
                    if (!sees) { lostTimer += Time.deltaTime; if (lostTimer > 2.5f) { state = State.Search; stateTimer = 7f; } }
                    if (player.Hidden && dist > 1.3f) { state = State.Search; stateTimer = 5f; aimUntil = -1f; }
                    if (hasGun && sees && dist <= gunRange && !player.Hidden && dist > attackRange) Shooting(dist);
                    else { aimUntil = -1f; Go(sees ? player.transform.position : lastKnown, runSpeed); }
                    if (dist <= attackRange && !player.Hidden) TryAttack();
                    break;
                case State.Search:
                    Go(lastKnown, alerted ? runSpeed : walkSpeed * 1.4f);
                    if (Arrived(1.5f)) { stateTimer -= Time.deltaTime; LookAround(); }
                    if (stateTimer <= 0f) { state = patrolRoute != null ? State.Patrol : State.Wander; }
                    break;
                case State.Patrol: Patrol(); break;
                default: Wander(); break;
            }
        }

        void DeerLogic(float dist, bool noticed)
        {
            if (state == State.Flee)
            {
                stateTimer -= Time.deltaTime;
                if (stateTimer > 0f) return;
                state = State.Wander;
            }
            if ((noticed && dist < 14f) || dist < 7f) StartFlee();
            else Wander();
        }

        void StartFlee()
        {
            state = State.Flee; stateTimer = 4f;
            Vector3 away = transform.position - player.transform.position; away.y = 0f;
            var target = transform.position + away.normalized * 16f + Random.insideUnitSphere * 3f;
            if (NavMesh.SamplePosition(target, out var hit, 6f, NavMesh.AllAreas)) Go(hit.position, runSpeed);
            else Go(home, runSpeed);
        }

        void ElephantLogic(float dist, bool sees)
        {
            switch (state)
            {
                case State.Charge:
                    Go(player.transform.position, runSpeed);
                    stateTimer -= Time.deltaTime;
                    if (dist <= attackRange) { TryAttack(); state = State.Recover; stateTimer = 7f; }
                    else if (stateTimer <= 0f || dist > 22f || player.Hidden) { state = State.Recover; stateTimer = 5f; }
                    break;
                case State.Recover:
                    Go(home, walkSpeed);
                    stateTimer -= Time.deltaTime;
                    if (stateTimer <= 0f) state = State.Wander;
                    break;
                default:
                    if (sees && dist < sight && !player.Hidden) { state = State.Charge; stateTimer = 6f; Bark(); GameManager.Instance?.Toast("The elephant is charging!"); }
                    else Wander();
                    break;
            }
        }

        // ------------------------------------------------------------------ movement helpers

        void Go(Vector3 target, float speed)
        {
            agent.speed = speed;
            if ((agent.destination - target).sqrMagnitude > 0.5f || !agent.hasPath) agent.SetDestination(target);
        }

        bool Arrived(float tolerance) => !agent.pathPending && agent.remainingDistance <= tolerance;

        void Wander()
        {
            state = State.Wander;
            agent.speed = walkSpeed;
            if (Time.time < idleUntil) return;
            if (!agent.hasPath || Arrived(0.6f))
            {
                var p = home + Random.insideUnitSphere * wanderRadius;
                if (NavMesh.SamplePosition(p, out var hit, 4f, NavMesh.AllAreas)) agent.SetDestination(hit.position);
                idleUntil = Time.time + Random.Range(2f, 6f);
            }
        }

        void Patrol()
        {
            agent.speed = walkSpeed;
            var wp = patrolRoute.GetChild(waypoint % patrolRoute.childCount).position;
            Go(wp, walkSpeed);
            if (Arrived(0.8f))
            {
                if (Time.time < idleUntil) return;
                waypoint = (waypoint + 1) % patrolRoute.childCount;
                idleUntil = Time.time + Random.Range(1f, 3f);
            }
        }

        void LookAround() => transform.Rotate(0f, 90f * Time.deltaTime, 0f);

        // ------------------------------------------------------------------ senses

        bool CanSee(float dist)
        {
            if (player.Hidden && dist > 1.3f) return false;
            float range = sight * (player.Crouched ? 0.6f : 1f) * (player.TorchOn ? 1.4f : 1f) * (alerted ? 1.4f : 1f);
            if (dist > range) return false;
            Vector3 eye = transform.position + Vector3.up * eyeHeight;
            Vector3 to = player.Chest - eye;
            if (dist > 2f && Vector3.Angle(transform.forward, to) > fov * 0.5f) return false;
            if (Physics.Raycast(eye, to.normalized, out var hit, to.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                if (hit.collider.transform.IsChildOf(transform)) return true;   // own body (rare)
                return hit.collider.GetComponentInParent<PlayerController>() != null;
            }
            return true;
        }

        bool Hears(float dist)
        {
            if (player.Hidden) return false;
            if (player.Sprinting && dist < hearing) return true;
            return Time.time - player.LastNoiseTime < 0.6f && dist < hearing * 0.8f;
        }

        // ------------------------------------------------------------------ combat

        bool ClearLine()
        {
            Vector3 from = transform.position + Vector3.up * Mathf.Min(eyeHeight, 1.2f);
            Vector3 to = player.Chest - from;
            if (!Physics.Raycast(from, to.normalized, out var hit, to.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore)) return true;
            return hit.collider.transform.IsChildOf(transform) || hit.collider.GetComponentInParent<PlayerController>() != null;
        }

        void TryAttack()
        {
            if (harmless || Time.time < nextAttack || !ClearLine()) return;
            nextAttack = Time.time + attackCooldown;
            Vector3 to = player.transform.position - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) Face(player.Chest, 10f);
            anim?.PlayAttack();
            attackAt = Time.time + 0.3f;   // the blow lands after a short wind-up: you can dodge
        }

        void LandAttack()
        {
            if (state == State.Dead || player == null || player.Hidden) return;
            float d = Vector3.Distance(transform.position, player.transform.position);
            if (d > attackRange + 0.7f || !ClearLine()) return;
            player.Health.Damage(attackDamage, transform.position);
            Vector3 dir = player.transform.position - transform.position; dir.y = 0f;
            player.AddKnockback(dir.normalized * hitKnock + Vector3.up * (kind == EnemyKind.Elephant ? 5f : 1f));
            GameManager.Instance?.OnPlayerHit(this);
        }

        // ------------------------------------------------------------------ firearms

        /// <summary>In range with a clear view: keep a comfortable distance, stop, aim, fire.</summary>
        void Shooting(float dist)
        {
            bool holding = Aiming || dist < gunRange * 0.55f || Time.time < reloadUntil;
            if (holding) { if (agent.hasPath) agent.ResetPath(); }
            else Go(player.transform.position, walkSpeed);   // close in while the gun is ready
            Face(player.Chest, 8f);

            if (Time.time < reloadUntil || Time.time < nextShot) return;
            // The first shot after spotting you takes longer (reaction time): a chance to get to cover
            if (!Aiming) { aimUntil = Time.time + gunAim + (Time.time - nextShot > 3f ? 0.8f : 0f); return; }
            if (Time.time < aimUntil || (anim != null && !anim.CanAimAt(player.Chest))) return;
            aimUntil = -1f;
            if (!ClearShot()) { nextShot = Time.time + 0.4f; return; }
            Fire(dist);
            nextShot = Time.time + gunInterval;
            if (--shotsLeft <= 0)
            {
                shotsLeft = gunMag;
                reloadUntil = Time.time + gunReload;
                GunFX.Play(GunSound.Reload, transform.position, 0.6f);
            }
        }

        void Face(Vector3 target, float speed)
        {
            if (anim != null && anim.FaceTarget(target, speed)) return;
            Vector3 to = target - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), speed * Time.deltaTime);
        }

        Vector3 MuzzlePos => muzzle != null ? muzzle.position : transform.position + Vector3.up * 1.4f + transform.forward * 0.5f;

        bool ClearShot()
        {
            Vector3 from = MuzzlePos, to = player.Chest - from;
            foreach (var h in Physics.RaycastAll(from, to.normalized, to.magnitude, ~(1 << 2), QueryTriggerInteraction.Ignore))
                if (!h.collider.transform.IsChildOf(transform) && h.collider.GetComponentInParent<PlayerController>() == null) return false;
            return true;
        }

        void Fire(float dist)
        {
            Vector3 from = MuzzlePos;
            float chance = gunAccuracy * (1f - 0.5f * dist / gunRange) * (player.Sprinting ? 0.6f : 1f) * (player.Crouched ? 0.75f : 1f) * (alerted ? 1.15f : 1f);
            chance = Mathf.Clamp(chance, 0.05f, 0.95f);
            float damage = 0f;
            for (int i = 0; i < gunPellets; i++)
            {
                if (Random.value < chance)
                {
                    damage += gunDamage;
                    if (i < 3) GunFX.Tracer(from, player.Chest + Random.insideUnitSphere * 0.25f + Vector3.up * 0.2f, 0.02f, 0.05f);
                }
                else if (i < 3)
                {
                    // A miss: the round cracks past and hits whatever is behind
                    Vector3 aim = player.Chest + Random.insideUnitSphere * 1.3f - from;
                    if (Physics.Raycast(from, aim.normalized, out var hit, gunRange * 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                        && !hit.collider.transform.IsChildOf(transform))
                    { GunFX.Tracer(from, hit.point, 0.02f, 0.05f); GunFX.Impact(hit.point, hit.normal, false); }
                    else GunFX.Tracer(from, from + aim.normalized * gunRange, 0.02f, 0.05f);
                }
            }
            GunFX.Flash(from, 5f);
            GunFX.Play(gunSound, from, 1f);
            anim?.Shoot();
            if (damage > 0f)
            {
                player.Health.Damage(damage, transform.position);
                GameManager.Instance?.OnPlayerHit(this);
            }
            MakeNoise(transform.position, 20f);   // gunfire draws the others
        }

        public void Knockback(Vector3 v) { v.y = 0f; knock += v * (kind == EnemyKind.Elephant ? 0.1f : 1f); }

        void OnDamaged(float amount, Vector3 from)
        {
            if (state == State.Dead) return;
            if (player != null) lastKnown = player.transform.position;
            anim?.Flinch();
            switch (kind)
            {
                case EnemyKind.Deer: StartFlee(); break;
                case EnemyKind.Elephant: state = State.Charge; stateTimer = 5f; break;
                default:
                    // A short flinch (shorter for people with guns, or a pistol could pin them down), then they know where you are
                    alerted = true;
                    resumeState = State.Chase; state = State.Stunned; stateTimer = hasGun ? 0.35f : 0.8f;
                    aimUntil = -1f;
                    agent.ResetPath();
                    transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(from - transform.position, Vector3.up).normalized + transform.forward * 0.001f);
                    break;
            }
        }

        void OnDied()
        {
            state = State.Dead;
            agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            anim?.Die();
            if (anim != null) anim.aiming = false;
            switch (kind)   // people drop what they were carrying
            {
                case EnemyKind.Guard: AmmoBox.Drop(transform.position + transform.right * 0.6f, 12, 0); break;
                case EnemyKind.Worker: AmmoBox.Drop(transform.position + transform.right * 0.6f, 0, 4); break;
                case EnemyKind.Hunter: AmmoBox.Drop(transform.position + transform.right * 0.6f, 8, 6); break;
            }
            GameManager.Instance?.OnEnemyDown(this);
        }

        /// <summary>The alarm: guards (and workers nearby) come running to where the player was.</summary>
        public void Alert(Vector3 where)
        {
            if (state == State.Dead) return;
            alerted = true;
            lastKnown = where;
            if (state != State.Chase) { state = State.Search; stateTimer = 12f; }
        }

        void Bark() => anim?.Roar();

        // Closed doors are not on the NavMesh's mind: open them on the way (guards have keys)
        void OpenDoors()
        {
            if (!agent.hasPath || agent.velocity.sqrMagnitude < 0.05f) return;
            foreach (var c in Physics.OverlapSphere(transform.position + transform.forward * 0.8f + Vector3.up, 1.2f, ~(1 << 2), QueryTriggerInteraction.Ignore))
            {
                var gi = c.GetComponentInParent<GreyboxInteractable>();
                if (gi == null || gi.IsOpen) continue;
                if (gi.kind == GreyboxKind.SwingDoor || gi.kind == GreyboxKind.SlideDoor)
                {
                    if (!gi.locked || kind == EnemyKind.Guard) gi.ForceOpen();
                }
                else if (gi.kind == GreyboxKind.Barricade && (kind == EnemyKind.Guard || kind == EnemyKind.Elephant)) gi.Interact();
            }
        }
    }
}
