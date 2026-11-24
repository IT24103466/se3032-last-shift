"""Exports what the isolated (embedded) agents need into Unity's Resources folder, so they run inside Unity with NO Python server:
   Assets/_Project/Resources/GuardAgent/policy_<role>.json   the trained PPO network (48 -> 128 -> 128 -> 16, tanh) as flat weight arrays
   Assets/_Project/Resources/GuardAgent/zone_graph_flat.json the zone map (rectangles, NavMesh points, path lengths, line of sight, doors)
Run again after training or after exporting a new zone graph from Unity:   python export_policies.py"""
import json
import os

import numpy as np
from stable_baselines3 import PPO

from guard_agent.config import ROLES
from guard_agent.zones import load_graph

ROOT = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(os.path.dirname(ROOT), "Assets", "_Project", "Resources", "GuardAgent")
os.makedirs(OUT, exist_ok=True)

zg = load_graph()
pts = getattr(zg, "points", None) or [zg.centre(i) for i in range(zg.n)]
flat = dict(
    n=zg.n, names=list(zg.names),
    rect=[float(v) for r in zg.rects for v in r],
    prot=[bool(v) for v in zg.protected], bag=[bool(v) for v in zg.bag],
    point=[float(v) for p in pts for v in p],
    dist=[float(v) for v in zg.dist.reshape(-1)], los=[float(v) for v in zg.los.reshape(-1)], adj=[bool(v) for v in zg.adj.reshape(-1)],
)
with open(os.path.join(OUT, "zone_graph_flat.json"), "w") as f:
    json.dump(flat, f)
print("zone graph:", zg.n, "zones")

for name, spec in ROLES.items():
    path = os.path.join(ROOT, "models", spec.model)
    if not os.path.exists(path):
        print("skip (no model):", name)
        continue
    m = PPO.load(path, device="cpu")
    net = m.policy.mlp_extractor.policy_net
    lin = [l for l in net if hasattr(l, "weight")]
    assert len(lin) == 2, "expected two hidden layers"
    act = m.policy.action_net
    arr = lambda t: [float(v) for v in t.detach().cpu().numpy().astype(np.float32).reshape(-1)]
    d = dict(role=name, inDim=int(lin[0].weight.shape[1]), h1=int(lin[0].weight.shape[0]), h2=int(lin[1].weight.shape[0]), outDim=int(act.weight.shape[0]),
             w0=arr(lin[0].weight), b0=arr(lin[0].bias), w1=arr(lin[1].weight), b1=arr(lin[1].bias), w2=arr(act.weight), b2=arr(act.bias))
    with open(os.path.join(OUT, f"policy_{name}.json"), "w") as f:
        json.dump(d, f)
    print(f"policy {name}: {d['inDim']} -> {d['h1']} -> {d['h2']} -> {d['outDim']}")
