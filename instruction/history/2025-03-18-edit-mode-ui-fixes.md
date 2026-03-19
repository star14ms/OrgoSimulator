# Edit mode UI and orbital fixes (2025-03-18)

## Summary (since last /compact)

Edit mode UX fixes: allow bonding to H when orbital explicitly selected (short-click); prevent orbital scale compounding when re-selecting; fix orbital size not restoring on deselect; sync Edit toggle color when E key toggles edit mode.

### Bond to H
- orbitalExplicitlySelected: when user short-clicks orbital, bond to it (including H). When user clicks atom only, replace H.
- Early return when same atom+orbital selected to avoid redundant highlight toggles.

### Orbital scale
- SetHighlighted(false): always divide by 1.2 to restore size; reset originalLocalScale after unhighlight so it doesn't compound on reselect.
- Fixes: orbital staying big after deselect; orbital growing when select→deselect→select.

### Edit toggle
- editToggleImage synced in Update() with editModeManager.EditModeActive.
- EditActiveColor (light blue) when on; gray when off. Matches E key toggle state.
