# Electron redistribution (3D repulsion layout): guide group

This note describes the **current** behavior for **3D full orbital geometry** (`OrbitalAngleUtility.UseFull3DOrbitalGeometry`): the **repulsion-only** target path and its **guide-group** prefix. Legacy 2D `RedistributeOrbitals` (angle collection / π-origin) is not covered here.

**Primary code:** `Assets/Scripts/AtomFunction.cs` — `RedistributeOrbitals` → `RedistributeOrbitals3D` → `GetRedistributeTargets3D` → `GetRedistributeTargets3DRepulsionLayoutOnly` → optional `TryBuildRedistributeTargets3DGuideGroupPrefix`.

---

## End-to-end flow (high level)

1. **Call site** (e.g. `EditModeManager.FormSigmaBondInstant`) passes **`redistributionOperationBond`**: the `CovalentBond` being formed (or broken) in that pass.
2. **`RedistributeOrbitals(..., redistributionOperationBond: bond)`** runs **`RedistributeOrbitals3D`**, which calls **`GetRedistributeTargets3D`**. When **`AtomFunction.UseRepulsionLayoutOnlyInGetRedistributeTargets3D`** is true (default), targets come from **`GetRedistributeTargets3DRepulsionLayoutOnly`**.
3. **Guide-group prefix** runs first **unless** the atom is in **σ-cleavage ref VSEPR framing** (`useSigmaCleavageRefForVsepr`: break refs + full σ cleavage between former partners). If the prefix succeeds, its slot list is returned and **no later repulsion branch** in that method runs for that atom.
4. **`ApplyRedistributeTargets(targets)`** applies each **`(orbital, localPosition, localRotation)`**. Bond formation may then run **`SnapHydrogenSigmaNeighborsToBondOrbitalAxes`** so H nuclei follow σ lobes (after `RedistributeOrbitals` returns).

---

## Guide resolution: `TryResolveRedistributionGuideGroupForLayout`

A **representative** guide orbital and optional **`guideBond`** are chosen by **strict evaluation order** (first match wins). The value of **`RedistributionGuideSource`** is the tier that matched.

**Operation “pair group”:** when `redistributionOperationBond` is set, every bond edge on **this** nucleus that satisfies `CovalentBondConnectsSameAtomPair(b, redistributionOperationBond)` counts as **in the operation group** (not only reference equality to the same `CovalentBond` instance).

**Guide “pair group”:** when the resolver returns a non-null **`guideBond`**, the **multiply-bond cluster** is the set of all bond hosting orbitals on this nucleus for that same atom pair. Targets and joint-rigid exclusion treat that **whole cluster** as fixed for tiers 1–4 (see Branch B below).

| Order | `RedistributionGuideSource` | Rule (simplified) |
|------|------------------------------|-------------------|
| 0 | (special) | π cleavage with break guide lobe → `LonePairFromOperation`-style ex-bond anchor (`guideBond` null); see XML on method. |
| 1 | `PiBondInOperation` | First π-line bond on this atom that lies on the **operation atom pair** (works when `redistributionOperationBond` is the **σ** line to that pair — the π partner edge is still “in op”). |
| 2 | `PiBondNotInOperation` | First π-line bond whose atom pair is **not** the operation pair. |
| 3 | `SigmaBondNotInOperation` | First σ-line bond whose atom pair is **not** the operation pair. |
| 4 | `SigmaBondInOperation` | `redistributionOperationBond` is σ-line on this atom and has `Orbital` (same instance as the passed op bond). |
| 5 | `LonePairFromOperation` | Occupied ex-bond lobe after break (`bondBreakGuideLoneOrbitalForTargets`), bonding cleared. |
| 6 | `EmptyOrbitalFromOperation` | 0e ex-bond lobe (`bondBreakGuideLoneOrbitalForTargets`), bonding cleared. |

**Note:** With **two** σ bonds to **different** partners (e.g. second H on carbon), tier **3** runs before tier **4**, so the guide σ is typically an **older** C–H σ (different pair than the new operation σ), not the new operation σ.

---

## Building targets: `TryBuildRedistributeTargets3DGuideGroupPrefix`

### Branch A — tier 6 (`EmptyOrbitalFromOperation`)

- Domains: **`GetCarbonSigmaCleavageDomains`** (σ lobes + all nonbond on nucleus).
- **Occupied** + **empty** nonbond lists feed **`TryComputeRepulsionSumElectronDomainLayoutSlots`**: repulsion in the plane **⊥** the **pinned 0e guide** (perpendicular-to-guide chemistry for σ-cleavage style frames).
- **`FindBestOrbitalToTargetDirsPermutation`** on **non-skipped** slots; guide lobe may stay pinned (`skipApply`).

### Branch B — tiers 1–5 (bonding or occupied lone guide)

1. **Movers:** `CollectRedistributionGuideGroupMoversExcludingGuide(guide, guideBond, …)` — excludes the **entire multiply-bond cluster** for `guideBond` when set (all hosting orbitals on that pair), and **every** bond hosting orbital on the **operation atom pair** when `redistributionOperationBond` is set, plus nucleus **`bondedOrbitals`** duplicates; still skips `OrbitalBeingFadedForCharge`.
2. Split movers into **`occ`** (occupied: σ/π on bond or occupied nonbond via **`IsRepulsionOccupiedDomainForGuideGroupLayout`**) and **`emp`** (0e nonbond via **`IsRepulsionEmptyNonBondMoverForGuideGroup`**).
3. **Merge occupied nonbond:** any lobe in **`GetCarbonSigmaCleavageDomains` → `nonBondOnNucleus`** with **`ElectronCount > 0`** is forced into **`occ`** so lone lobes are not missed.
4. **Electron geometry count:** **`nVseprGroups = 1 + occ.Count`** — **only occupied** movers count as VSEPR vertices; **0e nonbond orbitals are not** ideal-slot vertices.
5. **Frame alignment:** `VseprLayout.GetIdealLocalDirections(nVseprGroups)` then **`VseprLayout.AlignFirstDirectionTo(..., guideTip)`** (still uses current internuclear / guide axis for framing **non-cluster** movers).
6. **Permutation:** **`FindBestOrbitalToTargetDirsPermutation(occ, alignedIdeal[1..], ...)`** — directions are **fixed** before matching; the permute step only assigns **which** occupied orbital sits on **which** remaining vertex.
7. **0e movers:** **`TryComputeSeparatedEmptySlot`** against a framework built from **`guideTip`** + **`alignedIdeal[1..]`**, with multiple empties separated via accumulated tips.
8. **Multiply-bond guide cluster (tiers with `guideBond != null`):** every hosting orbital on the guide pair is appended with **current** `(localPosition, localRotation)` (cluster pinned), then mover slots use **`GetCanonicalSlotPositionFromNucleusIdealForOrbitalParent`**. **`orbitalsExcludedFromJointRigidInApplyRedistributeTargets`** is the **full** cluster. Tiers 5–6 with `guideBond == null` still use the **single** representative orbital canonical vertex-0 row.

**Output order:** guide cluster row(s) (multiply-bond pinned when `guideBond != null`) → permuted occupied mover slots → empty mover slots.

---

## Repulsion-only chain after guide group

If the prefix fails or is skipped, `GetRedistributeTargets3DRepulsionLayoutOnly` continues with σ-cleavage shell, σN=0 four-non-bond, **`TryComputeRepulsionSumElectronDomainLayoutSlots`** on electron domains, nonbond-only repulsion, etc. (see XML summary on that method).

---

## Flow chart

```mermaid
flowchart TD
  subgraph Entry["Entry"]
    RO[RedistributeOrbitals 3D path]
    R3[RedistributeOrbitals3D]
    GRT[GetRedistributeTargets3D → RepulsionLayoutOnly]
  end

  RO --> R3 --> GRT

  GRT --> Gate{σ-cleavage ref VSEPR?}
  Gate -->|yes| Later[Later repulsion / break paths]
  Gate -->|no| Prefix[TryBuildRedistributeTargets3DGuideGroupPrefix]

  Prefix --> Resolve[TryResolveRedistributionGuideGroupForLayout]
  Resolve --> T2first[1 π on op pair]
  T2first -->|miss| T1[2 π not op pair]
  T1 -->|miss| T3[3 σ not op pair]
  T3 -->|miss| T4[4 σ op instance]
  T4 -->|miss| T5[5 ex-bond occupied lone]
  T5 -->|miss| T6[6 empty break guide]
  T6 -->|miss| FailPrefix[prefix false]

  T2first -->|hit| BranchPick
  T1 -->|hit| BranchPick
  T3 -->|hit| BranchPick
  T4 -->|hit| BranchPick
  T5 -->|hit| BranchPick
  T6 -->|hit| BranchPick

  BranchPick{Tier 6 empty guide?}
  BranchPick -->|yes| RepulsionPin[TryComputeRepulsionSumElectronDomainLayoutSlots pinned 0e ⊥ plane]
  RepulsionPin --> PermA[Permute non-skipped movers]
  PermA --> SlotsA[outSlots]
  BranchPick -->|no| Vsepr[Vsepr ideal dirs n = 1 + occ occupied only]
  Vsepr --> Align[AlignFirstDirectionTo guideTip σ uses internuclear]
  Align --> PermB[Permute occ → ideal 1..n-1]
  PermB --> EmptyPlace[Separated ⊥ slots for 0e nonbond]
  EmptyPlace --> AppendGuide[guide cluster pinned then movers then empties]
  AppendGuide --> SlotsB[outSlots]

  SlotsA --> Apply[ApplyRedistributeTargets]
  SlotsB --> Apply
  FailPrefix --> Later
  Later --> MaybeApply[Apply targets if any]

  Apply --> Snap[Caller may SnapHydrogenSigmaNeighborsToBondOrbitalAxes]
```

---

## Related flags / logs

- **`CovalentBond.DebugLogBondBreakTetraFramework`**: `[break-tetra] GetRedistributeTargets3D ...` lines (guide source, `nVseprGroups`, `occMovers`, `empSlots`, ids).
- **`redistributionOperationBond`**: threaded from bond formation/break callers into `RedistributeOrbitals` / `GetRedistributeTargets3D` so tiers can exclude or prefer the “op” bond.

---

## Files (reference)

| Piece | Location |
|-------|----------|
| Resolver + enum | `AtomFunction.TryResolveRedistributionGuideGroupForLayout`, `RedistributionGuideSource` |
| Multiply-bond orbitals on nucleus | `AtomFunction.CollectMultiplyBondGroupOrbitalsOnThisNucleus` |
| Prefix builder | `AtomFunction.TryBuildRedistributeTargets3DGuideGroupPrefix` |
| Repulsion-only list | `AtomFunction.GetRedistributeTargets3DRepulsionLayoutOnly` |
| 3D apply + diag | `AtomFunction.RedistributeOrbitals3D`, `ApplyRedistributeTargets` |
| Ideal geometry | `VseprLayout.GetIdealLocalDirections`, `AlignFirstDirectionTo` |
| Domain inventory | `AtomFunction.GetCarbonSigmaCleavageDomains` |
