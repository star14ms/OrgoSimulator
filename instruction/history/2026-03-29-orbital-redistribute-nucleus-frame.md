# 2026-03-29 — Orbital redistribute nucleus frame

## Summary (since last /compact)

3D guide-group and repulsion redistribute targets were building VSEPR directions in **nucleus-local** space but calling `GetCanonicalSlotPositionFromLocalDirection` as if that space were the orbital **parent** (bond vs nucleus). That mis-framed **bond-parented σ/π** `rot` tuples, broke agreement with lone domains, and showed up badly when a center had multiple σ neighbors (e.g. trigonal **sp²** after π formation).

**`GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent`** converts nucleus → parent (bond) before canonical slot math and is used in `TryComputeRepulsionSumElectronDomainLayoutSlots` and `TryBuildRedistributeTargets3DGuideGroupPrefix` (guide vertex + movers + empty-tier branch).

**Joint rotation** now keeps **π/σ line** orbitals in the tip set (not only `IsSigmaBondLine`), drops skipping excluded-from-rigid guide rows for joint **tip pairs** (still excluded from fragment rigid motion), and documents that guides must contribute to the unified quaternion. **`ApplyRedistributeTargets`** uses **nucleus-local hybrid** via `OrbitalTipDirectionInNucleusLocal` for σ current tips to match nonbond domains.

**π step 2** (`ElectronOrbitalFunction`): after π snap, **`UpdateSigmaBondLineTransformsOnlyForAtoms`** plus a second **`RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute`** on both ends.

**Prefab:** `ElectronOrbital.prefab` bumps `bondAnimStep1Duration` / `bondAnimStep2Duration` from **0.5 → 4** (likely debug/slow-mo); consider reverting before release if unintended.

Other doc clarifications: `orbitalsExcludedFromJointRigidInApplyRedistributeTargets` summary; repulsion remarks; `ComputeJointRedistributeRotationWorldFromTargetsAndStarts` note on guides.
