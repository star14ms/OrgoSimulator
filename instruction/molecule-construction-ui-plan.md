# Molecule Construction UI — Implementation Plan

> **instruction/** is for project update summaries. This plan documents the molecule construction toolbar, edit mode, and disposal area.

## Button Layout

**Replace** the existing "Create Atom" button with a **"More"** button that opens the full periodic table (same behavior as `ButtonCreateAtom`).

**Toolbar layout (2 rows):**

| Row | Buttons |
|-----|---------|
| **1** | H, C, N, O, S, F, Cl, Br, I, More |
| **2** | Cycloalkanes dropdown (cyclopropane → cyclohexane), Benzene ring |

- **Row 1**: 9 element buttons + "More" (10 total). Elements: H(1), C(6), N(7), O(8), S(16), F(9), Cl(17), Br(35), I(53).
- **Row 2**: Cycloalkanes dropdown, Benzene button.

---

## Current State

- **UI**: Single "Create Atom" button opens full periodic table ([`ButtonCreateAtom.cs`](Assets/Scripts/ButtonCreateAtom.cs), [`PeriodicTableUI.cs`](Assets/Scripts/PeriodicTableUI.cs)).
- **Atom creation**: `PeriodicTableUI.CreateAtom(z)` instantiates atom prefab at random viewport position.
- **Bonding**: Via orbital drag-and-drop. `CovalentBond.Create` requires orbitals with 1 electron each for sigma bonds.
- **Selection**: No atom/molecule selection system exists.

---

## Implementation Phases

### Phase 1: UI Layout, Atom Quick-Add Buttons, and Disposal Area

1. **Create horizontal toolbar** — Top bar under Canvas, 2 rows with `HorizontalLayoutGroup` / `VerticalLayoutGroup`.
2. **Row 1 buttons**: H, C, N, O, S, F, Cl, Br, I, More. Each element button creates atom (or adds to selected in edit mode). "More" opens `PeriodicTableUI`.
3. **Row 2**: Cycloalkanes dropdown (options: cyclopropane–cyclohexane), Benzene button.
4. **Remove** standalone "Create Atom" button from scene.
5. **Disposal area** — Bottom-right panel. On `OnPointerUp` when molecule is dragged over disposal bounds, destroy all atoms in molecule.

### Phase 2: Edit Mode and Add-to-Selected

1. **EditModeManager** — `EditModeActive`, `SelectedAtom`, toggle with **E**.
2. **Selection** — Click atom → select; click background → deselect.
3. **Add to selected** — When atom button pressed with selection: create new atom bonded to selected (1 electron from each orbital). Block if no valence slot.
4. **Programmatic bond helper** — `AtomFunction.GetLoneOrbitalWithOneElectron(Vector3 dir)` for direction-based orbital selection; `FormBondProgrammatic` or equivalent.

### Phase 3: H-Auto Mode

1. **H-auto toggle** — Upper right. When on, saturate new atoms with hydrogen.
2. **SaturateWithHydrogen** — For each empty lone orbital, create H and form sigma bond.

### Phase 4: Keyboard Shortcuts

- **H, C, N, O, F, S, I** → add corresponding atom.
- **E** → toggle edit mode.

---

## MoleculeBuilder Presets

- **CreateCycloalkane(n)** — n = 3..6. Carbon ring, single bonds, regular polygon layout.
- **CreateBenzene()** — 6 carbons, alternating single/double bonds (3 double).

---

## File Changes

| File | Action |
|------|--------|
| `AtomQuickAddUI.cs` | **New** — toolbar, atom buttons, dropdown, benzene, More |
| `MoleculeBuilder.cs` | **New** — cycloalkanes, benzene, programmatic bonding |
| `EditModeManager.cs` | **New** — edit mode, selection, E shortcut |
| `DisposalZone.cs` | **New** — disposal area, destroy on drop |
| `AtomFunction.cs` | **Modify** — `GetLoneOrbitalWithOneElectron(Vector3 dir)` |
| `SampleScene.unity` | **Modify** — toolbar hierarchy, disposal zone, remove Create Atom |

---

## Dependencies

1. Phase 1 (toolbar, buttons, More, disposal area) first.
2. Phase 2.2 + 2.4 enable Phase 2.3.
3. MoleculeBuilder depends on programmatic bond API (Phase 2.4).
4. Phase 3 depends on Phase 2.4.
