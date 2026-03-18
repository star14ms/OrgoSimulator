# Edit mode, orbital redistribution, and molecule bonding

**Date:** 2025-03-18

## Summary

Edit mode and molecule construction were updated to support orbital selection, toolbar bonding, and correct orbital redistribution.

**Edit mode & selection**
- Added orbital selection: `selectedOrbital`, arrow keys (right/up/left/down) to change selected orbital
- Atom and orbital highlighting (`SetSelectionHighlight`, `SetHighlighted`)
- `OnOrbitalClicked` for selecting atom+orbital when clicking an orbital in edit mode
- Relaxed bonding condition: only `SelectedAtom` required (fallback to `GetOrbitalClosestToAngle` when `SelectedOrbital` is null)
- Removed `CanAcceptOrbital` check from `TryAddAtomToSelected` (wrong for bonding; only applies when adding new orbitals)

**Orbital redistribution**
- `RedistributeOrbitals` now uses `piBondAngleOverride` for `originAngle` when provided (fixes new-atom redistribution)
- `FormSigmaBondInstant` passes bond direction for `atomB` via `piBondAngleOverride`
- `GetPrimaryBondDirectionAngle()` added to AtomFunction for use when redistributing after H-auto
- `AddHydrogenAtDirection` and `SaturateWithHydrogen` pass primary bond direction to `RedistributeOrbitals`
- MoleculeBuilder: explicit redistribute pass before H-auto for cycloalkanes and benzene; bond to selected orbital when one is highlighted

**Bonding animation**
- Step 2 skip logic: skip when no actual movement (rearrange/redistribute/orbital-at-bond already aligned)
- Sigma and pi bond animations both skip step 2 when orbitals need no movement

**Other**
- Charge label offset: (0.5, 0.33)
- MoleculeBuilder: cycloalkanes and benzene bond to highlighted orbital when selected
