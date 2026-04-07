# Summary (since last /compact)

On `debug-orbital-redistribution`, **commit `c9d6f00`** landed **fix(sigma): stabilize phase3 guide lone orientation** (touches around σ formation phase 3 and related history/compact notes from the prior compact window).

**Uncommitted work (not yet in HEAD):** introduces **`SigmaBreakPureRedistribution`** — a σ bond–break path that builds layout from **current** occupied nonbond + σ-neighbor groups (internuclear axes), constrained by the **0e anti-guide** lobe on the atom that receives the empty ex-bond orbital. It drops the old post-break **`RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute`** pair and instead **smoothstep-lerps** nucleus nonbond orbital locals and **substituent fragment** world positions (same disjoint/overlap rules as existing σ rigid helpers). **`CovalentBond.BreakBond`** now passes **`antiGuideOnThis` / `antiGuideOnPartner`** into a slimmer **`CoLerpBondBreakRedistribution`**; doc comment for **`instantRedistributionForDestroyPartner`** points at the new coroutine instead of **`RedistributeOrbitals`**.

---

*Written by `/compact` on 2026-04-07.*
