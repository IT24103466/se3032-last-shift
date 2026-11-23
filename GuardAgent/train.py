"""Train the guard's tactical policy with PPO (stable-baselines3) against randomised intruders.
   python train.py --steps 800000            -> models/guard_ppo.zip  (+ models/train_log.csv)
The reward is the guard's score (config.ScoreWeights): damage dealt to the intruder, minus time the intruder spends inside the factory, breach / escape / death penalties."""
import argparse
import csv
import os
import time

import numpy as np
from stable_baselines3 import PPO
from stable_baselines3.common.callbacks import BaseCallback
from stable_baselines3.common.env_util import make_vec_env
from stable_baselines3.common.vec_env import SubprocVecEnv

from guard_agent.baselines import PatrolGuard, UtilityGuard
from guard_agent.evaluation import fmt, rollout
from guard_agent.sim.factory_env import FactoryDefenseEnv
from guard_agent.zones import load_graph

ROOT = os.path.dirname(os.path.abspath(__file__))


def make_env():
    return FactoryDefenseEnv(load_graph())


class EvalCallback(BaseCallback):
    def __init__(self, every, zg, path, log_path):
        super().__init__()
        self.every, self.zg, self.path, self.log_path = every, zg, path, log_path
        self.best = -1e9
        self.next = every
        self.t0 = time.time()
        with open(log_path, "w", newline="") as f:
            csv.writer(f).writerow(["timesteps", "score", "ci", "damage", "breach_rate", "kill_rate", "escape_rate", "guard_down_rate", "seconds"])

    def _on_step(self):
        if self.num_timesteps >= self.next:
            self.next += self.every
            d = rollout(lambda o: int(self.model.predict(o, deterministic=True)[0]), self.zg, episodes=60, seed=50_000)
            with open(self.log_path, "a", newline="") as f:
                csv.writer(f).writerow([self.num_timesteps, d["score"], d["score_ci"], d["damage"], d["breach_rate"], d["kill_rate"], d["escape_rate"], d["guard_down_rate"], int(time.time() - self.t0)])
            print(f"[{self.num_timesteps:>8}] " + fmt("ppo", d), flush=True)
            if d["score"] > self.best:
                self.best = d["score"]
                self.model.save(self.path)
        return True


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--steps", type=int, default=800_000)
    ap.add_argument("--envs", type=int, default=8)
    ap.add_argument("--seed", type=int, default=0)
    a = ap.parse_args()
    zg = load_graph()
    os.makedirs(os.path.join(ROOT, "models"), exist_ok=True)
    venv = make_vec_env(make_env, n_envs=a.envs, seed=a.seed, vec_env_cls=SubprocVecEnv)
    model = PPO("MlpPolicy", venv, learning_rate=3e-4, n_steps=512, batch_size=512, n_epochs=8, gamma=0.995, gae_lambda=0.95, ent_coef=0.01,
                clip_range=0.2, policy_kwargs=dict(net_arch=[128, 128]), seed=a.seed, verbose=0, device="cpu")
    cb = EvalCallback(40_960, zg, os.path.join(ROOT, "models", "guard_ppo.zip"), os.path.join(ROOT, "models", "train_log.csv"))
    print("baselines for reference:")
    print(fmt("patrol(game)", rollout(PatrolGuard(zg).act, zg, 60, seed=50_000, reset=None)))
    print(fmt("utility", rollout(UtilityGuard(zg).act, zg, 60, seed=50_000)))
    model.learn(total_timesteps=a.steps, callback=cb)
    model.save(os.path.join(ROOT, "models", "guard_ppo_last.zip"))
    print("done. best eval score", cb.best)


if __name__ == "__main__":
    main()
