# Update summary — 2026-03-20 (3D VSEPR & cycloalkanes)

Added **`VseprLayout`** for ideal 3D electron-domain directions (linear through octahedral), ring **—CH₂—** hydrogen placement (~109.5°, puckered out of the C–C–C plane), and a fourth substituent direction for **tertiary** carbons. **`OrbitalAngleUtility`** gates full 3D orbital geometry vs legacy XY behavior.

**`AtomFunction`** gains 3D redistribution (`GetSlotForNewOrbital3D`, `RedistributeOrbitals` with reference bond directions), 3D-friendly selection, and wiring for preset builds. **`ElectronOrbitalFunction`** improves 3D glow/highlighting, canonical slot placement from local directions, electron-pair layout during drag, and removes noisy electron-drag logging. **`ElectronFunction`** uses local ±Y for idle electron pairs in 3D.

**`MoleculeBuilder`**: puckered **C₃–C₆** templates (propane/butane/pentane envelope, **chair hexane** with **C–C–C ≈ 109.5°** via `h/r = √(1/32)`), default **no** attach to edit selection (plain cycloalkane), **Shift+click** cycloalkane item to bond to selected orbital.

**`EditModeManager`**: **`SaturateCycloalkaneWithHydrogen`** adds **two** or **one** H from `4 - σ` count (methylene vs attachment site), with helper orbital picking.

**`CovalentBond`**: fixes **`dirAtoB`/`dirBtoA` scope** in **`BreakBond`** for **`RedistributeOrbitals`**. **`AtomQuickAddUI`** passes **`attachToSelectedOrbital`** when Shift is held.

New script (add + commit): **`Assets/Scripts/VseprLayout.cs`** (+ Unity `.meta`). **`Assembly-CSharp.csproj`** already includes **`VseprLayout.cs`** for CLI builds.
