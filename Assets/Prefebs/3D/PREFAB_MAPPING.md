# 3D prefab mapping (OrgoSimulator)

Forked **presentation** prefabs live alongside the existing 2D set. Gameplay scripts are shared; only colliders and renderers differ.

| Role | 2D (orthographic `SampleScene`) | 3D (`Main3D`) |
|------|----------------------------------|---------------|
| Atom | `Prefebs/2D-Atom.prefab` — `SpriteRenderer`, `CircleCollider2D`, `Rigidbody2D` | `Prefebs/3D-Atom.prefab` — `MeshRenderer` + sphere mesh, `SphereCollider`, `Rigidbody` (no gravity) |
| Electron orbital | `Prefebs/2D-ElectronOrbital.prefab` — sprite + `CapsuleCollider2D` | `Prefebs/ElectronOrbital.prefab` — capsule mesh + `CapsuleCollider` + `ElectronOrbitalFunction` |
| Electron particle | `Prefebs/2D-Electron.prefab` (sprite) | `Prefebs/Electron.prefab` — sphere mesh + `SphereCollider` (already referenced by `ElectronOrbital`) |

Runtime **covalent bonds** add a line collider via `CovalentBond.CreateLineVisual`: `BoxCollider2D` when `Camera.main.orthographic`, otherwise `BoxCollider` for 3D picking.

Pointer drags use `PlanarPointerInteraction` + optional `MoleculeWorkPlane`: intersection of the camera ray with the plane (default normal `(0,0,1)` through origin), matching the XY chemistry board used by `AtomFunction`.
