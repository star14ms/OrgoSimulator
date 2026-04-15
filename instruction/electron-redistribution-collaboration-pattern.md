# Electron redistribution: successful collaboration pattern (design-led)

This note captures **how collaboration differed** in the iteration where electron redistribution and σ animation were **re-implemented successfully**—with **you** specifying animation behavior and control flow—versus earlier stretches where the assistant was leaned on mainly for **code review** and incremental fixes.

It also points to **your structured spec / pseudocode** in-repo as the **reference pattern** for **separate build vs execute** phases and for **independence between simultaneous animation events**.

---

## What you did differently this time

| Earlier pattern (higher risk) | Successful pattern (this arc) |
|------------------------------|--------------------------------|
| Implementation driven by **patching symptoms** after AI-suggested or AI-reviewed edits | **You** owned the **sequence of operations**, **what animates when**, and **what must stay logically separate** |
| Implicit behavior spread across coroutines and ad hoc branches | Explicit **phases**: resolve guide → build targets → execute motion, with **occupied-before-empty** ordering in the build |
| Relying on the assistant to **catch** mistakes in review | You supplied **step tables, event taxonomy, and sequencing** so the code had a **single authoritative story** to match |
| Tight coupling between “bond moved” and “shell moved” in one timeline | **Redistribution** (guide, template, assignment) treated as its own concern from **bond formation / cylinder / snap** animation |

The practical outcome documented in history (e.g. phase-1 σ chain, generalized `OrbitalRedistribution`, guide/non-guide snap fixes, permutation vertex-0 alignment) followed from **that** clarity—not from reverse-engineering intent from diffs.

---

## Why “AI as primary reviewer” tends to multiply errors

When the assistant is used mainly to **review** or **extend** code without a frozen spec:

- Fixes often **track symptoms** (wrong angle, wrong parent, one bond case) and add **local branches**, which duplicates rules the **core path** should encode (see `.cursor/rules/debug-generalize-root-cause.mdc`).
- Cross-cutting invariants (**vertex 0 / guide cluster**, **pivot→orbital-center rays**, **which logical event owns which animation**) are easy to **miss** in review because they are not visible in a single function.
- **RuntimeInitializeOnLoad**, orchestrator stubs, and **scene-only** references are invisible to casual static review—so “looks fine” can still be wrong.

**Code review by AI** remains useful for **consistency, naming, and spotting contradictions** *after* the design is written down—not as a substitute for **your** sequencing and invariants.

---

## Your pseudocode-style spec to follow (in-repo)

The clearest expression of the **build / execute** split, **occupied-then-empty** build order, **event taxonomy** (formation vs breakage, σ vs π steps), and **when animations stagger vs run in parallel** is:

**[`instruction/electron-redistribution-orbital-drag-events.md`](electron-redistribution-orbital-drag-events.md)**

Treat that document as **pseudocode at the architecture level**:

1. **Phases: build vs execution** — Build computes targets and ordering; execution applies or animates. Do not interleave “empty slot” build before occupied targets are settled.
2. **Separation of concerns** — Redistribution (**guide resolution, build, execute**) is **independent** of bond **formation/breakage** animation (operation orbitals, cylinder, snap). The **logical event type** supplies context; avoid burying policy under σ-vs-π-only branches.
3. **Simultaneous animation events** — **Logical** events stay **distinct** for state and bookkeeping; **animations** may **overlap in time** only when safe (no conflicting locks, no contradictory template order). The doc spells out **staggered** (non-guide then guide) vs **combined** vs **parallel** cases using **guide VSEPR group** alignment.
4. **Step 0 ordering** — Guide atom → guide VSEPR group / orbital → then decide single vs staggered redistribution animation.

When implementing a new pipeline, **mirror that structure in code**: a **build** stage that returns a plan, an **execute** stage that consumes it (and optional **timeline** glue that does not smuggle build logic into `Update`).

---

## Related history entries (this implementation line)

- `instruction/history/2026-04-14-phase1-sigma-chain-redistribution.md` — parallel phase-1 animation, `OrbitalRedistribution` module, chain recursion with visited-atom dedup.
- `instruction/history/2026-04-14-stabilize-sigma-op-empty-guide-redistribution.md` — consolidation around `OrbitalRedistribution`, guide snap handoff, vertex-0 permutation stabilization.

---

## Revision

- **2026-04-14** — First version: contrast design-led vs review-led collaboration; link spec as pseudocode pattern for build/execute and animation independence.
