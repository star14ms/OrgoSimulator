# Edit mode, orbital, and electron improvements (2025-03-18)

## Summary (since last /compact)

Edit mode and orbital interaction improvements: replace-hydrogen when adding atoms to selected H; short-click orbital selection; discrete 30° orbital rotation by dragging; bond-break electron fix (split electrons between atoms, unblock interaction).

### Replace hydrogen
- When H is selected in edit mode and a new atom is added, the new atom replaces the H (breaks H bond, creates new atom at H position, bonds to parent, applies H-auto).
- Added `GetLoneOrbitalForBondFormation` in AtomFunction for orbitals with 1 or 2 electrons.

### Orbital selection and rotation
- Short click (<10px drag) on a lone orbital in edit mode selects that orbital.
- Orbital drag-and-release without bond/swap now rotates the orbital to the nearest 30° step (instead of resetting).
- Overlap avoidance: picks the nearest 30° slot that is ≥30° from other orbitals.

### Bond break electron fix
- When a bond breaks, electrons are split: each atom gets 1 electron (instead of one atom getting both).
- Added `SetInteractionBlocked(false)` after break so electrons can be moved from newly formed orbitals.
