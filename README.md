# World Builder Kit (SE3032)

Editor tools for the World Builder role: level layout, texturing, lighting and NavMesh baking in Unity (URP).

## Install

1. Create the project in **Unity Hub → Unity 6 LTS → "Universal 3D" template** (Unity 2022.3 LTS with the URP template also works).
2. **Window → Package Manager → Unity Registry**, install:
   - **AI Navigation** (required — the bake tool won't compile without it)
   - **ProBuilder** (blockout and simple geometry)
3. **Edit → Project Settings → Editor**: Asset Serialization = **Force Text**, Version Control mode = **Visible Meta Files** (defaults in Unity 6, check anyway).
4. Copy `.gitignore`, `.gitattributes` and the `Assets/` folder from this kit into the project root (next to `Assets/`, `Packages/`, `ProjectSettings/`).
5. Wait for scripts to compile. A **Tools → World Builder** menu appears.
6. URP shadowmask: select your URP asset (`Assets/Settings/*_RPAsset`), Lighting → tick **Mixed Lighting**.

## The tools

| Where | What it does |
|---|---|
| Tools → World Builder → 0. Create Scene Hierarchy | Creates `--- ENVIRONMENT ---`, `--- LIGHTING ---`, `--- DYNAMIC ---`, `--- AGENTS ---`, `--- GAMEPLAY ---` roots |
| 1. Mark Environment Static | Static flags on everything under ENVIRONMENT; small props (< 1 m) set to light probes instead of lightmaps; only big objects (≥ 2 m) become occluders |
| 2a / 2b. Lighting Settings Draft / Final | Creates `<Scene>_Lighting.lighting`: baked GI, Shadowmask, GPU lightmapper, AO, directional lightmaps. Draft = 5 texels/m for fast iteration, Final = 15 texels/m |
| 3. Bake NavMesh | Adds a NavMeshSurface (children only, physics colliders) to ENVIRONMENT, bakes, saves `Scenes/<Scene>/NavMesh.asset` |
| 4. Generate Light Probes On NavMesh | Places probes every 3 m where agents can walk, at 0.5 m and 2 m height |
| 5. Bake Lighting / 6. Bake Occlusion Culling | Starts the bakes |
| Run Full Pipeline (Final quality) | 1 → 2b → 3 → 4 → save → 5 |
| Level Audit | Triangles, materials, texture memory, lights, lightmaps, probes + a list of problems. "Export report" writes `Docs/LevelAudit_*.md` |
| Re-apply Import Rules to Environment Art | Forces the texture/model import rules on everything already imported |
| Right-click textures → World Builder → Build URP Material From Selected Textures | Packs metal/AO/smoothness into one texture, creates `M_<Set>.mat` (URP Lit) |

Automatic import rules apply to anything imported under `Assets/_Project/Art/Environment/`:
- Textures: mipmaps + streaming, compressed, aniso 4; colour maps sRGB at 2048 max; normal maps as Normal Map; data maps (roughness/AO/metal/height) linear at 1024 max.
- Models: Blender axis conversion baked, no cameras/lights/animation, not readable, mesh compression low, colliders on, lightmap UVs generated.

## Folder layout (whole team)

```
Assets/
  _Project/
    Art/
      Environment/        World Builder   (Textures/<Set>/, Materials/, Models/)
      Props/              Core Developer  (custom Blender models)
      Characters/         Core Dev / Agent Controller
      Animations/         Agent Controller
    Prefabs/
      Environment/        World Builder
      Interactables/      Systems Engineer (doors, barricades, throwables)
      Agents/             Agent Controller
    Scenes/
      Main/Level_01.unity World Builder owns this scene
      Sandbox/<Name>.unity  one test scene per person
    Scripts/
      Runtime/Player, Runtime/Interaction, Runtime/Agents
      Editor/WorldBuilder
    Settings/             URP assets, volume profiles
  ThirdParty/             anything downloaded (Asset Store, CC0 packs)
Docs/                     audit reports, notes
```

**One rule that prevents most merge disasters:** only the World Builder edits `Level_01.unity`. Everyone else builds and tests in their own sandbox scene and hands over **prefabs**; the World Builder places them in the level. Unity scene files merge badly when two people edit them at once.

## Git

- Every teammate runs `git lfs install` once before cloning/committing.
- GitHub's free LFS quota is limited (storage and monthly bandwidth). Keep texture downloads at 2K, not 4K/8K, and don't commit raw renders or videos.
- Commit message style: `type(area): what changed` — e.g. `feat(env): block out warehouse arena with ProBuilder`, `perf(lighting): move small props to light probes`, `fix(nav): add colliders to catwalk so NavMesh connects`.
