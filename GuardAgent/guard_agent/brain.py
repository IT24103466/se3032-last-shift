"""Brain: the runtime agent of one person (a guard or a worker). One call per decision tick from Unity. Every person has his own brain with his own ROLE.

  percept (JSON from the game)
     -> Perception : own position -> zone, what he can see/hear, damage taken, alarm, ammo, hp
     -> Team radio : what the others reported seeing a moment ago (team.py)
     -> WorldModel : Bayes-filtered belief over where the intruder is (belief.py) + the team's opponent model as prior (opponent_model.py)
     -> Policy     : the role's trained PPO network picks a tactical goal; falls back to the utility planner if there is no model
     -> Reflexes   : hard rules that always win (reload when empty, take cover when hurt, shoot inside the role's engage range)
     -> Planner    : A* over the zone graph gives the route and ETA; Unity's NavMesh does the footsteps
  action (JSON): mode goto / engage / cover / reload, the world position to go to, run or walk, a short reason string.

Roles (config.ROLES): sentinel, gatekeeper (guards, pistol) | tea_keeper, lookout (workers, shotgun). Same architecture, different weapon, goal, score and focus zones.
Score: the role's own weights, from the game's scoring fields only. Those fields are NOT fed to the belief or the policy.
"""
from __future__ import annotations

import os
import time

import numpy as np

from .baselines import UtilityGuard, decode
from .belief import BeliefTracker
from .roles import focus_mask, get_role
from .team import Team
from .zones import ZoneGraph, load_graph


class GuardBrain:
    def __init__(self, model_path: str | None = None, zg: ZoneGraph | None = None, memory_path: str | None = None, role: str = "sentinel",
                 team: Team | None = None, agent_id: str = "agent", focus=None, model=None, confined: bool = False, post: str | None = None):
        self.zg = zg or load_graph()
        self.n = self.zg.n
        self.spec = get_role(role)
        # A confined person is an interior garrison: he never leaves the factory building by his own choice (see _keep_inside)
        self.confined = bool(confined)
        self.allowed = self.zg.protected.copy() if self.confined else np.ones(self.n, bool)
        self.post = self.zg.idx(post) if post in self.zg.names else -1
        self.G, self.W = self.spec.stats, self.spec.weights
        self.role, self.id = self.spec.name, agent_id
        self.focus = focus_mask(self.zg, self.spec, focus)
        self.team = team or Team(self.zg, memory_path)
        self.memory = self.team.memory
        self.team.join(self.id, self.role)
        self.belief = BeliefTracker(self.zg)
        self.model = model
        self.fallback = UtilityGuard(self.zg, role=self.spec)
        self.fallback.focus = self.focus
        if self.model is None and model_path and os.path.exists(model_path):
            from stable_baselines3 import PPO
            self.model = PPO.load(model_path, device="cpu")
        self.A_ENGAGE, self.A_COVER, self.A_RELOAD = self.n, self.n + 1, self.n + 2
        self.rng = np.random.default_rng()
        self.new_game()

    # ------------------------------------------------------------------ lifecycle
    def new_game(self):
        self.belief.reset(self.memory.prior())
        self.alarm = False
        self.visible = False
        self.was_visible = False
        self.under_fire = 0.0
        self.i_hp = 1.0
        self.score = 0.0
        self.damage_dealt = 0.0
        self.breached = False
        self.bags_seen = 0
        self.contacts = 0
        self.heard_gunshot = False
        self.heard_radio = False
        self.committed = None            # (zone, until) hysteresis so the brain does not dither between goals
        self.last_mode = "goto"
        self.last_obs = None
        self.guard = {}
        self.guard_zone = 0

    def end_game(self, outcome: str):
        """outcome: kill | escape | guard_down | timeout. Learns from the game and saves memory."""
        if outcome == "escape":
            self.score += self.W.escape
        elif outcome == "kill":
            self.score += self.W.kill_bonus
        elif outcome == "guard_down":
            self.score += self.W.death
        self.memory.end_game(self.score)
        return self.score

    def close(self):
        self.team.leave(self.id)

    # ------------------------------------------------------------------ perception -> belief -> observation vector
    def _update(self, p: dict):
        zg, n, W = self.zg, self.n, self.W
        dt = float(p.get("dt", 0.5))
        gx, gz = p["guard"]["pos"]
        self.guard_zone = zg.zone_of(gx, gz)
        self.guard = p["guard"]
        self.alarm = bool(p.get("alarm", False)) or self.alarm
        self.under_fire = max(0.0, self.under_fire - dt) if not p.get("damage_taken", 0) else 3.0
        self.was_visible = self.visible
        self.visible = bool(p.get("player_visible", False))
        ae = p.get("alarm_event_pos")
        if ae and len(ae) == 2:                                     # the alarm just went off: where he crossed in is the best clue we will get
            ez = zg.zone_of(*ae)
            self.belief.inject(ez, 0.7)
            self.memory.record_entry(ez)
        age_before = self.belief.last_seen_age
        self.belief.predict(dt)
        self.heard_gunshot = False
        for ev in p.get("noises", []):
            x, z = ev["pos"]
            if ev.get("kind") == "gunshot":
                # he hears THAT a shot was fired and roughly where from: direction/distance are blurred (more with distance)
                d = float(np.hypot(x - gx, z - gz))
                sigma = 3.0 + 0.2 * d
                x, z = x + self.rng.normal(0, sigma), z + self.rng.normal(0, sigma)
                self.belief.observe_noise(zg.zone_of(x, z), float(ev.get("strength", 0.8)) + 0.2, spread=24.0)
                self.heard_gunshot = True
                self.memory.record_lost(zg.zone_of(x, z))
            else:
                self.belief.observe_noise(zg.zone_of(x, z), float(ev.get("strength", 0.7)))
        # team radio: what the others saw a moment ago (only used when he does not see the intruder himself)
        self.heard_radio = False
        if not self.visible:
            for age, zone in self.team.fresh(self.id, 6.0)[:1]:
                self.belief.inject(zone, 0.45 * float(np.exp(-age / 3.0)))
                if age < self.belief.last_seen_age:
                    self.belief.last_seen_zone, self.belief.last_seen_age = zone, age
                self.heard_radio = True
        if self.visible:
            pz = zg.zone_of(*p["player_pos"])
            self.belief.observe_visible(pz)
            self.team.report(self.id, pz)
            self.i_hp = float(p.get("player_hp_frac", self.i_hp))
        else:
            if self.belief.last_seen_age < 1.0:
                self.memory.record_lost(self.belief.last_seen_zone)
            pv = np.array([min(0.95, 0.85 * (1.4 if self.alarm else 1.0) * zg.los[self.guard_zone, z]) * (1 - np.exp(-dt)) for z in range(n)])
            self.belief.observe_not_visible(pv)
        # score bookkeeping (uses the game's scoring fields only, never fed to the belief)
        sc = p.get("score_info", {})
        d = float(sc.get("damage_dealt_now", 0.0))
        self.damage_dealt += d
        self.score += W.damage * d + W.step * dt
        if sc.get("intruder_in_factory"):
            self.score += W.inside_per_s * dt
            if not self.breached:
                self.breached = True
                self.score += W.breach
        if sc.get("intruder_searching_bag"):
            self.score += W.bag_search_per_s * dt
        bags = int(sc.get("bags_searched_total", 0))
        if bags > self.bags_seen:
            self.score += W.bag_taken * (bags - self.bags_seen)
            self.bags_seen = bags
        if self.visible:
            self.score += W.track_per_s * dt
            if not self.was_visible and age_before > 25.0:
                self.contacts += 1
                self.score += W.contact

    def observation(self) -> np.ndarray:
        n, b, G = self.n, self.belief, self.G
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
        o[k + 4] = max(0.0, self.guard.get("hp", G.hp) / G.hp)
        o[k + 5] = self.guard.get("ammo", G.gun_mag) / G.gun_mag
        o[k + 6] = 1.0 if reloading else 0.0
        o[k + 7] = min(1.0, self.under_fire / 3.0)
        o[k + 8] = float(b.b[self.focus].sum())
        return np.clip(o, -1, 1)

    # ------------------------------------------------------------------ decision
    def decide(self, percept: dict) -> dict:
        self._update(percept)
        o = self.observation()
        self.last_obs = o
        d = decode(o, self.n)
        reason = ""
        dist = 99.0
        if self.visible and "player_pos" in percept:
            gx, gz = percept["guard"]["pos"]
            dist = float(np.hypot(percept["player_pos"][0] - gx, percept["player_pos"][1] - gz))
        # ---- reflexes (always win)
        if d["ammo"] <= 0.01 and not d["reloading"]:
            a, reason = (self.A_COVER if self.visible and d["fire"] > 0.3 else self.A_RELOAD), "magazine empty"
        elif self.visible and d["hp"] < self.spec.cover_hp and d["fire"] > 0.3:
            a, reason = self.A_COVER, "hurt and under fire"
        else:
            if self.model is not None:
                a = int(self.model.predict(o, deterministic=True)[0])
                reason = "learned policy"
            else:
                a, reason = self.fallback.act(o), "utility planner"
            # a visible intruder inside the role's engage range with a clear shot is engaged unless the policy chose to hold cover
            if self.visible and a < self.n and percept.get("clear_shot", False) and d["ammo"] > 0.01 and dist <= min(self.G.gun_range, self.spec.engage_range):
                a, reason = self.A_ENGAGE, reason + " + reflex: clear shot"
        # he sees the player inside his range but something (a crate, a machine) blocks the shot: step towards him to get a clear line instead of standing there
        if self.visible and not percept.get("clear_shot", False) and d["ammo"] > 0.01 and a != self.A_COVER and a != self.A_RELOAD and "player_pos" in percept \
                and dist <= max(6.0, min(self.G.gun_range, self.spec.engage_range)):
            pz = self.zg.zone_of(*percept["player_pos"])
            if self.allowed[pz]:
                px, pzz = percept["player_pos"]
                self.team.targets[self.id] = pz
                return dict(mode="goto", zone=self.zg.names[pz], target=[px, pzz], run=True, route=[], eta=0.0, why=reason + " | no clear shot: moving for a line", score=round(self.score, 2),
                            role=self.role, belief_top=[f"{self.zg.names[i]} {self.belief.b[i]:.2f}" for i in np.argsort(-self.belief.b)[:3]])
        if self.confined and a < self.n and not self.allowed[a]:
            a, reason = self._best_inside(), reason + " | stays inside"
        elif self.confined and a < self.n and not self.visible and sum(1 for k, v in self.team.targets.items() if k != self.id and v == a) >= 2:
            a, reason = self._best_inside(), reason + " | spreads out"          # the room is already covered by two others
        if self.heard_radio and not self.visible:
            reason += " | radio"
        if self.heard_gunshot:
            reason += " | gunshot"
        return self._to_command(a, d, reason)

    def _best_inside(self) -> int:
        """The best zone INSIDE the building to wait in: where the intruder probably is, or just behind the door he is probably about to come through.
        Zones next to an outside zone that carries belief mass count (an ambush position), the post gets a small bonus (no wandering about), distance costs a little."""
        zg, b = self.zg, self.belief.b
        best, best_s = self.guard_zone, -1e9
        for z in np.where(self.allowed)[0]:
            outside_next = sum(b[j] for j in np.where(zg.adj[z])[0] if not self.allowed[j])
            crowd = sum(1 for k, v in self.team.targets.items() if k != self.id and v == z)       # teammates already there or on the way
            s = b[z] + 0.6 * outside_next + (0.25 if z == self.post else 0.0) - 0.01 * zg.dist[self.guard_zone, z] - 0.45 * crowd
            if s > best_s:
                best, best_s = int(z), s
        return best

    def _to_command(self, a: int, d: dict, reason: str) -> dict:
        zg, G = self.zg, self.G
        if a < self.n:
            now = time.time()
            if self.committed and self.committed[0] != a and now < self.committed[1] and not self.visible:
                a = self.committed[0]                     # stay on the goal we picked a moment ago unless something new appears
            else:
                self.committed = (a, now + 2.0)
            route = zg.path(self.guard_zone, a)
            eta = float(sum(zg.dist[route[i], route[i + 1]] for i in range(len(route) - 1))) / (G.run if self.alarm else G.walk * 1.4)
            tgt = self.zone_point(a)
            cmd = dict(mode="goto", zone=zg.names[a], target=[tgt[0], tgt[1]], run=bool(self.alarm or self.visible), route=[zg.names[r] for r in route], eta=round(eta, 1))
        elif a == self.A_ENGAGE:
            cmd = dict(mode="engage", zone=zg.names[self.guard_zone], target=[], run=False)
            if not self.visible and self.belief.last_seen_zone >= 0 and self.belief.last_seen_age < 8:
                lz = self.belief.last_seen_zone
                if not self.allowed[lz]:
                    lz = self._best_inside()                  # he was last seen outside: wait at the door instead of running out after him
                    reason += " | stays inside"
                if lz != self.guard_zone:
                    cmd.update(mode="goto", zone=zg.names[lz], target=list(self.zone_point(lz)), run=True)
        elif a == self.A_COVER:
            cmd = dict(mode="cover", zone=zg.names[self.guard_zone], target=[], run=False)
        else:
            cmd = dict(mode="reload", zone=zg.names[self.guard_zone], target=[], run=False)
        self.team.targets[self.id] = a if a < self.n else self.guard_zone
        cmd["why"] = reason
        cmd["score"] = round(self.score, 2)
        cmd["role"] = self.role
        cmd["belief_top"] = [f"{zg.names[i]} {self.belief.b[i]:.2f}" for i in np.argsort(-self.belief.b)[:3]]
        self.last_mode = cmd["mode"]
        return cmd

    def zone_point(self, k: int):
        """World point to walk to for zone k: the exported NavMesh-snapped centre if the graph has one, else the rectangle centre."""
        pts = getattr(self.zg, "points", None)
        if pts is not None:
            return pts[k]
        return self.zg.centre(k)


Brain = GuardBrain
