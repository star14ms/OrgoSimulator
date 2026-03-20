# 2026-03-19 — 3D scenes, prefab split, UI-first raycasts

**Summary (since last /compact)**

Work on `main` since `d994d2c` adds first-class **3D molecule editing** alongside the 2D baseline. Generic `Atom` / `Electron` prefabs were replaced by **`2D-*` and `3D-*` variants** (plus a `3D/` prefab folder); **`ElectronOrbital`** prefab was updated for mesh/visual paths.

**New scenes and build:** **`Main3D.unity`** and **`SampleScene3D.unity`** (untracked at compact time) are wired into **`EditorBuildSettings`**.

**Input and camera:** **`PlanarPointerInteraction`** centralizes screen→world on a **`MoleculeWorkPlane`** depth; **`ScrollOrbitCamera`** orbits from wheel/trackpad via **`InputSystem.onAfterUpdate`** (default **scroll sensitivity 1.0**), skipping when the pointer is over a **`ScrollRect`**. **`UiScreenSpace`** keeps HUD canvas in **Screen Space – Overlay**.

**UI vs 3D raycasts:** **`PhysicsRaycasterUiFirst`** / **`Physics2DRaycasterUiFirst`** and **`PhysicsRaycasterSetup`** prefer UI hits before physics so toolbar and panels stay clickable.

**Visuals:** **`PeriodicTableUI`** splits **UI category colors** from **CPK-style atom sphere colors** (`GetAtomSphereColor` vs toolbar/UI APIs). **`AtomFunction`** applies tinted, semi-transparent bodies and contrasting labels; **`ElectronOrbitalFunction`** supports **MeshRenderer** / **Mesh2D-Lit** tint properties (`_White`, etc.), drag-hide of mesh, and **`orbitalVisualAlpha`**. **`CovalentBond`** uses **`BoxCollider`** when the main camera is **perspective** and **`BoxCollider2D`** when orthographic.

**Cleanup:** Removed **`Debug.Log`** from bond-angle helpers (renamed to **`ComputePiBondAngleDiffs`** / **`ComputeSigmaBondAngleDiff`**).

**Other:** **`MoleculeBuilder`** and **`AtomQuickAddUI`** adjusted for prefab/plane/canvas resolution; Desktop Development **build profile** tweaked.
