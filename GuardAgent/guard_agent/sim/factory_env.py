"""Gymnasium environment: the guard defends the premises against a randomised intruder, one decision per second.

Observation (all things the guard can actually perceive or has computed himself, never the intruder's true position):
  guard zone (one-hot) | belief over the intruder's zone | last seen zone (one-hot) | visible, last-seen age, alarm, intruder hp (as last seen),
  guard hp, ammo, reloading, under fire recently, belief mass inside the factory, time fraction
Actions: go to zone k (n zones) | ENGAGE | COVER | RELOAD
Reward: the score of the brief (see config.ScoreWeights): + damage dealt, - time the intruder spends inside the factory building, big terms for kill / breach / escape / death.
"""
from __future__ import annotations

import gymnasium as gym
import numpy as np
from gymnasium import spaces

from ..belief import BeliefTracker
from ..config import DECISION_DT, GUARD, PLAYER, SCORE
from ..zones import ENTRY_ZONE, EXIT_ZONE, OUTSIDE, ZoneGraph, load_graph
from .intruder import Intruder

MAX_STEPS = 300


class FactoryDefenseEnv(gym.Env):
    metadata = {"render_modes": []}

    def __init__(self, zg: ZoneGraph | None = None, style=None, seed=None):
        super().__init__()
        self.zg = zg or load_graph()
        self.n = self.zg.n
        self.style = style
        self.A_ENGAGE, self.A_COVER, self.A_RELOAD = self.n, self.n + 1, self.n + 2
        self.action_space = spaces.Discrete(self.n + 3)
        self.obs_dim = 3 * self.n + 9
        self.observation_space = spaces.Box(-1.0, 1.0, shape=(self.obs_dim,), dtype=np.float32)
        self.belief = BeliefTracker(self.zg)
        self._seed = seed
        self.reset(seed=seed)

    # ------------------------------------------------------------------ helpers
    def _p_see(self, gz: int, iz: int, stealth: float, alerted: bool) -> float:
        base = 0.85 * (1.4 if alerted else 1.0) * stealth
        return float(min(0.95, base * self.zg.los[gz, iz]))

    def _p_see_vector(self, gz: int, alerted: bool) -> np.ndarray:
        return np.array([self._p_see(gz, z, 1.0, alerted) for z in range(self.n)])

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        rng = self.np_random
        self.rng = rng
        zg = self.zg
        self.intruder = Intruder(zg, rng, self.style)
        self.guard_zone = zg.idx(rng.choice(["yard_w", "yard_e", "rolling", "storeroom", "compound", "north_lane"]))
        self.g_prev = self.guard_zone
        self.g_path: list[int] = []
        self.g_edge = 0.0
        self.g_hp = GUARD.hp
        self.ammo = GUARD.gun_mag
        self.reload_left = 0.0
        self.shot_cd = 0.0
        self.aim_left = GUARD.gun_aim + 0.8
        self.alarm = False
        self.visible = False
        self.under_fire = 0.0
        self.t = 0
        self.damage_dealt = 0.0
        self.breached = False
        self.last_i_hp = 1.0
        self.in_cover = False
        self.react_left = 1.2             # the intruder needs a moment to return fire after the guard first spots him
        self.unaware = True               # until he has seen the guard (ambush bonus on the guard's first burst)
        self.belief.reset(np.where(np.arange(self.n) == zg.idx(OUTSIDE), 3.0, 1.0))
        self.info_score = 0.0
        return self._obs(), {}

    def _obs(self):
        n = self.n
        o = np.zeros(self.obs_dim, np.float32)
        o[self.guard_zone] = 1.0
        o[n:2 * n] = self.belief.b
        if self.belief.last_seen_zone >= 0:
            o[2 * n + self.belief.last_seen_zone] = 1.0
        k = 3 * n
        o[k + 0] = 1.0 if self.visible else 0.0
        o[k + 1] = min(1.0, self.belief.last_seen_age / 60.0)
        o[k + 2] = 1.0 if self.alarm else 0.0
        o[k + 3] = self.last_i_hp
        o[k + 4] = max(0.0, self.g_hp / GUARD.hp)
        o[k + 5] = self.ammo / GUARD.gun_mag
        o[k + 6] = 1.0 if self.reload_left > 0 else 0.0
        o[k + 7] = min(1.0, self.under_fire / 3.0)
        o[k + 8] = self.belief.mass_protected()
        return np.clip(o, -1, 1)

    # ------------------------------------------------------------------ guard movement
    def _dist_to_intruder(self):
        iz = self.intruder
        return 8.0 if iz.zone == self.guard_zone else float(self.zg.dist[self.guard_zone, iz.zone])

    def _potential(self):
        """Shaping potential: being close (in walking metres) to where the belief says the intruder is, weighted towards the factory."""
        w = self.belief.b * (1.0 + 0.8 * self.zg.protected)
        return -float(np.dot(w, self.zg.dist[self.guard_zone])) / 40.0

    def _move_guard(self, dt):
        speed = GUARD.run if self.alarm else GUARD.walk * 1.4
        remaining = speed * dt
        while remaining > 0 and self.g_path:
            nxt = self.g_path[0]
            L = max(self.zg.dist[self.g_prev, nxt], 1.0)
            take = min(remaining, L - self.g_edge)
            self.g_edge += take
            remaining -= take
            if self.g_edge >= L - 1e-6:
                self.guard_zone, self.g_prev, self.g_edge = nxt, nxt, 0.0
                self.g_path.pop(0)
            elif self.g_edge > 0.5 * L:
                self.guard_zone = nxt

    def _set_goto(self, k):
        if self.g_path and self.g_path[-1] == k:
            return
        if self.guard_zone == k and not self.g_path:
            return
        p = self.zg.path(self.guard_zone, k)
        self.g_path = p[1:] if p and p[0] == self.guard_zone else p
        self.g_prev, self.g_edge = self.guard_zone, 0.0

    # ------------------------------------------------------------------ step
    def step(self, action):
        zg, rng, dt = self.zg, self.rng, DECISION_DT
        iz = self.intruder
        reward = SCORE.step
        action = int(action)
        self.t += 1
        self.shot_cd = max(0.0, self.shot_cd - dt)
        self.under_fire = max(0.0, self.under_fire - dt)
        self.in_cover = action == self.A_COVER

        # ---- reflex (same rule as the runtime brain): a visible intruder in range with a clear shot is engaged, unless the guard chose cover or reload
        if self.visible and action < self.n and self.ammo > 0 and self.reload_left <= 0 and self._dist_to_intruder() <= GUARD.gun_range:
            action = self.A_ENGAGE
        phi_before = self._potential()
        # ---- guard acts (movement)
        moving = action < self.n
        if moving:
            self._set_goto(action)
            self._move_guard(dt)
        else:
            if self.visible is False and action == self.A_ENGAGE and self.belief.last_seen_zone >= 0 and self.belief.last_seen_age < 8 and self.guard_zone != self.belief.last_seen_zone:
                self._set_goto(self.belief.last_seen_zone)            # advance on the last known position
                self._move_guard(dt)
            else:
                self.g_path = []
        if action == self.A_RELOAD or (self.ammo <= 0 and self.reload_left <= 0):
            if self.ammo < GUARD.gun_mag and self.reload_left <= 0:
                self.reload_left = GUARD.gun_reload
        if self.reload_left > 0:
            self.reload_left -= dt
            if self.reload_left <= 0:
                self.ammo = GUARD.gun_mag

        # ---- intruder acts
        iz_sees_guard = rng.random() < self._p_see(iz.zone, self.guard_zone, 1.0, False)
        if iz_sees_guard:
            self.unaware = False
        noise_zone = iz.step(dt, self.guard_zone, iz_sees_guard)
        if iz.exited:
            reward += SCORE.escape
            return self._finish(reward, terminated=True, why="escape")
        premises = not (iz.zone in (zg.idx(OUTSIDE), zg.idx(EXIT_ZONE)))
        if premises and not self.alarm:
            self.alarm = True                                            # entering the premises sets off the alarm
            entry = zg.idx(ENTRY_ZONE) if iz.zone != zg.idx("yard_w") else iz.zone
            self.belief.inject(entry, 0.7)
        in_factory = bool(zg.protected[iz.zone])
        if in_factory:
            reward += SCORE.inside_per_s * dt
            if not self.breached:
                self.breached = True
                reward += SCORE.breach

        # ---- belief: predict, then fold in what the guard perceives
        self.belief.predict(dt)
        sees = rng.random() < self._p_see(self.guard_zone, iz.zone, iz.style.stealth * (1.15 if iz.sprinting else 1.0), self.alarm)
        self.visible = bool(sees)
        if noise_zone is not None and rng.random() < np.exp(-zg.dist[self.guard_zone, noise_zone] / 18.0):
            self.belief.observe_noise(noise_zone, 0.7)
        if self.visible:
            self.belief.observe_visible(iz.zone)
            self.last_i_hp = max(0.0, iz.hp / PLAYER.hp)
        else:
            self.belief.observe_not_visible(self._p_see_vector(self.guard_zone, self.alarm))

        # ---- combat
        dist = 8.0 if iz.zone == self.guard_zone else float(zg.dist[self.guard_zone, iz.zone])
        in_range = dist <= GUARD.gun_range
        can_fire = (self.visible and in_range and not moving and self.reload_left <= 0 and self.ammo > 0 and action in (self.A_ENGAGE, self.A_COVER, self.A_RELOAD) and action != self.A_RELOAD)
        if can_fire:
            if self.aim_left > 0:
                self.aim_left -= dt                                       # wind-up before the first shot after spotting him
            else:
                rate = 1.0 / GUARD.gun_interval * (0.5 if self.in_cover else 1.0)
                shots = int(rng.poisson(rate * dt))
                shots = min(shots, self.ammo)
                p_hit = np.clip(GUARD.gun_accuracy * (1.5 if self.unaware else 1.0) * (1 - 0.5 * dist / GUARD.gun_range) * (0.75 if iz.style.stealth < 0.7 else 1.0) * (1.15 if self.alarm else 1.0), 0.05, 0.95)
                hits = int(rng.binomial(shots, p_hit))
                self.ammo -= shots
                dmg = hits * GUARD.gun_damage
                dmg = min(dmg, iz.hp)
                iz.hp -= dmg
                self.damage_dealt += dmg
                reward += SCORE.damage * dmg
        else:
            self.aim_left = GUARD.gun_aim + (0.8 if not self.visible else 0.0)
        # the intruder shoots back if he sees the guard and decides to fight
        if self.visible:
            self.react_left = max(0.0, self.react_left - dt)
        else:
            self.react_left = 1.2
        if iz_sees_guard and self.visible and in_range and self.react_left <= 0 and rng.random() < iz.style.fight:
            mod = 0.5 if self.in_cover else 1.0
            hits = rng.poisson(iz.hits_per_s * mod * dt)
            if hits:
                self.g_hp -= hits * PLAYER.pistol_damage
                self.under_fire = 3.0
        if iz.hp <= 0:
            reward += SCORE.kill_bonus
            return self._finish(reward, terminated=True, why="kill")
        if self.g_hp <= 0:
            reward += SCORE.death
            return self._finish(reward, terminated=True, why="guard_down")
        shaping = 0.99 * self._potential() - phi_before        # potential-based shaping: helps learning, not part of the score
        reward += shaping
        trunc = self.t >= MAX_STEPS
        self.info_score += reward - shaping
        info = {"damage": self.damage_dealt, "in_factory": in_factory}
        if trunc:
            info.update(why="timeout", score=self.info_score, breached=self.breached, t=self.t)
        return self._obs(), reward, False, trunc, info

    def _finish(self, reward, terminated, why):
        self.info_score += reward
        return self._obs(), reward, terminated, False, {"damage": self.damage_dealt, "why": why, "score": self.info_score, "breached": self.breached, "t": self.t}
