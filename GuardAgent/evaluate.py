"""Compare a role's policy with the baselines over the same seeded episodes, overall and per intruder style.
   python evaluate.py --role tea_keeper [--episodes 300]"""
import argparse
import os

from stable_baselines3 import PPO

from guard_agent.baselines import PatrolGuard, RandomGuard, UtilityGuard
from guard_agent.config import ROLES
from guard_agent.evaluation import fmt, rollout
from guard_agent.sim.intruder import STYLES
from guard_agent.zones import load_graph

ROOT = os.path.dirname(os.path.abspath(__file__))
ap = argparse.ArgumentParser()
ap.add_argument("--role", default="sentinel", choices=list(ROLES))
ap.add_argument("--model", default=None)
ap.add_argument("--episodes", type=int, default=300)
a = ap.parse_args()
spec = ROLES[a.role]
model_path = a.model or os.path.join(ROOT, "models", spec.model)
zg = load_graph()
print(f"role {a.role}: {spec.goal}", flush=True)
guards = {"random": RandomGuard(zg).act, "patrol(game)": PatrolGuard(zg, role=a.role).act, "utility": UtilityGuard(zg, role=a.role).act}
if os.path.exists(model_path):
    m = PPO.load(model_path, device="cpu")
    guards["ppo(" + a.role + ")"] = lambda o: int(m.predict(o, deterministic=True)[0])
for style in [None] + STYLES:
    print("\n=== intruder style: " + (style.name if style else "mixed") + " ===", flush=True)
    for name, act in guards.items():
        print(fmt(name, rollout(act, zg, a.episodes, seed=900_000, style=style, role=a.role)), flush=True)
