# Updates since last /compact

**MoleculeBuilder & functional groups:** π-bond formation after σ frameworks is centralized in `TryFormPiBondsForFunctionalGroupCenter` (multi-pass for centers like sulfonyl / phosphate). Bond formation and prefab swap use `GetLoneOrbitalForBondFormation` so lone pairs with two electrons can donate π where appropriate. Aldehyde carbonyl placement uses trigonal directions; amine/hydroxyl/methoxy/carboxyl/sulfo/nitrile paths lean on a hydrogen saturation pass instead of ad-hoc `TryBondHydrogen` helpers. **EditModeManager** adds `SaturateAtomsWithHydrogenPass` to run H-fill only on a chosen atom list after FG attachment. **AtomFunction** allows expanded octet valence (6) for period-3+ group 15/16 for σ+π after four σ bonds.

**WebGL / URP:** **GraphicsSettings** always-includes several URP shaders; **UniversalRP** uses a custom **UniversalRenderer_Molecule** renderer, HDR off; **UniversalRenderPipelineGlobalSettings** disables stripping runtime debug / debug shader variants. **CovalentBond** and **ElectronOrbitalFunction** prefer **URP Unlit** for runtime materials (fallback Lit/Simple Lit) so transparent meshes survive stripped builds.

**Deploy:** `scripts/deploy-webgl.sh` picks a WebGL output folder that contains `index.html` (prefers `WEBGL_DEPLOY`, else newest by mtime) and errors clearly if none found.

**Editor:** **EditorBuildSettings** disables **SampleScene** (2D) so the **Main3D** build is primary; **SampleScene3D.unity** updated. **Electron orbital prefabs** serialize orbital alpha fields with **orbitalHighlightAlpha 0.14**; script default highlight matches.

**Untracked (not in git diff):** `UniversalRenderer_Molecule.asset` (+ meta), build profile `OrgoSimulator - Desktop - Development 1.asset`, `TimelineSettings.asset` — add if desired.
