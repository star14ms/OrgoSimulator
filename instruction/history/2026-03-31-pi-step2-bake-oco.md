# Update summary (since last /compact)

π-bond formation in 3D: **step-2 visual bake** so the animated end pose matches the post-hybrid committed pose. `PiStep2VisualBakeState` captures/restores world poses and bond π redistribution (δ/flip); detached source/target lobes are passed into `Capture` so pre/post snapshots stay the same size; atoms/bonds/orbitals are sorted by `GetInstanceID()` for stable A/B pairing; after `Restore(piBakeB)` the π tail runs (`UpdateBondTransformToCurrentAtoms`, `SyncPiOrbitalRedistributionDeltaFromCurrentWorldRotation`, `TwistPiOrbitalRedistributionDeltaAwayFromColinearSigmaPartnerIfNeeded`, `SnapOrbitalToBondPosition`) so `LateUpdate` agrees with the restored lobe.

**CovalentBond:** bake helpers `CapturePiStep2RedistributionForBake` / `RestorePiStep2RedistributionForBake`; `SyncPiOrbitalRedistributionDeltaFromCurrentWorldRotation`; `TwistPiOrbitalRedistributionDeltaAwayFromColinearSigmaPartnerIfNeeded` and `FindSigmaBondToSamePartner` to separate π from σ tips on double bonds (e.g. O=C=O); `ApplySigmaOrbitalTipFromRedistribution` simplified (remove unused dot/flipped locals and R4 debug NDJSON).

**AtomFunction:** only reset orbital redistribution δ on **σ** bonds when clearing authoritative state; π trigonal **hemisphere flip** gated by nucleus ideal direction to avoid worsening +X vs VSEPR target; full-molecule hybrid refresh after π snap; `PiStep2VisualBakeState` and related helpers; optional NDJSON for molecule ECN, OCO Lewis/angles, and orbital visual tip probes (`H-visual-tip`, etc.).

**ElectronOrbitalFunction:** π `AnimateRedistributeOrbitals` bake path and paired ECN event ids for σ/π logging.

Also: small **ElectronOrbital** prefab change, **TODO** tweak. **`.cursor/debug-workspace.ndjson`** has grown very large from ingest; consider not committing it or adding to `.gitignore`.
