"""TCP bridge for the whole factory: every person with a brain opens ONE connection; each connection gets its own Brain (own role, own belief, own score)
and all of them share a Team (radio + the player's learned habits).

  -> {"type":"hello","version":2,"role":"tea_keeper","id":"Worker_17","focus":["storeroom"]}   <- {"type":"ready","zones":[...],"role":..,"model":"ppo"|"utility"}
  -> {"type":"episode_start"}                                                                   <- {"type":"ok"}
  -> {"type":"tick", "dt":0.5, "guard":{"pos":[x,z],"hp":80,"ammo":2,"reloading":false}, "player_visible":true, "player_pos":[x,z], "player_hp_frac":0.8,
      "clear_shot":true, "alarm":false, "alarm_event_pos":[], "noises":[{"pos":[x,z],"strength":0.7,"kind":"gunshot"}], "damage_taken":0,
      "score_info":{"damage_dealt_now":5,"intruder_in_factory":false,"intruder_searching_bag":false,"bags_searched_total":0}}   <- {"type":"action","mode":"goto",...}
  -> {"type":"episode_end","outcome":"kill|escape|guard_down|timeout"}                         <- {"type":"episode_summary","score":...}

Run:  python -m guard_agent.server --port 5555
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os

from .brain import GuardBrain
from .config import ROLES
from .team import Team
from .zones import load_graph

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


class Factory:
    """Everything the connections share: the zone graph, one loaded policy per role, and the team."""

    def __init__(self, memory_path):
        self.zg = load_graph()
        self.team = Team(self.zg, memory_path)
        self.models = {}
        for name, spec in ROLES.items():
            path = os.path.join(ROOT, "models", spec.model)
            if os.path.exists(path):
                from stable_baselines3 import PPO
                self.models[name] = PPO.load(path, device="cpu")
        print("[sentinel] policies loaded for: " + (", ".join(self.models) or "none") + " | roles without a policy use the utility planner", flush=True)

    def new_brain(self, role: str, agent_id: str, focus):
        if role not in ROLES:
            role = "sentinel"
        return GuardBrain(None, self.zg, role=role, team=self.team, agent_id=agent_id, focus=focus, model=self.models.get(role))


async def handle(reader, writer, factory: Factory):
    peer = writer.get_extra_info("peername")
    brain = None
    counts = {}
    try:
        while True:
            line = await reader.readline()
            if not line:
                break
            try:
                msg = json.loads(line)
            except ValueError:
                continue
            t = msg.get("type")
            counts[t] = counts.get(t, 0) + 1
            if t == "hello":
                if brain is not None:
                    brain.close()
                brain = factory.new_brain(msg.get("role", "sentinel"), msg.get("id", f"agent{peer[1]}"), msg.get("focus") or None)
                print(f"[sentinel] {brain.id} connected as {brain.role} ({'ppo' if brain.model is not None else 'utility'})", flush=True)
                out = {"type": "ready", "zones": factory.zg.names, "role": brain.role, "model": "ppo" if brain.model is not None else "utility"}
            elif brain is None:
                out = {"type": "error", "msg": "say hello first"}
            elif t == "episode_start":
                brain.new_game()
                out = {"type": "ok"}
            elif t == "tick":
                out = brain.decide(msg)
                out["type"] = "action"
                if counts[t] in (1, 2) or counts[t] % 100 == 0:
                    print(f"[sentinel] {brain.id}/{brain.role} tick #{counts[t]}: {out['mode']} -> {out['zone']} ({out['why']}) score {out['score']}", flush=True)
            elif t == "episode_end":
                score = brain.end_game(msg.get("outcome", "timeout"))
                print(f"[sentinel] {brain.id}/{brain.role} game over ({msg.get('outcome')}): score {score:.1f}, damage dealt {brain.damage_dealt:.0f}", flush=True)
                out = {"type": "episode_summary", "score": round(score, 2), "damage": round(brain.damage_dealt, 1), "games": brain.memory.games}
            else:
                out = {"type": "error", "msg": f"unknown message {t}"}
            writer.write((json.dumps(out) + "\n").encode())
            await writer.drain()
    except (ConnectionResetError, asyncio.IncompleteReadError):
        pass
    finally:
        if brain is not None:
            print(f"[sentinel] {brain.id} disconnected", flush=True)
            brain.close()
        writer.close()


async def main_async(args):
    factory = Factory(os.path.join(ROOT, "models", "opponent_memory.json"))
    server = await asyncio.start_server(lambda r, w: handle(r, w, factory), "127.0.0.1", args.port)
    print(f"[sentinel] listening on 127.0.0.1:{args.port}", flush=True)
    async with server:
        await server.serve_forever()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5555)
    asyncio.run(main_async(ap.parse_args()))


if __name__ == "__main__":
    main()
