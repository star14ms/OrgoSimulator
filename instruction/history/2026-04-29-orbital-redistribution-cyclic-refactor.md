## Summary (since last /compact)

Refactored cyclic / ring σ redistribution logic in `OrbitalRedistribution.cs` into a nested static class `OrbitalRedistribution.Cyclic` (same file), including cyclic σ bond break chain targets, stagger helpers, closure preassigns, ring-path indexing, and `CyclicSigmaChainAtomAnimation`. Removed the standalone `OrbitalRedistributionCyclic.cs` file in favor of this nesting.

Generalized repeated VSEPR group list construction with `BuildVseprGroupLists` and wired `TryGetGuideOrbitalForDebug`, `TryGetGuideGroupOrbitalForPiPrebondReference`, `CollectVseprGroupListsForGuidePathCheck`, and `BuildOrbitalRedistribution` through it.

`CovalentBond.cs` now calls cyclic APIs and types via `OrbitalRedistribution.Cyclic.*` and uses non-`global::` nested types in coroutine signatures where applicable.

**Note:** Untracked `Assets/Scripts/OrbitalRedistribution copy.cs` looks like a local duplicate; remove or `.gitignore` if unintended.
