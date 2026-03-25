# 2026-03-25 — H-auto / saturation H placement vs lone pairs

**Summary (since last /compact):**

H-auto and related edit-mode hydrogen placement used σ-only VSEPR directions for **world position** while `GetLoneOrbitalWithOneElectron` correctly picked a **1e** lobe. That mismatch made new H appear toward a **lone-pair** domain when the center had both 2e and 1e lone lobes. **EditModeManager** now, after resolving the 1e orbital, uses `OrbitalAngleUtility.GetOrbitalDirectionWorld` for spawn direction, H partner lobe selection, and (in `SaturateWithHydrogen`) the redistribute ref direction. The same alignment is applied for toolbar H on heavy (VSEPR pick), cycloalkane CH₂/CH saturation, and `AddHydrogenAtDirection`. **AtomFunction** documents that callers using σ-only preferred directions should place along the chosen lobe’s world axis. **FormSigmaBondInstant** now passes `animateOrbitalToBond: false` into `CovalentBond.Create` (instant σ bonds skip orbital-to-line animation).
