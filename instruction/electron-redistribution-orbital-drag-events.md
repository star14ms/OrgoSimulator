# Electron redistribution on orbital drag (future implementation)

This document specifies **design intent** for electron redistribution when bonding events are driven by **orbital dragging**. It does **not** prescribe concrete APIs or file locations; implementation should align with existing `AtomFunction`, `ElectronOrbitalFunction`, `CovalentBond`, and redistribution pipelines where possible.

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

## Guide atom (which nucleus is fixed)

For each operation involving two atoms:

- **Guide atom** — one atom is **fixed in world space** (does not translate for this op).
- **Non-guide atom** — may **move/rotate** (with its substituent subtree as defined elsewhere) to satisfy geometry during the guide atom’s redistribution or bonding animation.

**Selection rule:**

1. Prefer **heavier** side: the atom whose **sum of atomic masses** in its relevant fragment (define scope in implementation: e.g. connected component or op-local subgraph) is **larger** → **guide** (fixed). The **lighter** side → **non-guide** (moving).
2. **Tie-break** (equal mass): **legacy** — the atom whose **orbital was dragged** toward the partner is the **non-guide**; the partner is the **guide**.

**Behavioral note:** The guide atom stays at its **world position**; the non-guide atom (and substituents) may **rotate** to adjust **relative to** the guide during the guide atom’s redistribution. If redistribution is ordered **before** the bonding event in the pipeline, the **future** conformation after bonding must be **predicted** so builds are consistent.

---

## Guide VSEPR group (per atom, within redistribution)

Within **each** atom’s redistribution:

- Exactly one **guide VSEPR group** is chosen: the group whose **substituents have the largest sum of atomic masses** (heaviest substituent set wins).
- That group is **fixed** in the redistribution template sense: e.g. template **vertex 0** aligns to this guide group (consistent with “vertex 0 fixed” / guide-cluster alignment rules elsewhere in the project).
- If the **guide VSEPR group differs** between atom A and atom B, treat each atom’s redistribution as needing **separate** build/execute sequencing (see below).
- If both atoms share the **same** guide-group **role** in the sense required by the op (implementation must define “same” precisely—e.g. symmetric σ formation), **combined** animation may be possible.

---

## Predicting future conformation (before bonding)

When redistribution build runs **before** the bonding event completes:

- **σ formation (1-2):** Treat the **empty** orbital participating in the op as **one VSEPR group** in the count/layout so predicted geometry includes that domain.
- **π formation (1-1):** An **occupied** orbital involved in π formation should **not** be double-counted as its own VSEPR group (it merges into an existing group), but that lobe should still **rotate toward the bonding site** during animation.

---

## Sequencing summary — bond formation

**Step 0 (always):** Determine **guide VSEPR group** per atom (heaviest substituent sum per group).

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

## Open points for implementation (to resolve in code design)

- Exact definition of “mass sum” scope (whole molecule vs σ-neighbor shell vs op-closure).
- How “same” guide VSEPR group is detected across two atoms (symmetry, bond axis, template labeling).
- Interaction with existing **redistribution joint / vertex 0** invariants and π-step coroutines.
- Where **build** outputs are stored between occupied and empty phases (data structures, immutability during execution).

---

## Revision

- **2026-04-04** — Initial instruction from product spec (orbital drag, guide atom, guide VSEPR group, four event types, build/execute and occupied/empty ordering).
