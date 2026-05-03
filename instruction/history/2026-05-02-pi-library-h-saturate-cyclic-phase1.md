# 2026-05-02 — π library H fill, acyl geometry, cyclic phase-1

Amino-acid library build: re-enabled His side-chain π promotion via `TryPromoteToDoubleBond`. Removed the extra post-π `EnforceTrigonalPlanarAtDoubleBond` redistribution and the hard `ForceTrigonalPlanarNeighborPositionsForPiCenter` pass from `EnforceSideChainAcylTrigonalGeometry` so Asn/Gln amide guide geometry stays consistent with phase-1. `ForceFinalHydrogenFill` calls `SaturateWithHydrogen` with `preferOrbitalSpawnDirectionOnPiCenters: true` so Arg/imine-style N–H follow lobe axes.

`EditModeManager`: `FormSigmaBondInstant` exposes `skipHydrogenSigmaNeighborSnapAfterTail`; `SaturateWithHydrogen` uses optional lobe-based spawn on π centers and skips σ–H neighbor snap after each H on π-bearing centers.

`SigmaBondFormation`: cyclic π phase-1—precompute whether the σ detour has length ≥ 4, run a non-guide-centered phase-1 redistribution when `runBothAtomsInPhase1` is false (unless already covered by template-preview debug), align immediate π with animated pre–phase-1 lobe snap and template-preview flag.

`OrbitalRedistribution`: small clarity refactor around trigonal `TryGetPOrbitalAxisWorldForTrigonalAlign` gating.

Cursor rule `debug-generalize-root-cause`: document avoiding unsolicited “fallback” fixes.
