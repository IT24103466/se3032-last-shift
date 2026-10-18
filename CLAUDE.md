# LAST SHIFT: Kirimala Estate (SE3032 group project)

Stealth / survival FPS in Unity 6.6 (6000.6.6f1), URP. Set on a fictional Sri Lankan hill-country tea estate at blue hour in monsoon mist. Rogue estate robots (Crawlers: fast melee plucking robots; Sentries: patrolling security drones) guard it.

Game flow: start at a camp in the jungle -> cross the ravine and the old estate fence -> sneak up through the tea (drone patrols) -> get into the factory premises through the fence breach (entering sets off the alarm: security starts chasing) -> steal 5 tea-leaf bags (warehouse, loft, storeroom, line house) -> escape through the main gate onto the estate road. Hide in bushes, lockers and under beds; vault logs, low walls and pallets; doors can be locked; barricades block routes until broken. (Generators G1-G3 are still placed as placeholders: the team decides whether they power the main gate.)

I am the **World Builder** (Student 1): level layout, lighting, texturing, NavMesh baking. My marks come from my own Git commits and a viva, so explain what you change and why, and let me review and commit myself. Do not commit or push unless I ask.

## Level plan (1 unit = 1 m, origin = SW corner of estate, X = east, Z = north)
| Area | X | Z | Y |
|---|---|---|---|
| Jungle (start, Unity Terrain) | -20-80 | -64-0 | about -3 to 1, hills at the edges; stream ravine along Z = -34 + 5 sin(0.07X + 1) |
| Tea estate (Unity Terrain) | 0-60 | 0-40 | slopes 0 -> 8 |
| Factory yard | 10-50 | 40-52 | 8 |
| Rolling floor | 10-36 | 52-76 | 8, roof 17 |
| Withering loft (upper) | 10-36 | 66-76 | 12.5 |
| Catwalk | 32-36 | 58-66 | 12.5 |
| Storeroom | 36-50 | 52-62 | 8 |
| Boiler room | 36-50 | 62-76 | 8, ceiling 11.5 |
| Estate road + main gate (escape) | 0-10 | 44-48 | 8 |
| East compound (line houses X 68-76, lorry, water tower, fuel shed) | 50-76 | 40-80 | 8 |
| Dispatch warehouse (tea bags) | 52-64 | 66-80 | 8, roof 14 |
| Back alley / north service lane | 50-52 / 10-52 | 66-80 / 76-80 | 8 |

- Footpath zigzag (X,Z): (30,5) (18.3,12.7) (40,22.3) (30,32.7) (30,40). Leaf collection shed X 42-48, Z 20-26.
- Generators: G1 (21, 8, 57), G2 (15, 12.5, 73) on the loft, G3 (46, 8, 72) in the boiler room.
- Doors 2.2 m tall; loading door X 16-22 (4 m tall, locked); storeroom yard door X 42-46; storeroom east door Z 55-57; factory back door X 25.2-27.
- Jungle: player camp (7, -57); trail (6,-58) (16,-48) (26,-41) (30,-34) (24,-22) (34,-12) (30,-3) joins the estate footpath at (30, 0); log bridge over the ravine at X 30; ruined line house X 53-67, Z -23 to -17; estate boundary fence along Z -0.6 (gaps at X 28.4-31.6 and 9-11.5).
- Premises entry: collapsed fence X 46.5-48.5 on the yard's south fence (pedestrian gate X 28-32 is locked).
- Tea bags: warehouse (56.2, 8, 70.7) and (62.4, 8, 77), loft (23, 12.5, 72.5), storeroom (38.6, 8, 60.6), line room 3 (73.5, 8, 61).
- Built by Tools > World Builder > Build Kirimala Expansion (run after Blockout, Surfaces, Props, Set Dressing). Interactables are placeholders with GreyboxInteractable under DYNAMIC; hiding zones, triggers (alarm, exit), security spawns and patrol routes under GAMEPLAY/Kirimala_Expansion. Tags: Vaultable, HideSpot.

## Scene and project conventions
- Scene: `Assets/_Project/Scenes/Main/Level_01.unity`. Only I edit it; teammates hand over prefabs.
- Roots: `--- ENVIRONMENT ---` (static, baked), `--- LIGHTING ---`, `--- DYNAMIC ---` (doors, barricades, throwables, generators: never static), `--- AGENTS ---`, `--- GAMEPLAY ---` (spawn points).
- Environment art goes under `Assets/_Project/Art/Environment/` (import rules apply automatically).
- Editor tools are in `Assets/_Project/Scripts/Editor/WorldBuilder/` and appear under **Tools > World Builder** (blockout builder, static flags, lighting settings, NavMesh bake, light probes, level audit).
- Art direction: whitewashed walls with green trim, red corrugated roof, rusted metal, wooden loft floors, tea chests ("CEYLON TEA"), trilingual signs (Sinhala/Tamil/English). Cold blue ambient plus warm sodium lamps. Mostly baked lighting (Shadowmask), light probes for moving objects.
- Respectful setting: no combat in religious sites, enemies are only robots.
- Commit messages: `type(area): what changed`, e.g. `feat(env): ...`, `perf(lighting): ...`, `fix(nav): ...`.
