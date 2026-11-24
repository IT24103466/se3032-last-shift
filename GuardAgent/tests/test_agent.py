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


def test_gunshot_moves_belief_and_is_flagged():
    zg = default_graph()
    brain = GuardBrain(None, zg)
    p = dict(dt=0.5, guard=dict(pos=[30, 46], hp=100, ammo=12, reloading=False), player_visible=False, alarm=True, noises=[])
    brain.rng = __import__("numpy").random.default_rng(1)
    for _ in range(11):
        brain.decide(p)
    area = [zg.idx(n) for n in ("warehouse", "north_lane", "compound")]
    before = brain.belief.b[area].sum()
    p["noises"] = [dict(pos=[58, 73], strength=0.9, kind="gunshot")]
    brain.decide(p)
    assert brain.heard_gunshot
    assert brain.belief.b[area].sum() > before
    assert abs(brain.belief.b.sum() - 1) < 1e-6


def test_roles_have_distinct_goals_and_same_observation():
    from guard_agent.config import ROLES
    zg = default_graph()
    dims = {FactoryDefenseEnv(zg, role=r).obs_dim for r in ROLES}
    assert len(dims) == 1
    assert ROLES["tea_keeper"].stats.pellets > 1 and ROLES["sentinel"].stats.pellets == 1
    assert ROLES["lookout"].weights.track_per_s > 0 and ROLES["tea_keeper"].weights.bag_taken < 0 and ROLES["gatekeeper"].weights.escape < ROLES["sentinel"].weights.escape


def test_every_role_runs_an_episode():
    from guard_agent.config import ROLES
    zg = default_graph()
    for r in ROLES:
        env = FactoryDefenseEnv(zg, role=r)
        o, _ = env.reset(seed=5)
        done = False
        info = {}
        while not done:
            o, rew, te, tr, info = env.step(UtilityGuard(zg, role=r).act(o))
            done = te or tr
        assert "score" in info


def test_team_radio_shares_sightings():
    from guard_agent.team import Team
    zg = default_graph()
    team = Team(zg)
    a = GuardBrain(None, zg, role="lookout", team=team, agent_id="a")
    b = GuardBrain(None, zg, role="tea_keeper", team=team, agent_id="b")
    base = dict(dt=0.5, alarm=True, noises=[])
    b.decide(dict(base, guard=dict(pos=[43, 57], hp=80, ammo=2, reloading=False), player_visible=False))
    before = b.belief.b[zg.idx("rolling")]
    a.decide(dict(base, guard=dict(pos=[23, 59], hp=80, ammo=2, reloading=False), player_visible=True, player_pos=[24, 60], clear_shot=False))
    cmd = b.decide(dict(base, guard=dict(pos=[43, 57], hp=80, ammo=2, reloading=False), player_visible=False))
    assert b.belief.b[zg.idx("rolling")] > before
    assert "radio" in cmd["why"]


def test_lookout_does_not_start_a_duel_at_range():
    zg = default_graph()
    look = GuardBrain(None, zg, role="lookout", agent_id="l")
    cmd = look.decide(dict(dt=0.5, alarm=True, noises=[], guard=dict(pos=[23, 59], hp=80, ammo=2, reloading=False), player_visible=True, player_pos=[23, 70], clear_shot=True))
    assert "reflex" not in cmd["why"]            # 11 m away, outside his 6 m engage range
    cmd = look.decide(dict(dt=0.5, alarm=True, noises=[], guard=dict(pos=[23, 59], hp=80, ammo=2, reloading=False), player_visible=True, player_pos=[23, 63], clear_shot=True))
    assert cmd["mode"] == "engage"               # cornered at 4 m
