# Redistribution debug instrumentation cleanup (uncommitted)

Since last `/compact` (marker was `Ref: pending` on `debug-orbital-redistribution`):

**Heads-up:** The working-tree diff removes a **behavioral** block in `TryBuildRedistributeTargets3DGuideGroupPrefix` (commented “Far terminal O in a π step…”, `SigmaBondNotInOperation` + three occupied nucleus lone lobes → 3→2 collapse) that is still present in `HEAD` (`1d6be1c`). Confirm that removal is intentional before committing; if not, restore that `if` block and keep only log/NDJSON deletions.

**Instrumentation / noise removed (`AtomFunction.cs`):**

- Dropped `using System.IO` and session-specific perm-cost plumbing: `DebugLogPermCostInvariantNdjson`, hard-coded `PermCostInvariantNdjsonPath`, `DebugPermCostNdjsonSessionId`, `AppendPermCostInvariantNdjson`, `JsonEscapeForNdjson`, post–`FindBest` `[perm-cost]` log + NDJSON, and baseline NDJSON inside `MinimizeTargetDirsAzimuthForPermutationCostInPlace` (azimuth grid remains a no-op with a short comment).
- Removed `DebugLogTemplateTwistPermCost`, `DebugPermCostAlwaysApplyAzimuthMinimum`, and `DebugLogOcoSecondPiNdjson` plus all call sites (joint/orphan/ hybrid refresh NDJSON, `LogOxygenOffRedistShellDiagnostics` ingest-only JSON builder + `angVsRedistTarget` scan).
- Stripped many `ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson` hypothesis blocks (CoLerp bond-break stubs, guide resolution H1/H8, guide-group exits, etc.) and simplified `LogPiTrigonalOcoLine` to console-only with a single `[pi-trigonal-oco]` prefix.
- Tidied redundant `// #region agent log` wrappers around mol-ecn / oxygen helpers.
- `RefreshSigmaBondOrbitalHybridAlignmentForConnectedMoleculeAfterPiStep`: logging-only locals removed; logic unchanged.

**`ProjectAgentDebugLog.cs`:** ingest filename/session constants changed from `debug-d66405.log` / `d66405` to `cursor-workspace-debug.ndjson` / `workspace`.

**Not in this diff:** `HEAD` already contains `fix(redistribution): predictive VSEPR and disable azimuth twists` (`1d6be1c`).
