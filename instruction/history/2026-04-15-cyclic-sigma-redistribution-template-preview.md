# Update summary — 2026-04-15

Since last `/compact` (working tree vs `HEAD` `3cdacccd`):

**Orbital redistribution (cyclic σ phase 1)** — Extended `OrbitalRedistribution` with `CyclicRedistributionContext` (final positions, cycle neighbors, pivot/guide σ OPs, chain block set), cyclic final-direction templates pinned to cycle edges, OP bond-site preassign/steal, optional injected OP row for matching when the forming σ was missing from group lists, fixed-first DFS matching, and recursion that passes closure σ OP and respects blocked atoms. Added debug template capture (`Begin/EndDebugTemplateCapture`, snapshots) and `TryGetGuideOrbitalForDebug` for stepped preview tooling. Removed prior agent/NDJSON and verbose triage logs from this path.

**Sigma bond formation** — Cyclic phase-1: ring target positions, redistribution context aligned to final internuclear `(guide − pivot)` in pivot local, optional `debugDisableCyclicSigmaPhase1Redistribution` retained; removed agent NDJSON helpers. Post-bond phase 3 guide redistribution is **always skipped** when `prebondCycleCandidate` (ring-closure prebond path); removed the old `debugDisableCyclicSigmaPhase3Redistribution` toggle.

**Bond formation template preview** — Stepped-mode gating on `BondFormationDebugController.IsWaitingForPhase`, clearer pick path (orbital-linked pick, nearest atom fallback to guide orbital / description), `ApplyFromOrbital` highlight, and related UI/pick script updates. `ElectronOrbital.prefab` touched in support.

Agent/template-pick NDJSON instrumentation was stripped from preview input in favor of production behavior above.
