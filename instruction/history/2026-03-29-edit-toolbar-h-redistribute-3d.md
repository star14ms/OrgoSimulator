# Update summary (since last /compact)

**When:** 2026-03-29  
**Base:** `ee8e9ec` (last compact ref was pending)

Uncommitted work on `debug-orbital-redistribution`:

**EditModeManager** — Toolbar σ bond from a lone pair: removed the “anchor has a heavy σ neighbor” branch that forced full anchor `RedistributeOrbitals` when adding H to a heavy center (e.g. C already bonded to O). `redistributeAnchor` is now `!heavyHeavy && !hOnHeavy`, matching heavy–heavy “chain extend” behavior for the anchor when the new atom is hydrogen. Saturate-cycloalkane / `AddHydrogenAtDirection` call `SnapHydrogenSigmaNeighborsToBondOrbitalAxes` in 3D without an `AtomicNumber > 1` guard on the center.

**AtomFunction** — Large 3D redistribution pass: joint rigid fragment motion during partner break-lerp and related `ApplyRedistributeTargets` paths; guide-group orbitals excluded from joint rigid rotation via `orbitalsExcludedFromJointRigidInApplyRedistributeTargets`; tetrahedral guide-group azimuth search (`ApplyTetrahedralGuideAzimuthLockAboutVertex0InPlace`) with skip-on-tie / min-θ tie-break; `SnapHydrogenSigmaNeighborsToBondOrbitalAxes` — refresh hybrid alignment before reading tips, per-bond internuclear leg length + `ApplySigmaOrbitalTipFromRedistribution` in the snap loop and a second σ→H pass; bond-angle diagnostics and σ-form hybrid **Debug.Log** gates no longer require `AtomicNumber > 1`; new triage flags for tip-gap trace and joint-fragment milestones (per-frame off).

**ElectronOrbitalFunction** — σ formation step 2: snapshot / apply joint fragment world motion during step-2 lerp with `AtomFunction` helpers and optional milestone logging.

**Assets** — Small change to `ElectronOrbital.prefab`.

**Untracked** — `.cursor/plans/` (orbital redistribution plan notes).
