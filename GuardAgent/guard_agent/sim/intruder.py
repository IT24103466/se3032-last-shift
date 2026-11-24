"""Scripted stand-ins for the human player, used as the guard's training opponents (randomised every episode so the policy does not memorise one route).
Three styles: stealth (crouches, avoids the guard, rarely fights), standard, rusher (sprints, noisy, shoots back)."""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from ..zones import BAG_ZONES, EXIT_ZONE, OUTSIDE, ENTRY_ZONE, ZoneGraph


@dataclass
class IntruderStyle:
    name: str
    speed: float            # m/s along the graph
    stealth: float          # multiplier on how easily the guard sees him (crouching = lower)
    noise: float            # probability per second of making a noise the guard can hear
    fight: float            # probability he shoots back when he sees the guard
    caution: float          # probability he waits / reroutes instead of walking into a guard he has seen
    hits_per_s: float       # his effective hit rate with the pistol when he does shoot (human aim)


STYLES = [
    IntruderStyle("stealth", 1.9, 0.55, 0.02, 0.10, 0.85, 0.35),
    IntruderStyle("standard", 3.6, 1.0, 0.10, 0.50, 0.45, 0.5),
    IntruderStyle("rusher", 6.4, 1.25, 0.45, 0.85, 0.10, 0.65),
]


class Intruder:
    def __init__(self, zg: ZoneGraph, rng: np.random.Generator, style: IntruderStyle | None = None):
        self.zg, self.rng = zg, rng
        self.style = style or STYLES[rng.integers(len(STYLES))]
        # per-episode jitter
        self.speed = self.style.speed * rng.uniform(0.9, 1.1)
        self.hits_per_s = self.style.hits_per_s * rng.uniform(0.6, 1.4)
        self.hp = 100.0
        self.zone = zg.idx(OUTSIDE)
        self.path: list[int] = []
        self.edge_pos = 0.0           # metres travelled on the current edge
        self.prev = self.zone
        self.search_left = 0.0
        self.wait_left = 0.0
        self.aware_guard_zone = -1
        self.aware_age = 99.0
        self.has_ring = False
        # the order he visits the bag zones in, and which bag holds the ring
        order = list(dict.fromkeys(BAG_ZONES))
        rng.shuffle(order)
        self.plan = [zg.idx(z) for z in order]
        bag_slots = [zg.idx(z) for z in BAG_ZONES]
        self.ring_zone = bag_slots[rng.integers(len(bag_slots))]
        self.searched: set[int] = set()
        self.bags_left = {zg.idx(z): BAG_ZONES.count(z) for z in set(BAG_ZONES)}
        self.entered_premises = False
        self.exited = False

    # --------------------------------------------------------------------------
    def _target(self):
        if self.has_ring:
            return self.zg.idx(EXIT_ZONE)
        for z in self.plan:
            if self.bags_left.get(z, 0) > 0:
                return z
        return self.zg.idx(EXIT_ZONE)

    def _route(self, to: int):
        zg = self.zg
        g = zg.graph
        if self.aware_age < 6.0 and self.aware_guard_zone >= 0 and self.rng.random() < self.style.caution:
            h = g.copy()
            for u, v, d in h.edges(data=True):
                if u == self.aware_guard_zone or v == self.aware_guard_zone:
                    d["w"] = d["w"] + 60.0
            import networkx as nx
            try:
                return nx.shortest_path(h, self.zone, to, weight="w")
            except nx.NetworkXNoPath:
                pass
        return zg.path(self.zone, to)

    def sees_guard(self, guard_zone: int):
        self.aware_guard_zone, self.aware_age = guard_zone, 0.0

    @property
    def sprinting(self):
        return self.speed > 5.0

    def step(self, dt: float, guard_zone: int, guard_visible_to_him: bool):
        """Advance dt seconds. Returns (noise_zone or None)."""
        rng = self.rng
        self.aware_age += dt
        if guard_visible_to_him:
            self.sees_guard(guard_zone)
        if self.search_left > 0:
            self.search_left -= dt
            if self.search_left <= 0:
                self._finish_search()
            return None
        if self.wait_left > 0:
            self.wait_left -= dt
            return None
        goal = self._target()
        if not self.path or self.path[-1] != goal:
            self.path = self._route(goal)
            self.path = self.path[1:] if self.path and self.path[0] == self.zone else self.path
            self.edge_pos, self.prev = 0.0, self.zone
        # he is about to walk into the zone he thinks the guard is in: maybe wait (stealth)
        if self.path and self.aware_age < 5.0 and self.path[0] == self.aware_guard_zone and rng.random() < self.style.caution * 0.7:
            self.wait_left = rng.uniform(2.0, 5.0)
            return None
        remaining = self.speed * dt
        while remaining > 0 and self.path:
            nxt = self.path[0]
            L = max(self.zg.dist[self.prev, nxt], 1.0) if self.zg.adj[self.prev, nxt] else max(self.zg.dist[self.prev, nxt], 1.0)
            take = min(remaining, L - self.edge_pos)
            self.edge_pos += take
            remaining -= take
            if self.edge_pos >= L - 1e-6:
                self.zone, self.prev, self.edge_pos = nxt, nxt, 0.0
                self.path.pop(0)
            elif self.edge_pos > 0.5 * L:
                self.zone = nxt                       # past the half-way point he counts as being in the next zone
        if not self.path and self.zone == goal:
            if self.has_ring and goal == self.zg.idx(EXIT_ZONE):
                self.exited = True
            elif self.bags_left.get(goal, 0) > 0:
                self.search_left = 3.5
        noise = self.zone if rng.random() < self.style.noise * (1.0 if self.sprinting else 0.4) else None
        return noise

    def _finish_search(self):
        z = self.zone
        self.bags_left[z] = max(0, self.bags_left.get(z, 1) - 1)
        if z == self.ring_zone and self.rng.random() < 1.0 / max(1, self.bags_left[z] + 1):
            self.has_ring = True
        elif all(v == 0 for v in self.bags_left.values()):
            self.has_ring = True
        self.path = []
