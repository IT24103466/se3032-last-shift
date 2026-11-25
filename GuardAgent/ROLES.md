# The people in the factory: one agent architecture, four roles

Every guard and worker with a brain runs the **same** architecture (see `ARCHITECTURE.md`): perception -> Bayes belief -> trained PPO policy -> reflexes -> A* planner.
What differs is the **role**: the weapon, the goal (score weights), the zones he is responsible for, and when he is allowed to fight. A role is one entry in `guard_agent/config.py` (`ROLES`) and one policy (`models/<role>_ppo.zip`).

| Role | Who in Level_01 | Weapon | Goal | What he is paid / punished for |
|---|---|---|---|---|
| **Sentinel** | Guard_14 (factory), Guard_13 (back lane), Guard_12 (warehouse) | pistol, 100 hp | keep him out of the factory building, put him down | +1 per damage, +40 kill, -0.6 per second inside the factory, -15 breach, -60 escape, -30 death |
| **Gatekeeper** | Guard_10 (yard), Guard_11 (east compound) | pistol, 100 hp | stop the escape through the main gate; hold the yard and gate | +1 damage, +60 kill, **-120 escape**, -30 death, small factory terms |
| **Tea keeper** | Worker_17 (storeroom), Worker_18 (line houses) | double-barrel, 80 hp (5 pellets x 9, deadly under ~6 m, 2 shots then a long reload) | protect the tea stock; ambush at the bag rooms | +1 damage, **-1 per second he rummages a bag, -12 per bag searched**, -40 death |
| **Lookout** | Worker_15 (rolling floor), Worker_16 (loft) | double-barrel, 80 hp, but only fires when cornered (< 6 m) | spot, report, survive; keep him in sight from a safe distance and feed the radio | **+12 first contact after he was lost, +0.6 per second in sight**, +0.3 damage, **-70 death** |

The Hunter keeps the game's own AI (he never enters the factory).

## What is shared and what is not
* **Team radio** (`team.py`): a sighting by anyone is broadcast as "intruder in zone Z, N seconds ago". The others fold it into their own belief (weight 0.45, fading with age) only if they do not see him themselves.
  A lookout who spots him therefore pulls the sentinels and gatekeepers towards him: that is the lookout's contribution to the team's score.
* **Opponent model**: one for the whole factory (where the player crosses the fence, where he gets lost).
* **Not shared**: each person's belief, ammo, health, score and policy. Each has his own connection and his own `GuardBrain` on the server.

## Why the roles behave differently (same code, different incentives)
* The Lookout is punished 70 for dying and paid for keeping the intruder in sight, so the policy learns to stay at distance and avoid duels; the reflex only lets him shoot inside 6 m.
* The Tea keeper is punished for every second a bag is being rifled, so his belief and potential shaping are weighted towards the bag rooms and his short-range shotgun makes ambush at those rooms the only sensible tactic.
* The Gatekeeper's escape penalty is twice the sentinel's, so he learns to hold the yard and the gate rather than chase into the factory.

## Results (simulator, 300 games per row, mixed intruders; `logs/evaluate_<role>.txt`)
| Role | Game's patrol | Utility planner | **Trained** | Reading |
|---|---|---|---|---|
| Sentinel | -51 | -43 | **-12** | damage 25 -> 46, escapes 84% -> 50% |
| Gatekeeper | -78 | -66 | **-23** | escapes 83% -> 57%, damage 28 -> 46 |
| Tea keeper | -55 | -40 | **-22** | damage 16 -> 42; bags rummaged **unchanged** (about 2.8 of 5): honest weak point |
| Lookout | -0.5 | 17 | **34** | contacts 0.9 -> 1.6 per game, guard down only 4%; he does not stop the escape, by design |

## Using it
```powershell
cd GuardAgent
.venv\Scripts\python -m guard_agent.server          # one server for everybody (port 5555)
```
Unity: `Tools > Guard Agent > Attach Agents To Everyone In The Factory`, then Play. Each person has a `GuardAgentBridge` with a **Role** dropdown (and optional focus zones); changing it changes his job. The HUD (top right, F8 hides it) shows one line per person.
Retrain one role: `.venv\Scripts\python train.py --role lookout --steps 900000` then `evaluate.py --role lookout`.

## Limits
* The tea keeper's close-range shotgun deals damage but does not stop the rummaging in the simulator: the intruder only needs 3.5 s per bag. A next step is giving him a tactic that interrupts the search (the player being shot while searching).
* Roles are trained one at a time against a scripted intruder, not against each other: team tactics (flanking, covering fire) come from the shared belief only, not from learned coordination.
* Gunshot noise is heard by every bridge at runtime but is not in the simulator.
* The 9 agents were checked live once (all connected, correct roles, engaged the player in the yard, damage and scores consistent); no long-run balance test yet.

## Interior garrison and tougher bodies (added later)
* **Seven of the nine people wait INSIDE the factory building**, at a post, and never leave it by their own choice: Guard_14, Guard_12, Worker_15 (rolling floor), Worker_16 (loft), Worker_17 (storeroom), Guard_13 and Worker_18 (boiler room). Only the two Gatekeepers (Guard_10, Guard_11) hold the yard and the gate outside.
  Guard_12, Guard_13 and Worker_18 used to start outside; `Tools > Guard Agent > Attach Agents To Everyone In The Factory` moves them in (undoable), removes their patrol route and sets `stayInside` + `postZone` on their bridge.
* **How "stay inside" works** (`brain.py`): whatever zone the policy picks, a confined person who would walk to an outside zone is sent to `_best_inside()` instead: the interior zone with the most belief mass, or the one next to the outside zone he is most likely to come through (an ambush spot behind the door), a small bonus for his post, and a penalty for zones his teammates already cover, so the garrison spreads out. If he last saw the intruder outside, he waits at the door instead of chasing. He still shoots at anything he can see, including through an open door.
* **Close-range awareness** (bridge): within 4.5 m he notices the player whichever way he faces; while the alarm is on and he sees nobody he turns slowly on the spot.
* **No clear shot** (brain): if he sees the player inside his range but crates or machines block the muzzle line, he steps towards him for a line instead of standing there.
* **Tougher**: `toughness` x2.5 on the bridge: guards 250 hp, workers 200 hp (the brain still sees health as a fraction of the normal body, so the policies behave as before).
* **Bug fixed on the way**: a person never reloaded (`reloadUntil` started at 0); the 2-shell workers stood empty-handed after their first blast.
* **Live check (Level_01)**: all nine connected; the garrison stayed inside for the whole test with the player in the yard; with the player in the storeroom the garrison (two shotgun workers and the guards) put down a 600-hp player in about 50 s, nobody left the building.
* Not retrained for the higher health: the policies were trained on the normal bodies; they are simply harder to kill in the game. Retraining with `toughness` in the simulator would let them take more risks.
* Known limit: if the brain server is stopped or restarted during play, the stock AI takes over for a few seconds and may chase the player out of the building.

## Current setup in Level_01 (final)
* **One security guard agent**: Guard_14, role Sentinel, roams freely, the full goal set (keep the player out of the factory, put him down, score on damage dealt). The other four guards (Guard_10 to Guard_13) keep the game's own AI, unchanged.
* **Four factory staff agents** (the workers), goals bounded by the factory building, never leave it: Worker_15 and Worker_16 (Lookouts, rolling floor and loft), Worker_17 and Worker_18 (Tea keepers, storeroom and boiler room; Worker_18 was moved in from the line houses).
* Normal bodies (toughness x1, no extra hit points). `Tools > Guard Agent > Attach Agents To Everyone In The Factory` sets exactly this up and is safe to re-run.

## Isolated agents (embedded brain, no server)
* Each agent is now **self-contained**: `GuardAgentBridge.brainMode = Embedded` (the default) runs the brain inside Unity (`EmbeddedBrain.cs`): zone map -> Bayes belief (team radio folded in) -> the trained PPO network -> reflexes -> stay-inside rule -> command. There is **no server and no connection**, so the "guard agent not connected" message cannot happen.
* The trained weights and the zone map are exported from Python into `Assets/_Project/Resources/GuardAgent/` (`policy_<role>.json`, `zone_graph_flat.json`) by `GuardAgent/export_policies.py`. Re-run it after retraining.
* Each person owns his own `EmbeddedBrain` (belief, ammo, score); they share only the radio (`AgentTeam`).
* `brainMode = Remote` keeps the old behaviour (Python server on port 5555, `start_brain.ps1`) for training, debugging and comparing.
