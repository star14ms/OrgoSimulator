# `RedistributeOrbitals` / `RedistributeOrbitals3D` — step order and creation vs breaking

This document describes **what runs, in order**, when orbital geometry is updated in **3D** mode, and how **bond forming (creation)** vs **bond breaking** relate. Both paths call the **same** nucleus method; parameters differ.

**Source of truth:** `AtomFunction.RedistributeOrbitals` → `RedistributeOrbitals3D` in `Assets/Scripts/AtomFunction.cs`.

---

## 1. Mental model (simple)

**Intended job:** after connectivity or electron counts change, place **electron domains** (σ toward each distinct neighbor + occupied lone lobes) in a **VSEPR-style** polyhedron around the nucleus, then make **shared σ orbitals** on bonds point along the chosen hybrid directions.

**Why it feels complicated:** one function also handles **σ-neighbor nuclear motion** (relax hydrides / substituents), **bond-break reference axes**, **guide lobes** for animation, **π vs σ** cases, **sparse spread**, **carbocation** branches, **skips** for preview vs final, and **early exits** when the shell is already numerically tetrahedral. Creation and breaking **reuse** that machinery, so a rule tuned for breaking can affect H-auto on oxygen and vice versa.

---

## 2. Entry point and parameters

`RedistributeOrbitals(...)` dispatches to `RedistributeOrbitals3D` when `OrbitalAngleUtility.UseFull3DOrbitalGeometry` is true.

Arguments that most affect behavior:

| Parameter | Role |
|-----------|------|
| `piBondAngleOverride` | 2D-style angle hint; can influence reference resolution |
| `refBondWorldDirection` | World direction for **bond-break** reference (cleavage axis). Often **null** during normal formation |
| `relaxCoplanarSigmaToTetrahedral` | **true** for bond-break / π-related tet relaxation paths; **false** for typical FG build |
| `skipLoneLobeLayout` | If true, **skips** the occupied-lone VSEPR block entirely (preview / animation already placed lobes) |
| `pinAtomsForSigmaRelax` | Nuclei **frozen** during σ-relax (e.g. π break: both centers stay put) |
| `skipSigmaNeighborRelax` | If true, **skips** all `TryRelaxSigmaNeighbors*` / coplanar tet relax (e.g. final pass after animation applied moves) |
| `bondBreakGuideLoneOrbital` | **Pin** one non-bond lobe for break: not permuted like other loners; reserves an ideal vertex |
| `newSigmaBondPartnerHint` / `sigmaNeighborCountBeforeHint` | Instant σ bond / carbocation-style **tet formation** hints |
| `skipBondBreakSparseNonbondSpread` | Suppresses `TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors` after anim |
| `freezeSigmaNeighborSubtreeRoot` | Whole subtree of one σ neighbor frozen (FG attach, etc.) |

---

## 3. Inner steps of `RedistributeOrbitals3D` (in order)

Steps below are **gross order**; some branches return early.

### 3.1 Guards and logging

1. **`maxSlots <= 1`** → return (no polyhedron work).
2. **`LogReplaceHRedistributeOrbitalSnapshot("Redistribute3D enter")`** (when debug flags on).
3. **`MaybeApplyTetrahedralSigmaRelaxForBondFormation`** — only if **`!skipSigmaNeighborRelax`**; uses partner hint / σ-count-before for cases like “gain a σ toward new neighbor when no lone domains.”

### 3.2 Reference axis in nucleus local

4. **`refLocal = ResolveReferenceBondDirectionLocal(piBondAngleOverride, refBondWorldDirection, bondBreakGuideLoneOrbital)`** — normalized; preferred axis for aligning ideal polyhedra and sorting σ relax.

### 3.3 σ-neighbor nuclear relax (heavy body)

If **`!skipSigmaNeighborRelax`**:

5. **`relaxCoplanarSigmaToTetrahedral`** branch (**typical of bond break**, not normal sp² build):
   - `TryRelaxCoplanarSigmaNeighborsToTetrahedral3D`
   - optionally `TryRelaxSigmaNeighborsToTrigonalPlanar3D`, `TryRelaxSigmaNeighborsToLinear3D`, `TryRelaxSigmaNeighborsOpenedFromLinear3D` (subject to carbocation / planar framework skips)
6. Else (**typical formation**):
   - `TryRelaxSigmaNeighborsToTrigonalPlanar3D`, then linear / opened-from-linear variants.

7. If **`relaxCoplanarSigmaToTetrahedral && !skipSigmaNeighborRelax`**:  
   **`TryApplySp2BondBreakTrigonalPlanarSigmaNeighborRelax3D`** (trigonal / sp² break geometries).

### 3.4 Bond-break non-bond spread

8. If **`relaxCoplanarSigmaToTetrahedral`**, **`refBondWorldDirection`** valid, and not **`skipBondBreakSparseNonbondSpread`**:  
   **`TrySpreadNonbondOrbitalsForBondBreakSparseSigmaNeighbors`** (with optional skip for certain 2σ radical cases).

### 3.5 Exit: skip lone layout entirely

9. If **`skipLoneLobeLayout`** → log, diagnostics, **return** (no occupied-lone VSEPR below).

### 3.6 Occupied lone lobes: collect or exit

10. Build **`loneOrbitals`** (non-bond, `ElectronCount > 0`).
11. If **none**: σ-only / special paths — may still **`SyncSigmaBondOrbitalTipsFromLocks`** for multi-σ frameworks and **`OrientEmptyNonbondedOrbitalsPerpendicularToFramework`**; **return**.

### 3.7 VSEPR core (occupied lone path)

12. **`ClearSigmaBondOrbitalRedistributionDeltaWhereAuthoritative`**.
13. **`bondAxes = CollectSigmaBondAxesLocalMerged`**, **`domainCount = σ axes + lone count`**.
14. **`idealRaw = GetIdealLocalDirections(domainCount)`**.
15. **`pin`** = guide lobe if it is one of **`loneOrbitals`**; **`loneMatch`** = other loners for permutation.

#### Early exit — “already tetrahedral” (visual test)

16. If **4 domains**, all lone lobes **`ElectronCount == 2`**, axes+lone count = 4, **`SigmaTipsAlignedToBondAxes(24°)`**, and **`FourDomainsVisualElectronGeometryApproximatelyTetrahedral(8°, 6°)`**:  
    build **identity** locks `(axis, axis)` → **`SyncSigmaBondOrbitalTipsFromLocks`** → tet diagnostic → **return** (no lone snap).

#### Build ideal directions + TryMatch

17. **Tetrahedron (`domainCount == 4`)**: try **`k = 0..3`**, **`AlignTetrahedronKthVertexTo`** so vertex `k` lies on **`refLocal`**; for each candidate run **`TryMatchLoneOrbitalsToFreeIdealDirections`** (probe). **Pick `k`** by minimizing **max** lobe rotation, then **sum**, then **`k`** (tie).
18. Other domain counts: **`AlignFirstDirectionTo(idealRaw, refLocal)`** for **`newDirs`**.
19. **Full `TryMatchLoneOrbitalsToFreeIdealDirections`** with chosen **`newDirs`** → **`bestMapping`**, **`pinReservedDir`**, **`bondIdealLocks`**.

#### Optional bypass — “relabel only” (bond-ish context)

20. If 4 domains, all lone **2e**, visual tet **`(8°, 6°)`**, **`4° < maxTip ≤ 72°`** (and pin tip ≤ **72°**):  
    **skip** lone/pin **`transform` applies**; **`SyncSigmaBondOrbitalTipsFromLocks`** with **identity** locks only → diagnostic → **return**.  
    *Large motions (~109°) must not use this path — lone APPLY must run.*

#### Apply lone lobes and pin

21. **`RematchLoneOrbitalTargetDirectionsMinAngularMotion`** when break guide / `refBondWorld` and multiple loners.
22. Loop: **`GetCanonicalSlotPositionFromLocalDirection`** → set local pos/rot for each **`loneMatch`**.
23. If **pin** and reserved direction: apply pin lobe pose.
24. **`SyncSigmaBondOrbitalTipsFromLocks(bondIdealLocks)`** — align shared σ on bonds to **TryMatch** ideals (authoritative atom only per bond).
25. **`LogTetrahedralElectronDomainAngleDiagnostic`**.

---

## 4. Creating vs breaking on oxygen (H₂O-style)

**Same function:** every **`RedistributeOrbitals` / `RedistributeOrbitals3D`** on O runs §3. Differences are **flags** and **who calls**.

### 4.1 Creating (e.g. H-auto, `FormSigmaBondInstant` on O)

Typical pattern from **`EditModeManager`**:  
`RedistributeOrbitals(newSigmaBondPartnerHint: H, sigmaNeighborCountBeforeHint: σBefore, ...)` on O, sometimes **`pinAtomsForSigmaRelax`**, **`freezeSigmaNeighborSubtreeRoot`**.

- **`relaxCoplanarSigmaToTetrahedral`** is usually **false** → §3.3 takes the **else** branch (trigonal/linear relax, not full “coplanar→tet break” stack).
- **`refBondWorldDirection`** often **null** unless the call explicitly passes B→A direction for layout on the other atom.
- **`bondBreakGuideLoneOrbital`** usually **null** (no break guide).
- After first H: transitional **1e** on a lobe may appear; **early visual-tet skip** requires **all lone `ElectronCount == 2`**, so it may **not** trigger until counts settle — full **TryMatch + APPLY** runs more often.
- Adding the **second** H can hit **4 domains**, **tet orientation** (`k` loop), **bypass** if visual tet + modest **`maxTip`**, etc. — same code as break.

### 4.2 Breaking (`CovalentBond.BreakBond` → preview / instant / `CoAnimateBreakBondRedistribution`)

Typical pattern:

- **`relaxCoplanarSigmaToTetrahedral: true`** → §3.3 **coplanar→tet** stack, **`TryApplySp2`**, often **`TrySpread`**.
- **`refBondWorldDirection`** set along **cleavage** reference.
- **`bondBreakGuideLoneOrbital`** set on the nucleus that owns the **guide** lobe (from the broken bond).
- **`sigmaRelaxPins`**: only when **π-break** leaves both endpoints still bonded to each other (`GetBondsTo` > 0 after unregister is evaluated in context — see `CovalentBond`); full **O–H** cleavage often has **no** such pin between the broken pair.
- **`skipLoneLobeLayout`**: sometimes **true** during part of animation so lone targets don’t fight the coroutine; **`skipSigmaNeighborRelax`**: **true** on final pass when σ moves were **pre-applied**.

**3D animation (`CoAnimateBreakBondRedistribution`)** lerps orbitals and σ-neighbor targets, then calls **`RedistributeOrbitals`** with **`skipFinalSigmaRelax`** etc.; **multiple** `RedistributeOrbitals3D` invocations per user action are normal (preview vs final).

---

## 5. Why “breaking” changes can break “creating”

Any change under §3.7 (early visual skip, tet **`k`** scoring, **72°** bypass, identity vs **TryMatch** locks, **`SigmaTipsAligned`**) runs for **every** oxygen redistribution when those conditions match — not only when **`refBondWorldDirection`** is set.

So:

- Tuning **bypass** or **visual tolerances** for H₂O **break** also changes behavior when adding the **second** H on O.
- **`relaxCoplanarSigmaToTetrahedral`** mostly gates §3.3–3.4, but §3.7 is shared.

If the implementation should treat “formation” and “cleavage” differently at the VSEPR level, that requires **explicit branching** in §3.7 (e.g. only apply bypass when **`refBondWorldDirection.HasValue && bondBreakGuideLoneOrbital != null`**) rather than relying on accidental separation.

---

## 6. Log tag quick map (debug)

| Tag | Meaning |
|-----|---------|
| `[replace-h] Redistribute3D enter` | Start of `RedistributeOrbitals3D` for this atom |
| `VSEPR lone layout SKIPPED ([tetra-domain] visual…)` | Early §3.7 identity Σ sync only |
| `VSEPR tet orient \| pickedVertexK=… minMax∠…` | Tet **`k`** loop result |
| `VSEPR lone APPLY bypassed …` | §3.7 relabel bypass (≤72° motion) |
| `[lone-vsepr-apply]` | Per-lone APPLY (when enabled) |
| `[tetra-domain]` | Pairwise domain-angle diagnostic |
| `[bond-break-flow]` | `CovalentBond` instant vs `CoAnimate` vs 2D (`CovalentBond.cs`) |

---

## 7. Related code references

| Piece | File / symbol |
|-------|----------------|
| 3D body | `AtomFunction.RedistributeOrbitals3D` |
| TryMatch | `AtomFunction.TryMatchLoneOrbitalsToFreeIdealDirections` |
| Tet ideal dirs | `VseprLayout.GetIdealLocalDirections`, `AlignTetrahedronKthVertexTo` |
| Σ sync | `AtomFunction.SyncSigmaBondOrbitalTipsFromLocks` |
| Formation calls | `EditModeManager.FormSigmaBondInstant`, `SaturateWithHydrogen`, etc. |
| Break calls | `CovalentBond.ApplyInstantBreakBondRedistribution3D`, `CoAnimateBreakBondRedistribution`, `TryPreviewVseprSlotsForBreakBond` |

---

*Last updated to match `AtomFunction.cs` layout as of the session that added min-max tet orientation scoring, 72° bypass cap, and bypass identity locks.*
