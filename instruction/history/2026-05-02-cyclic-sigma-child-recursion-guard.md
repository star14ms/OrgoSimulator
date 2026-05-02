# Cyclic σ child recursion guard (BuildRedistributionAnimation)

Since last `/compact` (marker was still `pending` from 2026-04-30): bisection showed the regression from `d01e902` was not whole-file noise but a missing disjunct in **`BuildRedistributionAnimation`**. Commit **`d01e902`** gated nested **`BuildOrbitalRedistribution`** on **`!isGuideGroup`** only, which skipped the cyclic case where the row is still the guide σ group but the **pivot nucleus hosts the OP** (`ReferenceEquals(atom, atomHostingOp)`).

Restoring **`|| (cyclicContext != null && atomOrbitalOp != null && ReferenceEquals(atom, atomHostingOp))`** fixes cyclic σ formation when the neighbor’s child redistribution must still run. A short comment documents why so a future refactor does not drop it again.
