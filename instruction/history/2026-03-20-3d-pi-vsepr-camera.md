# Update summary — 2026-03-20 (3D work plane, π redistribution, VSEPR domains)

## 3D camera & work plane

- **`CameraViewModeToggle`** (new): switches Main Camera between legacy orthographic **SampleScene** and perspective **Main3D** presets; refreshes bond visuals and orbital redistribution after toggles.
- **`MoleculeWorkPlane`**: keeps the infinite pick/drag plane aligned with perspective orbit (`SyncToPerspectiveOrbit`), exposes plane point/normal API, optional **LineRenderer** + gizmo debug for the plane.
- **`ScrollOrbitCamera`**, **`PlanarPointerInteraction`**: supporting changes for 3D picking and orbit.

## VSEPR & orbitals (3D)

- **`VseprLayout`** (new): ideal domain directions (linear → octahedral), helpers for ring **CH₂** and tertiary **C** hydrogen directions.
- **`AtomFunction`**: 3D redistribution / slot placement using merged σ axes; **`GetVseprSlotCount3D`** uses **merged σ directions + lone lobes** (clamped to max valence) so **sp²-like** centers (e.g. 1 neighbor axis + 2 lone after π) use **trigonal** (3) instead of forcing **tetrahedral** (4), fixing **TryMatch** failures after π formation. Optional **`[π-redist]`** debug when 3D tween targets are empty.
- **`OrbitalAngleUtility`**, **`ElectronFunction`**, **`ElectronOrbitalFunction`**: 3D orbital visuals, drag behavior, **π** bond animation ends with **`TryRedistributeOrbitalsAfterBondChange`** on both atoms (no longer only when both tween lists were empty at step 2). **`DebugPiOrbitalRedistribution`** / **`LogPiRedistDebug`** for console tracing.
- **`CovalentBond`**: bond-break / redistribution fixes; **`DisposalZone`** touch-ups.

## Molecules & edit mode

- **`MoleculeBuilder`**: planar **cycloalkane** templates (puckered rings, chair hexane), **benzene** builds full σ then π with **deferred** `RedistributeOrbitals` (`redistributeEndpoints` on **FormSigmaBondInstant** / **FormPiBondInstant**); **Shift** attach to selection via **`AtomQuickAddUI`** / **`PeriodicTableUI`**.
- **`EditModeManager`**: **`SetEditMode`** clears orbital highlight without dropping atom selection when leaving edit mode; **click atom while edit off** still highlights molecule; **`ClearAllMolecules`**; **`SaturateCycloalkaneWithHydrogen`** and related H-auto / logging refinements.

**Note:** `Assets/_Recovery/` is Unity recovery output — usually omit from commits.
