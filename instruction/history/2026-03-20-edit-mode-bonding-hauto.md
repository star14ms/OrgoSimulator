# Summary (since last /compact)

Edit-mode deselect background no longer sits at a fixed 15 m depth: it is placed past the `MoleculeWorkPlane` along the view axis (with configurable margin), cached on `EditModeManager`, and its collider is enabled only while edit mode is on so it does not steal clicks in normal play. `ScrollOrbitCamera` repositions it when the work plane syncs after orbit.

Orbital bonding and same-atom swap accept **screen-space** overlap between dragged and target orbitals (`OrbitalViewOverlaps`, bounds projected to screen rects), with disambiguation by screen distance; `FindBondPartner` no longer gates on atom-center distance only and picks the best screen-space match.

H-auto in full 3D chooses each new H direction from **existing σ neighbor directions**: tetrahedral increments for sp³ (methane), and when `GetPiBondCount() > 0`, trigonal-planar rules (`TrigonalPlanarThirdDirectionFromTwoBondsWorld` in `VseprLayout`) so benzene C—H stays in-plane instead of CH₂-style pucker. All `[H-auto]` `Debug.Log` noise was removed.

**Files:** `EditModeManager.cs`, `ElectronOrbitalFunction.cs`, `AtomFunction.cs`, `ScrollOrbitCamera.cs`, `VseprLayout.cs`.
