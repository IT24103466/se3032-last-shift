"""Writes data/zone_defs.json (zone rectangles, flags, physical connections) for the Unity tool Tools > Guard Agent > Export Zone Graph,
which measures the real NavMesh path lengths and line of sight and writes data/zone_graph.json for training and for the runtime brain."""
import json
import os

from guard_agent.zones import EDGES, ZONE_DEFS

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data", "zone_defs.json")
names = [z[0] for z in ZONE_DEFS]
d = {"zones": [dict(name=z[0], x0=z[1], x1=z[2], z0=z[3], z1=z[4], protected=z[5], bag=z[6]) for z in ZONE_DEFS],
     "edges": [dict(a=names.index(a), b=names.index(b)) for a, b, _ in EDGES]}
os.makedirs(os.path.dirname(out), exist_ok=True)
json.dump(d, open(out, "w"), indent=1)
print("wrote", out)
