# Edit-mode spawn: bond alignment and Newman stagger

Since last `/compact` (working tree vs `HEAD`):

Edit-mode atom placement now orients the new center’s electron shell before σ formation: the bonding lobe is aligned to the anchor’s target lobe (anti-parallel to its world direction), then a Newman ψ twist about the child→partner axis staggers non-bond lobes vs the partner’s “back” substituents. `TryComputeNewmanStaggerPsiForEditAttach` reuses the existing Newman scoring; when the partner has no σ-bonded neighbors yet (pre–bond), parent Newman projections fall back to the partner’s other occupied lobes, excluding the bonding lobe (`partnerOrbitalTowardThis`). The same rotation runs for toolbar add-from-lone and for replace-terminal-H. The temporary cursor rule that forbade editing `RedistributeOrbitals3D` was removed from `.cursor/rules/`.
