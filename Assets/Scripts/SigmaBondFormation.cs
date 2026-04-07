using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// σ formation from <b>orbital drag</b>: three-phase pre-bond / bonding / post-bond-guide timeline.
/// Independent of edit mode; add this component to the scene (e.g. next to <see cref="EditModeManager"/>).
/// Timings are read from the gesture <see cref="ElectronOrbitalFunction"/> (the dragged lobe when available).
/// </summary>
[DefaultExecutionOrder(100)]
public class SigmaBondFormation : MonoBehaviour
{
    [SerializeField] EditModeManager editModeManager;

    /// <summary>
    /// σ phase 1: bond σ are children of <see cref="CovalentBond"/> transforms that move in <c>LateUpdate</c>.
    /// Shell world poses must be applied after bond frames (see <see cref="LateUpdate"/>); applying in the coroutine runs too early.
    /// </summary>
    bool phase1ApplyShellPosesInLateUpdate;
    List<(ElectronOrbitalFunction orb, Vector3 worldPos, Quaternion worldRot)> phase1WorldBeforeShell;
    Vector3 phase1Pivot0W;
    Vector3 phase1Off;
    Quaternion phase1QS;

    void Awake()
    {
        if (editModeManager == null)
            editModeManager = FindFirstObjectByType<EditModeManager>();
    }

    void LateUpdate()
    {
        if (!phase1ApplyShellPosesInLateUpdate || phase1WorldBeforeShell == null) return;
        foreach (var w in phase1WorldBeforeShell)
        {
            if (w.orb == null) continue;
            w.orb.transform.SetPositionAndRotation(
                phase1Pivot0W + phase1Off + phase1QS * (w.worldPos - phase1Pivot0W),
                phase1QS * w.worldRot);
        }
    }

    /// <summary>
    /// Ensures a scene runner exists for orbital-drag σ (three-phase). If none, adds this component to the
    /// <see cref="EditModeManager"/> GameObject when present; otherwise creates a dedicated GameObject.
    /// Call before <see cref="TryBeginOrbitalDragSigmaFormation"/> from gesture code so scenes without a manual runner still animate.
    /// </summary>
    public static SigmaBondFormation EnsureRunnerInScene()
    {
        var existing = FindFirstObjectByType<SigmaBondFormation>();
        if (existing != null)
            return existing;

        var edit = FindFirstObjectByType<EditModeManager>();
        if (edit != null)
            return edit.gameObject.AddComponent<SigmaBondFormation>();

        var go = new GameObject(nameof(SigmaBondFormation));
        return go.AddComponent<SigmaBondFormation>();
    }

    /// <summary>World σ bond length estimate from participating atoms (matches <see cref="EditModeManager"/> prefab rule when radii match prefab).</summary>
    public static float DefaultSigmaBondLengthForPair(AtomFunction a, AtomFunction b)
    {
        float r = 0.8f;
        if (a != null) r = Mathf.Max(r, a.BondRadius);
        if (b != null) r = Mathf.Max(r, b.BondRadius);
        return 1.2f * r;
    }

    static ElectronOrbitalFunction TimingSourceOrbital(
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        if (redistributionGuideTieBreakDraggedOrbital != null)
            return redistributionGuideTieBreakDraggedOrbital;
        return orbA != null ? orbA : orbB;
    }

    /// <summary>
    /// True if the three-phase coroutine was started. When <see cref="editModeManager"/> is null, returns false.
    /// </summary>
    public bool TryBeginOrbitalDragSigmaFormation(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        if (editModeManager == null)
        {
            Debug.LogWarning("[sigma-drag] SigmaBondFormation: no EditModeManager reference; cannot run σ formation pipeline.");
            return false;
        }
        StartCoroutine(CoOrbitalDragSigmaFormationThreePhase(
            atomA,
            atomB,
            orbA,
            orbB,
            redistributionGuideTieBreakDraggedOrbital));
        return true;
    }

    /// <summary>Orbital-drag σ: (1) pre-bond — translate non-guide + unified shell lerp; (2) bond — cylinder + orbital→line; (3) post-bond — guide hybrid lerp.</summary>
    IEnumerator CoOrbitalDragSigmaFormationThreePhase(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        var timingOrb = TimingSourceOrbital(orbA, orbB, redistributionGuideTieBreakDraggedOrbital);
        float phase1Sec = timingOrb != null ? timingOrb.SigmaFormationPhase1PrebondSeconds : 1f;
        float phase2CylinderSec = timingOrb != null ? timingOrb.SigmaFormationPhase2CylinderSecondsResolved : 0.55f;
        float phase2OrbitalToLineSec = timingOrb != null ? timingOrb.SigmaFormationPhase2OrbitalToLineSecondsResolved : 0.45f;
        float phase3Sec = timingOrb != null ? timingOrb.SigmaFormationPhase3PostbondGuideSeconds : 1f;

        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in atomA.GetConnectedMolecule())
            if (a != null) atomsToBlock.Add(a);
        foreach (var a in atomB.GetConnectedMolecule())
            if (a != null) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock)
            a.SetInteractionBlocked(true);

        try
        {
            // Pointer-up can still leave the dragged lobe at the drop world pose in the same frame StartCoroutine runs.
            // Snap to pre-drag locals, then wait one frame so placement / prebond read +X and nuclei from the restored configuration.
            var draggedLobeToRestore = redistributionGuideTieBreakDraggedOrbital != null
                ? redistributionGuideTieBreakDraggedOrbital
                : orbA;
            if (draggedLobeToRestore != null)
                draggedLobeToRestore.SnapToOriginal();
            yield return null;

            int merged = orbA.ElectronCount + orbB.ElectronCount;
            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : orbA;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(atomA, atomB, guideOrb, out var guide, out var nonGuide);

            if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide))
            {
                editModeManager.FormSigmaBondInstantBody(
                    atomA,
                    atomB,
                    orbA,
                    orbB,
                    redistributeAtomA: true,
                    redistributeAtomB: true,
                    pinSigmaRelaxForAtomA: null,
                    pinSigmaRelaxForAtomB: null,
                    freezeSigmaNeighborSubtreeRoot: null,
                    orbitalDragPostbondGuideHybridLerp: true,
                    redistributionGuideTieBreakDraggedOrbital,
                    phase3GuideLerpSecondsOverride: phase3Sec);
                yield break;
            }

            Vector3 n0 = nonGuide.transform.position;
            Vector3 pivot0W = n0;
            float bl = DefaultSigmaBondLengthForPair(guide, nonGuide);
            ElectronOrbitalFunction guideOpForPlacement = guide == atomA ? orbA : orbB;
            Vector3 nTarget = ElectronRedistributionOrchestrator.ComputeNonGuideNucleusTargetAlongGuideOpHead(
                guide, nonGuide, guideOpForPlacement, bl);
            Vector3 deltaTotal = nTarget - n0;

            var toMove = BuildNonGuideFragmentAtomsForApproach(guide, nonGuide);
            var initialWorld = new Dictionary<AtomFunction, Vector3>(toMove.Count);
            var initialWorldRot = new Dictionary<AtomFunction, Quaternion>(toMove.Count);
            foreach (var a in toMove)
            {
                if (a == null) continue;
                initialWorld[a] = a.transform.position;
                initialWorldRot[a] = a.transform.rotation;
            }

            ElectronOrbitalFunction nonGuideOpForAnim = nonGuide == atomA ? orbA : orbB;

            var snapBeforeNG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
            nonGuide.SnapshotAllBondedOrbitalLocalTransforms(snapBeforeNG, nonGuideOpForAnim);

            // Phase 1 pre-bond: only the non-guide atom’s unified shell (its bonding + nonbonding groups). Guide-atom redistribution is deferred to later σ-formation steps, not animated here.
            var worldBeforeNG = new List<(ElectronOrbitalFunction orb, Vector3 worldPos, Quaternion worldRot)>();
            nonGuide.CaptureAllBondedOrbitalWorldTransforms(worldBeforeNG, nonGuideOpForAnim);

            Quaternion wrotBeforeOp = Quaternion.identity;
            bool foundWrotBeforeOp = false;
            foreach (var w in worldBeforeNG)
            {
                if (w.orb == nonGuideOpForAnim)
                {
                    wrotBeforeOp = w.worldRot;
                    foundWrotBeforeOp = true;
                    break;
                }
            }
            if (!foundWrotBeforeOp && nonGuideOpForAnim != null)
                wrotBeforeOp = nonGuideOpForAnim.transform.rotation;

            ElectronRedistributionOrchestrator.RunSigmaFormation12PrebondNonGuideHybridOnly(atomA, atomB, orbA, orbB, guideOrb);

            Quaternion wrotAfterOp = nonGuideOpForAnim != null ? nonGuideOpForAnim.transform.rotation : Quaternion.identity;
            Quaternion qFullShell = wrotAfterOp * Quaternion.Inverse(wrotBeforeOp);

            AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapBeforeNG);

            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
            try
            {
                phase1WorldBeforeShell = worldBeforeNG;
                phase1ApplyShellPosesInLateUpdate = true;
                float p1 = Mathf.Max(1e-5f, phase1Sec);
                float e1 = 0f;
                while (e1 < p1)
                {
                    e1 += Time.deltaTime;
                    float s = Mathf.Clamp01(e1 / p1);
                    s = s * s * (3f - 2f * s);
                    Vector3 off = deltaTotal * s;
                    Quaternion qS = Quaternion.Slerp(Quaternion.identity, qFullShell, s);
                    foreach (var a in toMove)
                    {
                        if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                        if (!initialWorldRot.TryGetValue(a, out var r0))
                            r0 = a.transform.rotation;
                        a.transform.SetPositionAndRotation(
                            pivot0W + off + qS * (p0 - pivot0W),
                            qS * r0);
                    }
                    phase1Pivot0W = pivot0W;
                    phase1Off = off;
                    phase1QS = qS;
                    yield return null;
                }

                foreach (var a in toMove)
                {
                    if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                    if (!initialWorldRot.TryGetValue(a, out var r0End))
                        r0End = a.transform.rotation;
                    a.transform.SetPositionAndRotation(
                        pivot0W + deltaTotal + qFullShell * (p0 - pivot0W),
                        qFullShell * r0End);
                }
                // Do not RestoreNucleusParentedOrbitalLocalTransforms snap-after-prebond here: that snapshot was taken
                // before rigid phase-1 atom motion; applying those locals after nuclei moved teleports lone pairs and
                // misaligns the op orbital before phase 2. Shell world poses from LateUpdate stay correct relative to parents.
            }
            finally
            {
                phase1ApplyShellPosesInLateUpdate = false;
                phase1WorldBeforeShell = null;
                SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
            }

            // Phase 2 starts in this same coroutine tick (no WaitForSeconds inserted above). Durations are
            // sigmaFormationPhase2CylinderSeconds and sigmaFormationPhase2OrbitalToLineSeconds (resolved, non-negative).

            atomA.UnbondOrbital(orbA);
            atomB.UnbondOrbital(orbB);
            var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: true);
            if (bond == null)
                yield break;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            orbA.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(orbB);
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            float tCyl = Mathf.Max(0f, phase2CylinderSec);
            float tLine = Mathf.Max(0f, phase2OrbitalToLineSec);

            yield return StartCoroutine(orbA.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                atomA, atomB, orbB, orbA, bond, tCyl));

            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(tLine, orbB);
            orbA.ElectronCount = merged;
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f)
            {
                var snapBeforeG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                guide.SnapshotNucleusParentedOrbitalLocalTransforms(snapBeforeG);
                // Same σ-12 redistribution as EditModeManager postbond: guide-only because bond.SkipNonGuideExecuteSigmaFormation12HybridPass is set above.
                ElectronRedistributionOrchestrator.RunElectronRedistributionForBondEvent(
                    ElectronRedistributionOrchestrator.BondRedistributionEventId.SigmaFormation12,
                    atomA,
                    atomB,
                    guideOrb,
                    bond);
                var snapAfterG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                guide.SnapshotNucleusParentedOrbitalLocalTransforms(snapAfterG);
                AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapBeforeG);

                var emptySnap = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                yield return StartCoroutine(editModeManager.CoLerpSigmaFormationNucleusOrbitalLocals(
                    snapBeforeG,
                    snapAfterG,
                    emptySnap,
                    emptySnap,
                    phase3Sec,
                    () => editModeManager.FinishSigmaBondInstantTail(
                        atomA,
                        atomB,
                        skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true)));
            }
            else
            {
                editModeManager.FinishSigmaBondInstantTail(
                    atomA,
                    atomB,
                    skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
            }
        }
        finally
        {
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
        }
    }

    /// <summary>
    /// Lets orbital-drag σ phase 1 animate bond GO σ world pose; otherwise <see cref="CovalentBond.LateUpdate"/> overwrites it every frame.
    /// </summary>
    static void SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(AtomFunction atom, bool suppress)
    {
        if (atom == null) return;
        foreach (var cb in atom.CovalentBonds)
        {
            if (cb != null) cb.suppressSigmaPrebondBondFrameOrbitalPose = suppress;
        }
    }

    /// <summary>
    /// Atoms that move in σ phase 1. If the guide is not in the same molecule, the whole connected component moves.
    /// If it is σ formation in one molecule, move the non-guide <b>branch</b> (reachable without crossing the guide atom)
    /// so bond cylinders and substituents rigidly follow the same rotation as the unified shell.
    /// </summary>
    static List<AtomFunction> BuildNonGuideFragmentAtomsForApproach(AtomFunction guide, AtomFunction nonGuide)
    {
        var mol = nonGuide.GetConnectedMolecule();
        if (mol == null || mol.Count == 0)
            return new List<AtomFunction> { nonGuide };
        bool guideInMol = false;
        foreach (var a in mol)
        {
            if (a != null && a == guide) { guideInMol = true; break; }
        }
        if (!guideInMol)
            return new List<AtomFunction>(mol);

        var fragment = new List<AtomFunction>();
        var visited = new HashSet<AtomFunction>();
        var queue = new Queue<AtomFunction>();
        queue.Enqueue(nonGuide);
        visited.Add(nonGuide);
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            fragment.Add(a);
            for (int bi = 0; bi < a.CovalentBonds.Count; bi++)
            {
                var cb = a.CovalentBonds[bi];
                if (cb == null) continue;
                var other = cb.AtomA == a ? cb.AtomB : cb.AtomA;
                if (other == null || other == guide) continue;
                if (visited.Add(other))
                    queue.Enqueue(other);
            }
        }
        return fragment;
    }

}
