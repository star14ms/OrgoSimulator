# Remove orbital redistribution API and pipeline

## Summary (since last /compact)

The VSEPR redistribution stack was removed from `AtomFunction.cs`: target builders (`TryResolve…`, `TryBuild…`, `GetRedistributeTargets3D*`), `RedistributeOrbitals`, `ApplyRedistributeTargets`, `PeekRedistributeTargetsSameAsRedistributeOrbitals3D`, 3D entry stubs, bond-break override helpers, and joint-exclusion state. Replaced ordering with `OrderPairAtomsForBondFormationStep` (stable instance-ID order), added a no-op `TryGetRedistributionGuideBondAnchorForSteppedDebug` for stepped debug, renamed reference direction helper to `FormationReferenceDirectionLocalForPartner`, and shortened `CoLerpBondBreakRedistribution` to timing + hybrid refresh only.

`ElectronOrbitalFunction.cs` now uses empty redistribute target lists for σ/π step-2 paths and drops all `ApplyRedistributeTargets` calls. `MoleculeBuilder`, `EditModeManager`, `CameraViewModeToggle`, and `MoleculeViewPrefabSwap` no longer call `RedistributeOrbitals`; helper methods that only redistributed are no-ops or use discards. Untracked: `tools/stub_atom_redistribution_clear.py` (legacy stub script from an earlier pass).
