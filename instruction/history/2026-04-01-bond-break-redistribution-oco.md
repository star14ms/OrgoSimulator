# Summary (since last /compact)

Bond-break 3D redistribution for O=C=O–style π/σ cleavage: `CoLerpBondBreakRedistribution` now takes the cleaved `CovalentBond`, uses `PeekRedistributeTargetsSameAsRedistributeOrbitals3D` with `redistributionOperationBond`, and applies optional freeze via `FreezeRedistTargetsExceptGuideToCurrentLocals` / `TargetsListContainsPiBondLineRow` when π bond rows remain in targets.

0e ex-bond guides on σ cleavage get a perpendicular slot (`TryGetPerpendicularEmptyTargetForGuide` + `TryOverrideSigmaOpPiEmptyGuidePerpendicularToFramework`) even when no π rows remain (second σ break), with along-ref fallback (`TryOverridePiBreakEmptyGuideTargetAlongRef`). Perpendicular placement considers other 0e non-bond lobes (`avoidTipLocalDirections` in `TryComputePerpendicularEmptySlotFromFrameworkDirs`) to reduce empty-on-empty overlap.

MissingReference on destroyed `ElectronOrbitalFunction`: explicit Unity null checks in `RefreshElectronSyncOnBondedOrbitals`, `FinishBreakBondTail` atom refresh, and global collision setup (`BuildCollisionUniverse` / `SetupIgnoreCollisionsInvolvingAtoms` / `IgnoreCollision` orbital pairs) with `orbitals.RemoveAll(o => o == null)` after gathering.

Debug: `ProjectAgentDebugLog` ingest session file/id set to `debug-282ed4.log` / `282ed4`; H2/H3/H5 NDJSON and `[pi-break-stub]` logs in bond-break coroutine. Minor `TODO` cleanup. Tracked `.cursor/debug-workspace.ndjson` may show a large diff from log churn—consider restoring or not committing before a clean commit.
