# 2026-04-04 — Unified cursor debug log + guide-group permutation

Since last `/compact` (ref was pending): uncommitted work on **3D redistribution** and **workspace logging**.

**Guide-group / π-trigonal (`AtomFunction.cs`):** `TryBuildRedistributeTargets3DGuideGroupPrefix` now uses **`FindBestOrbitalToTargetDirsPermutation` only** (plus null fallback), with the **internuclear-axis resolver** (`TryResolveTrigonalTwoSigmaMoverPermUsingInternuclearAxes`) **removed**. Large blocks of redundant π-trigonal diagnostics (duplicate cost paths, extra NDJSON) are stripped in favor of a single cost story. **Debug** (gated, default on): `DebugLogTrigonalDiagD374b0` drives NDJSON for joint tip-pair stats, peripheral full redist on off-π atoms, and **`AppendCarbonSigmaNeighborWorldAnglesNdjson_d374b0`** (world σ-neighbor pairwise angles on carbon after π snap). **`AppendOcoCaseDebugNdjson`** / **`AppendDebugSessionNdjson_d374b0`** delegate to **`ProjectAgentDebugLog.AppendCursorWorkspaceDebugNdjson`** instead of separate `.cursor` files.

**Unified debug file (`ProjectAgentDebugLog.cs`):** All Cursor ingest lines, **orbital redistribution mirror** lines (now wrapped as NDJSON with `hypothesisId=orbitals-redist-mirror`, `data.line`), and the above probes go to **one file**: `.cursor/cursor-workspace-debug.ndjson`. Shared **`TryAppendLineToUnifiedCursorWorkspaceFile`**. `OrbitalRedistMirrorFileName` is deprecated/aliased to the same filename.

**Bond formation:** `BondFormationTemplatePreviewInput` agent logging now targets **`CursorDebugModeIngestNdjsonFileName`** (same unified file; session id `9ddc95` unchanged).

**`ElectronOrbitalFunction.cs`:** Calls **`AppendCarbonSigmaNeighborWorldAnglesNdjson_d374b0`** after π bond snap / hybrid refresh block.
