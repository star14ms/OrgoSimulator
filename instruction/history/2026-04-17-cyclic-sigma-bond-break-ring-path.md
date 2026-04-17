# Update summary — 2026-04-17

Since last `/compact` (marker **Ref: pending**; diff vs `4136b06` on `main`):

**Cyclic σ bond break (full cleavage on 3–6 rings)** — `CovalentBond` detects a cyclic σ ring break, builds the σ-only shortest path, **reorders the path so index 0 is the 2e redistribution recipient** (shortest path was always atomA→atomB), runs **single-fragment** redistribution into `CreateCyclicSigmaBondBreakRedistributionBlockContext`, and drives **`CyclicSigmaChainAtomAnimation`** during the post-break lerp so ring nuclei follow stagger targets. Optional **stepped** flow (`DebugCyclicSigmaBondBreakSteppedTemplate` + `BondFormationDebugController`) runs the chain target build without per-step stagger rays, pauses for **redistribution template preview** (`AppendCyclicSigmaBondBreakRedistributionTemplateDebugVisuals`), then lerps.

**`OrbitalRedistribution`** — Large cyclic-break slice: `SigmaRingPathOrderedC1ToCn` / `SigmaChainTargetWorld` / `FinalDirectionTemplateByAtom` on context; stagger **`TryComputeCyclicSigmaStaggerChainTargetsOneStep`** and **`TryComputeCyclicSigmaBondBreakChainTargetWorld`**; **`TryBuildFinalDirectionTemplateFromSigmaChainTargets`** with **ring-wrapped** slot-0/1 reorder for C1 and C_last; **`TryApplyCyclicSigmaChainContributorSlot01Preassign`** with **wrapped ring neighbors** and **partial pins** when only one σ contributor row resolves (cleaved bond to C1); OP bond-site preassign for chain templates; debug template capture and preview cylinders aligned to cyclic break. **Triage `Debug.Log` instrumentation for contributor preassign was removed** after verification.

**`SigmaBondFormation` / bond debug HUD** — Small follow-on edits (ring-size helper usage, debug controller/HUD touch-ups).
