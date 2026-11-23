"""Compare guards over the same seeded episodes, overall and per intruder style.
   python evaluate.py [--model models/guard_ppo.zip] [--episodes 300]"""
import argparse
import os

from stable_baselines3 import PPO

from guard_agent.baselines import PatrolGuard, RandomGuard, UtilityGuard
from guard_agent.evaluation import fmt, rollout
from guard_agent.sim.intruder import STYLES
from guard_agent.zones import load_graph

ROOT = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument("--model", default=os.path.join(ROOT, "models", "guard_ppo.zip"))
ap.add_argument("--episodes", type=int, default=300)
a = ap.parse_args()
zg = load_graph()
guards = {"random": RandomGuard(zg).act, "patrol(game)": PatrolGuard(zg).act, "utility": UtilityGuard(zg).act}
if os.path.exists(a.model):
    m = PPO.load(a.model, device="cpu")
    guards["ppo(sentinel)"] = lambda o: int(m.predict(o, deterministic=True)[0])
for style in [None] + STYLES:
    print(f"\n=== intruder style: {style.name if style else 'mixed'} ===")
    for name, act in guards.items():
        print(fmt(name, rollout(act, zg, a.episodes, seed=900_000, style=style)))
