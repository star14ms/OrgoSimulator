# Updates since last /compact (2026-03-28)

Uncommitted changes vs `5671653` (previous compact still **Ref: pending**).

- **`AtomFunction`:** Added `AcuteAngleBetweenDirections` and rewired `EmptyTipAlreadyIdealVsElectronFramework` so a single framework axis is “ideal” only when the empty tip is **perpendicular** to that axis in the undirected sense (acute ≈ 90°), not anti-parallel along the same line; separation from `occupiedLobeAxesMustSeparateFrom` uses the helper. Guide-group mover collection now takes optional `redistributionOperationBond`, skips `OrbitalBeingFadedForCharge`, and skips the forming bond’s 0e host lobe so transient formation lobes are not VSEPR movers; `TryBuildRedistributeTargets3DGuideGroupPrefix` passes the bond through.
- **`CovalentBond`:** Exposes `OrbitalBeingFadedForCharge` internally for the mover filter.
- **`EditModeManager`:** With full 3D orbital geometry, H-auto / `AddHydrogenAtDirection` / cycloalkane saturation use `FormSigmaBondInstant(..., redistributeAtomA: false, redistributeAtomB: false)` and **do not** call `RedistributeOrbitals` after each H (orthographic / 2D paths unchanged). Cycloalkane blocks skip the extra `atom.RedistributeOrbitals()` in 3D only.
