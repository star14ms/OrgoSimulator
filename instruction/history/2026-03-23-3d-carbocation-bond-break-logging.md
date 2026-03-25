# Update summary (since last /compact)

## Scope

Uncommitted changes on branch `3D` vs `HEAD` (`6ca902d`). Marker was `pending`; this summarizes the working tree.

## Summary

**Bond break & carbocation (AtomFunction + CovalentBond):** Extended `TryGetCarbocationOneEmptyAndThreeOccupiedDomains` so CH₃–CH₃–style shells work pre-cleave (guide with ≥2e, sole non-bond), post-cleave (`guide=null` / sole 0e fallback without wrongly treating the empty as “extra”), and with bond-break guide on nucleus; radicals with 1e on guide are excluded. `TryComputeCarbocationBondBreakSlots` uses ex-bond ref as trigonal normal when the empty is 0e or pre-cleave ≥2e. Carbocation empty placement uses canonical slots without twist minimization and `lockTipToHybridDirection` on apply/snap so the 0e lobe stays normal to the σ plane (avoids |empty·n̂| ≪ 1). CovalentBond adds `CorrectBreakGuideSlotTowardPartner`, σ redistribution world delta + `SyncSigmaOrbitalWorldPoseFromRedistribution`, and optional `[ex-bond-pose]` / empty-teleport logging hooks.

**Redistribute / VSEPR:** New flags and helpers for carbocation σ framework (`TryAppendCarbocationFrameworkSigmaLobeTips`, predicted ends), `skipBondBreakSparseNonbondSpread` on `RedistributeOrbitals`, `TryMatchLoneOrbitalsToFreeIdealDirections` now returns `bondAxisIdealLocks` and uses `FindBestOrbitalToTargetDirsPermutation` for lone mapping.

**σ formation & Newman (ElectronOrbitalFunction + AtomFunction):** Newman stagger ψ on σ-bond formation; precalc staggered H ends and twist redistribute targets; AX₃E-style tetrahedral relax when going 2→3 σ; optional `[σ-form-pose]` logging. `TryComputeNewmanStaggerPsi` and related helpers on `AtomFunction`.

**Edit / attach:** `EditModeManager` drops duplicate serialized attach-debug toggles (logging centralized on `AtomFunction`); stricter rules when adding atoms from a selected orbital (no silent fallback if explicit selection is not a 1e lone); improved hydrogen-saturation direction / lone choice in 3D.

**Logging defaults:** Many chatty traces default **off** (`DebugLogVseprRedistribute3D`, `DebugLogCcBondBreakGeometry`, …); carbocation planarity diagnostics stay easy to enable (`DebugLogCarbocationTrigonalPlanarityDiag` default **on**).

**Other:** `TODO` trimmed; `OrbitalAngleUtility` / `MoleculeBuilder` small updates; `GetDistinctSigmaNeighborAtoms` public.

## Files touched

- `Assets/Scripts/AtomFunction.cs` (large)
- `Assets/Scripts/CovalentBond.cs`
- `Assets/Scripts/ElectronOrbitalFunction.cs`
- `Assets/Scripts/EditModeManager.cs`
- `Assets/Scripts/MoleculeBuilder.cs`
- `Assets/Scripts/OrbitalAngleUtility.cs`
- `TODO`
- `.cursor/compact-last.md`

Untracked assets (e.g. recovery Unity scenes, `.log copy`) were not part of this summary.
