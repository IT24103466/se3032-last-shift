# Systems Engineer Integration Handoff & Verification Plan
**Project:** *Last Shift: Kirimala Estate* (SE3032)  
**Role:** Systems Engineer  
**Recipient / Teammate:** Randula (World Builder) & Team  
**Branch:** `feature/systems-engineer-interactions`  
**Latest Push:** Commit `a8a6cb6`  

---

## 1. Executive Summary & Architecture Overview

The Systems Engineer role delivers a modular, event-driven interaction and physics architecture built on Unity's New Input System (`UnityEngine.InputSystem`) and URP physics. It adheres strictly to the project convention: **all systems are encapsulated into reusable prefabs and decoupled components** so that the World Builder can integrate them into `Assets/_Project/Scenes/Main/Level_01.unity` without merge conflicts.

### Automated Sandbox vs. Main Level (`Level_01.unity`)
- **Automated Sandbox:** Run **Tools > Systems Engineer > Setup Sandbox Scene** at any time. This generates/refreshes `Assets/_Project/Scenes/Sandbox/Sandbox_SystemsEngineer.unity` with isolated demonstration zones for every system (wall obstruction, door mechanics, 3-generator progression, barricades, throw/drop tests). It **never** touches `Level_01.unity`.
- **Main Scene (`Level_01.unity`):** Follow the manual integration steps detailed in Section 3 to place the prefabs under the designated scene roots.

---

## 2. Reusable Prefabs & Component Specifications

All prefabs are located under `Assets/_Project/Prefabs/Interactables/`:

| Prefab | Location | Key Components | Role / Mechanics |
|---|---|---|---|
| `Generator_Interactable.prefab` | `Prefabs/Interactables/` | `GeneratorInteractable`, `BoxCollider`, `MeshRenderer`, `Point Light` | Single-use repairable generator. Transitions from inactive (red indicator) to restored (green emissive + light). Fires `OnRestored`. |
| `Door_Interactable.prefab` | `Prefabs/Interactables/` | `DoorInteractable`, `NavMeshObstacle`, `BoxCollider` (x2), `Hinge Transform` | Hinged rotating door ($90^\circ$). Supports open/close/locked states. Automatically manages NavMesh carve toggling for AI pathfinding. |
| `Movable_Barricade.prefab` | `Prefabs/Interactables/` | `MovableBarricade`, `Rigidbody`, `BoxCollider` | Heavy crate ($75\text{ kg}$) that can be pushed by player contact (`CharacterController`) or by pressing `[E]`. Constrained against tipping or spinning. |
| `Carryable_Prop.prefab` | `Prefabs/Interactables/` | `CarryableObject`, `Rigidbody`, `BoxCollider` | Physics prop ($10\text{ kg}$) that can be picked up, carried in hand socket, thrown forward (`[LMB]`), or safely dropped (`[E]` / `[Q]`). |

### Component Summary & Inspectors

#### A. `GeneratorInteractable.cs`
- **Implements:** `IInteractable`
- **Fields:**
  - `_state`: Inactive / Activated (default: `Inactive`).
  - `_singleUse`: `true` (prevents duplicate activation).
  - `_inactivePrompt`: `"Restore Generator"`.
  - `_activatedPrompt`: `"Generator Active"`.
  - `_indicatorLight`: Reference to child `Point Light`.
  - `_targetRenderer`: Reference to `MeshRenderer` for emissive color updates.
  - `_onActivated`: UnityEvent for audio or particle hooks.
  - `OnRestored`: C# action subscribed to by `EstateObjectiveManager`.

#### B. `DoorInteractable.cs`
- **Implements:** `IInteractable`
- **Hierarchy:**
  - Root: `Door_Interactable` (contains `DoorInteractable`, `NavMeshObstacle`, static frame collider).
  - Child: `Door_Hinge` (pivot for rotation).
  - Child of Hinge: `Door_Panel` (rotating door leaf with collider).
- **NavMesh AI Integration:**
  - `_navObstacle`: Attached `NavMeshObstacle` (`Center: (0.5, 1.1, 0)`, `Size: (1.0, 2.2, 0.2)`, `Carve: true`).
  - `_manageNavObstacle`: `true`.
  - `_obstacleClearAngle`: $35^\circ$.
  - **Dynamic Carve Optimization:** When door is closed (angle $< 35^\circ$), `NavMeshObstacle` is enabled with `carve = true`, cutting a hole through the NavMesh. When opened past $35^\circ$, the obstacle disables, letting Crawlers and Sentries route through. State changes only occur when crossing the $35^\circ$ threshold to prevent per-frame carve recalculations.

#### C. `MovableBarricade.cs`
- **Implements:** `IInteractable`
- **Rigidbody Configuration:**
  - `Mass: 75`, `Drag: 2.5`, `Angular Drag: 3.0`.
  - `Constraints: Freeze Rotation X, Freeze Rotation Z` (stays upright).
  - `Interpolate: Interpolate`, `Collision Detection: Continuous`.
- **Push Settings:**
  - `_interactPushForce: 3.5` (impulse on pressing `[E]`).
  - `_contactPushForce: 15.0` (sustained force when player walks into it).
  - `_maxPushSpeed: 2.0 m/s` (prevents uncontrolled catapulting).

#### D. `CarryableObject.cs` & `PlayerCarryController.cs`
- **CarryableObject:** Implements `IInteractable`. Becomes kinematic and parents to `HoldSocket` when carried. Ignores collision with player while held; restores collision after 0.35s delay upon release.
- **PlayerCarryController:** Attached to the player GameObject.
  - `_throwForce: 9.5` (impulse applied on throw).
  - `_dropForwardOffset: 0.9m`, `_dropUpOffset: 0.3m`.
  - `_dropObstructionLayers: Layer 1 (Default / walls)`.
  - `_dropCheckRadius: 0.35m`.
  - `_searchNearbySafePosition: true`.
  - **Drop Obstruction Guard:** When player drops an object, queries line-of-sight and overlap sphere. If blocked by a wall, checks nearby fallback candidate offsets (closer, ground-level, lateral). If all blocked, retains object in hands, warns the player, and updates the HUD prompt to `"Cannot Drop Here (Blocked)  |  [LMB] Throw"`.
  - **Point-Blank Throw Guard:** Raycasts forward so props thrown point-blank at walls spawn in front of the wall surface rather than inside geometry.

#### E. `EstateObjectiveManager.cs` & `ObjectiveProgressUI.cs`
- **EstateObjectiveManager:**
  - `_trackedGenerators`: List of 3 `GeneratorInteractable` references (G1, G2, G3).
  - `_requiredRestoredCount`: `3`.
  - `_exitDoor`: Reference to the `DoorInteractable` gate at the estate exit.
  - `_lockExitOnStart`: `true`.
  - Guarantees each generator counts exactly once via internal `HashSet` deduplication. Unlocks the exit door immediately when all 3 generators are restored.
- **ObjectiveProgressUI:**
  - Displays `"Generators Restored: 0/3"` up to `"All Generators Active! Escape through Main Gate!"`.
  - Connects to TextMeshProUGUI or uses automatic on-screen GUI fallback.

---

## 3. Step-by-Step Integration Guide for `Level_01.unity` (Randula / World Builder)

Follow these exact steps when placing Systems Engineer prefabs into `Level_01.unity`:

### Step 1: Player Setup (`PlayerRobot`)
1. Select the `PlayerRobot` instance or prefab in `Level_01.unity`.
2. Ensure the following components are attached to the root GameObject:
   - `CharacterController`
   - `StarterAssetsInputs`
   - `PlayerInput` (Notification Behavior: **Send Messages**)
   - `PlayerInteraction` (Range: 2.5m, Radius: 0.25m, Obstruction Layers: Default)
   - `PlayerCarryController` (Throw Force: 9.5, Drop Layers: Default)
3. *(Optional)* Create an empty child under `PlayerRobot` named `HoldSocket` at local position `(0.25, 0.95, 0.85)` and assign to `_holdSocket` on `PlayerCarryController` (if omitted, it auto-generates).

### Step 2: Generators Placement (`--- DYNAMIC ---`)
Place three instances of `Assets/_Project/Prefabs/Interactables/Generator_Interactable.prefab` under the `--- DYNAMIC ---` hierarchy root:
- **G1 (Rolling Floor):** Position `(21.0, 8.0, 57.0)`. Name: `Generator_G1`.
- **G2 (Withering Loft):** Position `(15.0, 12.5, 73.0)`. Name: `Generator_G2`.
- **G3 (Boiler Room):** Position `(46.0, 8.0, 72.0)`. Name: `Generator_G3`.
> **IMPORTANT:** Generators must **NOT** be marked as `Static`, as they undergo runtime material color changes and light state transitions.

### Step 3: Main Exit Gate & Interior Doors (`--- DYNAMIC ---`)
1. **Exit Gate:**
   - Instantiate `Door_Interactable.prefab` at the Estate Road main exit: Position `(5.0, 8.0, 46.0)` (or aligned with the estate perimeter fence).
   - Name: `Exit_Gate`.
   - On `DoorInteractable`, set:
     - `_isLocked = true`
     - `_lockedPrompt = "Gate Locked (Restore 3 Generators)"`
     - `_openPrompt = "Escape through Main Gate"`
2. **Interior Doors:**
   - Place `Door_Interactable.prefab` instances in factory doorways (e.g. Storeroom Yard door `X: 42-46, Z: 52`).
   - Leave `_isLocked = false` so the player and AI can use them.
3. Ensure all door instances remain under `--- DYNAMIC ---` so their `NavMeshObstacle` components can dynamically carve/uncarve the doorway.

### Step 4: Movable Barricades & Carryable Props (`--- DYNAMIC ---`)
1. Place `Movable_Barricade.prefab` instances in corridors, alleyways, or doorways to create physical obstruction puzzles.
2. Place `Carryable_Prop.prefab` instances near tea chests, sheds, or workbenches for the player to carry and throw.

### Step 5: Objective Coordinator & UI (`--- GAMEPLAY ---`)
1. Under `--- GAMEPLAY ---`, create an empty GameObject named `EstateObjectiveManager`.
2. Add the `EstateObjectiveManager` component:
   - Expand `_trackedGenerators` (Size: 3):
     - Element 0: Drag in `Generator_G1`
     - Element 1: Drag in `Generator_G2`
     - Element 2: Drag in `Generator_G3`
   - `_requiredRestoredCount`: `3`.
   - `_exitDoor`: Drag in `Exit_Gate` (the `DoorInteractable` component).
   - `_lockExitOnStart`: `true`.
3. Under `--- GAMEPLAY ---`, create an empty GameObject named `ObjectiveProgressUI`:
   - Add `ObjectiveProgressUI` component.
   - Leave `_objectiveManager` empty (it auto-finds `EstateObjectiveManager`) or drag it in.
   - If a Canvas HUD exists, assign `_progressText` to a TextMeshProUGUI element. If left unassigned, it renders via automatic OnGUI at top-left.
4. Add `InteractionPromptUI`:
   - Create GameObject `InteractionPromptUI` under `--- GAMEPLAY ---`.
   - Add `InteractionPromptUI` component. Renders contextual prompts (`[E] Open Door`, `[E] Restore Generator`, `[LMB] Throw | [E / Q] Drop`) automatically.

### Step 6: NavMesh Baking Pipeline
1. Run **Tools > World Builder > 1. Mark Environment Static** (ensures `--- ENVIRONMENT ---` has static flags).
2. Run **Tools > World Builder > 3. Bake NavMesh**.
   - Note: Because doors and barricades are under `--- DYNAMIC ---`, the base NavMesh will bake through doorways.
   - In runtime, the `NavMeshObstacle` on closed doors cuts the NavMesh; when opened, the cut is cleared, allowing Crawlers and Sentries to pursue.

---

## 4. Input Configuration Reference

The project uses `StarterAssets.inputactions` on the `Player` map with `PlayerInput` in **Send Messages** mode:

| Action | Type | Keyboard & Mouse | Gamepad | Callback Method |
|---|---|---|---|---|
| `Move` | Value (Vector2) | `W, A, S, D` / Arrows | Left Stick | `OnMove(InputValue)` |
| `Look` | Value (Vector2) | Mouse Delta | Right Stick | `OnLook(InputValue)` |
| `Jump` | Button | `Space` | South Button (`A`) | `OnJump(InputValue)` |
| `Sprint` | PassThrough | `Left Shift` | Left Trigger (`LT`) | `OnSprint(InputValue)` |
| `Interact` | Button | `E` | West Button (`X`) | `OnInteract(InputValue)` |
| `Throw` | Button | `Left Mouse Button` | Right Trigger (`RT`) | `OnThrow(InputValue)` |
| `Drop` | Button | `Q` | East Button (`B`) | `OnDrop(InputValue)` |

*Note: While carrying an object, pressing either `[E]` (Interact) or `[Q]` (Drop) safely drops the object.*

---

## 5. Master Verification Checklist

> [!IMPORTANT]
> In accordance with project instructions, compilation checks pass with **0 errors and 0 warnings**, but **all runtime behavior tests are marked as Pending** until manual Play Mode testing is performed.

### Category 1: Player Movement & Existing Controls
| Test Item | Expected Result | Status |
|---|---|---|
| 1.1 WASD / Gamepad movement | Player moves smoothly at configured speed without hitching | **Pending** |
| 1.2 Mouse / Stick look | First-person / third-person camera rotates with configured sensitivity | **Pending** |
| 1.3 Jump & Sprint | Jumping clears obstacles; sprint increases movement speed | **Pending** |
| 1.4 Star Collectible Pickup | Walking into existing star collectible triggers particle effect and sound | **Pending** |

### Category 2: Targeting, Wall Obstruction & Contextual Prompts
| Test Item | Expected Result | Status |
|---|---|---|
| 2.1 Interaction range | Objects within 2.5m display prompt; objects beyond 2.5m do not | **Pending** |
| 2.2 Line-of-sight wall obstruction | Interactable behind a wall does not show prompt and cannot be activated | **Pending** |
| 2.3 Contextual prompt text | Prompt updates accurately (`[E] Open Door`, `[E] Restore Generator`, etc.) | **Pending** |
| 2.4 Prompt dismiss | Looking away hides the interaction prompt immediately | **Pending** |

### Category 3: Generator Restoration
| Test Item | Expected Result | Status |
|---|---|---|
| 3.1 First interaction | Pressing `[E]` activates generator: indicator turns green, audio plays | **Pending** |
| 3.2 Duplicate activation prevention | Pressing `[E]` on active generator does nothing; prompt stays "Generator Active" | **Pending** |
| 3.3 Event invocation | `OnRestored` fires exactly once per generator | **Pending** |

### Category 4: Objective Progression & Exit Gate
| Test Item | Expected Result | Status |
|---|---|---|
| 4.1 Initial state | HUD shows `Generators Restored: 0/3`; Exit Gate is locked (`[E] Gate Locked...`) | **Pending** |
| 4.2 Incremental progression | Restoring G1, G2 updates counter to `1/3` and `2/3` | **Pending** |
| 4.3 Objective completion | Restoring G3 updates counter to `3/3` and displays `"All Generators Active!..."` | **Pending** |
| 4.4 Exit Gate unlock | Exit Gate unlocks automatically upon G3 activation; player can open gate to escape | **Pending** |
| 4.5 Attempting locked gate | Pressing `[E]` on gate before 3/3 plays locked audio and denies opening | **Pending** |

### Category 5: Door Mechanics & AI Navigation
| Test Item | Expected Result | Status |
|---|---|---|
| 5.1 Door swing | Pressing `[E]` swings door open $90^\circ$ smoothly over ~0.75s | **Pending** |
| 5.2 Reverse in motion | Pressing `[E]` while door is swinging immediately reverses direction safely | **Pending** |
| 5.3 AI NavMesh closed carve | Enemy agent (`NavMeshAgent`) cannot pathfind through closed door | **Pending** |
| 5.4 AI NavMesh open carve | Opening door past $35^\circ$ allows enemy agent to pathfind through doorway | **Pending** |

### Category 6: Movable Barricades
| Test Item | Expected Result | Status |
|---|---|---|
| 6.1 Contact push | Walking into barricade pushes it forward at max $2\text{ m/s}$ | **Pending** |
| 6.2 Interact push | Pressing `[E]` while targeting barricade gives a physical shove impulse | **Pending** |
| 6.3 Stability | Barricade does not tumble over, spin uncontrollably, or clip through walls | **Pending** |

### Category 7: Pickup, Carry, Throw & Drop Obstruction Guard
| Test Item | Expected Result | Status |
|---|---|---|
| 7.1 Prop pickup | Pressing `[E]` on prop attaches it to player's `HoldSocket` | **Pending** |
| 7.2 Carry prompt | HUD shows `[LMB] Throw \| [E / Q] Drop` while carrying prop | **Pending** |
| 7.3 Throw | Pressing `[LMB]` throws prop forward with velocity ($9.5\text{ m/s}$) and spin | **Pending** |
| 7.4 Clear space drop | Pressing `[E]` or `[Q]` in open space drops prop gently on ground | **Pending** |
| 7.5 Wall obstruction drop guard | Facing directly into a wall and pressing drop retains prop in hands with warning | **Pending** |
| 7.6 Drop obstructed prompt | Blocked drop displays `"Cannot Drop Here (Blocked) \| [LMB] Throw"` for 1.8s | **Pending** |
| 7.7 Point-blank throw | Throwing directly into a wall spawns prop in front of wall, avoiding clipping | **Pending** |
| 7.8 Player collision restoration | Released prop ignores player collision for 0.35s, then restores physical collision | **Pending** |

### Category 8: Input & Anti-Spam
| Test Item | Expected Result | Status |
|---|---|---|
| 8.1 Held button debounce | Holding `[E]`, `[Q]`, or `[LMB]` does not trigger rapid repeated drops/throws | **Pending** |
| 8.2 Gamepad mapping | Gamepad controls (RT throw, B drop, X interact) function identically to KBM | **Pending** |

### Category 9: Console Health & Regressions
| Test Item | Expected Result | Status |
|---|---|---|
| 9.1 Zero console errors | No NullReferenceExceptions or MissingComponentException during gameplay | **Pending** |
| 9.2 Scene reloading | Reloading scene resets objectives and states cleanly without leaked events | **Pending** |

---

## 6. Known Limitations & Technical Notes

1. **Unity AI Navigation Package:** Requires package `com.unity.ai.navigation` (2.0.16) which is already in `Packages/manifest.json`.
2. **NavMesh Baking Dependency:** Enemy navigation through doors requires a baked NavMesh on `--- ENVIRONMENT ---`. If the scene is not baked, `NavMeshObstacle` still runs safely with null guards.
3. **CharacterController Physical Push:** `CharacterController` does not apply standard Rigidbody forces automatically; `MovableBarricade` handles this through `OnControllerColliderHit` physics transfer.
4. **URP Material Instances:** `GeneratorInteractable` uses `MaterialPropertyBlock` for emissive color changes to avoid creating runtime material leak instances in memory.
