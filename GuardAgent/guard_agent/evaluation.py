"""Roll a guard policy through the simulator and report the numbers that matter: score, damage dealt, how often the intruder got into the factory, kills, escapes."""
from __future__ import annotations

import numpy as np

from .sim.factory_env import FactoryDefenseEnv
from .sim.intruder import STYLES


def rollout(act, zg, episodes=200, seed=1000, style=None, reset=None, role="sentinel"):
    rows = []
    for ep in range(episodes):
        env = FactoryDefenseEnv(zg, style=style, role=role)
        o, _ = env.reset(seed=seed + ep)
        if reset:
            reset()
        done, info = False, {}
        while not done:
            o, r, term, trunc, info = env.step(act(o))
            done = term or trunc
        rows.append((info.get("score", 0.0), info.get("damage", 0.0), float(info.get("breached", False)), info.get("why", ""), info.get("t", 0), info.get("bags", 0), info.get("contacts", 0)))
    sc = np.array([r[0] for r in rows])
    out = dict(score=float(sc.mean()), score_ci=float(1.96 * sc.std(ddof=1) / np.sqrt(len(sc))) if len(sc) > 1 else 0.0,
               damage=float(np.mean([r[1] for r in rows])), breach_rate=float(np.mean([r[2] for r in rows])),
               kill_rate=float(np.mean([r[3] == "kill" for r in rows])), escape_rate=float(np.mean([r[3] == "escape" for r in rows])),
               guard_down_rate=float(np.mean([r[3] == "guard_down" for r in rows])), steps=float(np.mean([r[4] for r in rows])),
               bags=float(np.mean([r[5] for r in rows])), contacts=float(np.mean([r[6] for r in rows])))
    return out


def fmt(name, d):
    return (f"{name:<14} score {d['score']:8.1f} ±{d['score_ci']:5.1f} | damage {d['damage']:6.1f} | factory breached {100 * d['breach_rate']:5.1f}% | "
            f"intruder killed {100 * d['kill_rate']:5.1f}% | escaped {100 * d['escape_rate']:5.1f}% | guard down {100 * d['guard_down_rate']:5.1f}% | "
            f"bags rummaged {d['bags']:.2f} | contacts {d['contacts']:.2f}")


def style_by_name(name):
    for s in STYLES:
        if s.name == name:
            return s
    return None
