# Summary (since last /compact)

Adds **stepped bond-formation debug**: `BondFormationDebugController` / `BondFormationDebugHud` (lower-left Next + toggle), pauses at three template phases during σ/π formation, and **green guide tint** on the multiply-bond cluster via `CovalentBond` / `CollectGuideBondsForBondStepDebug`.

**Template preview** uses green stem/tip meshes (`BondFormationTemplatePreviewPick`, `BondFormationTemplateStemRayPick`, `BondFormationTemplatePreviewInput`, `BondFormationTemplateDescriptionUI`). Preview tip colliders use **triggers** to avoid pushing 3D rigidbody atoms. Between phases **1→2** and **2→3**, the guide highlight and template root are **kept** (not cleared each Next); a **smoothstep lerp** morphs matched `Tip_*` / `Stem_*` toward the next layout. Template parts are named by **orbital instance id** (`Tip_{id}` / `Stem_{id}`) so transitions don’t pair the wrong lobe when `SigmaFormationRedistTargetHasSignificantDelta` drops rows between phases.

**Interaction**: while bond formation blocks interaction, **orbital** pointer hits stay disabled during stepped **phase waits** (atoms remain selectable). `AtomFunction`, `EditModeManager`, `AtomQuickAddUI`, and `WorkPlaneDistanceScrollbar` include small hook/guard updates for the debug flow.

Also adds **cursor rules** (`debug-phase-vs-fix-phase.mdc`, `redistribution-joint-vertex0-fixed.mdc`). `.cursor/cursor-workspace-debug.ndjson` is updated as debug ingest output (usually not committed).
