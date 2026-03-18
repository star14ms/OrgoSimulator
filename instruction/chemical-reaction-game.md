# Chemical Reaction Educational Game — Instructions

> **instruction/** is where project update summaries and detailed instructions live. Use these docs when implementing or modifying the chemical reaction game.

## Domain Model

- **Atom**: Core entity; can possess 1–4 electron orbitals (1 for H/He; 4 for others). Has a radius used for bonding checks. When created, spawns orbitals at cardinal positions (up, down, right, left). Orbitals are anchored to the atom. Has a **charge** (int): displayed as **oxidation state** using Pauling electronegativity—bonding electrons assigned to the more electronegative atom; equal EN splits 50-50.
- **Electron**: Individual particle; stays in the orbital, occupying half the orbital space. No orbiting; positioned statically via `ElectronFunction` (slotIndex, halfSpaceOffset). Two electrons: one in each half.
- **Electron Orbital**: Holds up to 2 electrons; can bond to an atom. Placed at dynamic slot positions (360°/n) around the atom. Rotated to face outward from the atom based on relative position (`Mathf.Atan2(dir.y, dir.x)`). Uses `ElectronOrbitalFunction`; spawns `ElectronFunction` children from prefab based on `ElectronCount`.

## Atom Creation

When an atom is created, it receives an **atomic number**. Spawn four electron orbitals and place them around the atom at cardinal positions (north, south, east, west). Orbitals are anchored to the atom. Rotate each orbital based on its direction from the atom so it faces outward. Add a **label at the center of the atom** displaying the element symbol (e.g. H, C, O) based on atomic number.

**Valence electrons** depend on the elemental group (1–18). Use `valenceElectrons = Mathf.Max(0, valence - charge)` so initial charge affects electron count. Distribute valence electrons across orbitals (max 2 per orbital):

| Group | Valence |
|-------|---------|
| 1 | 1 |
| 2 | 2 |
| 13 | 3 |
| 14 | 4 |
| 15 | 5 |
| 16 | 6 |
| 17 | 7 |
| 18 | 8 |

## Constraints

- Max 4 orbitals per atom (1 for H/He)
- Max 2 electrons per orbital
- Orbital positions: dynamic slots via `GetSlotAnglesForCount(n)` (4 slots: 0°, 90°, 180°, 270°)

## Interaction Mechanics

- **Drag**: User drags electron clouds (orbitals) and electrons. No bond break—orbitals stay bonded. While dragging an orbital, the main sprite is hidden and a **stretch sprite** is shown instead—a separate GameObject that extends from the atom (anchor) to the cursor. The orbital transform moves with the cursor (no scaling); electrons keep their size. On release, stretch visual is destroyed and the main sprite returns. Assign `stretchSprite` for a single custom sprite; if null, a composite of procedural **triangle** + **circle** is built. Triangle base = circle diameter; apex has `apexPadding` from atom center; circle center = triangle base. Triangle uses `OrgoSimulator/TriangleClipCircle` shader to hide the part inside the circle. `stretchVisualScale`, `circleRadius`, `apexPadding` are tunable.
- **Purpose**: Simulates chemical reactions through drag-and-drop bonding.
- **Mouse influence**: Atoms and electron orbitals near the cursor are affected by the player's mouse movement—automated movement (e.g., repulsion, attraction, or displacement) based on cursor position/velocity.
- **Electrons in bonded orbitals**: Electrons in orbitals that are part of a covalent bond (`orbital.Bond != null`) cannot be dragged. `ElectronFunction.OnPointerDown` returns early when the parent orbital has a bond.

## Pi Bond Support Summary

- **Double/triple bonds**: Multiple `CovalentBond` instances between the same atom pair; first = sigma, additional = pi.
- **Formation**: When `GetBondsTo(targetAtom) >= 1`, skip `RearrangeOrbitalToFaceTarget` and `AlignSourceAtomNextToTarget`; merge electrons and create bond.
- **Visual**: Sigma centered; pi lines offset perpendicular (0.2 spacing). Canonical atom order for consistent offset.
- **Colliders**: Each bond has its own line+collider; collider on `lineVisual` so it follows the line. `BondLineColliderForwarder` forwards clicks to parent.
- **OrbitalAngleUtility**: World-space angles for sigma bond flip/rearrange logic (avoids wrong-side-emptied bug).
- **RedistributeOrbitals**: Called after bond formation/breakage when pi bond count changes, or when forming/breaking a sigma bond (e.g. bonding to an orbital that was already rotated). Redistributes non-pi orbital angles to minimize total angular displacement.
- **Bond orbital position**: Orbital is reparented to the bond and positioned at the bond (along the line), rotated to point along the bond direction—not where the target orbital was.

## Covalent Bond Display (Pi Bonds: Double, Triple)

- **Architecture**: Each bond (sigma, pi) is a separate `CovalentBond` GameObject. Each has its own line visual and collider—no shared collider moved between positions.
- **Single bond**: One black line at center between atoms.
- **Double bond**: Two lines—sigma (index 0) centered; pi (index 1) offset perpendicular to bond. Offset uses canonical atom order (smaller InstanceID first) so direction is consistent for any bond angle.
- **Triple bond**: Three lines—sigma centered; pi bonds at ±`piOffset` (0.2) perpendicular to bond.
- **Offset direction**: Perpendicular to bond (works for any bond angle). `GetLineOffset(bondIndex, bondCount)` returns 0 for sigma, alternating ± for pi.
- **Line + collider**: Each bond's `lineVisual` has `SpriteRenderer` and `BoxCollider2D`. Collider size = sprite base (0.5 × 1) so it scales with the line. `BondLineColliderForwarder` forwards pointer events to parent `CovalentBond`.
- **On press**: Click a bond line to show its orbital; drag to break. Release without breaking returns to line view.
- **Bond orbital**: Reparented to the bond; positioned at the bond (center + perpendicular × offset), rotated to point along the bond direction—not where the target orbital was. `SetPointerBlocked(true)` and `SetVisualsEnabled(false)` when line view; orbital shown only while pressing the line.

## Bond Formation Animation (Steps 1–3)

- **Step 1 (align molecule)**: Animate atoms to target positions. Skip when all atoms already within 0.05 of target (`atomsAlreadyAligned`).
- **Step 2 (rearrange + redistribute + orbital to bond)**: Animate surviving orbital to bond center; optionally rearrange/redistribute other orbitals. Sigma bonds use `GetRedistributeTargets(piBefore, newBondPartner)` so redistribution runs when bonding to rotated orbitals. Skip when no rearrange/redistribute needed and orbital already at bond position.
- **Step 3 (orbital-to-line)**: Shrink orbital, grow line, fade target orbital (sigma) or source orbital (pi).
- **Electron overlap avoidance**: Between steps 2 and 3, ensure two electrons don't overlap—rotate one orbital 180° when needed.

### Sigma Bond Orbital Flip

- When source orbital direction is opposite to bond (`|sourceDiff| > 90°`), set `bond.orbitalRotationFlipped = true` so the bond orbital uses `worldRot * 180°`.
- `CovalentBond.orbitalRotationFlipped` is applied in `GetOrbitalTargetWorldState` and `LateUpdate` for orbital positioning.

### Pi Bond Orbital Flip

- Both source and target orbitals animate to the same bond center; one must be rotated 180° so electrons align in a row.
- Flip decision: `flipTarget = |sourceDiff| < |targetDiff|` (source closer to bond → flip target; target closer → flip source).
- When `flipTarget`: set `bond.orbitalRotationFlipped`, animate target to flipped rotation, source to opposite.
- When `!flipTarget`: animate source to `bondRot * 180`, target to `bondRot`.

### Bond Orbital Stretch (Drag)

- **Bond orbitals**: No apex padding; triangle tip can reach origin. No minimum stretch length (`minLength = 0`). No early return when distance < 0.01.
- **Lone orbitals**: Use `apexPadding` and `MinStretchLength` to avoid overlapping nucleus.
- Clip radius uses `Mathf.Max(..., 0.001f)` to avoid division by zero when stretch collapses.

## Bond Formation and Flip Logic

- **Sigma bond (first)**: When `!alreadyBonded`, run flip logic and `RearrangeOrbitalToFaceTarget` / `AlignSourceAtomNextToTarget` before creating bond.
- **Pi bond (second, third)**: When `alreadyBonded` (`GetBondsTo(targetAtom) >= 1`), skip rearrange/align; merge electrons, unbond both orbitals, create bond, destroy source orbital. No flip or orbital swap.
- **Source molecule movement**: For sigma bonds, the source atom and its connected molecule move to align with the target orbital.
- **Flip when opposite side filled**: If the source atom's side opposite to the target orbital is already filled (tolerance 45°), flip: move the target atom to the source instead. Uses `OrbitalAngleUtility` for world-space angle comparison.
- **Original position**: Use `originalLocalPosition` / `originalLocalRotation` for flip checks and alignment (not dragged position). Reset source orbital to original slot before creating bond in flip case.
- **Pi bonds and flip**: Do not flip when source has pi bonds and is not already aligned (`cannotRearrangeSource`). Do not flip when target would also flip but target has pi bonds and is not aligned with source's original tip (`cannotRearrangeTarget`). Use `sourceOrbitalOriginalTip = sourceAtom.transform.TransformPoint(originalLocalPosition)` for target alignment check—not the dragged position.
- **Target alignment (flip case)**: `IsOrbitalAlignedWithTargetPoint(targetOrbital, sourceOrbitalOriginalTip)` checks if target's orbital points toward the source's original tip. `RearrangeOrbitalToFaceTarget` accepts optional `targetPointOverride` (source's original tip) for flip case so direction and freed slot use the correct position.
- **Both sides filled**: If both atoms would flip and target cannot rearrange (pi bonds, not aligned), return false—no bond formed.
- **Orbital swap**: Dragging one orbital onto another on the same atom swaps their positions. `SwapPositionsWith` uses `originalLocalPosition` and `originalLocalRotation`.

## Orbital Slot Management

- **GetSlotForNewOrbital**: Uses `GetSlotAnglesForCount(GetOrbitalSlotCount())` for slot angles; slot tolerance = 360°/(2×slotCount). Supports `excludeOrbital` to avoid overlap. Returns canonical slot position for preferred direction.
- **GetOrbitalSlotCount**: Returns 4 for non-H/He, 1 for H/He (fixed; not reduced by pi bonds).
- **RedistributeOrbitals**: Called after bond formation (`FormCovalentBond`) and `BreakBond` when pi bond count changes or when forming/breaking a sigma bond. Uses `RedistributeOrbitals(float? piBondAngleOverride)` for bond slot exclusion. **Origin angle** (unless pi bond present): bond formation → direction to bond partner; bond break → 0°. `GetRedistributeTargets(piBefore, newBondPartner)` used for bond formation; pass `newBondPartner` for sigma redistribution.
- **Pi bonds**: Multiple bonds between the same atom pair; first = sigma, additional = pi. Each bond is a separate `CovalentBond` instance with its own orbital.
- **BreakBond**: Passes broken orbital as `excludeOrbital` when computing new slot. `SetPointerBlocked(false)` and `SetVisualsEnabled(true)` before reparenting. Calls `RedistributeOrbitals` on both atoms when pi count changes, when atom has pi bonds and got a new lone orbital, when breaking a sigma bond, or when `HasInconsistentOrbitalAngles()` returns true (orbital angular distances not evenly spaced).

## OrbitalAngleUtility

- **Purpose**: Consistent world-space angle convention (0° = right) for comparing orbital directions across different atoms. Avoids local-space mismatches that caused wrong-side-emptied bugs.
- **Methods**: `DirectionToAngleWorld(Vector3)`, `GetOrbitalAngleWorld(Transform)`, `LocalRotationToAngleWorld(Transform, Quaternion)`, `NormalizeAngle(float)`.
- **Used in**: `RearrangeOrbitalToFaceTarget`, `IsSourceFlippedSideFilled`, `IsSourceOrbitalAlreadyAlignedWithTarget`, `IsOrbitalAlignedWithTargetPoint` in `ElectronOrbitalFunction`.

## Charge Indicator

- **Atom charge**: Displayed as oxidation state (int). Computed via `ComputeCharge()` using Pauling electronegativity: bonding electrons assigned to the more electronegative atom; equal EN splits 50-50. Positive = oxidation; negative = reduction.
- **Charge label**: Child of ElementLabel (`ElementLabel/ChargeLabel`). Displays `"X+"` for positive, `"X-"` for negative (use `Mathf.Abs(charge)` for the number). Hidden when `charge == 0`.
- **Updates**: `OnElectronRemoved()` → `charge++`; `OnElectronAdded()` → `charge--`. Both call `RefreshChargeLabel()`.
- **Layout**: Charge label positioned via `chargeLabelOffset` (fraction of elementLabelSize). Smaller font than element symbol.

## Releasing and Accepting Electrons

- **Bonded orbitals**: Electrons in orbitals with `orbital.Bond != null` cannot be dragged; `OnPointerDown` returns early.
- **Release from orbital**: When user drags an electron and releases, if `!orbital.ContainsPoint(transform.position)` the electron has left the orbital. Call `orbital.RemoveElectron(this)` to detach it and update the bonded atom's charge.
- **Accept into orbital**: After release, `TryAcceptIntoOrbital()` searches all orbitals for one where `CanAcceptElectron()` (electronCount < 2) and `ContainsPoint(transform.position)`. If found, call `orb.AcceptElectron(this)`.
- **AcceptElectron**: Destroys the dragged electron, increments `electronCount`, calls `SyncElectronObjects()` to spawn new electron(s), then `bondedAtom.OnElectronAdded()` and `SetupIgnoreCollisions()`.
- **RemoveElectron**: Decrements `electronCount`, reparents electron to world (null parent), calls `bondedAtom.OnElectronRemoved()`.
- **During drag**: Orbital sets `SetPointerBlocked(true)` and `SetPhysicsEnabled(false)`; electron sets `SetIgnoreCollisionsWithAllOrbitals(true)` so it can pass through other orbitals for drop detection.

## Scripts and Setup

| Script | Role |
|--------|------|
| `AtomFunction` | Atom; `atomicNumber`, `bondRadius`, `charge`, `orbitalPrefab`. On Start, derives group from periodic table (1–118), maps to valence, creates orbitals at cardinal positions. `GetBondsTo(other)`, `GetPiBondCount()`, `GetOrbitalSlotCount()` (returns 4 for non-H/He). `CanAcceptOrbital`, `GetSlotForNewOrbital(preferredDirection, excludeOrbital)`. `HasInconsistentOrbitalAngles()` returns true when orbital angular distances are not evenly spaced. `GetRedistributeTargets(piBefore, newBondPartner)` returns redistribution targets; pass `newBondPartner` for sigma bond formation. `RedistributeOrbitals(float? piBondAngleOverride)` called after bond formation/breakage when pi count changes, sigma bond forms/breaks, or orbital angles inconsistent. `GetConnectedMolecule()` for bond formation alignment. Lanthanides (57–71) and actinides (89–103) = group 3. |
| `ElectronOrbitalFunction` | Orbital; `electronCount`, `electronPrefab`, `stretchSprite` (optional). `Bond` property when part of covalent bond. Spawns `ElectronFunction` children via `SyncElectronObjects()`. **Bond formation**: sigma bonds use flip logic and `RearrangeOrbitalToFaceTarget` (with `OrbitalAngleUtility` for world-space angles); pi bonds (`alreadyBonded`) skip rearrange/align and just form the bond. Flip uses `IsSourceOrbitalAlreadyAlignedWithTarget`, `IsOrbitalAlignedWithTargetPoint`, and `sourceOrbitalOriginalTip` for target alignment. `RearrangeOrbitalToFaceTarget` accepts optional `targetPointOverride` for flip case. Sigma and pi bond formation call `GetRedistributeTargets(piBefore, bondPartner)` to animate orbital redistribution. `SwapPositionsWith` for same-atom orbital swap. |
| `ElectronFunction` | Electron; static position along orbital width. `SetSlotIndex(i)`, `SetOrbitalWidth(w)`. Draggable only when `orbital.Bond == null`; on release outside orbital, `RemoveElectron` then `TryAcceptIntoOrbital()`; inside orbital returns to original position. Requires Collider for mouse input. |
| `CovalentBond` | Bond between two atoms; owns shared orbital. Orbital reparented to bond; positioned at bond (center + perpendicular × offset), rotated to point along bond direction. `orbitalRotationFlipped` when true applies 180° to orbital rotation (sigma/pi electron overlap avoidance). Each bond has its own `lineVisual` (SpriteRenderer + BoxCollider2D) and `BondLineColliderForwarder`. Sigma (index 0) centered; pi bonds offset perpendicular via `GetLineOffset()`. Canonical atom order (smaller InstanceID first) for consistent offset direction. Collider size = sprite base (0.5×1) to scale with line. `BreakBond(returnOrbitalTo)` reparents orbital back to atom, creates new lone orbital on other atom; calls `RedistributeOrbitals` on both atoms when pi count changes, when atom has pi bonds and got a new lone orbital, when breaking a sigma bond, or when `HasInconsistentOrbitalAngles()` is true. |
| `OrbitalAngleUtility` | Static utility for world-space angle convention (0° = right). `DirectionToAngleWorld`, `GetOrbitalAngleWorld`, `LocalRotationToAngleWorld`, `NormalizeAngle`. Used in `RearrangeOrbitalToFaceTarget` and `IsSourceFlippedSideFilled`. |
| `BondLineColliderForwarder` | Forwards `IPointerDownHandler`/`IDragHandler`/`IPointerUpHandler` from lineVisual (child) to parent `CovalentBond`. |
| `PeriodicTableUI` | Periodic table panel; `cellSize`, `spacing`, `blockPadding`, `fontSize`. Shows 118 elements; click backdrop to close. Scroll area 0.05–0.95. |
| `ButtonCreateAtom` | UI button; `atomPrefab`, `viewportMargin`. Opens periodic table; creates atom at random position with selected atomic number. Add listener in Start via `TryGetComponent<Button>`. |

**Mouse influence**: Implement automated movement for atoms and orbitals within a radius of the cursor; their position/velocity responds to player mouse movement (e.g., repulsion from cursor, or subtle drift toward/away from mouse).

**Prefabs**: Atom prefab needs `orbitalPrefab` (ElectronOrbitalFunction). Orbital needs `electronPrefab` (ElectronFunction). ButtonCreateAtom needs `atomPrefab` assigned. Requires `Collider` on orbitals and electrons for mouse input.

**Orbital rotation**: Rotate orbitals by `angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg`; apply `Quaternion.Euler(0, 0, angle)` so each orbital faces outward from the atom. Apply in `CreateOrbitalsWithValence`.

**Atom label**: When an atom is created, add a TextMeshPro label at the atom center (localPosition 0,0,0) displaying the element symbol. Use a static lookup for symbols 1–118 (H, He, Li, Be, B, C, N, O, F, Ne, …).

---

## Rule Maintenance

**When introducing a new feature**: Add corresponding instructions to `instruction/` (e.g. this file or a new `.md`). Document the feature's purpose, behavior, relevant APIs/properties, and any constraints. Update the Scripts table if new scripts or responsibilities are added. Keep `.cursor/rules/` concise and pointing to `instruction/` for details.
