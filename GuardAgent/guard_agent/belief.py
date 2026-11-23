"""Bayes filter (a hidden Markov model) over WHERE the intruder is, one probability per zone.
predict : the intruder diffuses along the zone graph, drawn towards the tea bags and the gate (what he came for)
observe : seeing him collapses the belief; NOT seeing him in the zones the guard can see removes mass there; a noise adds mass round the sound.
The belief is part of the policy's observation, so the neural policy reasons about a probability map instead of cheating with the true position."""
from __future__ import annotations

import numpy as np

from .zones import BAG_ZONES, EXIT_ZONE, ZoneGraph


class BeliefTracker:
    def __init__(self, zg: ZoneGraph, move_rate: float = 0.22):
        self.zg = zg
        n = zg.n
        pull = np.ones(n)
        for z in BAG_ZONES:
            pull[zg.idx(z)] += 0.35
        pull[zg.idx(EXIT_ZONE)] += 0.3
        T = np.zeros((n, n))
        for i in range(n):
            nb = np.where(zg.adj[i])[0]
            T[i, i] = 1.0 - move_rate
            if len(nb):
                w = pull[nb] / np.maximum(zg.dist[i, nb], 1.0)
                T[i, nb] = move_rate * w / w.sum()
            else:
                T[i, i] = 1.0
        self.T = T
        self.b = np.full(n, 1.0 / n)
        self.last_seen_zone = -1
        self.last_seen_age = 99.0

    def reset(self, prior: np.ndarray | None = None):
        self.b = np.full(self.zg.n, 1.0 / self.zg.n) if prior is None else np.asarray(prior, float) / np.sum(prior)
        self.last_seen_zone, self.last_seen_age = -1, 99.0

    def predict(self, dt: float = 1.0):
        steps = max(1, int(round(dt)))
        for _ in range(steps):
            self.b = self.b @ self.T
        self.last_seen_age += dt
        self._norm()

    def observe_visible(self, zone: int):
        self.b = np.zeros(self.zg.n)
        self.b[zone] = 1.0
        self.last_seen_zone, self.last_seen_age = zone, 0.0

    def observe_not_visible(self, p_see: np.ndarray):
        """p_see[z]: probability the guard would have seen the intruder if he were in zone z right now (from the guard's position)."""
        self.b = self.b * (1.0 - np.clip(p_see, 0.0, 0.97))
        self._norm()

    def observe_noise(self, zone: int, strength: float = 0.7):
        like = 0.15 + strength * np.exp(-self.zg.dist[zone] / 14.0)
        self.b = self.b * like
        self._norm()

    def inject(self, zone: int, weight: float = 0.6):
        """Alarm: we know he crossed the fence at `zone` just now."""
        e = np.zeros(self.zg.n)
        e[zone] = 1.0
        self.b = (1 - weight) * self.b + weight * e
        self._norm()

    def mass_protected(self) -> float:
        return float(self.b[self.zg.protected].sum())

    def _norm(self):
        s = self.b.sum()
        if not np.isfinite(s) or s < 1e-9:
            self.b = np.full(self.zg.n, 1.0 / self.zg.n)
        else:
            self.b = self.b / s
