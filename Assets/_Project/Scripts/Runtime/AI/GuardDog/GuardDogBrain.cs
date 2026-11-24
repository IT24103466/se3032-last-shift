using System.Collections.Generic;
using LastShift.AI.Core;
using UnityEngine;

namespace LastShift.GuardDog
{
    /// <summary>
    /// Kalu's decision making: a reactive behaviour tree ticked ten times a second.
    ///
    /// Root (selector, highest priority first)
    ///  ├─ Idle         game not running                         → sit
    ///  ├─ Engage       sees the player (or saw him &lt; 4 s ago) AND has the turn (ThreatDirector)
    ///  │    ├─ Alert   loud bark every 5 s: brings guards, alarm inside the premises
    ///  │    └─ Attack  bite in reach │ pounce (jump-bite) within 4.5 m │ zigzag in if a gun is on it │
    ///  │               chase (A*, ε = 1.5), barking all the way
    ///  │  (no retreat: Kalu fights until he's down: five hits)
    ///  ├─ Wait turn    sees him but someone else is fighting him  → keep 10 m off, growl, watch
    ///  ├─ Indicate     smells him hiding (locker / bush) nearby  → stand and bark at the spot
    ///  ├─ Investigate  heard a noise (gunshots always, small noises while still curious)
    ///  ├─ Track        fresher scent within 4.5 m                → follow the trail (scent-weighted A*)
    ///  ├─ Cast         lost the trail &lt; 25 s ago                 → search ahead then around
    ///  └─ Patrol       walk the estate rounds, sniffing
    ///
    /// The tree decides WHAT to do; <see cref="DogNavigator"/> decides HOW to get there and
    /// <see cref="IDogBody"/> does it. Steering runs every frame from the current plan, so the
    /// 10 Hz brain doesn't make movement choppy.
    /// </summary>
    [RequireComponent(typeof(DogBody))]
    public class GuardDogBrain : MonoBehaviour
    {
        public float walkSpeed = 2.2f, trackSpeed = 4.2f, runSpeed = 7.8f, flankSpeed = 7.2f, biteRange = 1.7f, leapRange = 4.5f;
        public float pursuitMemory = 4f;   // keeps chasing this long after losing sight of him
        const float TickInterval = 0.1f;

        public DogBlackboard Blackboard { get; private set; }
        public DogNavigator Navigator { get; private set; }
        public BehaviourTree Tree { get; private set; }
        public string ActiveBranch { get; private set; } = "-";
        public float TickMs { get; private set; }
        public int StuckEvents { get; private set; }

        IDogBody body;
        DogSenses senses;
        PlayerController player;
        ScentTracker world;
        Vector3[] patrol;

        float nextTick, moveSpeed;
        Vector3 progressPos; float progressTime; int stuckCount;
        int patrolIndex; float pauseUntil = -1f;
        readonly List<Vector3> castPoints = new List<Vector3>(); int castIndex; float castPauseUntil;
        float nextChaseBark, zigSide = 1f, zigFlipAt, nextTreedBark, nextIndicateBark, investigateSniffUntil, lastTrackToast = -99f;
        Vector3 lastTrailPoint;
        readonly System.Diagnostics.Stopwatch sw = new System.Diagnostics.Stopwatch();

        float Now => Time.time;
        bool Playing => GameManager.Instance == null || GameManager.Instance.IsPlaying;

        public void Init(PlayerController p, ScentTracker w, Vector3 kennelPos, Vector3[] patrolPoints)
        {
            player = p; world = w; patrol = patrolPoints;   // (kennelPos: where it starts; it no longer retreats there)
            body = GetComponent<DogBody>();
            Blackboard = new DogBlackboard();
            senses = new DogSenses(Blackboard, body, player, world);
            Navigator = new DogNavigator(world, player);
            Tree = new BehaviourTree(BuildTree(), () => Time.time);
            progressPos = transform.position; progressTime = Now;
        }

        void OnDestroy() => senses?.Dispose();

        // ------------------------------------------------------------------ the tree

        Node BuildTree()
        {
            var bb = Blackboard;
            return new Selector("Root",
                new Sequence("Idle",
                    new Condition("Game not running", () => !Playing || player.Health.Dead),
                    new ActionNode("Sit", () => { Halt(); return Status.Running; })),

                new Sequence("Engage",
                    new Condition("Sees / just saw player, my turn", () => (bb.SeesPlayer || InPursuit()) && MyTurn()),
                    new AlwaysSucceed("Alert",
                        new Cooldown("Every 5 s", 5f, new ActionNode("Bark", () => { BarkAndAlert(); return Status.Success; }))),
                    new Selector("Attack",
                        new Sequence("Bite",
                            new Condition("In reach", () => bb.DistToPlayer < biteRange),
                            new ActionNode("Bite", Bite)),
                        new Sequence("Pounce",
                            new Condition("Close, sees him, can jump", () => bb.DistToPlayer < leapRange && bb.SeesPlayer && body.CanLeap),
                            new ActionNode("Jump and bite", Pounce)),
                        new Sequence("Dodge",
                            new Condition("Gun on me", () => bb.PlayerAimingAtMe && bb.DistToPlayer > 5f),
                            new ActionNode("Zigzag in", Zigzag)),
                        new ActionNode("Chase", Chase))),

                new Sequence("Wait turn",
                    new Condition("Someone else is fighting him", () => bb.SeesPlayer),
                    new ActionNode("Keep my distance", WaitTurn, enter: () => body.Growl())),

                new Sequence("Indicate",
                    new Condition("Smells him hiding, my turn", () => SmellsHiddenPlayer() && MyTurn()),
                    new ActionNode("Point and bark", Indicate, enter: () => body.Growl())),

                new Sequence("Investigate",
                    new Condition("Heard something", HeardSomething),
                    new ActionNode("Check the noise", Investigate, enter: () => investigateSniffUntil = -1f, exit: () => body.SetSniffing(false))),

                new Sequence("Track",
                    new Condition("On the trail", () => bb.TrailAhead.Valid),
                    new ActionNode("Follow the trail", FollowTrail, enter: StartTracking, exit: () => body.SetSniffing(false))),

                new Sequence("Cast",
                    new Condition("Lost the trail", () => Now - bb.TrailLostTime < 25f),
                    new ActionNode("Search around", Cast, enter: BuildCastPattern, exit: () => body.SetSniffing(false))),

                new ActionNode("Patrol", Patrol, exit: () => body.SetSniffing(false)));
        }

        // ------------------------------------------------------------------ loop

        void Update()
        {
            if (Tree == null) return;
            if (!body.Alive) { Halt(); ThreatDirector.Release(this); enabled = false; senses.Dispose(); return; }
            if (body.Leaping) { ThreatDirector.TryAttack(this, "Kalu (the estate dog)", player.transform.position); return; }   // mid-jump: keep the turn, no steering

            if (Now >= nextTick)
            {
                nextTick = Now + TickInterval;
                sw.Restart();
                senses.Update(Now, TickInterval);
                Tree.Tick();
                sw.Stop();
                TickMs = (float)sw.Elapsed.TotalMilliseconds;
                ActiveBranch = Tree.ActivePath();
            }

            // Steer every frame along the current plan
            if (moveSpeed > 0f && Navigator.Steer(body.Position, out var target)) body.MoveTo(target, moveSpeed);
            WatchProgress();
        }

        /// <summary>Edge case: not getting anywhere (closed door, crowd) → avoid that way and re-plan; repeatedly → snap back onto the NavMesh.</summary>
        void WatchProgress()
        {
            if (moveSpeed <= 0f || Navigator.Arrived(body.Position, 1.2f)) { progressPos = body.Position; progressTime = Now; return; }
            if ((body.Position - progressPos).sqrMagnitude > 0.25f) { progressPos = body.Position; progressTime = Now; stuckCount = 0; return; }
            if (Now - progressTime < 1.5f) return;
            Navigator.MarkBlockedAhead(body.Position, 8f, Now);
            StuckEvents++;
            if (++stuckCount >= 3) { body.Unstick(); stuckCount = 0; }
            progressTime = Now;
        }

        bool GoTo(Vector3 goal, DogNavigator.Mode mode, float speed, bool force = false)
        {
            Navigator.PlanTo(body.Position, goal, mode, Now, force);
            moveSpeed = speed;
            return Navigator.Arrived(body.Position, 1.2f);
        }

        void Halt() { moveSpeed = 0f; body.Stop(); }

        void BarkAndAlert()
        {
            body.Bark();
            EnemyAI.MakeNoise(body.Position, 35f);                    // the guards and workers come to the barking
            if (player.transform.position.z > 39f) GameManager.Instance?.RaiseAlarm();
        }

        // ------------------------------------------------------------------ conditions

        /// <summary>One fight at a time: only attack when the ThreatDirector gives Kalu the turn.</summary>
        bool MyTurn() => ThreatDirector.TryAttack(this, "Kalu (the estate dog)", player.transform.position);

        /// <summary>Lost sight of him a moment ago (round a corner, behind a bush): keep after him.</summary>
        bool InPursuit() => !Blackboard.PlayerHidden && Now - Blackboard.LastSeenTime < pursuitMemory;

        /// <summary>Barks all the way while it chases (sound only; the alert bark that calls the guards is every 5 s).</summary>
        void ChaseBark()
        {
            if (Now < nextChaseBark) return;
            nextChaseBark = Now + Random.Range(0.8f, 1.5f);
            if (Random.value < 0.75f) body.Bark(); else body.Growl();
        }

        bool SmellsHiddenPlayer()
        {
            var bb = Blackboard;
            if (!bb.PlayerHidden || bb.DistToPlayer > 9f) return false;
            return world.Scent.Strength(world.CellOf(bb.PlayerPos), Now) > 0.4f;
        }

        bool HeardSomething()
        {
            var bb = Blackboard;
            if (bb.NoiseHandled || Now - bb.NoiseTime > 15f) return false;
            if (bb.NoiseLoudness >= 30f) return true;                 // the player's gunfire: always worth a look
            if (bb.TrailAhead.Valid) return false;                    // on a fresh trail, small noises don't distract it
            return bb.Interest > 0.3f;                                // pebbles, distant shots: only while it still falls for them
        }

        // ------------------------------------------------------------------ actions

        Status WaitTurn()
        {
            // Stay about 10 m away, facing him, until the other fight is over
            Vector3 away = body.Position - Blackboard.PlayerPos; away.y = 0f;
            if (Blackboard.DistToPlayer < 9f && away.sqrMagnitude > 0.01f)
                GoTo(body.Position + away.normalized * 5f, DogNavigator.Mode.Walk, trackSpeed);
            else { Halt(); body.Face(Blackboard.PlayerPos); }
            return Status.Running;
        }

        Status Bite()
        {
            Halt();
            body.Face(Blackboard.PlayerPos);
            body.TryBite(player);
            ChaseBark();
            return Status.Running;
        }

        Status Pounce()
        {
            moveSpeed = 0f;
            body.TryLeap(player);
            return Status.Success;
        }

        Status Zigzag()
        {
            // Run in on alternating sides of the line of fire, choosing paths outside the aim cone
            bool flip = Now >= zigFlipAt;
            if (flip) { zigSide = -zigSide; zigFlipAt = Now + Random.Range(0.55f, 0.85f); }
            Vector3 to = Blackboard.PlayerPos - body.Position; to.y = 0f;
            Vector3 side = Vector3.Cross(Vector3.up, to.normalized) * zigSide;
            GoTo(Blackboard.PlayerPos + side * Mathf.Min(4f, to.magnitude * 0.5f), DogNavigator.Mode.Flank, flankSpeed, flip);
            ChaseBark();
            return Status.Running;
        }

        Status Chase()
        {
            GoTo(Blackboard.PlayerPos, DogNavigator.Mode.Chase, runSpeed);
            ChaseBark();
            // Edge case: he's somewhere the dog can't follow (the loft, a roof): wait below and keep barking
            if (Navigator.GoalUnreachable && Navigator.Arrived(body.Position, 1.5f))
            {
                Halt();
                body.Face(Blackboard.PlayerPos);
                if (Now >= nextTreedBark) { nextTreedBark = Now + 2.5f; BarkAndAlert(); }
            }
            return Status.Running;
        }

        Status Indicate()
        {
            // A dog isn't fooled by a locker: it stands at the hiding place and barks for the guards
            if (Blackboard.DistToPlayer > 1.9f) GoTo(Blackboard.PlayerPos, DogNavigator.Mode.Walk, trackSpeed);
            else { Halt(); body.Face(Blackboard.PlayerPos); }
            if (Now >= nextIndicateBark) { nextIndicateBark = Now + 2.2f; BarkAndAlert(); }
            return Status.Running;
        }

        Status Investigate()
        {
            var bb = Blackboard;
            if (investigateSniffUntil < 0f)
            {
                bool loud = bb.NoiseLoudness >= 30f;
                bool there = GoTo(bb.NoisePos, DogNavigator.Mode.Walk, loud ? runSpeed * 0.85f : trackSpeed)
                             || Vector3.Distance(body.Position, bb.NoisePos) < 2f || Navigator.GoalUnreachable && Navigator.Arrived(body.Position, 1.5f);
                if (there) { Halt(); body.SetSniffing(true); investigateSniffUntil = Now + 1.5f; }
                return Status.Running;
            }
            if (Now < investigateSniffUntil) return Status.Running;

            // Sniffed around: was he actually here? If not, it was a trick (a thrown pebble) and the
            // dog gets less interested in small noises (habituation); real scent hands over to Track.
            var hit = world.Scent.FreshestNear(world.CellOf(body.Position), 5f, 0.06f, bb.NoiseTime - 10f, Now);
            if (!hit.Valid && bb.NoiseLoudness < 30f) bb.Interest *= 0.5f;
            bb.NoiseHandled = true;
            return Status.Success;
        }

        void StartTracking()
        {
            body.SetSniffing(true);
            lastTrailPoint = body.Position;
            if (Now - lastTrackToast > 40f) { lastTrackToast = Now; GameManager.Instance?.Toast("A dog is following your scent...", 2.5f); body.Growl(); }
        }

        Status FollowTrail()
        {
            var bb = Blackboard;
            Vector3 target = world.WorldOf(bb.TrailAhead.Cell);
            bool alerted = bb.Alerted(Now);
            GoTo(target, DogNavigator.Mode.Track, alerted ? trackSpeed + 1.2f : trackSpeed);
            Vector3 d = target - body.Position; d.y = 0f;
            if (d.magnitude < 1.3f || Navigator.Arrived(body.Position, 1.2f) && d.magnitude < 2.5f)
            {
                // Reached this point of the trail: from now on only newer scent counts (never backtrack)
                Vector3 heading = target - lastTrailPoint; heading.y = 0f;
                if (heading.sqrMagnitude > 0.01f) bb.TrailHeading = heading.normalized;
                lastTrailPoint = target;
                bb.TrailTime = bb.TrailAhead.Time;
                Navigator.Clear();
            }
            return Status.Running;
        }

        /// <summary>
        /// Search pattern where the trail went cold (e.g. he waded through the stream): first an
        /// arc ahead in the direction the trail was heading (most likely), then all around, at
        /// 4, 7 and 11 m. Senses keep sniffing while it moves, so Track takes over the moment
        /// newer scent turns up.
        /// </summary>
        void BuildCastPattern()
        {
            castPoints.Clear(); castIndex = 0; castPauseUntil = -1f;
            body.SetSniffing(true);
            var bb = Blackboard;
            Vector3 heading = bb.TrailHeading.sqrMagnitude > 0.01f ? bb.TrailHeading : body.Forward;
            float[] angles = { 0f, 35f, -35f, 70f, -70f, 110f, -110f, 150f, -150f, 180f };
            foreach (float r in new[] { 4f, 7f, 11f })
                foreach (float a in angles)
                {
                    if (r < 5f && Mathf.Abs(a) > 75f) continue;   // the close ring only needs the forward half
                    Vector3 p = bb.TrailLostPos + Quaternion.Euler(0f, a, 0f) * heading * r;
                    int c = world.Grid.NearestWalkable(world.CellOf(p), 2);
                    if (c >= 0) castPoints.Add(world.WorldOf(c));
                }
        }

        Status Cast()
        {
            if (castIndex >= castPoints.Count) { Blackboard.TrailLostTime = -99f; return Status.Failure; }   // give up: back to patrol
            if (castPauseUntil > 0f)
            {
                if (Now < castPauseUntil) return Status.Running;
                castPauseUntil = -1f; castIndex++;
                return Status.Running;
            }
            bool arrived = GoTo(castPoints[castIndex], DogNavigator.Mode.Walk, trackSpeed);
            if (arrived || Navigator.GoalUnreachable && Navigator.Arrived(body.Position, 1.5f)) { Halt(); castPauseUntil = Now + 0.7f; }
            return Status.Running;
        }

        Status Patrol()
        {
            if (patrol == null || patrol.Length == 0) { Halt(); return Status.Running; }
            if (pauseUntil > Now) { Halt(); body.SetSniffing(true); return Status.Running; }
            body.SetSniffing(false);
            if (GoTo(patrol[patrolIndex], DogNavigator.Mode.Walk, walkSpeed))
            {
                pauseUntil = Now + Random.Range(2f, 4f);
                patrolIndex = (patrolIndex + 1) % patrol.Length;
                Navigator.Clear();
            }
            return Status.Running;
        }
    }
}
