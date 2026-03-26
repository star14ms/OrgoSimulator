# σ bond formation / σ-relax — rotation bugs addressed (2026-03)

This note summarizes **user-reported visual bugs** (orbital “spin,” ~180° pops, spurious motion) tied to **animated σ formation** and **tetrahedral σ-relax**, especially after **break/rebond** or when adding the **third H** on carbon. It records **what went wrong**, **what we changed**, and **where to look** if similar issues return.

**Main code touched**

- `Assets/Scripts/ElectronOrbitalFunction.cs` — `FormCovalentBondSigmaCoroutine`, bonding-orbital step-2 pose, diagnostics.
- `Assets/Scripts/CovalentBond.cs` — `ApplySigmaOrbitalTipFromRedistribution`, `CommitSigmaRedistributionDeltaFromWorldOrbitalRotation`, `SyncSigmaOrbitalWorldPoseFromRedistribution`, `SnapOrbitalToBondPosition`, `LateUpdate` σ pose, peripheral **world-rotation freeze** hooks.
- `Assets/Scripts/AtomFunction.cs` — `UpdateSigmaBondVisualsForAtoms` → `UpdateSigmaBondVisualsAfterBondTransform`.

**Workspace rule (unchanged)**

- Do not expand `RedistributeOrbitals3D` beyond a thin forwarder; put behavioral fixes in `RedistributeOrbitals3DOld`, 2D path, call sites, or `CovalentBond` (see `.cursor/rules/no-edit-redistribute-orbitals-3d-temporary.mdc`).

---

## 1. Peripheral σ line orbitals “rotate” during pure σ-relax

**Symptom**  
During step 2 with **only** nuclear σ-relax (no redistribute/rearrange), **existing** σ bonds (e.g. other C–H lines) appeared to **twist in world space** while logs for the **forming** σ and redistribute stayed quiet.

**Cause**  
`UpdateSigmaBondVisualsForAtoms` + `LateUpdate` **re-snapped** each σ line orbital to `GetOrbitalTargetWorldState()` **every frame**. Relaxing H nuclei moved bond frames; those snaps accumulated as visible rotation even when redistribute was idle.

**Fix**

- On each `CovalentBond`: flags **`BeginSigmaFormationStep2PeripheralOrbitalWorldRotFreeze` / `End...`**, snapshot **world rotation** once, **freeze** it during step 2 for qualifying peripheral σ lines (all incident σ lines except the **forming** bond).
- **`LateUpdate`** and **`UpdateSigmaBondVisualsAfterBondTransform`**: while frozen, update **position** (and scale) from live geometry but keep **frozen world rotation**.
- **`FormCovalentBondSigmaCoroutine`**: start freeze before the step-2 loop for **3D + σ-relax + !redistribute + !rearrange**; after nuclei reach `endW`, end freeze and call **`UpdateSigmaBondVisualsForAtoms`** once to **resync** δ and canonical poses.

---

## 2. ~180° pop from `ApplySigmaOrbitalTipFromRedistribution` (anti-parallel branch)

**Symptom**  
Log: `[σ-form-rot-bond] antiParallel -> toggled orbitalRotationFlipped` with visible **~180°** jump even when the σ **tip** was already along the bond/hybrid direction (often alongside precalc `alongAxisMatch` / small `σTipUndir°`).

**Cause**  
When `dot(tip0, want) < -0.9999`, the code **toggled** `orbitalRotationFlipped` and cleared δ. That is correct when the lobe really points the wrong way, but wrong when the **live** orbital’s **+X** already matched **`want`**: the canonical base and hybrid disagreed by **gauge** only.

**Fix**

- In the anti-parallel branch, if `(orbital.rotation * right)` is **aligned with `want`** (`dot > 0.9999`), **do not** toggle flip; set  
  `orbitalRedistributionWorldDelta = orbital.transform.rotation * Quaternion.Inverse(baseR)`  
  and sync pose.
- Otherwise keep the **toggle flip** + δ = identity path.

---

## 3. Bonding σ “spins” during pure σ-relax — moving slerp target

**Symptom**  
Precalc could show **Δrot°≈0** / good alignment for a **snapshot**, but the user still saw **rotation** during step 2.

**Cause**  
`bondEndLive = bond.GetOrbitalTargetWorldState()` **changes every frame** as nuclei lerp. **Slerping** `bondOrbitalStartWorldRot` toward `rotTarg` with `rotT < 1` **chases a moving target** → lag and curved motion read as **orbital spin**.

**Fix**

- For **3D + hasSigmaRelaxMovement + !needsRedistribute + !needsRearrange**, stop **slerping** bonding rotation toward a moving target; **follow** live bond-cylinder rotation (see §4 for the final gauge-safe form).

---

## 4. ~180° gauge: `Δrot°=180` with `σTipUndir°=0` (“same line,” wrong quaternion)

**Symptom**  
Logs like: **`Δrot°=180.00`**, **`σTipUndir°=0.00`**, **`alongAxisMatch=True`**, **`orbitalAlreadyAtBond=True`**, yet a **visible** flip or spin during/after step 2.

**Cause**  
`orbitalAlreadyAtBond` uses **tip / undirected** agreement along the internuclear axis. The **dragged** σ and **`GetOrbitalTargetWorldState().worldRot`** can still differ by **~180°** in **full SO(3)** (cylinder / flip / roll gauge). **Forcing** `sourceOrbital.rotation = bondEndLive.worldRot` each frame **picked the canonical gauge** and **popped** the other representation.

**Fix**

- On the **first** step-2 frame (after bond visuals update, `s = 0`), record  
  `sigmaPureRelaxGaugeRel = Inverse(bondEndLive.worldRot) * sourceOrbital.transform.rotation`.
- Each subsequent frame:  
  `sourceOrbital.transform.rotation = bondEndLive.worldRot * sigmaPureRelaxGaugeRel`  
  so the σ **co-rotates** with the live canonical frame without **snapping** across the 180° quaternion ambiguity.

**End of step 2**  

- **`CovalentBond.CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(sourceOrbital.transform.rotation)`** runs once after peripheral σ refresh and final nuclear positions: sets **δ = R_orbital · baseR⁻¹** so **`GetOrbitalTargetWorldState().worldRot`** matches the bonding orbital **before** reparent. That avoids the follow-up block that calls **`ApplySigmaOrbitalTipFromRedistribution`** when `Angle(orb, wrSnap) > rotThreshold` with tips already matching — the preserve branch was correct but **Sync + Snap** could still feel like a late pop.
- **`SnapOrbitalToBondPosition(sourceOrbital.transform.rotation)`** still aligns position and δ/`LateUpdate` for step 3.

**FAQ — “Not a new case”: log shows `[σ-form-rot-bond] antiParallel: preserved…` but motion remains**  

That line means the **flip toggle** was **not** used (good). The scenario is still the same **gauge class**: **Δrot°≈180** with **σTipUndir°≈0**. Step 2 was moving **`sourceOrbital.rotation`** with **`gaugeRel`** while **`orbitalRedistributionWorldDelta`** on the bond could stay at its **pre-step-2** value. After step 2, **`wrSnap` from `GetOrbitalTargetWorldState`** therefore disagreed with the visible orbital by ~180°; the code **correctly** entered **`ApplySigma`** (preserve) to fold that into **δ**, but **`Sync`/`Snap`** could still produce a visible hitch. **Committing δ once** after σ-relax (see above) removes that **second** correction when tips already match.

---

## 5. Related issues (earlier in the same workstream — short)

- **Occupied lone “~60°” after σ-relax:** avoid driving **`applyEnd*`** from **occupied** redistribute rows when targets were **neutralized** during σ-relax; optional local restore when no redistribute/rearrange.
- **σ cylinder vs internuclear axis in flip tests:** `ComputeSigmaBondAngleDiff` and tip matching use **internuclear** data; **`SyncSigmaOrbitalWorldPoseFromRedistribution(forceApplyPoseDuringBondToLineAnim: true)`** during step 2 so pose matches δ/flip while `animatingOrbitalToBondPosition` is true.
- **`SigmaFormationBondingOrbitalTargetWorldRotPreservingRollAroundTip`:** reduces **pure roll** / anti-parallel cylinder artifacts when **slerp** path is still used (**redistribute/rearrange** branches).

Diagnostics (when enabled): `[σ-form-rot]`, `[σ-form-rot-bond]`, `[σ-form-sigmaFlip]`, `[break-tetra]`, etc.

---

## Possible future bug cases (predictions)

1. **Gauge init timing**  
   `sigmaPureRelaxGaugeRel` assumes **first frame** `sourceOrbital.rotation` matches the intended **pre-step-2** pose. If anything **writes** the bonding orbital **between** “nuclei at `startWorld`” and the **first** loop iteration, the offset can be wrong → transient jump. *Mitigation:* snapshot gauge **after** all pre-loop transforms, or use an explicit `bondOrbitalStartWorldRot` only when known equal to world layout at `startWorld`.

2. **δ or flip changes mid–step 2**  
   Pure-relax paths assume **hybrid refresh** is off (`!needsRedistribute && !needsRearrange`). If another system mutates **`orbitalRedistributionWorldDelta`** or **`orbitalRotationFlipped`** during step 2, **`bondEndLive.worldRot`** and the gauge product can **diverge** → pops.

3. **Forming bond `LateUpdate` when `animatingOrbitalToBondPosition`**  
   Bond **transform** is not driven by `LateUpdate` while animating; the coroutine relies on **`UpdateBondTransformToCurrentAtoms`** in **`UpdateSigmaBondVisualsForAtoms`**. Any **new** code path that reads **`bond.transform.rotation`** without that update can see **stale** frames.

4. **Peripheral freeze + final snap**  
   If an edge case **skips** `End...` + refresh (exception, early `return`, new branch), peripheral σ could stay **frozen** or **unsnapped**.

5. **Non–σ lines, π stacks, metal centers**  
   Freeze applies to **`IsSigmaBondLine()`** only. **Pi** or odd **multi-bond** layouts might still show **motion** that looks like “orbital rotation” via different visuals.

6. **Newman stagger + pure relax**  
   Stagger adjusts **sigmaRelax** ends and redistribute twists; interactions with **gauge** preservation have had less exercise — worth testing **stagger on** with **σTipUndir≈0** / **Δrot≈180** precalc.

7. **Anti-parallel `ApplySigma` when actualTip ∥ −want**  
   Current preserve branch handles **`dot(actualTip, want) > 0.9999`**. If chemistry needs **opposite** hybrid sense but **cylinder** convention disagrees, we still **toggle flip**; mis-classification could **rare**-pop.

8. **`SnapOrbitalToBondPosition` without preserve**  
   Any new call that **snaps** the forming σ to **canonical** without **`preserveWorldRollFrom`** after gauge fixes can **reintroduce** ~180° jumps.

---

*End of document.*
