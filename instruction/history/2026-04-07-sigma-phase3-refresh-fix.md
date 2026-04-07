# 2026-04-07 — σ phase-3 guide refresh stabilization

Since the last /compact marker (`Ref: pending`, previous batch tracked against `ab81d83`), this delta is a focused follow-up in two runtime files. `SigmaBondFormation` now keeps phase-3 guide ownership latched from phase 1 (instead of re-resolving guide/non-guide after `Create`, which could swap sides and mutate the wrong center).

`AtomFunction.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute` now preserves guide non-op lone orientation during predictive **sigma** post-bond refresh by skipping lone remap application when the predictive operation bond is sigma, while still syncing sigma tip locks. This removes the step-3 lone-group teleport without reverting the refresh system itself.
