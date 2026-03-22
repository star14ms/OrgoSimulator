# Updates since last /compact

**Functional-group π logic (`MoleculeBuilder`):** `TryFormPiBondsForFunctionalGroupCenter` takes the attachment atom and **skips π toward the parent chain** (σ-only link). π count is capped by **octet headroom** (`GetMaxBondOrderSumAroundAtom` − `GetSumBondOrderToNeighbors` − reserved σ for e.g. aldehyde **C—H** via `GetMinReservedBondOrderForPiPass`), and by **half-filled** orbitals before the pass. **`FormSecondPiBondInstant`** returns early for **period-2 C—O** pairs so carbonyl stays **C=O**, not **C≡O**. **Redistribute** runs on touched atoms **before** the π pass so lone-pair counts are sane.

**Nitro:** Anchor nitrogen is spawned with **formal charge +1** (`SpawnAtomElement(..., formalCharge)`), giving **4 valence e⁻** in **four 1e⁻** orbitals (N⁺, no 2e lone pair). **Planar** trigonal σ layout (`ComputeTrigonalDirsTowardParentForFunctionalGroup`) restored; comments clarify sulfo vs nitro placement.

**AtomFunction:** **`GetSumBondOrderToNeighbors`** and **`GetMaxBondOrderSumAroundAtom`** support the π caps.
