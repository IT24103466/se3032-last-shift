import numpy as np

from guard_agent.baselines import PatrolGuard, UtilityGuard
from guard_agent.belief import BeliefTracker
from guard_agent.brain import GuardBrain
from guard_agent.sim.factory_env import FactoryDefenseEnv
from guard_agent.zones import default_graph


def test_graph_connected():
    zg = default_graph()
    assert zg.dist.max() < 1e3
    p = zg.path(zg.idx("tea_field"), zg.idx("loft"))
    assert p[0] == zg.idx("tea_field") and p[-1] == zg.idx("loft")


def test_belief_normalised_and_collapses():
    zg = default_graph()
    b = BeliefTracker(zg)
    for _ in range(20):
        b.predict(1.0)
    assert abs(b.b.sum() - 1) < 1e-6
    b.observe_visible(zg.idx("rolling"))
    assert b.b[zg.idx("rolling")] == 1.0
    b.predict(1.0)
    assert b.b[zg.idx("rolling")] < 1.0 and abs(b.b.sum() - 1) < 1e-6


def test_env_runs_and_scores():
    zg = default_graph()
    env = FactoryDefenseEnv(zg)
    for g in (PatrolGuard(zg), UtilityGuard(zg)):
        o, _ = env.reset(seed=3)
        done, steps = False, 0
        while not done:
            o, r, te, tr, info = env.step(g.act(o))
            done = te or tr
            steps += 1
            assert np.all(np.isfinite(o))
        assert "score" in info and steps <= 300


def test_brain_command_shape():
    zg = default_graph()
    brain = GuardBrain(None, zg)
    p = dict(dt=0.5, guard=dict(pos=[30, 46], hp=80, ammo=8, reloading=False), player_visible=False, alarm=False, noises=[])
    cmd = brain.decide(p)
    assert cmd["mode"] in ("goto", "engage", "cover", "reload")
    p.update(player_visible=True, player_pos=[33, 46], clear_shot=True, alarm=True)
    cmd = brain.decide(p)
    assert cmd["mode"] == "engage"
    p["guard"]["ammo"] = 0
    assert brain.decide(p)["mode"] in ("reload", "cover")
