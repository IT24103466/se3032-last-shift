# Sentinel: the agent brain of the people in the factory

> The same architecture drives every guard and worker, each with his own **role**, weapon and goal: see `ROLES.md`. The text below describes the architecture using the Sentinel (security guard) as the example.

One NPC (a security guard) is driven by a Python agent. The game stays in Unity; the brain runs as a local server and talks to the guard over a TCP socket (JSON lines, 2 decisions per second).
If the brain is not running, the guard silently falls back to the game's own `EnemyAI`.

## Goal and score
The guard plays to maximise a **score** (also the reward used for training), defined in `guard_agent/config.py`:

| Term | Value | Meaning |
|---|---|---|
| damage dealt | +1 per hit point | the number on the HUD |
| intruder defeated | +40 | |
| intruder inside the factory building | -0.6 per second | "do not let the player in" |
| first breach of the factory building | -15 | |
| intruder escapes with the ring | -60 | |
| guard killed | -30 | stops suicidal charges |
| time | -0.01 per second | no idling |

## Architecture (hybrid: probabilistic world model + learned tactics + planner + reflexes)

```
 Unity (GuardAgentBridge.cs)                         Python (guard_agent/)
 ───────────────────────────                         ─────────────────────────────────────────────
 sensors: sight cone + line of sight, hearing,       Perception   -> zone of the guard, what he saw / heard / suffered
 damage taken, alarm, ammo, hp   ── tick JSON ──▶    Belief       -> HMM Bayes filter over WHERE the intruder is (belief.py)
                                                     Opponent mdl -> learns the player's habits between games (opponent_model.py)
                                                     Policy       -> PPO network (stable-baselines3) picks a tactical goal
                                                     Reflexes     -> reload, cover when nearly dead, shoot on a clear shot
                                                     Planner      -> A* over the zone graph (networkx): route + ETA
 executes: NavMesh walk/run, aim, fire, cover,  ◀── action JSON ──  { mode, target (x,z), run, why, score, belief_top }
 reload, open doors, HUD
```

**Fair play.** The policy sees only a perceived observation vector: the guard's zone, the belief map, last-seen zone and age, visible flag, alarm, ammo, hp, "under fire".
The player's true position is never given to the agent. (Scoring fields `intruder_in_factory` and `damage_dealt_now` go to the score only, not to the belief or the policy.)

### Layers
1. **World model** (`zones.py`, `belief.py`): the premises as a graph of 13 zones (yard, rolling floor, loft, catwalk, storeroom, boiler room, warehouse...).
   Real NavMesh path lengths and measured line of sight come from Unity (`Tools > Guard Agent > Export Zone Graph`), otherwise hand-made defaults are used.
   The belief is a probability per zone: it diffuses along the graph towards tea bags and the gate (the player's goals), collapses on a sighting, loses mass in the zones the guard looks at and does not see him in, and gains mass around noises.
2. **Strategic policy** (`train.py`): PPO, MLP 2x128, 16 discrete actions: go to zone k (13), engage, cover, reload.
   Trained in a fast simulator (`sim/factory_env.py`) against randomised intruders (stealth / standard / rusher, random speeds, aim, bag order, ring position) with the same constants as the game.
3. **Reflexes** (`brain.py`): hard rules that override the network: magazine empty -> reload; badly hurt and under fire -> cover; visible target in range with a clear shot -> engage (the network decides *where to be*, the reflex decides *when to shoot*).
4. **Planner**: A* over the zone graph gives the route and ETA; Unity's NavMesh does the footsteps.
5. **Learning between games** (`opponent_model.py`): Dirichlet counts of where the player crossed the fence and where the guard lost him. They shape the belief prior of the next game (saved in `models/opponent_memory.json`).

### Why these choices
* A pure rule-based guard (what the game ships) cannot reason about hidden information; the belief filter does.
* Pure RL on raw positions would need millions of slow Unity episodes; the zone abstraction plus a simulator lets PPO train in minutes, and the exported graph keeps the simulator faithful to the real level.
* Reflexes keep the NPC safe and believable even if the network chooses badly in a situation it never saw.
* The score is the reward, so "increasing his score" is literally what training optimises; online, the opponent model keeps adapting.

## Files
| File | Role |
|---|---|
| `guard_agent/config.py` | game constants + score weights |
| `guard_agent/zones.py` | zone graph, A*, loader of the Unity export |
| `guard_agent/belief.py` | Bayes filter |
| `guard_agent/sim/` | Gymnasium environment and scripted intruders |
| `guard_agent/baselines.py` | patrol guard (like the game), utility planner (also the runtime fallback), random guard |
| `guard_agent/brain.py` | runtime agent (perception -> belief -> policy -> reflexes -> command) |
| `guard_agent/server.py` | TCP server for Unity |
| `train.py`, `evaluate.py` | PPO training; comparison with baselines per intruder style |
| `Assets/_Project/Scripts/Runtime/GuardAgent/GuardAgentBridge.cs` | Unity side: sensors, executor, HUD |
| `Assets/_Project/Scripts/Editor/GuardAgent/GuardAgentTools.cs` | `Tools > Guard Agent`: attach, detach, export zone graph |

## Run it
```powershell
cd GuardAgent
.venv\Scripts\python train.py --steps 1000000      # ~25 min on CPU, writes models\guard_ppo.zip
.venv\Scripts\python evaluate.py                    # score table vs the baselines
.venv\Scripts\python -m guard_agent.server          # start the brain (port 5555)
```
In Unity: open the level, `Tools > Guard Agent > Attach Agent To Guard`, press Play. The top-right HUD shows the guard's score, mode, reason and belief.

## Gunshot hearing
The game has no public 'shot fired' event, so `GuardAgentBridge.PollGunshots` watches the player's magazine (it drops by one on every shot). A shot within `gunshotRadius` (default 40 m, the game's own gunshot noise radius) is heard through walls, whether or not the guard can see the player. It is sent as a `gunshot` noise (louder when closer) and the guard decides at once instead of waiting for the next tick.
The brain blurs the position (3 m + 20% of the distance, like a real direction/distance estimate), widens the belief update (a shot is audible from far away but pins him down less than footsteps), and the HUD shows GUNSHOT HEARD for 3 s.

## Agent-mode guard stats
The stock guard (80 hp, 5 damage per shot, one shot every 1.3 s) cannot hurt a 100 hp player in a straight fight, so a damage-scored agent would have nothing to optimise.
The agent-mode guard has 100 hp, 8 damage, 1.0 s between shots, 12-round magazine (`config.GuardStats` and the matching inspector fields on `GuardAgentBridge`). Change both together, then retrain.

## Known limits
* The simulator is an abstraction (zones, expected combat), not the game: the exported graph and `evaluate.py` show how well it transfers; the final test is playing against the real guard.
* One guard has the brain. Other guards keep the stock AI (they are alerted by the alarm and by gunfire as before).
* The simulator does not model gunshot noise yet (the intruder only fires when he sees the guard); the policy was trained without it. The belief still uses real gunshots at runtime. Retrain after adding it to `sim/` if you want the policy to learn to react to far-off shots.

## Troubleshooting (found during the first live test)
* **The guard just stands there / ticks stay at 0:** Unity pauses the game loop while its window is not focused unless *Run In Background* is on (Project Settings > Player). Click the Game view, or tick it. The inspector on `GuardAgentBridge` shows `linkStatus`, `debug`, `ticksSent` and `actionsReceived`.
* **The game stays on the intro screen:** the guard only acts while the game is in the Playing phase (start it normally).
* **Start order:** start the brain first (`python -m guard_agent.server`); the bridge retries the connection every 3 s and hands control back to the stock `EnemyAI` whenever the brain is not connected.
* Unity's `JsonUtility` works only on the main thread: the network thread keeps the newest raw reply and `Update` parses it.

## First live result (Level_01, Guard_14)
Player placed in the open yard at full health: the brain took over, the alarm went off, the guard ran to the yard, engaged and landed hits (score 25 after 3 hits) while the other, stock guards also fought. The zone graph exported from the real NavMesh (13 zones, all connected) was used for the final training; see `logs/evaluate_real_graph.txt` (mixed intruders: score -11.7 vs -50.6 for the game's patrol guard, -42.7 for the utility planner).
