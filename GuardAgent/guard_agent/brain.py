"""GuardBrain: the runtime agent. One call per decision tick from Unity.

  percept (JSON from the game)
     -> Perception : guard position -> zone, what he can see/hear, damage taken, alarm, ammo, hp
     -> WorldModel : Bayes-filtered belief over where the intruder is (belief.py) + opponent model prior (opponent_model.py)
     -> Policy     : trained PPO network (stable-baselines3) choosing a tactical goal; falls back to the utility planner if no model is present
     -> Reflexes   : hard rules that always win (reload when empty, take cover when nearly dead and under fire, shoot when he has a clear shot)
     -> Planner    : A* over the zone graph gives the route and ETA; Unity's NavMesh does the footsteps
  action (JSON): mode goto / engage / cover / reload, the world position to go to, run or walk, a short reason string.

Score: the brain keeps the same score the policy was trained on (damage dealt - time inside the factory - breach/escape/death penalties),
using the scoring fields the game reports. Those fields are NOT fed to the belief or the policy: the agent plays with what it perceives.
"""
from __future__ import annotations

import os
import time

import numpy as np

from .baselines import UtilityGuard, decode
from .belief import BeliefTracker
from .config import GUARD, SCORE
from .opponent_model import OpponentModel
from .zones import ENTRY_ZONE, ZoneGraph, load_graph


class GuardBrain:
    def __init__(self, model_path: str | None = None, zg: ZoneGraph | None = None, memory_path: str | None = None):
        self.zg = zg or load_graph()
        self.n = self.zg.n
        self.belief = BeliefTracker(self.zg)
        self.memory = OpponentModel(self.zg, memory_path)
        self.model = None
        self.fallback = UtilityGuard(self.zg)
        if model_path and os.path.exists(model_path):
            from stable_baselines3 import PPO
            self.model = PPO.load(model_path, device="cpu")
        self.A_ENGAGE, self.A_COVER, self.A_RELOAD = self.n, self.n + 1, self.n + 2
        self.new_game()

    # ------------------------------------------------------------------ lifecycle
    def new_game(self):
        self.belief.reset(self.memory.prior())
        self.alarm = False
        self.visible = False
        self.under_fire = 0.0
        self.i_hp = 1.0
        self.score = 0.0
        self.damage_dealt = 0.0
        self.breached = False
        self.t0 = time.time()
        self.prev_ammo = GUARD.gun_mag
        self.last_t = None
        self.committed = None            # (zone, until) hysteresis so the brain does not dither between goals
        self.last_mode = "goto"
        self.last_obs = None

    def end_game(self, outcome: str):
        """outcome: kill | escape | guard_down | timeout. Learns from the game and saves memory."""
        if outcome == "escape":
            self.score += SCORE.escape
        elif outcome == "kill":
            self.score += SCORE.kill_bonus
        elif outcome == "guard_down":
            self.score += SCORE.death
        self.memory.end_game(self.score)
        return self.score

    # ------------------------------------------------------------------ perception -> belief -> observation vector
    def _update(self, p: dict):
        zg, n = self.zg, self.n
        dt = float(p.get("dt", 0.5))
        gx, gz = p["guard"]["pos"]
        self.guard_zone = zg.zone_of(gx, gz)
        self.guard = p["guard"]
        self.alarm = bool(p.get("alarm", False)) or self.alarm
        self.under_fire = max(0.0, self.under_fire - dt) if not p.get("damage_taken", 0) else 3.0
        self.visible = bool(p.get("player_visible", False))
        ae = p.get("alarm_event_pos")
        if ae and len(ae) == 2:                                     # the alarm just went off: where he crossed in is the best clue we will get
            ez = zg.zone_of(*ae)
            self.belief.inject(ez, 0.7)
            self.memory.record_entry(ez)
        self.belief.predict(dt)
        for ev in p.get("noises", []):
            self.belief.observe_noise(zg.zone_of(*ev["pos"]), float(ev.get("strength", 0.7)))
        if self.visible:
            pz = zg.zone_of(*p["player_pos"])
            self.belief.observe_visible(pz)
            self.i_hp = float(p.get("player_hp_frac", self.i_hp))
        else:
            if self.belief.last_seen_age < 1.0:
                self.memory.record_lost(self.belief.last_seen_zone)
            pv = np.array([min(0.95, 0.85 * (1.4 if self.alarm else 1.0) * zg.los[self.guard_zone, z]) * (1 - np.exp(-dt)) for z in range(n)])
            self.belief.observe_not_visible(pv)
        # score bookkeeping (uses the game's scoring fields only)
        sc = p.get("score_info", {})
        d = float(sc.get("damage_dealt_now", 0.0))
        self.damage_dealt += d
        self.score += SCORE.damage * d + SCORE.step * dt
        if sc.get("intruder_in_factory"):
            self.score += SCORE.inside_per_s * dt
            if not self.breached:
                self.breached = True
                self.score += SCORE.breach

    def observation(self) -> np.ndarray:
        n, b = self.n, self.belief
        o = np.zeros(3 * n + 9, np.float32)
        o[self.guard_zone] = 1.0
        o[n:2 * n] = b.b
        if b.last_seen_zone >= 0:
            o[2 * n + b.last_seen_zone] = 1.0
        k = 3 * n
        reloading = bool(self.guard.get("reloading", False))
        o[k] = 1.0 if self.visible else 0.0
        o[k + 1] = min(1.0, b.last_seen_age / 60.0)
        o[k + 2] = 1.0 if self.alarm else 0.0
        o[k + 3] = self.i_hp
        o[k + 4] = max(0.0, self.guard.get("hp", GUARD.hp) / GUARD.hp)
        o[k + 5] = self.guard.get("ammo", GUARD.gun_mag) / GUARD.gun_mag
        o[k + 6] = 1.0 if reloading else 0.0
        o[k + 7] = min(1.0, self.under_fire / 3.0)
        o[k + 8] = b.mass_protected()
        return np.clip(o, -1, 1)

    # ------------------------------------------------------------------ decision
    def decide(self, percept: dict) -> dict:
        self._update(percept)
        o = self.observation()
        self.last_obs = o
        d = decode(o, self.n)
        reason = ""
        # ---- reflexes (always win)
        if d["ammo"] <= 0.01 and not d["reloading"]:
            a, reason = (self.A_COVER if self.visible and d["fire"] > 0.3 else self.A_RELOAD), "magazine empty"
        elif self.visible and d["hp"] < 0.25 and d["fire"] > 0.3:
            a, reason = self.A_COVER, "badly hurt and under fire"
        else:
            if self.model is not None:
                a = int(self.model.predict(o, deterministic=True)[0])
                reason = "learned policy"
            else:
                a, reason = self.fallback.act(o), "utility planner"
            # a visible intruder in range with a clear shot is engaged unless the policy chose to hold cover
            if self.visible and a < self.n and percept.get("clear_shot", False) and d["ammo"] > 0.01 and a not in (self.zg.idx("tea_field"),):
                a, reason = self.A_ENGAGE, reason + " + reflex: clear shot"
        return self._to_command(a, d, reason)

    def _to_command(self, a: int, d: dict, reason: str) -> dict:
        zg = self.zg
        if a < self.n:
            now = time.time()
            if self.committed and self.committed[0] != a and now < self.committed[1] and not self.visible:
                a = self.committed[0]                     # stay on the goal we picked a moment ago unless something new appears
            else:
                self.committed = (a, now + 2.0)
            route = zg.path(self.guard_zone, a)
            eta = float(sum(zg.dist[route[i], route[i + 1]] for i in range(len(route) - 1))) / (GUARD.run if self.alarm else GUARD.walk * 1.4)
            cx, cz = zg.centre(a)
            tgt = self.zone_point(a)
            cmd = dict(mode="goto", zone=zg.names[a], target=[tgt[0], tgt[1]], run=bool(self.alarm or self.visible), route=[zg.names[r] for r in route], eta=round(eta, 1))
        elif a == self.A_ENGAGE:
            cmd = dict(mode="engage", zone=zg.names[self.guard_zone], target=[], run=False)
            if not self.visible and self.belief.last_seen_zone >= 0 and self.belief.last_seen_age < 8:
                lz = self.belief.last_seen_zone
                cmd.update(mode="goto", zone=zg.names[lz], target=list(self.zone_point(lz)), run=True)
        elif a == self.A_COVER:
            cmd = dict(mode="cover", zone=zg.names[self.guard_zone], target=[], run=False)
        else:
            cmd = dict(mode="reload", zone=zg.names[self.guard_zone], target=[], run=False)
        cmd["why"] = reason
        cmd["score"] = round(self.score, 2)
        cmd["belief_top"] = [f"{zg.names[i]} {self.belief.b[i]:.2f}" for i in np.argsort(-self.belief.b)[:3]]
        self.last_mode = cmd["mode"]
        return cmd

    def zone_point(self, k: int):
        """World point to walk to for zone k: the exported NavMesh-snapped centre if the graph has one, else the rectangle centre."""
        pts = getattr(self.zg, "points", None)
        if pts is not None:
            return pts[k]
        return self.zg.centre(k)
