# Update summary (since last /compact)

Uncommitted work on `debug-orbital-redistribution` (base `HEAD` = `5fc6b31`).

## Electron carry and pointer UX

- Added **`ElectronCarryInput`**: free electrons follow the pointer until the next primary click; drop merges into an overlapping orbital, deletes over **`DisposalZone`**, or stays in world space. Runs at **DefaultExecutionOrder(-1000)**. While carrying, **`BlockOrbitalPointerForCarryFinalize`** stays true so orbitals do not get a paired pointer-down during carry.
- **`ElectronFunction`**: removed per-electron drag; kept eraser **`OnPointerDown`**; **`TryAcceptIntoOrbital`** is public; carry uses **`SetPointerFollowCarry`** / **`OnDestroy`** notifies carry host.
- **`MoleculeBuilder.CreateFreeElectronAtViewport`**: spawns at the current pointer on the work plane and starts carry like a peeled electron.
- **`ElectronOrbitalFunction`**: **short click** (pointer up ≈ pointer down) on a **lone** lobe with e⁻ peels one electron into carry; **real drag** keeps bonding/VSEPR/break behavior. **`orbitalPressHasPairedPointerDown`** clears orphan **`OnPointerUp`** after blocked carry-release clicks so peel/bond/edit do not run without a real down.

## Bond break and hit area

- **`TryBreakBond`**: replaced loose **`OrbitalViewOverlapsAtom`** with **`BondBreakDropTargetsPartnerAtom`** — partner must **`CanAcceptOrbital()`** and the tip must be near the nucleus in 3D or overlap a nucleus **lone** lobe (view or **`ContainsPoint`**), so releases in empty space cancel instead of breaking toward a false screen hit.
- **`EnsureCollider`**: rebuilds colliders to match visuals — **3D** sphere lobe uses **`SphereCollider`** from mesh bounds; **2D** uses **`BoxCollider2D`** from sprite bounds × scale; removed thin default bar for those cases.

## Files touched

`ElectronCarryInput.cs` (+ `.meta`), `ElectronFunction.cs`, `ElectronOrbitalFunction.cs`, `MoleculeBuilder.cs`.

Ref: **pending** until commit.
