# LAST SHIFT: Kirimala Estate (SE3032 group project)

Survival FPS in Unity 6.6 (6000.6.6f1), URP. Set on a fictional Sri Lankan hill-country tea estate at blue hour in monsoon mist. The player is the night watchman. Rogue estate robots (Crawlers: fast melee plucking robots; Sentries: patrolling security drones) attack. Restore 3 generators, then escape through the main gate.

I am the **World Builder** (Student 1): level layout, lighting, texturing, NavMesh baking. My marks come from my own Git commits and a viva, so explain what you change and why, and let me review and commit myself. Do not commit or push unless I ask.

## Level plan (1 unit = 1 m, origin = SW corner of estate, X = east, Z = north)
| Area | X | Z | Y |
|---|---|---|---|
| Tea estate (Unity Terrain) | 0-60 | 0-40 | slopes 0 -> 8 |
| Factory yard | 10-50 | 40-52 | 8 |
| Rolling floor | 10-36 | 52-76 | 8, roof 17 |
| Withering loft (upper) | 10-36 | 66-76 | 12.5 |
| Catwalk | 32-36 | 58-66 | 12.5 |
| Storeroom | 36-50 | 52-62 | 8 |
| Boiler room | 36-50 | 62-76 | 8, ceiling 11.5 |
| Estate road + main gate | 0-10 | 44-48 | 8 |

- Footpath zigzag (X,Z): (30,5) (18.3,12.7) (40,22.3) (30,32.7) (30,40). Leaf collection shed X 42-48, Z 20-26.
- Generators: G1 (21, 8, 57), G2 (15, 12.5, 73) on the loft, G3 (46, 8, 72) in the boiler room.
- Doors 2.2 m tall; loading door X 16-22 (4 m tall); storeroom yard door X 42-46.

## Scene and project conventions
- Scene: `Assets/_Project/Scenes/Main/Level_01.unity`. Only I edit it; teammates hand over prefabs.
- Roots: `--- ENVIRONMENT ---` (static, baked), `--- LIGHTING ---`, `--- DYNAMIC ---` (doors, barricades, throwables, generators: never static), `--- AGENTS ---`, `--- GAMEPLAY ---` (spawn points).
- Environment art goes under `Assets/_Project/Art/Environment/` (import rules apply automatically).
- Editor tools are in `Assets/_Project/Scripts/Editor/WorldBuilder/` and appear under **Tools > World Builder** (blockout builder, static flags, lighting settings, NavMesh bake, light probes, level audit).
- Art direction: whitewashed walls with green trim, red corrugated roof, rusted metal, wooden loft floors, tea chests ("CEYLON TEA"), trilingual signs (Sinhala/Tamil/English). Cold blue ambient plus warm sodium lamps. Mostly baked lighting (Shadowmask), light probes for moving objects.
- Respectful setting: no combat in religious sites, enemies are only robots.
- Commit messages: `type(area): what changed`, e.g. `feat(env): ...`, `perf(lighting): ...`, `fix(nav): ...`.
