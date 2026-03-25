# Update summary — 2026-03-23

Since last `/compact` (working tree vs `HEAD`): **3D bond break/form, Newman stagger, ex-bond guide hemisphere correction, functional-group attach preservation, and debug logging churn.**

- **`CovalentBond`**: Replaced generic bond-break angle logging with `[ex-bond-pose]` helpers (`LogExBondOrbitalPose` / `LogBreakGuideOrbitalsPose`). Added `CorrectBreakGuideSlotTowardPartner` so σ-cleavage guide slots flip when the orbital tip is anti-parallel to the axis toward the former partner (fixes mirrored-world rotation on one fragment). Applied after slot assignment in `BreakBond`, and **before** Newman twist in both `CoAnimateBreakBondRedistribution` and `ApplyInstantBreakBondRedistribution3D`. Break animation now computes Newman ψ, lerps ex-bond orbitals to `slotFin*`, optionally runs the redistribution step when only Newman applies, pins non-moving H starts during the loop, and applies `ApplyNewmanStaggerTwistProgress` so stagger matches the animated end state.

- **`AtomFunction`**: Removed `[attach-anchor-pos]` / `[attach-added-group]` logging API and flags. Exposed `GetDistinctSigmaNeighborAtoms()`. Added `TryComputeNewmanStaggerPsi`, `ApplyNewmanStaggerTwistProgress`, `UpdateSigmaBondVisualsForAtoms`, and related scoring/helpers for Newman stagger during coroutines.

- **`ElectronOrbitalFunction`**: Optional `[σ-form-pose]` logging around σ formation. Animated σ formation precalculates Newman-staggered σ-relax ends and twists redistribute targets via `TwistRedistributeTargetsForNewmanStaggerEnd`; step-2 loop updates bond visuals each frame; post–Newman refresh of σ bond transforms where needed. Removed attach-anchor move logging from the σ-relax snap path.

- **`EditModeManager` / `MoleculeBuilder`**: Dropped inspector toggles and calls for attach debug logs. `SaturateWithHydrogen` no longer takes logging parameters. After H-auto in full 3D, `TryStaggerNewmanRelativeToPartner` runs on the new heavy fragment. `BuildFunctionalGroup` gains `preserveAttachmentParentGeometry` (used from edit mode): σ bond without redistributing the parent, and fragment-wide `RedistributeOrbitals` skips the parent so attachment does not wipe existing C—H / lobe geometry. `FormSigmaBondInstant` / `BondSigma` accept per-end redistribute flags.

- **`TODO`**: Removed items that are now addressed (stagger / H-auto / group rotation notes).
