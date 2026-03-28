# Summary (since last /compact)

`ElectronOrbitalFunction` σ formation step 2 is narrowed to **bonding orbitals only**: tetrahedral σ-relax (moving σ-neighbor hydrogens) and Newman stagger (twisting redistribute targets / extra H motion) are removed from the animated path. `GetRedistributeTargets` results are filtered to entries whose orbital is the forming `sourceOrbital` or `targetOrbital`, so step 2 does not lerp unrelated lone lobes. Sibling “rearrange” during step 2 is disabled by keeping `rearrangeTargetInfo` null and deleting `GetRearrangeTarget`, `RearrangeOrbitalToFaceTarget`, and `IsOrbitalAlignedWithTargetPoint`. `SkipSigmaFormationStep2GeometryMoves3D` defaults to **false** again so step-2 animation runs when not explicitly skipped; the flag’s summary now describes instant snap when true. Post–step 3 Newman refresh on `childForStagger` was removed as dead code.

---

_Files: `Assets/Scripts/ElectronOrbitalFunction.cs` (uncommitted at compact time)._
