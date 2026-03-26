## Summary (since last /compact)

Functional-group construction and post-processing were tightened for 3D chemistry geometry. Cycloalkane hydrogen placement was restored to ring-VSEPR directions to prevent chair/envelope distortion, and sigma-formation post-apply gating was adjusted so sigma-relax-only cases no longer skip critical end-of-step alignment work. FG trigonal-planar handling was expanded with targeted diagnostics (`[fg-sp2]`, `[sp2-relax]`), a fallback π-enforcement pass for trigonal C/N centers, and a deterministic sp2-neighbor placement fallback with rigid-fragment motion to preserve attached bond lengths.

Expanded-octet rules were updated: period-3+ group 15 now uses up to five slots, group 16 up to six, with FG π-cap logic aligned to slot-based bond-order headroom. Nitro behavior was normalized toward protonated-style handling by preventing anchor-N H-auto, targeting only singly bonded O for protonation, and updating the UI label to `NO2H` for consistency.
