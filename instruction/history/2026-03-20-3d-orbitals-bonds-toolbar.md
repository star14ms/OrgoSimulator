# Update summary — 2026-03-20

Adds a first-class **Electron** prefab (YAML fileIDs safe for Unity 6) and points **ElectronOrbital**’s `electronPrefab` at its `ElectronFunction` component. **MoleculeBuilder** can spawn a **free electron** in the viewport; **AtomQuickAddUI** exposes an **e⁻** button using TMP rich text (`e<sup>-</sup>`) so LiberationSans SDF doesn’t miss glyphs.

**AtomFunction** billboards element/charge labels in **LateUpdate**, placing them along the view ray **outside** the atom body (configurable margin), and extends **global collision ignores** to **ElectronFunction** instances not parented under orbitals (free electrons).

**CovalentBond** uses a **transparent black URP Lit cylinder** along the true bond axis when the main camera is **non-orthographic**, keeping the **sprite line** for 2D; bond frame math uses **FromToRotation** for 3D and updated perpendicular handling for pi offsets.

**ElectronFunction** / **ElectronOrbitalFunction** gain substantial **3D** behavior: URP mesh sphere for electrons, positioning inside the hemisphere during stretch drag, **LateUpdate** slot re-application when not dragging, and **perspective-only** idle rule: a **single** electron sits at **local origin**; **pairs** keep lateral separation and Y bias—**2D** slot layout is unchanged. **`.cursor/rules/chemical-reaction-game.mdc`** was updated to describe labels, 3D orbitals/electrons, and cylinder bonds.

All of the above is **uncommitted** on top of `72ed706`; restage this compact’s “since” boundary after you commit.
