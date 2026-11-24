"""Reference guards to measure the learned brain against. They only see the same observation vector as the agent."""
from __future__ import annotations

import numpy as np

from .roles import focus_mask, get_role
from .zones import ZoneGraph


def decode(o: np.ndarray, n: int) -> dict:
    return dict(zone=int(np.argmax(o[:n])), belief=o[n:2 * n], seen_zone=int(np.argmax(o[2 * n:3 * n])) if o[2 * n:3 * n].max() > 0 else -1,
                visible=o[3 * n] > 0.5, age=float(o[3 * n + 1] * 60.0), alarm=o[3 * n + 2] > 0.5, i_hp=float(o[3 * n + 3]), hp=float(o[3 * n + 4]),
                ammo=float(o[3 * n + 5]), reloading=o[3 * n + 6] > 0.5, fire=float(o[3 * n + 7]), prot=float(o[3 * n + 8]))


class PatrolGuard:
    """The guard as the game ships him: walks a fixed patrol, shoots what he sees, searches the last known spot for ~7 s, and runs to the alarm."""

    def __init__(self, zg: ZoneGraph, route=None, role="sentinel"):
        self.zg = zg
        self.route = [zg.idx(r) for r in (route or get_role(role).patrol)]
        self.i = 0
        self.A_ENGAGE, self.A_COVER, self.A_RELOAD = zg.n, zg.n + 1, zg.n + 2

    def reset(self):
        self.i = 0

    def act(self, o):
        d = decode(o, self.zg.n)
        if d["ammo"] <= 0.01 and not d["reloading"]:
            return self.A_RELOAD
        if d["visible"]:
            return self.A_ENGAGE
        if d["seen_zone"] >= 0 and d["age"] < 7.0:
            return d["seen_zone"] if d["seen_zone"] != d["zone"] else self.A_ENGAGE
        if d["alarm"] and d["age"] < 30.0 and d["seen_zone"] >= 0:
            return d["seen_zone"]
        if d["zone"] == self.route[self.i % len(self.route)]:
            self.i += 1
        return self.route[self.i % len(self.route)]


class RandomGuard:
    def __init__(self, zg: ZoneGraph, seed=0):
        self.n = zg.n + 3
        self.rng = np.random.default_rng(seed)

    def reset(self):
        pass

    def act(self, o):
        return int(self.rng.integers(self.n))


class UtilityGuard:
    """Hand-written expected-value brain over the belief map; also the fallback inside the runtime when no trained policy is available.
    Picks the zone with the highest  belief mass x (1 + protected bonus) / travel time  and engages whenever he has the intruder in sight."""

    def __init__(self, zg: ZoneGraph, protect_bonus: float = 1.8, role="sentinel"):
        self.zg = zg
        self.pb = protect_bonus
        self.spec = get_role(role)
        self.focus = focus_mask(zg, self.spec)
        self.A_ENGAGE, self.A_COVER, self.A_RELOAD = zg.n, zg.n + 1, zg.n + 2

    def reset(self):
        pass

    def act(self, o):
        zg = self.zg
        d = decode(o, zg.n)
        if d["ammo"] <= 0.01 and not d["reloading"]:
            return self.A_RELOAD if not d["visible"] or d["fire"] < 0.2 else self.A_COVER
        if d["visible"]:
            if d["hp"] < 0.3 and d["fire"] > 0.3:
                return self.A_COVER
            return self.A_ENGAGE
        b = d["belief"]
        utility = b * (1.0 + (self.pb - 1.0) * self.focus) / (1.0 + zg.dist[d["zone"]] / 20.0)
        utility[zg.idx("tea_field")] *= 0.2                      # no point standing in the tea field
        utility[d["zone"]] *= 1.0 if b[d["zone"]] > 0.15 else 0.6
        return int(np.argmax(utility))
