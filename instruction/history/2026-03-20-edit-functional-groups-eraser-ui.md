# Update summary — 2026-03-20

Since last compact (`Ref` was pending; diff vs `HEAD` **9114cc1**):

**Functional groups (edit mode):** `MoleculeBuilder` gains `FunctionalGroupKind` and `BuildFunctionalGroup` for NH₂, OH, OCH₃, aldehyde, carboxyl, SO₃H, C≡N, NO₂, and PO₃H₂ (with tetrahedral P and P=O + two OH for phosphate). `EditModeManager.TryAttachFunctionalGroup` attaches by replacing selected H or σ-bonding to a free 1e lone orbital; `FunctionalGroupAttachmentReady()` enables the toolbar when edit mode + valid anchor (non-H no longer requires an explicit orbital click—atom selection already picks `GetOrbitalClosestToAngle(0)`). `AtomQuickAddUI` adds the **Func. grp.** dropdown before cycloalkanes, keeps the control visible with `interactable` gated on readiness, closes cyclo/FG panels on outside click (`LateUpdate`), and mutual-close when opening the other dropdown. Benzene button restyled to match cycloalkanes (gray + white label); row width increased.

**Eraser:** `EditModeManager.TryEraseAtomIfChainEnd` removes only “chain ends” (&lt;2 distinct non-H σ neighbors) plus H on that center; `AtomFunction` uses it instead of destroying the whole molecule.

**Electrons:** `ElectronFunction` removes an electron on eraser click or when released over `DisposalZone` (screen-space); `ElectronOrbitalFunction.RemoveElectron` calls `SyncElectronObjects()` so remaining slots stay consistent. Default orbital highlight alpha lowered.

**Visual / UI:** `CovalentBond` 3D cylinder `radiusScale` 0.25→0.15. **Clear all** anchored bottom-right, immediately left of trash. `PeriodicTableUI.IsVisible` added. Cursor rule table extended for FG / `MoleculeBuilder` / `EditModeManager`.
