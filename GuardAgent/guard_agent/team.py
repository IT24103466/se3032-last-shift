"""The team radio. Every person with a brain shares one Team: sightings are broadcast, everybody else folds them into his own belief
(a few seconds old and fading), and the opponent model (the player's habits) is learned once for the whole factory.
Nothing here is magic: a report only carries 'the intruder was in zone Z, this many seconds ago', exactly what a shout or a radio call would."""
from __future__ import annotations

import time

from .opponent_model import OpponentModel
from .zones import ZoneGraph


class Team:
    def __init__(self, zg: ZoneGraph, memory_path: str | None = None, clock=time.time):
        self.zg = zg
        self.memory = OpponentModel(zg, memory_path)
        self.clock = clock
        self.reports: list[tuple[float, int, str]] = []        # (time, zone, who)
        self.members: dict[str, str] = {}                      # agent id -> role

    def join(self, agent_id: str, role: str):
        self.members[agent_id] = role

    def leave(self, agent_id: str):
        self.members.pop(agent_id, None)

    def report(self, agent_id: str, zone: int):
        now = self.clock()
        self.reports = [r for r in self.reports if now - r[0] < 15.0][-30:]
        self.reports.append((now, zone, agent_id))

    def fresh(self, agent_id: str, max_age: float = 6.0):
        """Reports made by the others in the last max_age seconds: [(age, zone)], newest first."""
        now = self.clock()
        out = [(now - t, z) for t, z, who in self.reports if who != agent_id and now - t <= max_age]
        return sorted(out)
