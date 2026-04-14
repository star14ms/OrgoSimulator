Phase-1 sigma orbital-drag work was expanded from a placeholder into a parallel animation pipeline with a dedicated redistribution module. The flow now computes guide/non-guide operation directions, builds VSEPR-template targets (including counting 0e op orbitals as occupied for phase-1), and applies assignment-based orbital motion.

`SigmaBondFormation` was refactored to run phase-1 tracks via a common timeline executor while preserving phase-2/phase-3 behavior. A new `SigmaPhase1OrbitalRedistribution` module now plans/plays redistribution, includes bond-group handling with adjacent-fragment movement, and recurses chain redistribution with visited-atom dedup plus guide-orbital propagation.

Several compile and integration fixes were applied during this iteration (signature alignment, collection type fixes, access-level fix, and callsite syntax cleanup), and logging/docs text were updated to reflect the new phase-1 approach behavior.
