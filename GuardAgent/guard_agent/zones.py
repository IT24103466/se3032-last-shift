"""The premises as a graph of zones (rooms and yards). Rectangles come from the level plan (X east, Z north, metres).
A zone graph exported from Unity (Tools > Guard Agent > Export Zone Graph) replaces the hand-made distances and visibility with
real NavMesh path lengths and measured line of sight; if the JSON is missing the defaults below are used, so training works without Unity."""
from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass, field

import networkx as nx
import numpy as np

# name: (x0, x1, z0, z1, protected, is_bag_zone)
ZONE_DEFS = [
    ("tea_field", 0, 60, 8, 40, False, False),
    ("yard_w", 10, 30, 40, 52, False, False),
    ("yard_e", 30, 50, 40, 52, False, False),
    ("gate_road", 0, 10, 40, 52, False, False),
    ("rolling", 10, 36, 52, 66, True, False),
    ("loft", 10, 36, 66, 76, True, True),
    ("catwalk", 32, 36, 58, 66, True, False),
    ("storeroom", 36, 50, 52, 62, True, True),
    ("boiler", 36, 50, 62, 76, True, False),
    ("compound", 50, 68, 40, 66, False, False),
    ("line_houses", 68, 76, 40, 72, False, True),
    ("warehouse", 52, 64, 66, 80, False, True),
    ("north_lane", 10, 52, 76, 80, False, False),
]
# physical connections (doors, gaps, stairs). Costs are centre to centre distance times a door factor.
EDGES = [
    ("tea_field", "yard_e", 1.0),       # the collapsed fence: the only open way in
    ("tea_field", "yard_w", 2.2),       # pedestrian gate (locked)
    ("gate_road", "yard_w", 1.0),
    ("gate_road", "tea_field", 1.3),    # the estate road
    ("yard_w", "yard_e", 1.0),
    ("yard_w", "rolling", 1.3),         # loading door
    ("yard_e", "storeroom", 1.2),       # storeroom yard door
    ("yard_e", "compound", 1.0),
    ("rolling", "catwalk", 1.1),
    ("catwalk", "loft", 1.1),
    ("rolling", "loft", 1.5),           # stairs
    ("rolling", "storeroom", 1.3),
    ("rolling", "boiler", 1.3),
    ("storeroom", "boiler", 1.2),
    ("storeroom", "compound", 1.2),     # east door
    ("loft", "north_lane", 1.3),        # back door
    ("boiler", "north_lane", 1.3),
    ("north_lane", "warehouse", 1.0),
    ("warehouse", "compound", 1.2),
    ("compound", "line_houses", 1.0),
]
BAG_ZONES = ["warehouse", "warehouse", "loft", "storeroom", "line_houses"]   # 5 bags, one of them holds the ring
ENTRY_ZONE = "yard_e"            # where the alarm trips (the fence breach)
EXIT_ZONE = "gate_road"          # the main gate
OUTSIDE = "tea_field"


@dataclass
class ZoneGraph:
    names: list
    rects: np.ndarray                   # (n, 4)
    protected: np.ndarray               # bool (n,)
    bag: np.ndarray                     # bool (n,)
    dist: np.ndarray                    # (n, n) shortest path metres
    los: np.ndarray                     # (n, n) probability two random people in the zones see each other
    adj: np.ndarray                     # (n, n) bool
    graph: nx.Graph = field(repr=False, default=None)

    @property
    def n(self):
        return len(self.names)

    def idx(self, name):
        return self.names.index(name)

    def centre(self, i):
        x0, x1, z0, z1 = self.rects[i]
        return 0.5 * (x0 + x1), 0.5 * (z0 + z1)

    def zone_of(self, x, z):
        best, area = -1, 1e18
        for i, (x0, x1, z0, z1) in enumerate(self.rects):       # smallest containing rectangle wins (catwalk inside rolling)
            if x0 <= x <= x1 and z0 <= z <= z1 and (x1 - x0) * (z1 - z0) < area:
                best, area = i, (x1 - x0) * (z1 - z0)
        if best >= 0:
            return best
        cs = np.array([self.centre(i) for i in range(self.n)])
        return int(np.argmin(np.hypot(cs[:, 0] - x, cs[:, 1] - z)))

    def path(self, a, b):
        """A* over the zone graph (heuristic: straight line between centres)."""
        cs = [self.centre(i) for i in range(self.n)]
        h = lambda u, v: math.hypot(cs[u][0] - cs[v][0], cs[u][1] - cs[v][1])
        return nx.astar_path(self.graph, a, b, heuristic=h, weight="w")


def _build(names, rects, protected, bag, edges, dist_override=None, los_override=None):
    n = len(names)
    cs = [(0.5 * (r[0] + r[1]), 0.5 * (r[2] + r[3])) for r in rects]
    g = nx.Graph()
    g.add_nodes_from(range(n))
    adj = np.zeros((n, n), bool)
    for a, b, f in edges:
        i, j = names.index(a), names.index(b)
        w = math.hypot(cs[i][0] - cs[j][0], cs[i][1] - cs[j][1]) * f if dist_override is None else dist_override[i][j]
        g.add_edge(i, j, w=w)
        adj[i, j] = adj[j, i] = True
    sp = dict(nx.all_pairs_dijkstra_path_length(g, weight="w"))
    dist = np.array([[sp[i].get(j, 1e3) for j in range(n)] for i in range(n)])
    if los_override is not None:
        los = np.asarray(los_override, float)
    else:                                               # default: sight inside a zone is good, across a door it is a slit
        los = np.eye(n) * 0.85 + adj * 0.18
    return ZoneGraph(names, np.array(rects, float), np.array(protected, bool), np.array(bag, bool), dist, los, adj, g)


def default_graph() -> ZoneGraph:
    names = [z[0] for z in ZONE_DEFS]
    rects = [z[1:5] for z in ZONE_DEFS]
    return _build(names, rects, [z[5] for z in ZONE_DEFS], [z[6] for z in ZONE_DEFS], EDGES)


def load_graph(path: str | None = None) -> ZoneGraph:
    """Zone graph exported from Unity if present, else the hand-made default."""
    path = path or os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "data", "zone_graph.json")
    if not os.path.exists(path):
        return default_graph()
    with open(path, "r", encoding="utf-8") as f:
        d = json.load(f)
    names = [z["name"] for z in d["zones"]]
    rects = [z["rect"] for z in d["zones"]]
    prot = [z["protected"] for z in d["zones"]]
    bag = [z["bag"] for z in d["zones"]]
    edges = [(names[e["a"]], names[e["b"]], 1.0) for e in d["edges"]]
    dist = np.array(d["dist"], float)
    los = np.array(d["los"], float)
    zg = _build(names, rects, prot, bag, edges, dist_override=dist, los_override=los)
    zg.points = [tuple(z["point"]) for z in d["zones"]]            # NavMesh-snapped (x, z) of each zone centre
    return zg
