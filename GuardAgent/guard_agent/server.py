"""TCP bridge: Unity sends one JSON line per decision tick, the brain answers with one JSON line.

  -> {"type":"hello","version":1}                                   <- {"type":"ready","zones":[...],"model":"ppo"|"utility"}
  -> {"type":"tick", "dt":0.5, "guard":{"pos":[x,z],"hp":80,"ammo":8,"reloading":false}, "player_visible":true, "player_pos":[x,z], "player_hp_frac":0.8,
      "clear_shot":true, "alarm":false, "alarm_event":{"pos":[x,z]}, "noises":[{"pos":[x,z],"strength":0.7}], "damage_taken":0,
      "score_info":{"damage_dealt_now":5,"intruder_in_factory":false}}                 <- {"type":"action", "mode":"goto", "target":[x,z], ...}
  -> {"type":"episode_end","outcome":"kill|escape|guard_down|timeout"}                    <- {"type":"episode_summary","score":..., "games":...}
  -> {"type":"episode_start"}                                                             <- {"type":"ok"}

Run:  python -m guard_agent.server --model models/guard_ppo.zip --port 5555
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os

from .brain import GuardBrain

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


async def handle(reader, writer, brain: GuardBrain, has_model: bool):
    peer = writer.get_extra_info("peername")
    print(f"[sentinel] Unity connected from {peer}")
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
            if t == "hello":
                out = {"type": "ready", "zones": brain.zg.names, "model": "ppo" if has_model else "utility"}
            elif t == "episode_start":
                brain.new_game()
                out = {"type": "ok"}
            elif t == "tick":
                out = brain.decide(msg)
                out["type"] = "action"
            elif t == "episode_end":
                score = brain.end_game(msg.get("outcome", "timeout"))
                print(f"[sentinel] game over ({msg.get('outcome')}): score {score:.1f}, damage dealt {brain.damage_dealt:.0f}, games {brain.memory.games}")
                out = {"type": "episode_summary", "score": round(score, 2), "damage": round(brain.damage_dealt, 1), "games": brain.memory.games}
            else:
                out = {"type": "error", "msg": f"unknown message {t}"}
            writer.write((json.dumps(out) + "\n").encode())
            await writer.drain()
    except (ConnectionResetError, asyncio.IncompleteReadError):
        pass
    finally:
        print("[sentinel] Unity disconnected")
        writer.close()


async def main_async(args):
    brain = GuardBrain(args.model, memory_path=os.path.join(ROOT, "models", "opponent_memory.json"))
    has_model = brain.model is not None
    print(f"[sentinel] brain ready: {'trained PPO policy' if has_model else 'utility planner (no model found)'}, {brain.n} zones")
    server = await asyncio.start_server(lambda r, w: handle(r, w, brain, has_model), "127.0.0.1", args.port)
    print(f"[sentinel] listening on 127.0.0.1:{args.port}")
    async with server:
        await server.serve_forever()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=os.path.join(ROOT, "models", "guard_ppo.zip"))
    ap.add_argument("--port", type=int, default=5555)
    asyncio.run(main_async(ap.parse_args()))


if __name__ == "__main__":
    main()
