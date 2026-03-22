# Update summary (since last /compact)

HUD toolbar scaling now uses a fraction of the shortest screen side (default **1/16** for the H button) instead of scaling from a 1080 reference with tight clamps, so WebGL buttons size better. **`HudPx`** exposes the same scale for other HUD. **`PeriodicTableUI`** raises the max HUD layout scale clamp to **12** so the periodic table stays proportional on large windows.

**`ScrollOrbitCamera`** gains dolly helpers (`GetDepthAlongView`, `ApplyDollyToTargetDepthAlongView`, clamp range), **`IsOrbitFocusAtInitialPivot`** for orbit-pivot vs initial focus, and wheel-orbit is suppressed when the pointer is over a **`Scrollbar`** as well as **`ScrollRect`**.

**`WorkPlaneDistanceScrollbar`** (new): right-edge vertical bar for camera distance to the molecule work plane (perspective) or orthographic size (2D); track height fraction and right margin aligned with **`AtomQuickAddUI`** via **`HudPx(10)`**.

**`WorldOriginMarker`** (new): small unlit sphere at world origin; color switches to the edit selection ring color when orbit focus is at the initial pivot, otherwise **`defaultColor`** (white). **`AtomFunction.GetSelectionHighlightRingColorRgb`** centralizes that RGB.

**`EditModeManager`**: **`editModeActive`** defaults to **true**.

Removed stray **`Assets/Resources`** performance test JSON/meta. Build profile tweak: WebGL texture subtarget **0 → 1**.
