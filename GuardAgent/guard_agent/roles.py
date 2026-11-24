"""Helpers shared by the simulator, the brain and the baselines: a role's focus zones as a mask over the zone graph."""
from __future__ import annotations

import numpy as np

from .config import ROLES, RoleSpec
from .zones import ZoneGraph


def get_role(role) -> RoleSpec:
    if isinstance(role, RoleSpec):
        return role
    if role not in ROLES:
        raise KeyError(f"unknown role '{role}', choose one of {list(ROLES)}")
    return ROLES[role]


def focus_mask(zg: ZoneGraph, spec: RoleSpec, override=None) -> np.ndarray:
    """Zones the role is responsible for. None -> every protected zone (the factory building). `override`: names chosen for this one agent."""
    names = override if override else spec.focus
    if names is None:
        return zg.protected.copy()
    m = np.zeros(zg.n, bool)
    for nme in names:
        if nme in zg.names:
            m[zg.idx(nme)] = True
    return m if m.any() else zg.protected.copy()
