# 2026-03-25 — Sp2 bond-break: skip rigid σ-relax when already tetrahedral

**Summary (since last /compact):**

**AtomFunction:** Added **`AreThreeSigmaDirectionsMutuallyTetrahedral`** (~109.47° pairwise tolerance 12°). In **`TryComputeSp2BondBreakTrigonalPlanarSigmaNeighborRelaxTargets`**, for the **radical / tet** branch (not sp² trigonal/carbocation framework), **`sigmaN == 3`**, and three σ directions already mutually tetrahedral, the function **returns no targets** and skips **`BuildSigmaNeighborTargetsWithFragmentRigidRotation`**, avoiding pointless rigid rotation of whole substituents (e.g. remote CH₃) when only global orientation differed from the canonical frame. Reused a single **`sp2TrigonalFramework`** flag for the ideal-world branch.

**CovalentBond:** **`BondBreakGuideLoneOrbitalOnAtom`** is computed once per redistribute block into locals (**`gA`/`gB`**, **`gPreviewA`/`gPreviewB`**, **`gInstA`/`gInstB`**, **`gCoA`/`gCoB`**) instead of repeating the call inline on each **`RedistributeOrbitals`** (2D break, VSEpr preview pass 1 & 2, instant 3D break, coroutine end).
