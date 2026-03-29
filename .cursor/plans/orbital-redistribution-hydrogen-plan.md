# Orbital redistribution and hydrogen-only failures

## Observation

The visual / framework error appears when **adding hydrogen**, not when attaching other atoms. That points to paths that treat **σ→H** differently (snap, hybrid refresh, `maxSlots` on H) rather than generic heavy–heavy bond formation.

## Direction (replaces temporary σ reparenting as primary hypothesis)

1. **Remove `AtomicNumber > 1` guards** in the **redistribution-related** pipeline where they only skip work for “heavy” but the same code is safe for Z=1 (no-op or empty loops). Rationale: hydrogen centers were excluded from hybrid refresh and σ snap even when the meaningful work is on **bonds**; aligning behavior avoids H-specific drift relative to other attachments.

2. **Do not remove** `AtomicNumber > 1` where it encodes **chemistry/geometry semantics** (e.g. “heavy σ neighbor” lists in `TryPlaceTetrahedralHydrogenSubstituentsAboutSingleHeavyNeighbor`, or `FormSigmaBondInstant` choosing which endpoint calls `SnapHydrogenSigmaNeighborsToBondOrbitalAxes`).

3. **Optional follow-up** (if issues remain): revisit σ vs lone-parenthood in `GetOrbitalTargetWorldState` / `RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute` for σ→H only.

## Files touched

- [`Assets/Scripts/AtomFunction.cs`](Assets/Scripts/AtomFunction.cs): `SnapHydrogenSigmaNeighborsToBondOrbitalAxes`, `RedistributeOrbitals3D` bond-angle diagnostics, `RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute` debug conditions.
- [`Assets/Scripts/EditModeManager.cs`](Assets/Scripts/EditModeManager.cs): post–`FormSigmaBondInstant` `SnapHydrogenSigmaNeighborsToBondOrbitalAxes` calls that were gated on `atom.AtomicNumber > 1`.

## Verification

- Re-run :::C–O + H (or equivalent) protonation / H-add flows in 3D.
- Confirm no new console spam from `DebugLogRedistributeOrbitals3DBondAngles` on H unless that flag is on.
