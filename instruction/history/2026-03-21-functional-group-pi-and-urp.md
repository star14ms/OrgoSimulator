# Updates since last /compact

**Functional groups (MoleculeBuilder + EditModeManager):** After σ frameworks, π bonds are formed in `TryFormPiBondsForFunctionalGroupCenter` (multi-round) instead of per-group ad-hoc calls; π/second-π use `GetLoneOrbitalForBondFormation` so atoms like O can donate from a 2e lone pair. Aldehyde carbonyl O is placed with `ComputeTrigonalDirsTowardParentForFunctionalGroup` so R—C—O is ~120°. Substituent H (amine, hydroxyl, carboxyl sulfo, etc.) comes from `SaturateAtomsWithHydrogenPass` on the FG fragment only—not the whole molecule or the parent anchor. **AtomFunction** gives period-3+ group 15/16 centers six valence slots (expanded octet) so sulfonyl/phosphate-style σ+π can succeed after four σ bonds.

**Rendering / WebGL:** URP uses a dedicated **UniversalRenderer_Molecule** asset; global settings and graphics prefs favor reliable WebGL builds (shader stripping, includes). **CovalentBond** / **ElectronOrbitalFunction** prefer URP Unlit for runtime bond/orbital materials.

**Deploy:** `scripts/deploy-webgl.sh` resolves the WebGL folder that contains `index.html` (prefers `WEBGL_DEPLOY`, else newest by mtime) and fails with a clear message if none.

**Editor / scene:** **EditorBuildSettings** favors the 3D main scene; **SampleScene3D** and electron orbital prefabs updated (e.g. highlight alpha).

**Untracked assets:** Build profile `OrgoSimulator - Desktop - Development 1`, **TimelineSettings.asset**, **UniversalRenderer_Molecule** (+ meta) — add when committing if they should be versioned.
