# Update summary — 2026-04-30

Since last `/compact` marker was **pending**, this reflects **uncommitted** changes vs `HEAD`.

**OrbitalRedistribution:** Hardened cyclic σ-bond-break and matching: `cyclicAtomOrbitalOpOverride`, `ResolveHostedOperationOrbitalForAtom` / closure σ-OP helpers, `IsOperationOrbital` on redistribution targets, precooked ring endcap template when `FinalDirectionTemplateByAtom` matches VSEPR count, contributor slot-0/1 preassign when template + chain-blocked, `FinalWorldByAtom` on cyclic break context, `TryGetCyclicSigmaBreakPartnerBondDirLocal`, and broader `EnsureCyclicClosureSigmaOpCountedForMatching` (uses pivot/guide σ OP vs wrong root). Fragment rigid apply now skips near-identity mutual-σ rotations (`fragAxisDeltaDeg >= 0.08f`) so parallel π phase-1 tracks do not overwrite substituents from stale `rel0`. Removed like-heavy σ pair fragment suppression; cycle-blocked neighbors clear fragment list but still register the bond target.

**CovalentBond:** Cyclic σ-break path picks the correct anti/recipient orbital (`returnOrbitalTo` / `orbital` vs `newOrbital`), builds a **second** redistribution animation on the partner with the paired σ OP, and sets `applySecondaryFragmentRedistribution` so both ends animate.

**SigmaBondFormation:** Removed NDJSON debug instrumentation and oxygen-snapshot helpers from π phase 1; dropped stray `Debug.Log`; trimmed dead locals in `CoOrbitalDragPiPhase1RedistributionOnly`.

**Note:** `1.cs` / `2.cs` remain untracked at repo root; not part of this diff unless you add them on purpose.
