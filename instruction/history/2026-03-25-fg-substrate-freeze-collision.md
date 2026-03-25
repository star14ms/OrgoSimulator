# 2026-03-25 — FG substrate freeze and collision cost

Functional-group attachment no longer builds an O(N) pin set over the whole connected molecule. `RedistributeOrbitals` / `RedistributeOrbitals3D` take `freezeSigmaNeighborSubtreeRoot` so σ-neighbor relax and fragment rigid rotation skip the substrate branch; `BuildFunctionalGroup` passes the attachment `parent` with `pin: null` on internal σ/π steps and routes post-build redistribution through `RedistributeOrbitalsFunctionalGroupSide`.

`AtomFunction` adds `SuppressAutoGlobalIgnoreCollisions` (batch FG builds), refactors collision pairing via `BuildCollisionUniverse`, and adds `SetupIgnoreCollisionsInvolvingAtoms` for O(|involved|·scene) ignores after small edits. `EditModeManager` forwards the freeze through `FormSigmaBondInstant` and `SaturateWithHydrogen` / `SaturateAtomsWithHydrogenPass`, optionally using incremental involving-atom ignores during H pass; default `hAutoMode` is on, and Newman stagger runs after H-auto so tetrahedral saturation does not erase the twist.

`BuildFunctionalGroup` wraps the build in suppress + `finally` that calls `SetupIgnoreCollisionsInvolvingAtoms` for the σ-bond side set (or global fallback).
