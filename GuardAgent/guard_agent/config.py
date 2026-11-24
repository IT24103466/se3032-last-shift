"""Game constants the agent knows about (copied from the Unity scripts, so the simulator matches the game) and the score definition."""
from dataclasses import dataclass


@dataclass(frozen=True)
class GuardStats:
    """'Agent mode' guard. The stock guard (EnemyAI.Configure: hp 80, 5 damage per shot every 1.3 s, 8-round magazine) cannot realistically hurt a 100 hp player
    in a straight fight, so an agent that is scored on damage would have nothing to learn. The Unity bridge exposes the same numbers as inspector fields (GuardAgentBridge)
    and the simulator is trained on exactly these, so change them in both places together."""
    hp: float = 100.0
    walk: float = 1.8
    run: float = 5.2
    sight: float = 22.0
    fov_deg: float = 120.0
    hearing: float = 12.0
    gun_range: float = 22.0
    gun_damage: float = 8.0
    gun_interval: float = 1.0
    gun_aim: float = 0.5
    gun_accuracy: float = 0.65
    gun_mag: int = 12
    gun_reload: float = 1.8
    pellets: int = 1


@dataclass(frozen=True)
class PlayerStats:         # PlayerController / PlayerGun
    hp: float = 100.0
    walk: float = 3.6
    sprint: float = 6.4
    crouch: float = 1.9
    pistol_damage: float = 25.0
    pistol_interval: float = 0.22
    pistol_mag: int = 12
    pistol_reload: float = 1.5


@dataclass(frozen=True)
class ScoreWeights:
    """Score the guard is trying to maximise (also the RL reward).
    damage       : +1 per hit point of damage dealt to the intruder (the number the player sees as 'score')
    kill_bonus   : intruder defeated
    inside_per_s : intruder is inside the factory building (protected zones), per second
    breach       : intruder enters the factory building for the first time
    escape       : intruder gets out with the ring
    death        : the guard is killed
    step         : tiny time cost, stops the agent idling
    bag_search_per_s : (tea keeper) per second the intruder spends rummaging in a tea bag
    bag_taken    : (tea keeper) per tea bag the intruder gets to search
    contact      : (lookout) the first sighting after he had been lost for a while: raising the alert
    track_per_s  : (lookout) per second he keeps the intruder in sight
    """
    damage: float = 1.0
    kill_bonus: float = 40.0
    inside_per_s: float = -0.6
    breach: float = -15.0
    escape: float = -60.0
    death: float = -30.0
    step: float = -0.01
    bag_search_per_s: float = 0.0
    bag_taken: float = 0.0
    contact: float = 0.0
    track_per_s: float = 0.0


GUARD = GuardStats()
PLAYER = PlayerStats()
SCORE = ScoreWeights()
DECISION_DT = 1.0           # seconds per strategic decision in the simulator (the Unity bridge decides at 2 Hz and the brain re-plans each tick)


# ---------------------------------------------------------------------------------------------------- the people and their jobs
SHOTGUN_WORKER = GuardStats(hp=80.0, walk=1.4, run=4.6, sight=14.0, fov_deg=110.0, hearing=9.0, gun_range=10.0, gun_damage=9.0, gun_interval=1.6, gun_aim=0.6,
                            gun_accuracy=0.5, gun_mag=2, gun_reload=2.2, pellets=5)
"""Worker with the double-barrel: 5 pellets x 9 damage, deadly inside ~6 m, almost useless beyond 10 m, two shots then a long reload (stock worker: 55 hp, 6 damage)."""


@dataclass(frozen=True)
class RoleSpec:
    name: str
    person: str                      # "guard" | "worker": decides the weapon and the body
    title: str
    goal: str
    stats: GuardStats
    weights: ScoreWeights
    focus: tuple                     # zones he is responsible for (None = every protected zone); the belief mass on them is part of his observation and his shaping
    starts: tuple                    # zones he may start the simulated game in
    engage_range: float              # a visible intruder closer than this is shot at automatically (the reflex); the lookout keeps his distance
    cover_hp: float                  # below this fraction of health, under fire, he takes cover
    patrol: tuple                    # route of the game's own scripted guard, used as the baseline in evaluate.py
    model: str


ROLES = {
    "sentinel": RoleSpec(
        "sentinel", "guard", "Sentinel", "Keep the intruder out of the factory building and put him down (damage, kill, no breach).",
        GUARD, SCORE, None, ("yard_w", "yard_e", "rolling", "storeroom", "compound", "north_lane"), GUARD.gun_range, 0.25,
        ("yard_w", "rolling", "storeroom", "yard_e", "compound", "north_lane"), "guard_ppo.zip"),
    "gatekeeper": RoleSpec(
        "gatekeeper", "guard", "Gatekeeper", "Stop the escape: nobody leaves with the ring through the main gate. Hold the yard and the gate, cut him off, punish a getaway hard.",
        GUARD, ScoreWeights(damage=1.0, kill_bonus=60.0, inside_per_s=-0.1, breach=-3.0, escape=-120.0, death=-30.0, step=-0.01),
        ("gate_road", "yard_w", "yard_e"), ("yard_w", "gate_road", "yard_e", "compound"), GUARD.gun_range, 0.25,
        ("gate_road", "yard_w", "yard_e", "yard_w"), "gatekeeper_ppo.zip"),
    "tea_keeper": RoleSpec(
        "tea_keeper", "worker", "Tea keeper", "Protect the tea stock: stop the intruder rummaging in the bags. Shotgun ambush at the bag rooms: close range only.",
        SHOTGUN_WORKER, ScoreWeights(damage=1.0, kill_bonus=40.0, inside_per_s=0.0, breach=-2.0, escape=-20.0, death=-40.0, step=-0.01, bag_search_per_s=-1.0, bag_taken=-12.0),
        ("warehouse", "loft", "storeroom", "line_houses"), ("storeroom", "loft", "warehouse", "rolling", "line_houses"), 7.0, 0.4,
        ("storeroom", "rolling", "storeroom", "boiler"), "tea_keeper_ppo.zip"),
    "lookout": RoleSpec(
        "lookout", "worker", "Lookout", "Spot, report and survive: find the intruder first, keep him in sight from a safe distance, feed the team radio, avoid duels unless cornered.",
        SHOTGUN_WORKER, ScoreWeights(damage=0.3, kill_bonus=15.0, inside_per_s=-0.1, breach=-3.0, escape=-10.0, death=-70.0, step=-0.01, contact=12.0, track_per_s=0.6),
        None, ("rolling", "loft", "boiler", "line_houses", "compound", "storeroom"), 6.0, 0.6,
        ("rolling", "boiler", "storeroom", "loft"), "lookout_ppo.zip"),
}
