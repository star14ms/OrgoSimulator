# Retrospective: O–C–O → O=C–O and O=C–O → O=C=O (π / carbonyl chemistry)

This note summarizes **how** the two situations were eventually fixed, **what slowed debugging**, and **how to run the next similar investigation faster**. It is meant to reduce trial-and-error chat and repeated miscommunication.

---

## What “solved” looked like (high level)

Both flows touch **π-bond formation**, **trigonal (sp²) geometry on oxygen and carbon**, **electron counting (ECN/Lewis)**, and **3D orbital pose** (σ vs π, hybrid alignment, bond-hosted lobes).

Successful outcomes required **correct accounting**, **correct geometric relationships** (e.g. lone vs σ vs π directions), and **stable visuals** (no teleport at the end of step-2 animation).

---

## Situation A: O–C–O → O=C–O (first π on a carbonyl-like fragment)

### Approaches tried (chronological pattern)

1. **Symptom-first**: wrong angles on oxygen, wrong lone-pair vs bond framing, or ECN not matching Lewis expectations.
2. **Narrow the subsystem**: distinguish **σ** vs **π** bonds, **which orbital** owns which electrons, and **which atom** is the “operation” endpoint vs **far** atoms in the same molecule.
3. **Geometry pipeline**: redistribute targets → apply → **hybrid alignment refresh** → bond **snap** / **δ (orbitalRedistributionWorldDelta)** sync for the **correct** bond line (σ index 0 vs π index > 0).

### What actually fixed things (themes)

- **δ only where it belongs**: clearing or resetting redistribution on **π** lines when the logic assumed **σ** caused subtle wrong poses (fix: only reset δ on **σ** bond lines where authoritative).
- **σ/π separation**: π tip nearly **colinear** with σ tip on the same atom pair → need explicit **twist** of π δ and ordering with **Snap** / **Sync** so tips don’t collapse to 0° / 180° confusion.
- **Hybrid scope**: endpoints got refreshed while **other oxygens** in the connected molecule still had **stale** hybrid frames → **full-molecule** refresh after π snap for σ alignment on “far” centers.
- **Trigonal π slot / hemisphere**: canonical slot rotation could flip a lobe 180° against the **VSEPR ideal** in nucleus space → a **guard** (compare flip vs unflip to ideal direction) avoided “correct math, wrong hemisphere.”

### What delayed solving it

| Delay | Why it hurts |
|--------|----------------|
| **One symptom, many causes** | “Oxygen angles wrong” can be redist targets, hybrid basis, bond δ, animation order, or a **non-endpoint** atom never refreshed. |
| **Mixed σ and π code paths** | Same `CovalentBond` type hosts **σ** and **π** lines; index and `IsSigmaBondLine()` matter. Easy to fix σ and leave π broken or vice versa. |
| **Implicit “who moves”** | Joint fragment motion, σ-neighbor relax, and π plane logic interact; order of **Apply** vs **snap** vs **refresh** changes outcomes. |
| **Console-only debugging** | Without **structured logs** (bond id, phase, atom id), two runs are hard to compare. |

---

## Situation B: O=C–O → O=C=O (second π, double-bond completion)

### Approaches tried

1. **Same as A**, plus **second π** specifics: two bonds between the same atom pair share **one cylinder frame**; π lobes must not end up **parallel** to the σ lobe tip.
2. **Animation vs truth**: step-2 **lerp** used **pre-hybrid** targets while the **real** final state was produced only after **Finalize** (apply + hybrid + snap) → **visible teleport** or wrong “final frame.”
3. **Bake path**: capture **A** (pre-final), run **Finalize** once to get **B**, capture **B**, restore **A**, animate by **lerping captured world state** toward **B**, then **Restore(B)**.

### What actually fixed things (themes)

- **Bake enabled**: `PiStep2VisualBakeState` must **count the same orbitals** in **A** and **B**. A **detached source lobe** (`SetParent(null)` during animation) was **missing from A** but **present after reparent in B** → count mismatch → bake **disabled** → old lerp path → mismatch. **Fix:** pass **floating** `sourceOrbital` / `targetOrbital` into `Capture`, and **sort** atoms/bonds/orbitals by **`GetInstanceID()`** so indices pair the same object in both snapshots.
- **Authority after `Restore(B)`**: `LateUpdate` recomputes the bond-hosted lobe from **δ × baseR**; after restore, run the same **π tail** as elsewhere: **UpdateBondTransformToCurrentAtoms** → **SyncPiOrbitalRedistributionDeltaFromCurrentWorldRotation** → **TwistPi…** (if needed) → **SnapOrbitalToBondPosition** so the **first frame** after `animatingOrbitalToBondPosition` becomes false doesn’t pop.
- **Twist π away from σ partner**: explicit **TwistPiOrbitalRedistributionDeltaAwayFromColinearSigmaPartnerIfNeeded** when σ and π tips would be too close in angle.

### What delayed solving it

| Delay | Why it hurts |
|--------|----------------|
| **Missing signal** | Instrumentation expected **`H-pi-visual-bake`** in logs; when it **never appeared**, the real issue was “**bake never turned on**,” not “lerp math is slightly wrong.” That misdirection cost a long iteration. |
| **Hidden state** | **δ** and **orbitalRotationFlipped** are not obvious in the Scene view; **world** lerp can look “almost right” while **internal** state disagrees with `GetOrbitalTargetWorldState()`. |
| **Parent hierarchy changes** | `GetComponentsInChildren` order and **detached** orbitals break naive “same list length” assumptions. |
| **Long chat / trial and error** | Without a **short written hypothesis list** and **one log line per hypothesis**, each message rediscovers context. |

---

## How you can speed up the *next* similar question

### 1. **Name the pipeline stage first**

For bond formation bugs, state explicitly:

- **Phase**: before / during step 2 / after step 2 / step 3 / `LateUpdate` only.
- **Bond**: σ index 0 vs π index > 0; `bondId` or instance id.
- **Atoms**: operation endpoints vs **rest of molecule**.

That cuts “fix hybrid” vs “fix animation” vs “fix δ” confusion.

### 2. **Require one “decision log” line per run**

Prefer **NDJSON** (or a single `[tag]` line) that answers:

- Which path ran? (e.g. **bake on/off** and **why** if off: count mismatch / exception.)
- Same **orbital set** size for A/B when baking?

If that signal existed **early** for O=C=O, the bake-disable root cause would have surfaced in **one** run.

### 3. **Freeze the scenario**

“O=C–O → O=C=O” is a **specific** topology. A small **scene name or prefab** + **exact click sequence** in one sentence saves paragraphs of back-and-forth.

### 4. **Separate “chemistry correctness” from “graphics authority”**

- **ECN / Lewis**: invariants and per-atom counts.
- **Pose**: δ, flip, parent, snap, **LateUpdate**.

Mixing them in one sentence (“oxygen is wrong”) triggers fixes in the wrong layer.

### 5. **After a fix, add a one-line regression guard**

Example: assert or log **`usePiVisualBake`** when π + 3D + non-bake fallback (dev-only). Cheap insurance against **parent(null)** orbitals breaking capture again.

### 6. **Use `/compact` or a short file after each milestone**

A **5-line** summary in `instruction/history/` (what was true, what was false, what’s next) prevents the next session from re-deriving the same dead ends.

---

## One-line takeaway

**O=C–O**-class issues were slowed by **σ/π coupling and refresh scope**; **O=C=O**-class issues were slowed by **hidden disable of the bake path** (detached orbitals + list pairing) and **δ vs `LateUpdate` authority** after animation. **Structured phase/bond/orbit-count signals** and **separating ECN from pose** would have shortened both threads.
