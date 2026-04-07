# Electron redistribution on orbital drag

This document mixes **(A) product / design intent** for gesture-driven bonding and **(B) what the codebase does today**. When re-implementing σ formation, read **§ Current implementation (σ drag)** first so you do not assume APIs or timelines that were removed.

---

## Current implementation (σ drag) — do not confuse with older docs

**As of the σ refactor:** the **animated** σ path (`FormCovalentBondSigmaCoroutine`, prebonding wall, post–`Create` shell/joint/step‑2 redistribution, `bondAnimStep1Duration`, `sigmaFormationPrebondWallFraction`, orchestrator `TryBuildSigmaFormation*`, conditional postbond guide refresh tied to that pipeline) has been **removed** on purpose so σ bonding can be re-built cleanly.

**What runs today for a new σ bond from orbital drag:**

- `ElectronOrbitalFunction` → `FormCovalentBondSigmaStart` / `FormCovalentBondSigmaStartAsSource` → **`EditModeManager.FormSigmaBondInstant`** (public).
- That path matches toolbar/builder behavior: **`CovalentBond.Create(..., animateOrbitalToBond: false)`**, merge electrons, destroy the partner lobe, `SnapHydrogenSigmaNeighborsToBondOrbitalAxes` where applicable, `RefreshSelectedMoleculeAfterBondChange`. **No** prebond translation timeline, **no** unified smoothstep over Create, **no** step‑2 joint redistribution coroutine.

**Orchestrator:** `ElectronRedistributionOrchestrator` is a **stub** (`RunElectronRedistributionForBondEvent` may log `[redist-orch]` only). It does **not** build σ preview targets or drive execution.

**π drag** is unchanged at a high level: still uses `FormCovalentBondPiCoroutine`, `AnimateBondFormationOperationOrbitalsTowardBondCylinder`, `bondAnimStep2Duration` / `bondAnimStep3Duration` / `BondAnimOrbitalToLineDuration`, and `animateOrbitalToBond: true` where that path creates the bond.

---

## Design intent (future / not fully wired for σ)

The sections below (**Guide atom**, **VSEPR group**, **predictive 1‑2**, **sequencing**, **milestones**) describe **target behavior** and **ordering principles** for when σ formation is reconnected to redistribution and animation. They are **not** a description of the current instant-σ implementation unless explicitly stated.

**Separation of concerns:** **Electron redistribution** (guide resolution, build, execute) is **independent** of **bond formation/breakage animation** (moving operation orbitals, bond cylinder/line, snap). Bonding gestures must not embed redistribution policy behind σ-vs-π branches; the **logical** event type supplies **context** only. **Guide selection** uses **heaviest substituent / VSEPR mass rules** below — not chemistry-specific one-off helpers tied to a single example.

---

## Scope: two major chemical events

1. **Bond formation** — σ or π step that changes bonding between two atoms.
2. **Bond breakage** — σ cleavage (detach) or π cleavage (still σ-connected afterward).

In both cases, **both** participating atoms undergo electron redistribution. Formation and breakage are **logical events**; their **animations** may run **in parallel** when conditions allow (see sequencing below).

---

## Phases: build vs execution

Redistribution is split into:

| Phase | Meaning |
|--------|--------|
| **Build** | Compute targets, templates, and ordering (no visible motion or deferred motion). |
| **Execution** | Apply or animate toward the built targets (lobe motion, nucleus-attached substituents, bond visuals). |

Within the **build** phase, order is fixed:

1. **Occupied orbital redistribution** — build first (electrons / filled or partially filled lobes, σ framework that carries electron density).
2. **Empty orbital redistribution** — build **after** occupied, using the **updated** (predicted) electron conformation as reference so empty slots match the new VSEPR shell.

Do not interleave empty-lobe targets before occupied targets are settled in the build plan.

---

## Event taxonomy (four types)

### 1. Bond formation

| Id | Description |
|----|-------------|
| **1-1** | **π formation** — atoms were already σ-bonded; add π (double/triple character). |
| **1-2** | **σ formation** — atoms were not σ-bonded; form a **new single bond**. |

### 2. Bond breakage

| Id | Description |
|----|-------------|
| **2-1** | **π breakage** — σ bond remains; only π character is reduced or removed. |
| **2-2** | **σ breakage** — σ bond breaks; atoms **detach** after the event. |

Logical **formation** and **breakage** are separate events; **animation** may overlap in time per rules below.

---

## σ pre-bond rigid shell (bonding vs non-bonding) — implementation note

When animating **electron density / lobes** on the **non-guide** atom before `CovalentBond.Create` (σ **1-2** orbital drag, phase 1), the shell to rotate as **one rigid body about the non-guide nucleus** must include:

1. Every orbital in **`bondedOrbitals`** (nucleus-parented non-bond lobes and any list entries still present after `UnbondOrbital` bookkeeping).
2. For **each existing** `CovalentBond` on that atom: the σ reference **`CovalentBond.Orbital`** and **all** `ElectronOrbitalFunction` components **under the bond GameObject** (so **π** visuals on the same edge move with σ when they live in that hierarchy). **Do not** assume an incipient σ exists in `CovalentBonds` until after `Create`.
3. The **forming** operation lobe on the non-guide side (**explicit** `orbA` / `orbB` for that atom), **unioned** with (1–2) and **deduped** by instance id, so list drift cannot drop the op lobe.

**Anti-pattern:** building the shell only from orbitals whose `transform.parent ==` the nucleus. **Bonding σ (and often π)** is commonly parented to the **`CovalentBond`** transform (and is often **absent** from `bondedOrbitals` after reparent / `UnbondOrbital`).

**δ milestone:** use **one shared world quaternion** for the whole unified set when the VSEPR **domain count** on that center is unchanged during this step; **per-group δ** when domain count changes is a later milestone.

**`vseprGroups[n].to[m]`** in product docs remains **conceptual** — there is no matching runtime graph type; enumeration comes from `bondedOrbitals`, `CovalentBonds`, and gesture orbitals as above.

**Substituent partner atoms:** when the non-guide **fragment** includes **heavy** neighbors (not H), phase 1 may apply the **same rigid δ** (and approach translation) to those **partner nuclei** so substituents stay coherent with the rotating shell; hydrogen-only substituents may stay translation-only depending on mass/guide rules.

---

## Guide atom (which nucleus is fixed)

For each operation involving two atoms:

- **Guide atom** — one atom is **fixed in world space** (does not translate for this op).
- **Non-guide atom** — may **move/rotate** (with its substituent subtree as defined elsewhere) to satisfy geometry during the guide atom’s redistribution or bonding animation.

**Selection rule:**

1. Prefer **heavier** side: the atom whose **sum of standard atomic masses** over the **whole connected molecule** (that atom’s side / component reachable through bonds) is **larger** → **guide** (fixed). The **lighter** side → **non-guide** (moving).
2. **Tie-break** (equal mass): the atom whose **orbital was dragged** toward the partner is the **non-guide**; the partner is the **guide**.

**Behavioral note:** The guide atom stays at its **world position**; the non-guide atom (and substituents) may **rotate** to adjust **relative to** the guide during the guide atom’s redistribution. If redistribution is ordered **before** the bonding event in the pipeline, the **future** conformation after bonding must be **predicted** so builds are consistent.

---

## Guide VSEPR group (per atom, within redistribution)

Within **each** atom’s redistribution:

- Exactly one **guide VSEPR group** is chosen: the group whose **substituents have the largest sum of atomic masses** (heaviest substituent set wins).
- That group is **fixed** in the redistribution template sense: e.g. template **vertex 0** aligns to this guide group (consistent with “vertex 0 fixed” / guide-cluster alignment rules elsewhere in the project).
- If the **guide VSEPR group differs** between atom A and atom B, treat each atom’s redistribution as needing **separate** build/execute sequencing (see below).
- If both atoms share the **same** guide-group **role** in the sense required by the op (implementation must define “same” precisely—e.g. symmetric σ formation), **combined** animation may be possible.

### VSEPR domain grouping (counting / targeting)

When mapping physical orbitals to **groups** for guide selection and templates:

1. **Multiply-bonded edge (intact bond):** treat **σ + π** on the **same** bonded neighbor as **one** VSEPR group — those orbitals **move together** during redistribution.
2. **Bond breakage:** the **newly released occupied** orbital (after **2-1** π break or **2-2** σ break) is its **own** VSEPR group for counting and targeting — **not** coalesced with the remaining multiply-bond framework on that edge.

Domain enumeration must take **event context** into account (bonded vs post-breakage layout) so coalescence vs split is consistent.

---

## Predicting future conformation (before bonding)

When redistribution build runs **before** the bonding event completes (especially **σ formation**), the graph may **not yet** contain the new bond. Guide resolution must still use **counterfactual / predictive** groups.

- **σ formation (1-2):**
  - Treat the **empty** orbital participating in the op as **one VSEPR group** in the count/layout so predicted geometry includes that domain.
  - Treat the **incipient σ bond to the partner** as a **candidate VSEPR group** whose “substituent” is the **partner’s fragment** (everything on the partner side of that would-be bond). Its mass competes with other σ-neighbor groups (e.g. three C–H directions on a methyl). **Example:** forming **H₃C–CH** from fragments: on the **H₃C** carbon, the new **C–CH** group can be the **guide** group because the **CH** fragment is **heavier** than a single **H** substituent — so the **guide orbital** for that atom’s redistribution aligns with the **forming** bond direction, not a generic “pick CH” special case.
- **π formation (1-1):** An **occupied** orbital involved in π formation should **not** be double-counted as its own VSEPR group (it merges into the multiply-bond **edge** group per domain rules above), but that lobe should still **rotate toward the bonding site** during **bonding** animation.

---

## σ formation (1-2): target process (phases) — **not implemented** after instant-σ refactor

This table is the **intended** runtime order when σ formation is **fully** wired to redistribution + animation again. **Today**, σ from drag is **instant** only (see **§ Current implementation (σ drag)**). **“Orbital in op”** means the orbital(s) participating in this bonding operation (dragged side vs partner side as appropriate).

**Do not** use deprecated global “Step 1/2/3” bond-formation labels from older docs; use **σ formation phases** below for **design**. **Setup** is not an animation.

| Phase | What happens (target design) |
|------|----------------|
| **Setup (not animated)** | **Guide atom and guide orbitals** — resolve whole-molecule mass (guide vs non-guide), logical event / orchestrator hook, per-atom guide VSEPR group / guide orbital using heaviest-substituent rules and **incipient σ** partner-fragment logic where needed (see **Predicting future conformation (before bonding)**). |
| **Prebonding animation** *(removed from codebase)* | **Move the non-guide atom** toward the **guide atom’s orbital in the op** and **concurrently** run **electron redistribution animation** for the **non-guide** center on one master timeline. *Historical inspector fields were* `bondAnimStep1Duration` *and* `sigmaFormationPrebondWallFraction` *splitting time before vs after* `Create` *— they no longer exist on* `ElectronOrbitalFunction` *after the refactor.* |
| **Bonding event** | **`CovalentBond` created**, σ line visual, then **orbital → line / cylinder** (duration fields for **π** and legacy paths may still use `bondAnimStep2Duration` / `bondAnimStep3Duration`; not the same as the old σ-only combined clock). |
| **Postbonding** | **Guide atom** electron redistribution / hybrid alignment **only if** needed (e.g. guide VSEPR domain count changes — empty op orbital becomes bonding). Otherwise **skip**. |

Prebonding (when reintroduced) should separate **non-guide** approach + non-guide shell motion from the **bonding event** (new bond + cylinder). **Postbonding** remains **conditional** guide-side work in principle.

---

## Sequencing summary — bond formation (π and other cases)

For **1-1 π formation**, or when not using the **σ 1–2** table above, use the following. **Step 0** still applies.

**Step 0 (always), fixed order:**

1. **Guide atom** — whole-molecule mass per side; tie-break via dragged orbital (see above).
2. **Guide VSEPR group / guide orbital** per atom — heaviest substituent group, with **incipient σ** and **multiply-edge** rules where applicable (for **1-2 σ** target design, use **§ σ formation (1-2): target process (phases)** above instead of this generic list).
3. **Single vs staggered** redistribution animation — from whether the two atoms’ guide-group **roles** match in the sense required by the op (still **TODO**: precise predicate + tests; see open points).

Only after this does **build** (occupied → empty) and **execute** apply per the subsections below where relevant.

### When guide VSEPR groups are **different** between the two atoms

1. Build e-redistribution for **non-guide** atom (with **conformation prediction** as needed).
2. Run **bond formation** + **animate** non-guide atom’s e-redistribution.
3. Build e-redistribution for **guide** atom.
4. **Animate** guide atom’s e-redistribution.

→ **Two separate animation sequences** (non-guide first, then guide).

### When guide VSEPR groups are the **same** (eligible for unified treatment)

1. Build e-redistribution for **non-guide** atom (prediction).
2. Build e-redistribution for **guide** atom (prediction).
3. **Single** animation: e-redistribution of **both** atoms **together** + **bond formation**.

→ **One combined** animation sequence.

---

## Sequencing summary — bond breakage

### 2-1 (π breakage) — guide VSEPR groups **different**

1. Bond breakage (π step).
2. **Non-guide** atom: e-redistribution **with animation**.
3. **Guide** atom: e-redistribution **with animation** (after non-guide).

→ Staggered animations.

### 2-1 (π breakage) — guide VSEPR groups **same**, **or** 2-2 (σ breakage, detach)

1. Bond breakage.
2. **Non-guide** and **guide** atoms: e-redistribution **with animation in parallel** (simultaneously).

→ Concurrent animations where applicable.

---

## Animation parallelism (cross-cutting)

- **Logical** events (formation vs breakage, or σ vs π steps) remain **distinct** for state and bookkeeping.
- **Animation** may **overlap in time** when the implementation determines it is safe (no conflicting world locks, no contradictory template application order).

---

## Implementation milestones (agreed direction)

- **Status:** Animated **1-2 σ** was **removed** from code; **instant σ** (`FormSigmaBondInstant`) is the live path. Re-implementation should treat this doc’s **σ phases** as the **spec to converge to**, not as a description of current behavior.
- **Next:** Reconnect **1-2 σ** to redistribution + optional animation in a **new** pipeline (orchestrator builds, execution order, feature flags) without resurrecting the deleted coroutine by name unless you intentionally port logic.
- **Then:** **1-1** π formation, **2-1** / **2-2** breakage, optional alignment with authoritative σ pose on bonds vs mass-based guide (incremental).
- **Deferred:** **cyclic** ring-closure redistribution (multiple ring atoms, regular-polygon-style target) — separate milestone; document APIs when added.

---

## Wiring and rollout

- **Wired** means a **logical bond event** actually **invokes** the redistribution orchestrator (helpers alone are not sufficient). Invocation may be gated by a **feature flag** for safe rollout.
- **Default behavior** for that flag (gameplay on vs log-only dry run) is chosen at implementation time; diagnostic logging for triage may default on separately per project conventions.

---

## Implementation registry (code)

### σ new bond from drag (current)

| API | Role |
|-----|------|
| `ElectronOrbitalFunction.FormCovalentBondSigmaStart` / `FormCovalentBondSigmaStartAsSource` | Resolve flip vs normal case; call **`EditModeManager.FormSigmaBondInstant`** (instant bond). |
| `EditModeManager.FormSigmaBondInstant` | `CovalentBond.Create(..., animateOrbitalToBond: false)`, merge electrons, destroy partner lobe, H σ snap when applicable, selection refresh. |

### Shared / π / future σ

| API | Phase | Purpose |
|-----|--------|---------|
| `ElectronRedistributionGuide` (`Assets/Scripts/ElectronRedistributionGuide.cs`, static) | Build / policy | Standard atomic weights, `SumAtomicMassInConnectedMolecule`, `ResolveGuideAtomForPair`, `EnumerateVseprGroupMassEntries` (+ incipient σ partner), `TryGetHeaviestVseprGroupMassEntry`, `SplitRedistributeTargetsOccupiedThenEmpty`. Still the right place for guide rules when σ redistribution is wired back. |
| `ElectronRedistributionOrchestrator` (`Assets/Scripts/ElectronRedistributionOrchestrator.cs`, static) | Stub | **`RunElectronRedistributionForBondEvent`** may log only; **no** `TryBuildSigmaFormation*` / guide preview builders (removed with animated σ). Extend here when orchestration returns. |
| `AtomFunction.SnapshotNucleusParentedOrbitalLocalTransforms` / `RestoreNucleusParentedOrbitalLocalTransforms` | Build (preview) | Snapshot/restore nucleus orbitals for hybrid-refresh preview without mutating the scene (used elsewhere; not σ-drag-specific today). |
| `ElectronOrbitalFunction` — `FormCovalentBondPiCoroutine` + `AnimateBondFormationOperationOrbitalsTowardBondCylinder` | Execution | **π** bonding animation: lerp operation orbitals toward shared bond cylinder; uses **`bondAnimStep2Duration`**, **`BondAnimOrbitalToLineDuration`**, `animateOrbitalToBond: true` on create in that path. |

**Removed (do not search for these as live σ paths):** `FormCovalentBondSigmaCoroutine`, `bondAnimStep1Duration`, `sigmaFormationPrebondWallFraction`, `CovalentBond` σ step‑2 flags (`sigmaFormationStep2*`, `Begin/EndSigmaFormationStep2PeripheralOrbitalWorldRotFreeze`), orchestrator σ target builders.

---

## Open points for implementation (to resolve in code design)

- **Same guide VSEPR group across two atoms:** precise predicate (symmetry, bond axis, template labeling) — needs **tests** before animation unification relies on it.
- Interaction with existing **redistribution joint / vertex 0** invariants (`.cursor/rules/redistribution-joint-vertex0-fixed.mdc`) when combining joint world rotation with guide alignment.
- Where **build** outputs are stored between occupied and empty phases (data structures, immutability during execution).

**Resolved for product (documented above):** guide-atom mass scope = **whole connected molecule** per side; VSEPR **domain** rules for multiply bonds vs released lobes; **incipient σ** group for predictive **1-2** guide selection; independence of redistribution from bonding-only animation.

---

## Revision

- **2026-04-04** — Initial instruction from product spec (orbital drag, guide atom, guide VSEPR group, four event types, build/execute and occupied/empty ordering).
- **2026-04-04** — Bond formation step 2: `AnimateBondFormationOperationOrbitalsTowardBondCylinder` only (lerp + bond snap); redistribution is not invoked from bond formation; registry updated.
- **2026-04-04** — Cumulative update: separation of concerns; whole-molecule mass for guide atom; VSEPR domain grouping (multiply edge vs post-break); σ **incipient** partner-fragment example (H₃C–CH); step-0 ordering (guide atom → guide group → single/staggered); milestones (1-2 σ first + animation redesign); wiring/rollout glossary; deferred cyclic; open points trimmed.
- **2026-04-04** — **σ formation (1-2)** authoritative four-step process: (1) guide atom + guide orbitals, (2) non-guide moves toward guide’s op orbital with **concurrent** non-guide redistribution and **opposite** op directions; non-guide guide group = **newly forming** group toward rotation target; (3) cylinder formation; (4) **conditional** guide redistribution if VSEPR group count changes (e.g. empty op orbital on guide).
- **2026-04-04** — Implementation registry (historical; **superseded 2026-04-06**): described `FormCovalentBondSigmaCoroutine` and orchestrator σ builders — those are **gone**; see **§ Implementation registry** above for current vs removed.
- **2026-04-06** — Renamed product vocabulary: **setup** (non-animated), **prebonding animation**, **bonding event** (`Create` + orbital-to-line / cylinder), **postbonding** (conditional guide redist). Deprecated global “Step 1/2/3” bond-formation list in `instruction/chemical-reaction-game.md`.
- **2026-04-06** — **Major doc correction:** animated σ formation **removed** from codebase; σ drag = **`FormSigmaBondInstant`** only; orchestrator **stubbed**; registry and σ-phase table relabeled **target vs current**; listed **removed** symbols so new sessions do not chase deleted APIs (`FormCovalentBondSigmaCoroutine`, `bondAnimStep1Duration`, `sigmaFormationPrebondWallFraction`, orchestrator `TryBuildSigmaFormation*`, etc.).
- **2026-04-06** — **σ pre-bond rigid shell:** implementation note for **bond-parented σ/π** (unify `bondedOrbitals` + orbitals under `CovalentBond` + explicit op), **anti-pattern** `parent == nucleus` only, **δ** milestone, conceptual `vseprGroups`, and **heavy substituent** policy in phase 1.
