## Summary (since last /compact)

Orbital-drag guide phase 3 now treats every σ leg whose hybrid **δ** matches the global maximum within a small ε as participating in fragment targeting (`includeForFragment`), not only the single max-index leg—so chemically equivalent branches (e.g. all three −H on a methyl) get the same rigid `FromToRotation` path instead of one leg moving while identical-δ neighbors stay fixed.

Removed remaining phase-3 triage plumbing: hard-coded NDJSON append path/helpers on `SigmaBondFormation`, `Phase3GuideBondLerpActive` and its try/finally toggles, and the paired `CovalentBond` `#region agent log` NDJSON calls in `SyncSigmaOrbitalWorldPoseFromRedistribution` and `LateUpdate`. Dropped unused phase-3 locals (`tipTipSpanDeg`, `maxDeltaIdx`, `maxDirSpanDeg`) and tightened the substituent-motion scan with an early exit.

Untracked in the repo: `.cursor/skills/debug/` and `.cursor/skills/fix/` (workflow skills—not part of the script diff above).
