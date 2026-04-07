# Summary (since last /compact)

Orbital-drag σ formation: **guide bond axis** for phase-1 nucleus placement and prebond facing is unified in `NormalizedGuideSigmaOpHeadForDrag`—world op +X, with a **hemisphere flip** when +X points away from the non-guide (`dot < 0` vs guide→non-guide), so **2e↔0e** cases with an empty guide lobe no longer place the partner on the wrong side. Degenerate head falls back to guide→non-guide. **SigmaBondFormation** drops an unused `System.Globalization` import after NDJSON cleanup.

**ElectronOrbitalFunction** main σ-start path calls **`SnapToOriginal()`** on the dragged lobe before the three-phase runner (comment: do not snap the partner receptor). **ElectronOrbital** prefab: **phase-1 prebond** duration **2s → 1s**.

---

_Last compact marker: `Ref: pending` until these changes are committed._
