"""Learns the player between games: where he tends to cross the fence, where he is last seen, where he tends to be when the guard loses him.
Dirichlet counts per zone (with a slow decay so a changed habit overrides the old one); saved to JSON next to the models, so the guard remembers across sessions.
The posterior mean becomes the prior of the belief tracker at the start of the next game."""
from __future__ import annotations

import json
import os

import numpy as np

from .zones import ZoneGraph


class OpponentModel:
    def __init__(self, zg: ZoneGraph, path: str | None = None, decay: float = 0.97):
        self.zg, self.path, self.decay = zg, path, decay
        self.entry = np.ones(zg.n) * 0.5
        self.lost = np.ones(zg.n) * 0.5
        self.games = 0
        self.scores: list[float] = []
        if path and os.path.exists(path):
            self.load()

    def prior(self) -> np.ndarray:
        """Where he is likely to be at the start: mostly outside, with the learned fence-crossing habit."""
        p = np.full(self.zg.n, 0.2)
        p[self.zg.idx("tea_field")] = 2.0
        e = self.entry / self.entry.sum()
        return p / p.sum() * 0.6 + e * 0.4

    def search_bias(self) -> np.ndarray:
        """Zones where he has slipped away before, to be checked first when he disappears."""
        return self.lost / self.lost.sum()

    def record_entry(self, zone: int):
        self.entry *= self.decay
        self.entry[zone] += 1.0

    def record_lost(self, zone: int):
        self.lost *= self.decay
        self.lost[zone] += 1.0

    def end_game(self, score: float):
        self.games += 1
        self.scores.append(float(score))
        self.save()

    def save(self):
        if not self.path:
            return
        os.makedirs(os.path.dirname(self.path), exist_ok=True)
        with open(self.path, "w", encoding="utf-8") as f:
            json.dump(dict(names=self.zg.names, entry=self.entry.tolist(), lost=self.lost.tolist(), games=self.games, scores=self.scores[-200:]), f, indent=1)

    def load(self):
        try:
            with open(self.path, "r", encoding="utf-8") as f:
                d = json.load(f)
            if d.get("names") == self.zg.names:
                self.entry, self.lost = np.array(d["entry"]), np.array(d["lost"])
                self.games, self.scores = d.get("games", 0), d.get("scores", [])
        except (OSError, ValueError, KeyError):
            pass
