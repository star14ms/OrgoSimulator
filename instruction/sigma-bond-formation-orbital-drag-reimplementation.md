# σ bond formation from orbital drag — reimplementation guide

This document summarizes the **implemented** orbital-drag σ pipeline (runner, timing, redistribution hooks) and the **bugs / corrections** encountered while building it. Use it when reimplementing from design docs alone so you do not repeat the same mistakes.

**Related:** High-level redistribution intent, guide rules, and VSEPR notes remain in [`electron-redistribution-orbital-drag-events.md`](electron-redistribution-orbital-drag-events.md). That file’s early **“Current implementation (σ drag) — instant only / orchestrator stub”** section is **out of date** for the animated path: the codebase now uses **`SigmaBondFormation`**, **`ElectronRedistributionOrchestrator`**, and **`ElectronRedistributionGuide`** as described below.

---

## 1. Entry points and fallback

| Step | Behavior |
|------|----------|
| Gesture | `ElectronOrbitalFunction.FormCovalentBondSigmaStart` / `FormCovalentBondSigmaStartAsSource` (swap ends when source cannot rearrange). |
| Primary | `SigmaBondFormation.EnsureRunnerInScene()` then `TryBeginOrbitalDragSigmaFormation(...)`. Pass the **dragged** orbital as `redistributionGuideTieBreakDraggedOrbital` for mass tie-break consistency with `ElectronRedistributionGuide.ResolveGuideAtomForPair`. |
| Fallback | If no `SigmaBondFormation` runner or `TryBegin…` cannot run (e.g. no `EditModeManager`), fall back to `EditModeManager.FormSigmaBondInstant` (**instant** path, no three-phase animation). |

Timings for phases 1–3 are read from the **timing source orbital** (dragged orbital when set, else `orbA`).

---

## 2. Three phases (actual order)

### Phase 1 — Pre-bond (non-guide only)

1. **Resolve** guide vs non-guide (`ElectronRedistributionGuide.ResolveGuideAtomForPair`).
2. **Degenerate case** (`guide == null`, `nonGuide == null`, or same atom): do **not** run phase 1 animation; call `EditModeManager.FormSigmaBondInstantBody` with `orbitalDragPostbondGuideHybridLerp: true` and exit.
3. Otherwise:
   - **Place** non-guide nucleus target: `ElectronRedistributionOrchestrator.ComputeNonGuideNucleusTargetAlongGuideOpHead` (guide op head × bond length).
   - **Move set:** `BuildNonGuideFragmentAtomsForApproach` — if guide is in the same molecule as non-guide, move only the **branch** reachable from non-guide **without crossing the guide atom**; if guide is not in that molecule, move the **whole** non-guide component (so approach stays coherent).
   - **Snapshot** unified-shell **local** transforms: `AtomFunction.SnapshotAllBondedOrbitalLocalTransforms` (must match shell enumeration — see §4).
   - **Capture** unified-shell **world** poses **before** rigid align: `CaptureAllBondedOrbitalWorldTransforms`.
   - **Rigid align opposites:** `RunSigmaFormation12PrebondNonGuideHybridOnly` — rotates the **non-guide unified shell** so the forming lobe’s head opposes the guide lobe (or no-op if already aligned within ~0.02°).
   - **Derive** `qFullShell` from **forming lobe** world rotation before vs after that step.
   - **Restore** locals from pre-rigid snapshot (shell is driven in world space during the lerp).
   - **Suppress** bond-frame σ pose: set `CovalentBond.suppressSigmaPrebondBondFrameOrbitalPose` on **all bonds of non-guide** for the duration of phase 1 (**§4.1**).
   - **Animate:** smoothstep lerp on fragment atom roots (translation + `qFullShell` on rotation). **Shell world poses** must be applied in **`SigmaBondFormation.LateUpdate`**, not in the coroutine body, because existing σ children of `CovalentBond` are updated in bond `LateUpdate` — applying shell poses in `Update`/coroutine runs **too early** and fights the bond transform pipeline.
4. Use **`[DefaultExecutionOrder(100)]`** on `SigmaBondFormation` so its `LateUpdate` runs after typical bond/orbital updates when ordering matters.

### Phase 2 — Create bond + cylinder + orbital→line

1. `UnbondOrbital` both gesture `orbA`/`orbB`.
2. `CovalentBond.Create(..., animateOrbitalToBond: true)`.
3. Set **`bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true`** **before** any hybrid refresh that would run the standard two-endpoint σ formation pass (**§4.3**).
4. Reparent/fade charge as implemented (`SetOrbitalBeingFaded`, etc.).
5. **Cylinder lerp:** `AnimateBondFormationOperationOrbitalsTowardBondCylinder` with explicit duration from `SigmaFormationPhase2CylinderSecondsResolved` (or 0 if skipped).
6. **Skip cylinder lerp** when both operation orbitals already match cylinder pose by **position + lobe +X direction** (not raw quaternion angle alone — twist about bond axis can differ) — `TryPrepareBondFormationCylinderStep` / `SkipCylinderLerp` (**§4.4**).
7. **Orbital→line:** `AnimateOrbitalToLine` with `SigmaFormationPhase2OrbitalToLineSecondsResolved`.

### Phase 3 — Post-bond guide hybrid (heavy guide only)

1. If guide `AtomicNumber > 1` and phase 3 seconds &gt; ε: snapshot guide nucleus-parented locals, run `RunSigmaFormation12PostbondGuideHybridOnly` (refresh hybrid on **guide** with bond context), snapshot “after”, restore “before”, then **lerp** guide nucleus orbital locals via `EditModeManager.CoLerpSigmaFormationNucleusOrbitalLocals` over phase 3 duration.
2. **`FinishSigmaBondInstantTail`** with **`skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true`** — phase 1 already aligned the non-guide shell; running `SnapHydrogenSigmaNeighborsToBondOrbitalAxes` / heavy-end hybrid refresh again causes **visible lone-pair teleport / repack** (**§4.5**).

---

## 3. Timing model (avoid a second class of bugs)

- Use **two independent serialized durations** for phase 2: **cylinder** (`sigmaFormationPhase2CylinderSeconds`) and **orbital→line** (`sigmaFormationPhase2OrbitalToLineSeconds`), each **non-negative**; resolved values are `max(0, field)`.
- **Do not** reintroduce a single “phase 2 total” × **0.55 / 0.45** split: it duplicated π vs σ semantics and forced fragile prefab negatives.
- **π formation** still uses `AnimateBondFormationOperationOrbitalsTowardBondCylinder` with default negative duration → resolves to **cylinder** field; `BondAnimOrbitalToLineDuration` uses line resolved, falling back to **phase 3** seconds when line ≈ 0.

---

## 4. Pitfalls and fixes (checklist)

### 4.1 Unified shell must include bond-parented orbitals

**Bug:** Rotating only `bondedOrbitals` with `parent == nucleus` leaves **σ/π under `CovalentBond` GOs** frozen; “bonding group doesn’t rotate” with lone pairs.

**Fix:** One enumeration for pre-bond: nucleus `bondedOrbitals` ∪ **all `ElectronOrbitalFunction` under each existing `CovalentBond`** on that atom ∪ **explicit forming op**; **dedupe** by instance id (`CollectSigmaPrebondRigidShellOrbitals`). Use the **same** set for `CaptureAllBondedOrbitalWorldTransforms` and `SnapshotAllBondedOrbitalLocalTransforms`.

See also § “σ pre-bond rigid shell” in [`electron-redistribution-orbital-drag-events.md`](electron-redistribution-orbital-drag-events.md).

### 4.2 Bond `LateUpdate` overwrites shell animation

**Bug:** During phase 1, `CovalentBond.LateUpdate` drives the shared σ orbital from the bond frame; coroutine-only shell motion **fights** that.

**Fix:** `suppressSigmaPrebondBondFrameOrbitalPose` on affected bonds during phase 1; **`SnapOrbitalToBondPosition`** and related pose paths must **respect** the same flag.

### 4.3 Double hybrid pass on non-guide after phase 1

**Bug:** `ExecuteSigmaFormation12HybridAlignment` runs **non-guide then guide** `RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute`. Phase 1 already aligned the non-guide shell; the **first** pass **teleports** lone pairs / breaks the animation.

**Fix:** Set `SkipNonGuideExecuteSigmaFormation12HybridPass` on the **new** bond before calling `RunElectronRedistributionForBondEvent` / tail logic that triggers `ExecuteSigmaFormation12HybridAlignment`.

### 4.4 Wrong snapshot after phase 1 atom motion

**Bug:** Applying `RestoreNucleusParentedOrbitalLocalTransforms` from a snapshot taken **before** phase 1 **fragment translation/rotation** runs moves lone pairs as if nuclei never moved.

**Fix:** Commented invariant in `SigmaBondFormation`: **do not** restore pre-phase-1 locals after final atom poses; shell stays correct via `LateUpdate` world reapplication relative to parents.

### 4.5 Cylinder lerp duration vs skip

**Bug:** Skipping cylinder only on **quaternion** match rejected valid cases (direction matches, twist differs) or caused 0-second weirdness when timing was split incorrectly.

**Fix:** Skip when **position** and **lobe +X direction** (from world) match targets within eps; use explicit resolved seconds for passed-in duration; allow **0** cylinder time when skipped.

### 4.6 UI / prefab footguns

- **`BondFormationDebugHud`:** Do not `AddComponent<RectTransform>()` on a `GameObject` that already gets one from `LayoutElement` / Unity UI — duplicate `RectTransform` is invalid.
- **Prefab timings:** 3D vs 2D may need different phase-2 splits; **zero** cylinder time is valid when phase 1 already placed orbitals so π/σ instant cylinder is intended.

### 4.7 Instant σ from molecule builder / toolbar

**MoleculeBuilder** `FormSigmaBondInstant`: if `redistributeAtomA || redistributeAtomB`, call `RunElectronRedistributionForBondEvent(SigmaFormation12, ..., bond)` so hybrid refresh runs **after** create (parity with editor paths). Do not silently discard redistribute flags without an intentional reason.

---

## 5. Orchestrator responsibilities (v1)

| API | Role |
|-----|------|
| `RunSigmaFormation12PrebondNonGuideHybridOnly` | Phase 1 only: rigid shell δ on non-guide (op head vs guide head). |
| `RunSigmaFormation12PostbondGuideHybridOnly` | Phase 3 only: guide hybrid refresh with bond. |
| `RunElectronRedistributionForBondEvent` + `SigmaFormation12` | Instant / post-create path: `ExecuteSigmaFormation12HybridAlignment` (non-guide + guide), subject to `SkipNonGuideExecuteSigmaFormation12HybridPass`. |
| `DryRunLogOnly` | If true: resolve guide only, **no** hybrid execution (testing). |

---

## 6. Files to read first (implementation map)

- `Assets/Scripts/SigmaBondFormation.cs` — phase 1 `LateUpdate`, fragment BFS, phase 2–3 glue.
- `Assets/Scripts/ElectronRedistributionOrchestrator.cs` — prebond rigid, postbond guide, `ExecuteSigmaFormation12HybridAlignment`.
- `Assets/Scripts/ElectronRedistributionGuide.cs` — guide / non-guide by mass + drag tie-break.
- `Assets/Scripts/AtomFunction.cs` — `CollectSigmaPrebondRigidShellOrbitals`, `ApplyRigidWorldRotationToNucleusParentedOrbitals`, snapshots/capture.
- `Assets/Scripts/CovalentBond.cs` — `suppressSigmaPrebondBondFrameOrbitalPose`, `SkipNonGuideExecuteSigmaFormation12HybridPass`, `LateUpdate` pose guards.
- `Assets/Scripts/ElectronOrbitalFunction.cs` — gesture entry, timing fields, `TryPrepareBondFormationCylinderStep`, cylinder animation.
- `Assets/Scripts/EditModeManager.cs` — `FormSigmaBondInstant` / `FormSigmaBondInstantBody`, phase 3 lerp, `FinishSigmaBondInstantTail` (H snap skip flag).

---

## 7. What this guide does **not** replace

- **RedistributeOrbitals3D** internals, π trigonal joint “vertex 0 fixed” math, and full predictive VSEPR policy — see project rules and other instruction/history docs.
- **Chemistry-specific** edge cases; the list above is **engineering** debt from the first σ-drag animation build.

When the animated pipeline changes, update **this** file so the next reimplementation (or port) stays aligned with proven fixes.
