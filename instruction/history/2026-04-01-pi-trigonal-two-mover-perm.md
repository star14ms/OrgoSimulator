# π trigonal two-mover perm and pivot-based orbital tip

Since last `/compact` (`Ref` was still `pending` on `debug-orbital-redistribution`):

**On `HEAD` (now `fa3d178`, branch +2 vs origin):** `1d6be1c` adds predictive VSEPR wiring and disables azimuth twists for redistribution; `fa3d178` reverts / strips broad session NDJSON and perm-cost instrumentation from that line of work.

**Working tree (uncommitted, `Assets/Scripts/AtomFunction.cs` only):**

- **`OrbitalTipDirectionInNucleusLocal`:** Primary path uses reference nucleus pivot → orbital transform position in world, expressed in **this** atom’s local space and normalized; degenerate offset falls back to the prior +X hybrid-tip behavior. XML docs spell out that the reference nucleus is always `this` and that redistribution should use the pivot atom under redistribution.
- **`TryResolveTrigonalTwoSigmaMoverPermUsingInternuclearAxes`:** No longer requires both movers to be σ-bond lines. Handles (1) one σ-line + one other mover using internuclear σ axis vs the other tip in the π⊥ plane, with optional unified bond-world cross and cone+`QuaternionSlotCostOnly` fallback when crosses are weak; (2) two non-σ movers via tip-vs-tip and tip-vs-target crosses with the same fallbacks; `(3)` original two-σ path retained, with `usedUnifiedBondWorldCross` only set when the non-degenerate unified cross path runs. Helper `PreferIdentityAssignmentByConeAndSlotQuat` includes an optional `#region agent log` append to `.cursor/debug-d66405.log` (session `d66405`) for triage.
- **Joint / docs:** Removes unused `sigmaJointUsedOppHemisphere`; updates `LogPiOrbitalVisualTipProbeNdjson` summary to match pivot→center tip semantics.
- **`using System.IO`:** Added for the NDJSON append path.

**Not in source control:** `.cursor/cursor-workspace-debug.ndjson` is untracked ingest output.

**Note:** If shipping without session logging, drop or gate the `debug-d66405` append and remove `System.IO` if unused afterward.
