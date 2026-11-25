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

        enum State { Wander, Patrol, Chase, Search, Flee, Charge, Recover, Stunned, Dead, Stalk }

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
        float nextHurtVoice;
        bool spotted;

        State state, resumeState;
        NavMeshAgent agent;
        Health health;
        CreatureAnimator anim;
        HumanLocomotion humanMotion;
        TigerVoice tigerVoice;
        GuardCombatVoice guardVoice;
        TigerNavigation tigerNavigation;
        float tigerMemoryUntil, tigerSearchProbe;
        AudioSource elephantVoice; float nextElephantCry;
        float nextClueCheck, nextInvestigation;
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
        /// <summary>Every noise in the world (position, radius): lets other agents (e.g. the guard dog) listen without coupling to EnemyAI.</summary>
        public static event System.Action<Vector3, float> NoiseMade;

        public static void MakeNoise(Vector3 pos, float radius)
        {
            NoiseMade?.Invoke(pos, radius);
            foreach (var e in all.ToArray())
                if (e != null && Vector3.Distance(e.transform.position, pos) < radius) e.HearNoise(pos);
        }

        public static void NoticeProjectile(Vector3 from, Vector3 to, Transform source)
        {
            if ((to - from).sqrMagnitude < 0.001f) return;
            foreach (var e in all)
            {
                if (e == null || e.transform == source || e.health.Dead || e.Chasing || Time.time < e.nextInvestigation) continue;
                var eye = e.transform.position + Vector3.up * e.eyeHeight;
                Vector3 near = EnemyAwareness.ClosestPoint(from, to, eye);
                float distance = Vector3.Distance(near, eye);
                if (distance > 6f) continue;
                e.nextInvestigation = Time.time + 1f;
                if (Random.value > Mathf.Lerp(0.85f, 0.25f, distance / 6f) || !EnemyAwareness.Clear(eye, near, e.transform, source)) continue;
                e.HearNoise(near + Random.insideUnitSphere * 1.5f);
            }
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
                    if (IsHuman && state != State.Search && !SlotFree(pos)) return;
                    lastKnown = NavMesh.SamplePosition(pos, out var navPoint, 3f, agent.areaMask) ? navPoint.position : new Vector3(pos.x, transform.position.y, pos.z);
                    state = State.Search; stateTimer = 5f;
                    if(kind==EnemyKind.Tiger)tigerMemoryUntil=Time.time+7f;
                    break;
            }
        }
        // ---- One fight at a time (see ThreatDirector): an enemy that notices the player only
        //      attacks if it gets the turn; otherwise it keeps its distance and watches.
        // ---- Stealth: noticing takes time. Suspicion (0..1) fills while the player is in view —
        //      faster up close, when already alerted or when he isn't crouching — and drains when
        //      he's out of sight. Only at 1 does the enemy commit to a chase.
        public float Suspicion { get; private set; }
        public static System.Collections.Generic.IReadOnlyList<EnemyAI> All => all;

        /// <summary>How many guards/workers may be out hunting the player at once (the rest hold their posts).</summary>
        public const int MaxResponders = 3;

        /// <summary>Guards and workers currently chasing or searching.</summary>
        public static int Responders()
        {
            int n = 0;
            foreach (var e in all)
                if (e != null && e.IsHuman && (e.state == State.Chase || e.state == State.Search)) n++;
            return n;
        }

        /// <summary>May I leave my post to respond to something at pos? Yes if there's a free slot or it's right next to me.</summary>
        bool SlotFree(Vector3 pos) => Responders() < MaxResponders || Vector3.Distance(transform.position, pos) < 10f;

        bool MayAttackPlayer() => harmless || ThreatDirector.TryAttack(this, DisplayName, player.transform.position);

        /// <summary>Not my turn: keep my distance from the player and watch him.</summary>
        void HoldBack(float dist)
        {
            aimUntil = -1f;
            if (dist < 12f) Go(home, walkSpeed);
            else if (agent.hasPath) agent.ResetPath();
            Vector3 to = player.transform.position - transform.position; to.y = 0f;
            if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), 3f * Time.deltaTime);
        }

        /// <summary>Fills while the player is in view; true once fully noticed.</summary>
        bool Notice(bool sees, float dist)
        {
            if (sees)
            {
                float closeness = Mathf.Lerp(2.6f, 0.55f, Mathf.Clamp01(dist / Mathf.Max(1f, sight)));
                float rate = closeness * (alerted ? 2f : 1f) * (player.Crouched ? 0.5f : 1f) * (player.TorchOn ? 1.4f : 1f);
                Suspicion = Mathf.Min(1f, Suspicion + rate * Time.deltaTime);
            }
            else Suspicion = Mathf.Max(0f, Suspicion - 0.35f * Time.deltaTime);
            return Suspicion >= 1f;
        }

        public string DisplayName => kind.ToString();
        public bool IsHuman => kind == EnemyKind.Guard || kind == EnemyKind.Worker || kind == EnemyKind.Hunter;
        public bool IsAnimal => kind == EnemyKind.Tiger || kind == EnemyKind.Deer || kind == EnemyKind.Elephant;

        /// <summary>Animals never go past this line: the factory fence keeps them out of the premises.</summary>
        const float AnimalLimitZ = ThreatDirector.PremisesFenceZ - 3f;

        /// <summary>Where an animal may go: never over the factory fence.</summary>
        Vector3 Allowed(Vector3 target)
        {
            if (!IsAnimal || target.z <= AnimalLimitZ) return target;
            target.z = AnimalLimitZ;
            return NavMesh.SamplePosition(target, out var hit, 6f, NavMesh.AllAreas) && hit.position.z <= AnimalLimitZ + 0.5f ? hit.position : home;
        }
        public bool Aiming => aimUntil > 0f;
        public string StateName => state.ToString();
        public bool IsPursuing => state == State.Chase || state == State.Charge;
        public bool Reloading => Time.time < reloadUntil;

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
                    Set(15f, 150f, 12f, 2.2f, 6.2f, 2.0f, 8f, 1.8f, 3.5f, 1.0f); health.max = 40f; leash = Mathf.Max(60f,leash); break;
                case EnemyKind.Deer:
                    Set(16f, 300f, 16f, 1.4f, 7.5f, 0f, 0f, 99f, 0f, 1.7f); health.max = 30f; harmless = true; break;
                case EnemyKind.Elephant:
                    Set(10f, 360f, 10f, 1.2f, 6.2f, 3.4f, 25f, 3f, 9f, 2.8f); health.max = 220f; wanderRadius = 7f; break;
                case EnemyKind.Guard:
                    Set(20f, 110f, 10f, 1.8f, 5.2f, 1.8f, 8f, 1.6f, 3f, 1.7f); health.max = 100f;   // 4 pistol body shots or 2 headshots
                    SetGun(GunSound.Pistol, 22f, 5f, 1, 1.3f, 0.6f, 0.4f, 8, 2.2f); break;
                case EnemyKind.Worker:
                    // Factory officers: as sharp as the security guards (sight, speed, reaction time),
                    // with a double-barrel shotgun: deadly close up, quick to aim, quick to reload
                    Set(20f, 120f, 11f, 1.8f, 5.4f, 1.7f, 8f, 1.4f, 3f, 1.7f); health.max = 100f;
                    SetGun(GunSound.Shotgun, 16f, 3.5f, 6, 1.3f, 0.35f, 0.45f, 2, 1.6f); break;
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
            if (kind == EnemyKind.Tiger)
            {
                tigerVoice = GetComponent<TigerVoice>();
                if (tigerVoice == null) tigerVoice = gameObject.AddComponent<TigerVoice>();
            }
            if (IsHuman)
            {
                humanMotion = GetComponent<HumanLocomotion>();
                if (humanMotion == null) humanMotion = gameObject.AddComponent<HumanLocomotion>();
            }
            guardVoice = GuardCombatVoice.For(this);
            home = transform.position;
            if (NavMesh.SamplePosition(transform.position, out var hit, 4f, NavMesh.AllAreas)) { agent.Warp(hit.position); home = hit.position; }
            if(kind==EnemyKind.Tiger)tigerNavigation=new TigerNavigation(agent,TigerDestinationAllowed);
            player = FindAnyObjectByType<PlayerController>();
            waypoint = startWaypoint;
            state = patrolRoute != null && patrolRoute.childCount > 0 ? State.Patrol : State.Wander;
            idleUntil = Time.time + Random.Range(0f, 3f);
        }

        // ------------------------------------------------------------------ main loop

        void Update()
        {
            if (state == State.Dead || !agent.enabled) return;
            if (!agent.isOnNavMesh && (tigerNavigation == null || !tigerNavigation.Attach())) return;
            bool playing = GameManager.Instance == null || GameManager.Instance.IsPlaying;
            agent.isStopped = !playing;
            if (!playing || player == null) return;
            if (humanMotion != null && humanMotion.Airborne) { agent.isStopped = true; return; }

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
            if (!sees && !player.Health.Dead && Time.time >= nextClueCheck)
            {
                nextClueCheck = Time.time + 0.5f;
                Vector3 eye = transform.position + Vector3.up * eyeHeight;
                if (Time.time >= nextInvestigation && EnemyAwareness.TorchClue(player.torch, player.transform, transform, eye, transform.forward, out var clue))
                { nextInvestigation = Time.time + 3f; HearNoise(clue + Random.insideUnitSphere * 2f); }
            }

            if (state == State.Stunned)
            {
                aimUntil = -1f;
                stateTimer -= Time.deltaTime;
                if (stateTimer <= 0f) state = resumeState;
                return;
            }

            // The factory fence keeps animals out: once he's inside the premises they lose interest,
            // and one that has strayed over the line heads back home
            if (IsAnimal && (ThreatDirector.InPremises(player.transform.position) || transform.position.z > AnimalLimitZ + 1f))
            {
                sees = false; hears = false;
                if (state == State.Chase || state == State.Charge || state == State.Search) { state = State.Wander; aimUntil = -1f; Go(home, walkSpeed); }
            }

            switch (kind)
            {
                case EnemyKind.Tiger: TigerLogic(dist, sees, hears); break;
                case EnemyKind.Deer: DeerLogic(dist, sees || hears); break;
                case EnemyKind.Elephant: ElephantLogic(dist, sees); break;
                default: HunterLogic(dist, sees, hears); break;
            }
        }

        bool TigerDestinationAllowed(Vector3 point) => point.z <= AnimalLimitZ && !ThreatDirector.InCamp(point)
            && Vector3.Distance(point,home) <= Mathf.Max(60f,leash)*1.5f;

        void TigerLogic(float dist,bool sees,bool hears)
        {
            if(player.Health.Dead || ThreatDirector.InCamp(player.transform.position) || ThreatDirector.InPremises(player.transform.position))
            {
                ThreatDirector.Release(this);Suspicion=0;lostTimer=0;tigerMemoryUntil=0;
                state=State.Wander;
                if(Vector3.Distance(transform.position,home)>4f)tigerNavigation.Move(home,walkSpeed);else TigerWander();
                return;
            }
            bool noticed=Notice(sees,dist) || (sees && (dist<4f || state==State.Chase || state==State.Stalk));
            if(sees && noticed)
            {
                if(state!=State.Chase && state!=State.Stalk)Bark();
                lastKnown=player.transform.position;tigerMemoryUntil=Time.time+9f;lostTimer=0;
                state=State.Chase;
            }
            else if(hears && !Chasing)
            {lastKnown=player.transform.position;tigerMemoryUntil=Time.time+6f;state=State.Search;stateTimer=7f;}
            if(Vector3.Distance(transform.position,home)>leash*1.45f)
            {ThreatDirector.Release(this);tigerMemoryUntil=0;state=State.Wander;tigerNavigation.Move(home,runSpeed);return;}
            if(state==State.Chase || state==State.Stalk)
            {
                if(Time.time>tigerMemoryUntil || (player.Hidden && !sees))
                {ThreatDirector.Release(this);state=State.Search;stateTimer=8f;tigerSearchProbe=0;}
                else
                {
                    bool turn=MayAttackPlayer();state=turn?State.Chase:State.Stalk;
                    if(turn)
                    {
                        if(dist<=attackRange*0.8f && sees && ClearLine())
                        {agent.ResetPath();Face(player.Chest,10f);TryAttack();tigerNavigation.ResetProgress();}
                        else {tigerNavigation.Move(Allowed(lastKnown),runSpeed);if(dist<=attackRange && sees)TryAttack();}
                    }
                    else
                    {
                        // Waiting predators stalk a changing flank instead of freezing on the spot.
                        float angle=Time.time*0.32f+(GetEntityId().GetHashCode()%31)*0.7f;
                        Vector3 flank=lastKnown+new Vector3(Mathf.Cos(angle),0,Mathf.Sin(angle))*8f;
                        tigerNavigation.Move(Allowed(flank),walkSpeed*1.25f);
                        if(dist<10f)Face(lastKnown+Vector3.up,4f);
                    }
                    return;
                }
            }
            if(state==State.Search)
            {
                stateTimer-=Time.deltaTime;
                if(Time.time>=tigerSearchProbe)
                {
                    tigerSearchProbe=Time.time+1.8f;
                    Vector2 offset=Random.insideUnitCircle*(stateTimer<5f?4f:0f);
                    tigerNavigation.Move(Allowed(lastKnown+new Vector3(offset.x,0,offset.y)),walkSpeed*1.6f);
                }
                if(stateTimer<=0){state=State.Wander;tigerMemoryUntil=0;idleUntil=0;}
                return;
            }
            if(sees && !noticed)Face(player.Chest,4f);
            TigerWander();
        }
        void TigerWander()
        {
            if(agent.hasPath && !Arrived(0.8f)) {tigerNavigation.Move(agent.destination,walkSpeed);return;}
            if(Time.time<idleUntil)return;
            if(!agent.hasPath || Arrived(0.8f))
            {
                Vector2 offset=Random.insideUnitCircle*wanderRadius;
                tigerNavigation.Move(Allowed(home+new Vector3(offset.x,0,offset.y)),walkSpeed);
                idleUntil=Time.time+Random.Range(1f,2.5f);
            }
        }

        void ElephantCry()
        {
            if(Time.time<nextElephantCry || (elephantVoice!=null && elephantVoice.isPlaying))return;
            elephantVoice=GunFX.Play(Random.value<0.5f?GunSound.ElephantTrumpet1:GunSound.ElephantTrumpet2,transform.position,0.95f);
            nextElephantCry=Time.time+Mathf.Max(5f,elephantVoice.clip.length+0.5f);
        }

        void HunterLogic(float dist, bool sees, bool hears)
        {
            // The hunter keeps to the jungle: once the player is up in the tea, he lets him go
            if (kind == EnemyKind.Hunter && player.transform.position.z > -1f) { sees = false; hears = false; if (state == State.Chase) { state = State.Search; stateTimer = 3f; } }
            bool noticed = Notice(sees, dist) || (sees && state == State.Chase);
            if (noticed && !MayAttackPlayer())
            {
                // Someone else is fighting him (or he's at the camp): wait for my turn
                if (state == State.Chase) { state = State.Search; stateTimer = 4f; }
                HoldBack(dist);
                return;
            }
            if (sees && !noticed)
            {
                // Something's there... turn and edge closer to look (the moment to duck out of sight)
                Vector3 to = player.transform.position - transform.position; to.y = 0f;
                if (to.sqrMagnitude > 0.01f) transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(to), 4f * Time.deltaTime);
                if (Suspicion > 0.5f && state != State.Search) { lastKnown = player.transform.position; state = State.Search; stateTimer = 4f; }
                sees = false;
            }
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
                    if (!MayAttackPlayer()) { state = State.Recover; stateTimer = 3f; break; }
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
                    if (Notice(sees && dist < sight && !player.Hidden, dist) && MayAttackPlayer()) { state = State.Charge; stateTimer = 6f; Bark(); GameManager.Instance?.Toast("The elephant is charging!"); }
                    else Wander();
                    break;
            }
        }

        // ------------------------------------------------------------------ movement helpers

        void Go(Vector3 target, float speed)
        {
            target = Allowed(target);
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
                if (NavMesh.SamplePosition(p, out var hit, 4f, NavMesh.AllAreas)) agent.SetDestination(Allowed(hit.position));
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
            Vector3 eye = IsHuman && anim != null ? anim.SightOrigin : transform.position + Vector3.up * eyeHeight;
            Vector3 to = player.Chest - eye;
            if (dist > 2f && Vector3.Angle(transform.forward, to) > fov * 0.5f) return false;
            if(kind==EnemyKind.Tiger)return EnemyAwareness.Clear(eye,player.Chest,transform,player.transform);
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
            if(kind==EnemyKind.Tiger)return EnemyAwareness.Clear(from,player.Chest,transform,player.transform);
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
            if(kind==EnemyKind.Tiger && !MayAttackPlayer())return;
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
            if (!Aiming) { guardVoice?.Threaten(); aimUntil = Time.time + gunAim + (Time.time - nextShot > 3f ? 0.8f : 0f); return; }
            if (Time.time < aimUntil || (anim != null && !anim.CanAimAt(player.Chest))) return;
            aimUntil = -1f;
            if (!ClearShot()) { nextShot = Time.time + 0.4f; return; }
            Fire(dist);
            nextShot = Time.time + gunInterval;
            if (--shotsLeft <= 0)
            {
                shotsLeft = gunMag;
                reloadUntil = Time.time + gunReload;
                GunFX.Play(GunFX.ReloadFor(gunSound), transform.position, 0.6f);
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
            guardVoice?.FireCallout();
            GunFX.Play(gunSound, from, 1f);
            float chance = gunAccuracy * (1f - 0.5f * dist / gunRange) * (player.Sprinting ? 0.6f : 1f) * (player.Crouched ? 0.75f : 1f) * (alerted ? 1.15f : 1f);
            chance = Mathf.Clamp(chance, 0.05f, 0.95f);
            float damage = 0f;
            for (int i = 0; i < gunPellets; i++)
            {
                if (Random.value < chance)
                {
                    damage += gunDamage;
                    NoticeProjectile(from, player.Chest, transform);
                    if (i < 3) GunFX.Tracer(from, player.Chest + Random.insideUnitSphere * 0.25f + Vector3.up * 0.2f, 0.02f, 0.05f);
                }
                else if (i < 3)
                {
                    // A miss: the round cracks past and hits whatever is behind
                    Vector3 aim = player.Chest + Random.insideUnitSphere * 1.3f - from;
                    if (Physics.Raycast(from, aim.normalized, out var hit, gunRange * 1.5f, ~(1 << 2), QueryTriggerInteraction.Ignore)
                        && !hit.collider.transform.IsChildOf(transform))
                    { GunFX.Tracer(from, hit.point, 0.02f, 0.05f); GunFX.Impact(hit.point, hit.normal, false); NoticeProjectile(from, hit.point, transform); }
                    else { GunFX.Tracer(from, from + aim.normalized * gunRange, 0.02f, 0.05f); NoticeProjectile(from, from + aim.normalized * gunRange, transform); }
                }
            }
            GunFX.Flash(from, 5f);
            anim?.Shoot();
            if (damage > 0f)
            {
                player.Health.Damage(damage, transform.position);
                GameManager.Instance?.OnPlayerHit(this);
            }
            MakeNoise(transform.position, 20f);   // gunfire draws the others
        }

        public void Knockback(Vector3 v) { v.y = 0f; knock = Vector3.ClampMagnitude(knock + v * (kind == EnemyKind.Elephant ? 0.1f : 1f), IsHuman ? 7f : 12f); }

        void OnDamaged(float amount, Vector3 from)
        {
            if (state == State.Dead) return;
            if (kind == EnemyKind.Tiger && !health.Dead) {tigerVoice?.TryGrowl();tigerMemoryUntil=Time.time+10f;tigerNavigation?.ResetProgress();}
            if(kind==EnemyKind.Elephant && !health.Dead)ElephantCry();
            if (IsHuman && !health.Dead && Time.time >= nextHurtVoice)
            {
                nextHurtVoice = Time.time + 0.3f;
                GunFX.Play(Random.Range(0, 2) == 0 ? GunSound.HumanHurt1 : GunSound.HumanHurt2, transform.position + Vector3.up, 0.8f);
            }
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
                    if(agent.enabled && agent.isOnNavMesh)agent.ResetPath();
                    transform.rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(from - transform.position, Vector3.up).normalized + transform.forward * 0.001f);
                    break;
            }
        }

        void OnDied()
        {
            if (IsHuman) GunFX.Play(GunSound.HumanDeath, transform.position + Vector3.up, 0.9f);
            state = State.Dead;
            ThreatDirector.Release(this);
            agent.enabled = false;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            anim?.Die();
            if (anim != null) anim.aiming = false;
            switch (kind)   // people drop what they were carrying
            {
                case EnemyKind.Guard: AmmoBox.Drop(transform.position + transform.right * 0.6f, 20, 4); break;
                case EnemyKind.Worker: AmmoBox.Drop(transform.position + transform.right * 0.6f, 8, 8); break;
                case EnemyKind.Hunter: AmmoBox.Drop(transform.position + transform.right * 0.6f, 8, 6); break;
            }
            GameManager.Instance?.OnEnemyDown(this);
        }

        /// <summary>The alarm: guards (and workers nearby) come running to where the player was.</summary>
        /// <summary>Returns false if there's no free responder slot yet (the GameManager sends him later).</summary>
        public bool Alert(Vector3 where)
        {
            if (state == State.Dead) return true;
            alerted = true;
            if (state == State.Chase || state == State.Search) { lastKnown = where; return true; }
            if (!SlotFree(where)) return false;
            lastKnown = where;
            state = State.Search; stateTimer = 12f;
            return true;
        }

        public bool Dead => state == State.Dead;

        void Bark()
        {
            anim?.Roar();
            if (kind == EnemyKind.Tiger) tigerVoice?.TryGrowl();
            if (kind == EnemyKind.Elephant) ElephantCry();
        }

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
