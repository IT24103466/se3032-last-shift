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
    """
    damage: float = 1.0
    kill_bonus: float = 40.0
    inside_per_s: float = -0.6
    breach: float = -15.0
    escape: float = -60.0
    death: float = -30.0
    step: float = -0.01


GUARD = GuardStats()
PLAYER = PlayerStats()
SCORE = ScoreWeights()
DECISION_DT = 1.0           # seconds per strategic decision in the simulator (the Unity bridge decides at 2 Hz and the brain re-plans each tick)
