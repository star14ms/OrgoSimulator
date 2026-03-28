# Summary (since last /compact)

Restores the full **`AtomFunction`** redistribution pipeline from the pre-strip revision (repulsion-first **`GetRedistributeTargets3D`**, **`ApplyRedistributeTargets`**, **`RedistributeOrbitals3DOld`**, bond-break / guide-group helpers, etc.). **`RedistributeOrbitals3D`** again runs its body: the stray early `return` that disabled the new path is removed so **`GetRedistributeTargets3D` → `ApplyRedistributeTargets`** executes. Adds **`DebugLogRedistributeOrbitals3DBondAngles`** and **`[redist3d-angle]`** logs: summary, per-target tips from redistribute, pre/post domain directions (how σ vs lone dirs are defined), and pairwise **`Vector3.Angle`** pre/post; skips hydrogen (Z>1). **`CovalentBond`** adds **`DebugLogBreakBondMotionSources`** so **`AtomFunction.RedistributeOrbitals`** entry logs compile and match restored code.

---

_Files: `Assets/Scripts/AtomFunction.cs` (large restore + diagnostics; partially staged), `Assets/Scripts/CovalentBond.cs` (unstaged). **Ref:** pending until commit._
