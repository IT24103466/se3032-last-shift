# HANDOVER: custom Blender models (branch `feat/custom-art-assets`)

Everything here is **new files only**. No existing prefab, material, script, scene or project setting was edited, and `Level_01` was never opened.
All work files (.blend, high-poly meshes, bakes, renders, generator scripts) are in `BlenderSource/` (git-excluded). `BlenderSource/PROGRESS.md` is the running log, `BlenderSource/SOURCES.md` lists sources and licences (nothing was downloaded; every mesh and texture is generated).

## 1. What is in the box

| Prefab (new) | Replaces / use | Game tris | Textures (all URP Lit: BaseColor + Normal + MetalAOSmooth) | Collider |
|---|---|---|---|---|
| `Prefabs/Characters_Custom/Guard_Custom` | `Characters/Guard` | 7,960 | 2048 | unchanged capsule |
| `.../Worker_Custom` | `Characters/Worker` | 7,672 | 2048 | unchanged capsule |
| `.../Hunter_Custom` | `Characters/Hunter` | 8,380 | 2048 | unchanged capsule |
| `.../Tiger_Custom` | `Characters/Tiger` | 2,916 (+ the original two eye spheres) | 2048 | unchanged capsule |
| `.../Elephant_Custom` | `Characters/Elephant` | 2,944 | 2048 | unchanged capsule |
| `.../Deer_Custom` | `Characters/Deer` | 2,008 | 2048 | unchanged capsule |
| `Prefabs/Environment_Custom/Tree_Estate_02`, `Tree_Estate_02B`, `Tree_Estate_02C` | estate / shade trees (three crown shapes, same wood) | LOD0 9,462 / 10,102 / 8,182; LOD1 1,645 / 1,685 / 1,525 | bark 1024, leaves atlas 2048x1024 (alpha cut) | trunk **capsule** only |
| `.../TeaRow_Estate_01` | one 4 m tea hedge segment, long side along **X**, base on the ground | LOD0 1,428 / LOD1 308 | 512 shell + 1024 leaf atlas | box 4 x 0.95 x 0.9 (same as the placeholder) |
| `.../Bush_Hide_Custom` | bush to hide in | LOD0 2,768 / LOD1 928 | 512 + 1024 | none on purpose (the level's HideZone trigger does the hiding) |
| `.../Fern_Jungle_Custom` | undergrowth | LOD0 240 / LOD1 54 | 1024 | none |
| `.../Rock_A_Custom` ... `Rock_D_Custom` | jungle rocks 1.15 m, 0.78 m, 1.25 x 0.34 m slab, 0.42 m | 700 / 520 / 520 / 360 | one 2048 atlas (1K per rock) | box |
| `.../Barrel_Custom` | rusty 200 L drum (0.58 x 0.88 m) | 672 | 1024 | capsule |
| `.../Crate_Custom` | wooden crate 0.9 m | 160 | 1024 | box |
| `.../TeaChest_Custom` | stencilled tea chest 0.52 x 0.6 m ("CEYLON TEA") | 160 | 1024 | box |
| `.../TeaBag_Custom` | the objective sack (see below) | 528 | 1024 (burlap weave, stencil) | capsule on `Sack` |
| `.../Pallet_Custom`, `Log_Custom` | cover to vault (tagged `Vaultable`): pallet 1.2 x 0.8 m, fallen log 3.2 m | 528 / 576 | 1024 each | box / capsule along Z |
| `.../Pistol_Custom`, `Shotgun_Custom`, `Rifle_Custom` | the enemies' guns (already inside the `*_Custom` people; the pistol is the guard's, the double-barrel the worker's, the scoped bolt rifle the hunter's) | 380 / 704 / 912 | 1024 each | none |

Sandbox to look at all of it side by side with the originals at night: `Assets/_Project/Scenes/Sandbox/Sandbox_Models.unity`.

## 2. How to swap (no gameplay code changes)

### Characters
Each `*_Custom` prefab is a **copy of the original enemy prefab** with only the visual `Model` replaced, so the root has exactly the same components and values: `CapsuleCollider`, `NavMeshAgent`, `Health`, `EnemyAI` (same `kind`), `CreatureAnimator`.
- Pivot names and positions are the ones `CreatureAnimator` expects: `Model/Head`, `Leg_L/R` (people) or `Leg_FL/FR/BL/BR` (animals), `Arm_L/R`, `Tail`, `Trunk` (elephant, child of `Head`). They sit at the same places as the primitives, so everything that hangs on them still fits: the **gun with its `Muzzle`** on `Arm_R`, the worker's **lantern + light** on `Arm_L`, the guard's **flashlight** and hunter's **head lamp** on `Head`, the animals' **glowing eye spheres** on `Head`. They were moved over from the original prefab with the same local offsets (one exception: the hunter's hat is taller than the old cap, so the head-lamp bulb and its spot light sit on the hat band at local (0, 0.17, 0.13) instead of (0, 0.21, 0.12)).
- The three people carry **real gun models** (`Pistol_Custom` / `Shotgun_Custom` / `Rifle_Custom`) on the original `Arm_R/Gun` pivot. The primitive meshes were removed, the `Muzzle` child stays exactly where it was (so `EnemyAI`'s muzzle lookup and the tracer origin are unchanged).
- Only one value differs: `Worker_Custom` has `CreatureAnimator.stride = 24` (original 35) because the sarong is a static skirt and long strides push the shins through it.
- Eye height, capsule size and NavMeshAgent radius/height are unchanged. The people are the same height as before: 1.86 to 1.88 m to the top of the cap/hat/head (originals 1.88 to 1.99 m including the old props), feet on y = 0. The tiger, elephant and deer match the originals' length and height within about 0.15 m.
- **To adopt them in a scene**: open the scene and run `Tools > Custom Art > Swap Open Scene Enemies To Custom`. It replaces every EnemyAI character with its `*_Custom` prefab at the same position, rotation, parent and name and copies the whole `EnemyAI` component (patrol route, leash, wander radius ...). It is undoable and acts only on the open scene; I never ran it on Level_01. (The gameplay builder regenerates the original prefabs on every run, so run the swap after it.)

### Environment
- Trees: `Tree_Estate_02`, `02B`, `02C` have an explicit `LODGroup` (LOD0 below ~18 % screen height, LOD1 down to ~2.5 %, then culled, cross-fade). Collision is a capsule on the trunk only. **The project's import rules add mesh colliders to every model under `Art/Environment`; the custom builders switch that off per model** (a leaf card must never block the player or the NavMesh). If you re-run "Re-apply Import Rules to Environment Art" on the whole folder it will turn colliders back on for the `Models/*.fbx`: run `Tools > Custom Art > Build Custom Environment Prefabs` afterwards (it resets the model settings).
- Tea rows: `Tools > Custom Art > Swap Open Scene Tea Rows To Custom` replaces every box under `TeaRows_Placeholder` (same footprint, base on the ground). Or place `TeaRow_Estate_01` yourself: pivot on the ground, centre of the 4 m length.
- `TeaBag_Custom` keeps the structure of the greybox bag: root pivot on the floor, child `Sack` (mesh + collider), child `Tag` (orange emissive box), and the `LastShift.TeaBag` component. `TeaBag.Search()` squashes `Sack` and hides `Tag`, so it works unchanged; `hasRing` and `place` are set by the gameplay builder as before. It does not carry `GreyboxInteractable` (the Systems Engineer is replacing that).
- Everything else is a plain prefab: drop it in.

## 2b. Scene tools (Tools > Custom Art), all undoable (one Ctrl+Z per menu item), safe to re-run, and they ask first when the open scene is not under `Scenes/Sandbox/`
| Menu item | What it does |
|---|---|
| Swap Open Scene Enemies To Custom | enemies -> `*_Custom` prefabs (position, rotation, parent, name, EnemyAI values copied) |
| Swap Open Scene Tea Rows To Custom | boxes under `TeaRows_Placeholder` -> `TeaRow_Estate_01` |
| Swap Open Scene Props To Custom | `Cover_Crate*`, `Crate_Camp/Ruin`, `TeaChest`, `OilDrum`, `Vault_Pallets`, `Vault_Log`: the placeholder object stays (name, tag `Vaultable`, collider, components, parent) with its renderer switched off and a child `CustomArt_Visual` holding the custom mesh (tiled to fill the same box). `TeaBag` objectives: the whole object is replaced by `TeaBag_Custom` (same name/position/rotation/parent, `TeaBag` and `GreyboxInteractable` values copied). Re-run does nothing (already-swapped objects are skipped). |
| Restore Placeholder Props | deletes the `CustomArt_Visual` children and switches the placeholder renderers back on (tea bags: Ctrl+Z) |
| Place Custom Nature In Open Scene | rocks, ferns, hide bushes and the 3 tree crowns under ONE parent `CustomArt_Nature` (inside `--- ENVIRONMENT ---/Props`), terrain height + slope, fixed seed 20261123. Keeps clear of the footpath, trail, stream, doors/lockers/barricades, HideSpot/alarm/exit triggers, spawns, existing trees/rocks and solid colliders. Re-run rebuilds the same group (no duplicates). Hide bushes are visual only: they do not add HideSpot triggers. |
| Remove Custom Nature | deletes `CustomArt_Nature` in one step |
| Swap Everything To Custom | all of the above in one undo step |

### First-person hands (new)
The builder gives every weapon the same blob mesh (`Player_Fist` + `Player_Sleeve`) as the player's hand. `Art/Characters/Custom/FPHands_Custom.fbx` replaces it with three real hands (3,684 tris each, one 2048 atlas, URP Lit, material `Materials/M_FPHands_Custom`): fingers wrapped round the grip with knuckles, creases and nails, a thumb, a back of hand with tendons and veins, hairy forearm and a rolled shirt sleeve with hem stitching.
| Mesh | Used for | Modelled in the frame of |
|---|---|---|
| `GripR` (right hand) | club, pistol grip, shotgun wrist | grip axis +Y (club: stick axis; pistol: the grip box, tilted -14 deg; shotgun: the wrist, tilted -20 deg, scaled 0.92) |
| `ForeL` (left hand) | pump fore-end | the `Pump` pivot, barrel axis +Z |
| `SlingL` (left hand) | slingshot handle | the `Slingshot` pivot, handle axis +Y |
Tools: `Tools > Custom Art > Build Custom Hands` (import settings + material, once), `Swap Open Scene Player Hands To Custom` (adds a `CustomArt_Hand` child under each hand pivot and switches the old `Fist`/`Sleeve` renderers off; the new hand moves with the pump and every other animation), `Restore Placeholder Hands`. Undoable, re-run safe, asks first outside `Scenes/Sandbox/`. Regenerate with `BlenderSource/human/scripts/run_fphands.sh` (about 6.5 min). The KirimalaGameplayBuilder regenerates the blob hands every run, so run the swap after it.

### The player's M16 (new)
Weapon 4, key `4` (the mouse wheel cycles through it too): a fully automatic M16A2, the strongest weapon in the game. **34 damage a bullet** (pistol 25), **652 rounds a minute** (hold the left button), 30-round magazine, 90 spare (180 max), 120 m range, 1 degree spread, loud like every gun (it wakes the guards and raises the alarm inside the premises). Ammo boxes top it up with twice the pistol rounds they hold.
* **Model:** `Art/Characters/Custom/M16_Custom.fbx` (3,292 triangles, one 1K atlas, URP Lit): carry handle, ribbed round hand guard, flash hider, 30-round magazine, diamond-pattern grip, fixed stock. Generated by `BlenderSource/human/scripts/m16.py` (`run_m16.sh`, about 2 minutes). The custom hands are mounted on it: right hand on the pistol grip, left hand under the hand guard.
* **Code:** `PlayerGun.cs` got an `automatic` flag on `Gun` (hold to fire), key 4, and M16 ammo from ammo boxes; `GameManager.cs` shows "4 M16" in the weapon hint. `Tools > Custom Art > Build M16 Assets`, `Add M16 To Player` and `Remove M16 From Player` (`PlayerM16Tool.cs`): undoable, no duplicate M16 on re-run. The gameplay builder rebuilds the player every run, so add the M16 again after it.

## 3. How it was made (so it can be changed)
`BlenderSource/human/scripts/run_human.sh <Kind>` (characters; `CHAR_MOD=tiger|animals2|props` for animals/props, props also `ATLAS=1024 BAKE_SIZE=1024 PROP=1`), `rocks.py`, `BlenderSource/tree/scripts/tree2.py`, `BlenderSource/tea/scripts/tea.py` and `veg.py`. Parametric lofted parts are sampled twice (game mesh and a ~200k-face high-poly with folds, pockets, pores, wrinkles), baked per part in Cycles (normal, colour, roughness/metal, AO) into one atlas, composed in numpy and exported as FBX. In Unity: `Tools > Custom Art > Build Custom Characters`, `Build Custom Environment Prefabs`, `Build Sandbox Scene` (editor scripts in `Assets/_Project/Scripts/Editor/CustomArt/`, a tiny sandbox-only runtime helper `Scripts/Runtime/Sandbox/SandboxWalker.cs`).
Blender -> Unity axes: Blender (x, y, z) becomes Unity (-x, z, -y); characters are modelled facing -Y.

## 4. Caveats and known weak spots
- **Faces and skin** are the weakest part: believable at gameplay distance, plastic in a close-up. Hands are fists (no separate fingers, grooves only).
- Characters are rigid parts animated by the existing procedural `CreatureAnimator` (no skeleton): clothes cannot deform at the joints (visible gaps at the shoulders for big arm swings).
- Fur is a texture (stripes, grain), not strands. The elephant's ears and the tiger's head are simple.
- No LODs on the characters (they are 2.9k to 8.4k tris), no wind on foliage.
- The leaf/bark assets are procedural, not photo-scanned: species details come from general knowledge, not from photos of a silver oak or a tea plant.
- Characters use real-time lighting + light probes only (no lightmap UVs); the environment prefabs have no lightmap UVs either (`generateSecondaryUV` off), so they receive probes, not baked lightmaps.
- The sandbox uses the Level_01 PostFX profile read-only.
- Console: zero errors / warnings from these files after the final build.
