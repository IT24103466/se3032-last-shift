# Kalu – the estate guard dog (SE3062 agent, IT24103466 Liyanage R.K.S)

Kalu is a black estate dog that guards the tea estate between the jungle and the factory.
It **tracks Nimal by scent**: every step leaves scent that fades with time and is washed away by
the rain; the dog follows the trail forwards, loses it at the stream and casts around to pick it
up again, can't be fooled by a locker, and barks to bring the human guards. Once it has seen him it is
aggressive: it barks all the way through the chase, keeps after him for a few seconds when he slips
out of sight, pounces (jump-bite) from a few metres away, and never backs off - it only stops when
it has been hit five times. It reacts to the
player's weapon: with a gun pointed at it, it stops running straight in and zigzags outside the
line of fire.

Press **F12** in play mode for the AI debug view (map of the scent field, A* search and path,
the behaviour tree's active branch, planner timings and senses).

## Architecture

```
            ┌──────────────── LastShift.AI.Core (own assembly, no engine references) ───────────────┐
            │ GridMap        walkable cells, ground height, water / cover flags                    │
            │ GridAStar      weighted A*, octile heuristic, dynamic per-cell cost, partial paths   │
            │ ScentField     lazily-decaying scent with deposit times                              │
            │ BehaviourTree  reactive Selector / Sequence / Condition / Action / Cooldown           │
            └─────────────────────────────────────────────▲──────────────────────────────────────────┘
                                                          │ uses
 Game layer (Assets/_Project/Scripts/Runtime/AI/GuardDog) │
   ScentTracker ── builds the grid from the NavMesh, deposits the player's scent (4 Hz)
   DogSenses    ── sight / smell / hearing / weapon awareness → DogBlackboard
   GuardDogBrain ─ the behaviour tree (10 Hz) → chooses WHAT to do
   DogNavigator ── A* cost model per mode → decides HOW to get there (string-pulled path)
   DogBody (IDogBody) ── NavMeshAgent steering, animation, bite, bark, sounds
   GuardDogSpawner ── places the dog at runtime (no scene edits); GuardDogDebugView ── F12
```

The decision logic never touches GameObjects: it reads the blackboard and calls `IDogBody` /
`DogNavigator`. The core algorithms are pure C# in their own assembly (`noEngineReferences`),
so they can't accidentally depend on the scene, and could be unit-tested outside Unity.

## Why a behaviour tree (and not an FSM, GOAP or utility AI)

* The dog has a clear **priority order** of concerns (fight what it sees → point out a
  hidden intruder → check noises → follow the trail → search → patrol). A reactive BT expresses
  exactly that: the tree is re-evaluated from the root every tick and the first branch whose
  conditions hold wins, pre-empting (aborting) whatever was running. With an FSM the same logic
  needs a transition from every state to every higher-priority state (n² transitions); adding a
  new behaviour (e.g. *Indicate*) is one new branch here.
* GOAP would plan sequences of actions toward goals, but the dog's actions don't chain into
  multi-step plans – it needs fast reactions, not plans; GOAP's search cost would be wasted.
* Utility AI scores options continuously, which is useful when many options are equally valid;
  here the priorities are strict (a visible intruder always beats a noise), so scoring would add
  tuning without adding behaviour. The *movement* uses cost-based reasoning instead (A* weights).

## Pathfinding: weighted A* on a scent grid

Unity's NavMesh can't express "prefer the scent trail" or "stay out of the line of fire", so the
dog plans globally with its own A* on a 1 m grid (104 × 148 cells) rasterised from the NavMesh
triangles (lowest floor kept: the dog can't climb to the loft). The NavMeshAgent only steers
locally towards the farthest path cell in a straight, NavMesh-clear line (string pulling).

* Edge cost a→b = |ab| × m(b), with 8-way moves and no corner cutting.
* Heuristic h(n) = octile(n, goal) × m_min, where octile = (max − min) + √2·min of |dx|, |dz|.
  Octile is the exact shortest distance on an empty 8-connected grid, and every step costs at least
  its length × m_min, so **h is admissible and consistent → A* returns an optimal path** (ε = 1).
* Chase uses ε = 1.5 (f = g + 1.5h): far fewer expansions, path ≤ 1.5 × optimal – the target
  moves every frame, so re-planning fast matters more than the last few percent of path length.
* Cost multiplier per mode:

| Mode | m(cell) | m_min | Purpose |
|---|---|---|---|
| Walk | 1 | 1 | patrol, going home |
| Track | 1 − 0.6·min(1, scent) | 0.4 | stay **on** the trail like a real tracker dog instead of cutting corners |
| Chase | 1, ε = 1.5 | 1 | fast re-plans at 2.5 Hz |
| Flank | 1 + 4·inAimCone | 1 | approach from outside the gun's ±20–35° cone |

  Water (the stream) costs +∞ in every mode: Kalu never goes into the water, and string pulling
  never takes a shortcut across it. If the player is on the other side, A* returns a partial path
  and the dog waits on the bank, barking. The aim-cone weight 4 makes a cell in the line of fire cost 5×,
  so the dog accepts up to ~4× the distance to stay out of it, but still comes straight in when
  there is no way round.
* Bookkeeping: binary heap with lazy deletion, "search stamps" instead of clearing arrays
  (O(1) reset), 9 000-node budget, and a partial path to the closest node when the goal is
  unreachable (the dog then waits below and barks).
* Measured in play: 15–250 nodes expanded, 0.01–0.4 ms per plan.

## Scent model

* Each cell stores strength s0 and deposit time t0; strength now = s0 · 2^(−(t − t0)/T½),
  evaluated lazily (no per-frame cost for 15 000 cells).
* T½ = 45 s in the open (monsoon rain), 110 s under a roof or canopy (cover found by raycasting
  up from every cell once). Water cells (the stream) take no scent at all.
* The player deposits 4×/s through a 3 × 3 kernel (1 / 0.55 / 0.35): sprinting 0.9, walking 0.6,
  crouching 0.35, standing 0.3, in a locker 0.5 (scent pools where he hides).
* **Tracking rule:** go to the walkable cell within 4.5 m whose deposit time is the newest and newer
  than the trail point already reached. Deposit times strictly increase along the player's path,
  so this always leads forwards, never back along older parts of the trail – even when the path
  loops or crosses itself – and needs no "visited" list.
* The stream band is ~5 m wide, more than the 4.5 m sniffing radius, so crossing water breaks the
  trail: the dog switches to **Cast** – it searches points 4, 7 and 11 m from where it lost the
  scent, ahead in the trail's direction first (most likely), then all around.

## Behaviour tree

```
Root (Selector)
 ├─ Idle         game not running                         → sit
 ├─ Engage       sees the player (or saw him < 4 s ago) and has the turn (ThreatDirector)
 │    ├─ Alert   Cooldown 5 s: bark → noise 35 m (guards come), alarm if inside the premises
 │    └─ Attack (Selector)                                  (barks every 0.8–1.5 s throughout)
 │         ├─ Bite      within 1.7 m                       → 7 damage + knockback / 1.2 s
 │         ├─ Pounce    within 4.5 m, sees him, every 3 s → jump at him, 10 damage on landing
 │         ├─ Dodge     gun aimed at it (< 12°) and > 5 m  → zigzag in, Flank-mode A*
 │         └─ Chase                                         → Chase-mode A*; unreachable → wait & bark
 ├─ Wait turn    sees him but another enemy has the turn  → keep ~10 m off, growl, watch
 ├─ Indicate     player hidden (locker/bush) ≤ 9 m and his scent there > 0.4 → stand at the spot and bark
 ├─ Alarm        siren on and he is inside the premises    → run to him (Chase-mode A*), barking
 ├─ Investigate  heard a noise (player gunfire always; small noises only if not on a trail and interest > 0.3)
 ├─ Track        newer scent within 4.5 m                   → follow the trail (Track-mode A*)
 ├─ Cast         lost the trail < 25 s ago                   → search pattern
 └─ Patrol       9 points round the tea estate, pausing to sniff
```

### Senses
* Sight: 220° field of view (dogs' eyes are on the side of the head) but short at night: 22 m,
  30 m for a minute after hearing a gunshot, ×1.5 if the player's torch is on, ×0.7 if crouching.
  Needs a clear raycast from the dog's eyes to the player's chest.
* Hearing: listens to every `EnemyAI.MakeNoise` (one event added), at 1.5× the human radius.
* Weapon awareness: reads the player's `PlayerGun` – current weapon and aim direction.

### Adaptive behaviour (habituation)
Each small noise that turns out to have no scent behind it (a thrown pebble) halves the dog's
`interest` in small noises; interest recovers linearly over 90 s. A player can fool Kalu once or
twice with the slingshot, but not forever. Gunfire is never ignored.

## Edge cases handled

| Situation | What the dog does |
|---|---|
| Player hides in a locker or bush | Can't see him, but smells him: *Indicate* – barks at the spot, guards come |
| Player on the loft / roof (unreachable) | A* returns a partial path; dog waits at the closest point and keeps barking |
| Door closes / crowd in the way | No progress for 1.5 s → path cells ahead (≥ 2 m away) cost ∞ for 8 s, re-plan; 3× → warp back onto the NavMesh |
| Player crosses the stream | Trail breaks (water holds no scent) and Kalu never enters the water: he waits on the bank barking, or casts along his side |
| Trail loops or crosses itself | Deposit-time rule never backtracks |
| Pebble distractions | Habituation (above) |
| Factory alarm | Inside the premises the one-at-a-time rule is off: Kalu runs to the intruder and fights alongside the guards |
| Gun pointed at it | Zigzags in outside the aim cone |
| Shot | Five hits and it's down, whatever the weapon; a shotgun blast's 8 pellets land together and count as one hit. No retreat: it fights on |
| Loses sight mid-chase | Keeps chasing for 4 s (pursuit memory), then follows the scent trail |
| Shot dead mid-pounce | Drops to the ground (snapped back onto the NavMesh) |
| Noise from its own bark | Ignored (< 2 m) |
| Off the NavMesh (pushed by knockback) | Warped to the nearest NavMesh point |
| Game paused / player dead | Idle branch |

## Integration
* Spawned at runtime by `GuardDogSpawner` (no edits to Level_01, so no scene merge conflicts).
* Talks to the other agents only through the noise channel: its bark is a 35 m noise that the
  guards and workers investigate; it hears their gunfire the same way.
* Damage, hit markers and knock-outs work through the shared `Health` component.
* Per-tick cost ≈ 0.02–0.06 ms (brain + senses), A* ≤ 0.4 ms, grid build ≈ 150 ms once at load.

## Files
* `Assets/_Project/Scripts/Runtime/AI/Core/` – `GridMap`, `GridAStar`, `ScentField`, `BehaviourTree`, `LastShift.AI.Core.asmdef`
* `Assets/_Project/Scripts/Runtime/AI/GuardDog/` – `ScentTracker`, `DogSenses` (+ `DogBlackboard`), `DogNavigator`, `GuardDogBrain`, `DogBody` (+ `IDogBody`), `DogSounds`, `GuardDogSpawner`, `GuardDogDebugView`
* `Assets/_Project/Scripts/Editor/GuardDog/GuardDogBuilder.cs` – **Tools > World Builder > Build Guard Dog (Kalu)**
* `Assets/_Project/Resources/GuardDog/Kalu.prefab` – the sculpted dog

## Trying it
1. Run **Tools > World Builder > Build Guard Dog (Kalu)** once (the prefab is committed, so only after changing the model).
2. Play, press **F12**. F4 jumps to the fence breach; walk south into the tea estate.
3. Watch the map: your trail appears in orange; when the dog's 4.5 m sniffing circle touches it the
   tree switches to *Track*; break line of sight and it keeps following the scent; cross the stream
   and it *Casts*; hide in a locker and it *Indicates*; point a gun at it and it *Dodges*.
