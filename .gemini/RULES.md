# OrgoSimulator — Project Overview

**OrgoSimulator** is a **sandbox game** for simulating **organic chemistry reactions**. Players build molecules, manipulate electrons, and observe reaction mechanisms in an interactive 2D/3D environment.

## Current Development Focus

1. **Bond-making / bond-breaking processes** — implementing the core mechanics for forming and breaking covalent bonds (σ and π), including curved-arrow electron movement.
2. **3D conformation (VSEPR)** — positioning electron orbitals and bonds in 3D based on the number of electron orbital groups around each atom (linear, trigonal planar, tetrahedral, etc.).

## Planned Features

- **Problem & quiz mode** — Reaction-prediction challenges and mechanism-solving quizzes.
- **Lessons** — Guided tutorials covering key organic chemistry concepts (functional groups, stereochemistry, named reactions, etc.).

---

# Temporary: leave `RedistributeOrbitals3D` alone

**Status:** Temporary. Remove this rule when the planned `RedistributeOrbitals3D` rewrite is done.

In `Assets/Scripts/AtomFunction.cs`:

1. **Do not** change the body of `RedistributeOrbitals3D` except to keep it as a thin forwarder to `RedistributeOrbitals3DOld` (or the eventual replacement), unless the user explicitly asks to replace that forwarder.
2. Put behavioral fixes in **`RedistributeOrbitals3DOld`**, **`RedistributeOrbitals`** (2D path), **call sites** (e.g. `FormSigmaBondInstant`, `SnapHydrogenSigmaNeighborsToBondOrbitalAxes`), or **`CovalentBond`** — not inside `RedistributeOrbitals3D`.

```csharp
// ✅ OK: one-line delegate to RedistributeOrbitals3DOld
// ❌ Avoid: inlining or rewriting the full 3D pipeline inside RedistributeOrbitals3D without direct user request
```

---

# Debug logs: default flags on

When adding **new** diagnostic logging (e.g. `Debug.Log`, `ProjectAgentDebugLog.MirrorToProjectDotLog`, or a dedicated `LogXyz` helper) that is gated by a `bool` flag (`static`, `[SerializeField]`, or instance field):

1. **Initialize the flag to `true` by default** so output appears in Play mode and in `.log` without the developer hunting for toggles.
2. Document in the **summary comment** on that flag that it defaults on for triage and can be set to `false` for quiet runs.
3. **Exception**: If the log would run **every frame** or **flood** the console (e.g. per-orbital `Update`), default **`false`** and say so in the comment; offer a one-line note in the rule summary when you choose this.

Pattern:

```csharp
// ✅ When introducing a new triage flag — default on
/// <summary>… Default on for triage; set false for quiet runs.</summary>
public static bool DebugLogMyFeature = true;

// ❌ Avoid for new flags unless exception (high-frequency / spam risk)
public static bool DebugLogMyFeature = false;
```

Existing legacy flags may stay default-off; apply this rule to **newly added** debug gates in the same change as the logs.

---

# Debug and diagnostic logging format

When adding or editing `Debug.Log`, `Debug.LogWarning`, `ProjectAgentDebugLog.MirrorToProjectDotLog`, or similar user-visible diagnostic output:

1. **Bracket prefix** — Each log line MUST begin with a bracketed tag so Console and `.log` filtering stay consistent, e.g. `[σ-form-rot-pose]`, `[break-tetra]`, `[my-feature]`.
2. **No `\n` in messages** — Do not concatenate multiple logical lines into one string. Emit **separate** calls per line (each with its own prefix). If you would have used `\n`, use multiple `Debug.Log` / mirror calls instead.
3. **One idea per line** — Keep each line a single record (ids, numbers, booleans); avoid prose paragraphs in one log call.

**Exceptions**

- Unity or third-party APIs that require a single blob (e.g. some stack-trace dumps): keep as one call; add a comment at the call site.
- **High-frequency** logs (per-frame): gate behind a flag and prefer default **off** with a comment (see debug flags rule exception for flood risk).

This applies to new logs and to refactors of existing multi-line `StringBuilder` + single `Debug.Log` patterns.

---

# Domain: Organic Chemistry Simulation

Sandbox game simulating organic chemistry reactions with electron-level detail. Main species: **Atom**, **Electron**, **Electron Orbital**.

## Domain Model

- **Atom**: Core entity; can possess up to 4 electron orbitals (bonds). Has a radius used for bonding checks. When created, spawns four electron orbitals placed at north, south, east, and west around the atom; orbitals are anchored to the atom. Has a **charge** (int) for the label: default **Lewis formal / octet** (half of bonding electrons); optional **oxidation state** (Pauling EN) via `AtomFunction.ChargeDisplayMode` / lower-left HUD toggle. **Element + charge labels** (`ElementLabel` child): `AtomFunction.LateUpdate` billboards toward `Camera.main` (`LookRotation` to camera) so text stays upright and readable while the molecule or camera moves in 3D.
- **Electron**: Individual particle; stays in the orbital, occupying half the orbital space. No orbiting; positioned statically via `ElectronFunction` (slotIndex, halfSpaceOffset). Two electrons: one in each half.
- **Electron Orbital**: Holds up to 2 electrons; can bond to an atom. Placed at cardinal positions (north, south, east, west) around the atom. Rotated to face outward from the atom based on relative position (`Mathf.Atan2(dir.y, dir.x)`). Uses `ElectronOrbitalFunction`; spawns `ElectronFunction` children from prefab based on `ElectronCount`.

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

- Max 4 orbitals per atom
- Max 2 electrons per orbital
- Orbital positions: north, south, east, west (fixed slots)

## Interaction Mechanics

- **Drag**: User drags electron clouds (orbitals) and electrons. No bond break—orbitals stay bonded. While dragging an orbital, the main sprite is hidden and a **stretch sprite** is shown instead—a separate GameObject that extends from the atom (anchor) to the cursor. The orbital transform moves with the cursor (no scaling); electrons keep their size. On release, stretch visual is destroyed and the main sprite returns. Assign `stretchSprite` for a single custom sprite; if null, a composite of procedural **triangle** + **circle** is built. Triangle base = circle diameter; apex has `apexPadding` from atom center; circle center = triangle base. Triangle uses `OrgoSimulator/TriangleClipCircle` shader to hide the part inside the circle. `stretchVisualScale`, `circleRadius`, `apexPadding` are tunable.
- **Purpose**: Simulates chemical reactions through drag-and-drop bonding.
- **Mouse influence**: Atoms and electron orbitals near the cursor are affected by the player's mouse movement—automated movement (e.g., repulsion, attraction, or displacement) based on cursor position/velocity.

## Charge Indicator

- **Atom charge**: Displayed int on `ChargeLabel`. `AtomFunction.ChargeDisplayMode` (HUD lower-left toggle in `AtomQuickAddUI`): default **Octet formal** (`ComputeOctetFormalCharge`, half of each bond's electrons); optional **oxidation state** (`ComputeOxidationStateCharge`, Pauling EN). `ComputeCharge()` follows the current mode. `AtomFunction.RefreshAllDisplayedCharges()` updates all scene atoms after toggling.
- **Charge label**: Child of ElementLabel (`ElementLabel/ChargeLabel`). Displays `"X+"` for positive, `"X-"` for negative (use `Mathf.Abs(charge)` for the number). Hidden when `charge == 0`.
- **Updates**: `OnElectronRemoved()` → `charge++`; `OnElectronAdded()` → `charge--`. Both call `RefreshChargeLabel()`.
- **Layout**: Charge label positioned via `chargeLabelOffset` (fraction of elementLabelSize). Smaller font than element symbol.

## Releasing and Accepting Electrons

- **Release from orbital**: When user drags an electron and releases, if `!orbital.ContainsPoint(transform.position)` the electron has left the orbital. Call `orbital.RemoveElectron(this)` to detach it and update the bonded atom's charge.
- **Accept into orbital**: After release, `TryAcceptIntoOrbital()` searches all orbitals for one where `CanAcceptElectron()` (electronCount < 2) and `ContainsPoint(transform.position)`. If found, call `orb.AcceptElectron(this)`.
- **AcceptElectron**: Destroys the dragged electron, increments `electronCount`, calls `SyncElectronObjects()` to spawn new electron(s), then `bondedAtom.OnElectronAdded()` and `SetupIgnoreCollisions()`.
- **RemoveElectron**: Decrements `electronCount`, reparents electron to world (null parent), calls `bondedAtom.OnElectronRemoved()`.
- **During drag**: Orbital sets `SetPointerBlocked(true)` and `SetPhysicsEnabled(false)`; electron sets `SetIgnoreCollisionsWithAllOrbitals(true)` so it can pass through other orbitals for drop detection.

## Scripts and Setup

| Script | Role |
|--------|------|
| `AtomFunction` | Atom; `atomicNumber`, `bondRadius`, `charge`, `orbitalPrefab`. On Start, derives group from periodic table (1–118), maps to valence, creates 4 orbitals at N/S/E/W with valence electrons. Rotates each orbital via `Quaternion.Euler(0, 0, Atan2(dir.y, dir.x) * Rad2Deg)`. Creates ElementLabel with element symbol and ChargeLabel (child). `OnElectronRemoved()` / `OnElectronAdded()` update charge and `RefreshChargeLabel()`. Lanthanides (57–71) and actinides (89–103) = group 3. |
| `ElectronOrbitalFunction` | Orbital; `electronCount`, `electronPrefab`, `stretchSprite` (optional), `orbitalVisualAlpha`, `debugDraw3DElectronDrag` (Scene gizmos while dragging 3D orbital). **Lone lobe slot swap** (`TryFindSwapTarget` / `SwapPositionsWith`): not offered when one lobe is **empty** (`ElectronCount == 0`) and the other **has electrons**. **Perspective idle**: `LateUpdate` keeps 3D electrons at **local origin** unless an electron is pointer-dragged (`IsElectronPointerDragActive`). **Orthographic / 3D drag / stretch**: unchanged from prior rules (`SyncElectronObjects`, cone+hemisphere, etc.). |
| `ElectronFunction` | Electron; **orthographic**: sprite + offset along orbital width (`SetSlotIndex` / `SetOrbitalWidth`). **Perspective**: **black** URP Lit sphere (`electron3DSphereRadius`), **`SphereCollider`**. Idle: **orbital center** (`localPosition` zero). **Orbital drag (3D)**: electrons **inside hemispherical cap** from seam: `+stretch.up * capRadius * drag3DElectronDepthInCapFraction` (not on outer dome), `Update3DElectronPositionsForStretchDrag`. After drag: `ApplyOrbitalSlotPosition`. Draggable: on release outside orbital, `RemoveElectron` then `TryAcceptIntoOrbital()`; inside orbital returns to original position. |
| `CovalentBond` | Bond between two atoms; owns shared orbital. **`AuthoritativeAtomForOrbitalRedistributionPose`**: heavy end when one partner is H (Z=1), else lower `InstanceID` — so σ hybrid sync after VSEPR runs on the heavy center (H never runs full 3D redistribute). **Orthographic**: black sprite line (pi bonds offset with parallel lines). **Perspective / non-orthographic `Camera.main`**: black **cylinder** mesh along the true 3D axis (`Quaternion.FromToRotation`), same length/thickness scaling as the sprite. Step 3 bond formation scales cylinder height with `orbitalToLineAnimProgress` like the 2D line. |
| `AtomQuickAddUI` | Molecule toolbar (elements, **Func. grp.** + cycloalkanes dropdowns, benzene, e⁻ test); **lower-left** charge mode toggle (octet formal vs oxidation #). **Func. grp.** button is **always visible**; **interactable** when `FunctionalGroupAttachmentReady()` (edit mode + valid anchor). Cyclo/func menus **close on outside click** (`LateUpdate` rect test) or when opening the other dropdown. |
| `MoleculeBuilder` | Cycloalkanes, benzene, and **`BuildFunctionalGroup`** (programmatic σ/π fragments for common substituents). |
| `EditModeManager` | **`TryAttachFunctionalGroup(FunctionalGroupKind)`**: attach by **replacing selected H** or bonding to **`selectedOrbital`** (free 1e, no bond). **`OnAtomClicked`** in edit mode sets **`selectedOrbital`** via `GetOrbitalClosestToAngle(0)` so FG is enabled without a separate orbital click. **`FunctionalGroupAttachmentReady()`** matches that anchor (no extra "explicit orbital only" gate for non-H). |
| `ButtonCreateAtom` | UI button; `atomPrefab`, `viewportMargin`. Creates an atom at a random position with a random atomic number (1–118). Logs atom creation; diagnostic logs for missing prefab/camera. Add listener in Start via `TryGetComponent<Button>`. |

**Mouse influence**: Implement automated movement for atoms and orbitals within a radius of the cursor; their position/velocity responds to player mouse movement (e.g., repulsion from cursor, or subtle drift toward/away from mouse).

**Prefabs**: Atom prefab needs `orbitalPrefab` (ElectronOrbitalFunction). Orbital needs `electronPrefab` (ElectronFunction). ButtonCreateAtom needs `atomPrefab` assigned. Requires `Collider` on orbitals and electrons for mouse input.

**Orbital rotation**: Rotate orbitals by `angle = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg`; apply `Quaternion.Euler(0, 0, angle)` so each orbital faces outward from the atom. Apply in `CreateOrbitalsWithValence`.

**Atom label**: When an atom is created, add a TextMeshPro label at the atom center (localPosition 0,0,0) displaying the element symbol. Use a static lookup for symbols 1–118 (H, He, Li, Be, B, C, N, O, F, Ne, …).

---

## Rule Maintenance

**When introducing a new feature**: Add corresponding instructions to this rule file. Document the feature's purpose, behavior, relevant APIs/properties, and any constraints. Update the Scripts table if new scripts or responsibilities are added. This keeps the rule in sync with the codebase and helps AI assistants and developers understand the system.
