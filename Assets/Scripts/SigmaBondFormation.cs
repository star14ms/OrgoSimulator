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

    bool phase1ApplyPerOrbitalLocalShell;

    void Awake()
    {
        if (editModeManager == null)
            editModeManager = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
    }

    void LateUpdate()
    {
        if (phase1ApplyShellPosesInLateUpdate && phase1WorldBeforeShell != null && !phase1ApplyPerOrbitalLocalShell)
        {
            foreach (var w in phase1WorldBeforeShell)
            {
                if (w.orb == null) continue;
                w.orb.transform.SetPositionAndRotation(
                    phase1Pivot0W + phase1Off + phase1QS * (w.worldPos - phase1Pivot0W),
                    phase1QS * w.worldRot);
            }
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

    /// <summary>
    /// σ phase-1 local offset: <see cref="Vector3.Lerp"/> drives the tip through the nucleus when start/end are obtuse
    /// (antipodal offsets). Blend direction on the unit sphere and magnitude along the edge instead.
    /// </summary>
    static Vector3 LerpOrbitalLocalOffsetAvoidThroughNucleus(Vector3 localStart, Vector3 localEnd, float t)
    {
        float m0 = localStart.magnitude;
        float m1 = localEnd.magnitude;
        const float eps = 1e-8f;
        if (m0 < eps && m1 < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        if (m0 < eps || m1 < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        Vector3 u0 = localStart / m0;
        Vector3 u1 = localEnd / m1;
        if (Vector3.Dot(u0, u1) >= 0f)
            return Vector3.Lerp(localStart, localEnd, t);
        Vector3 u = Vector3.Slerp(u0, u1, t);
        if (u.sqrMagnitude < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        u.Normalize();
        float m = Mathf.Lerp(m0, m1, t);
        return u * m;
    }

    /// <summary>
    /// Nucleus-local tip offset: blend direction on the unit sphere and length separately so the lobe sweeps along the shell
    /// instead of chord-cutting through interior (misleading motion toward another fixed lobe, e.g. σ prebond 0e pin).
    /// </summary>
    static Vector3 LerpLocalOrbitalOffsetSpherical(Vector3 localStart, Vector3 localEnd, float t)
    {
        float m0 = localStart.magnitude;
        float m1 = localEnd.magnitude;
        const float eps = 1e-8f;
        if (m0 < eps && m1 < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        if (m0 < eps || m1 < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        Vector3 u0 = localStart / m0;
        Vector3 u1 = localEnd / m1;
        Vector3 u = Vector3.Slerp(u0, u1, t);
        if (u.sqrMagnitude < eps)
            return Vector3.Lerp(localStart, localEnd, t);
        u.Normalize();
        float m = Mathf.Lerp(m0, m1, t);
        return u * m;
    }

    /// <summary>
    /// Along the great circle from <paramref name="v0"/> to <paramref name="v1"/>, find a blend parameter <c>t</c> so the angle
    /// from <paramref name="refTipParentLocal"/> to <c>Slerp(v0,v1,t)</c> matches <c>Lerp(∠(ref,v0), ∠(ref,v1), s)</c> (linear in gesture progress).
    /// Used when a pinned 0e lobe stays fixed: plain <c>Slerp(v0,v1,s)</c> on tips does not linearize that inter-domain angle in <c>s</c>.
    /// </summary>
    static float PrebondShellTipSlerpTForLinearAngleToRef(
        Vector3 refTipParentLocal, Vector3 v0, Vector3 v1, float s)
    {
        float a0 = Vector3.Angle(refTipParentLocal, v0);
        float a1 = Vector3.Angle(refTipParentLocal, v1);
        float targetAng = Mathf.Lerp(a0, a1, s);
        const int steps = 48;
        float bestT = s;
        float bestErr = float.MaxValue;
        for (int k = 0; k <= steps; k++)
        {
            float t = k / (float)steps;
            Vector3 w = Vector3.Slerp(v0, v1, t);
            if (w.sqrMagnitude < 1e-14f) continue;
            w.Normalize();
            float err = Mathf.Abs(Vector3.Angle(refTipParentLocal, w) - targetAng);
            if (err < bestErr)
            {
                bestErr = err;
                bestT = t;
            }
        }
        return bestT;
    }

    /// <summary>
    /// Nucleus-parented lerp for guide redistribution (matches σ-break <see cref="SigmaBreakPureRedistribution.MotionPlan.NonBondLerp"/>).
    /// For lone orbitals on a nucleus: slerp hybrid +X directions in parent space (not quaternion slerp * right, which diverges under roll),
    /// rebuild rotation via <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> for twist continuity,
    /// and place centroid on the same axis with magnitude lerped from start/end and start hemisphere sign (± vs +X) for back-side 0e pins.
    /// Orbital-drag phase-1 prebond: when <paramref name="prebondShellPinnedZeroEForLinearInterTipAngle"/> and guide/non-guide are set,
    /// bond-parented σ orbitals on other bonds (substituents) keep the pre-phase snapshot local pose so only nucleus lone lobes tween toward the predictive shell end.
    /// </summary>
    static void ApplySigmaLerpStepPhase3(
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> startRows,
        Dictionary<int, (Vector3 lp, Quaternion lr)> endById,
        float s,
        ElectronOrbitalFunction prebondShellPinnedZeroEForLinearInterTipAngle = null,
        AtomFunction sigmaFormationPrebondGuideAtom = null,
        AtomFunction sigmaFormationPrebondNonGuideAtom = null)
    {
        if (startRows == null) return;
        const float epsMag = 1e-8f;
        const float epsTip = 1e-12f;
        bool freezeNonFormingBondSigmaInOrbitalDragPrebond =
            prebondShellPinnedZeroEForLinearInterTipAngle != null
            && sigmaFormationPrebondGuideAtom != null
            && sigmaFormationPrebondNonGuideAtom != null;
        Vector3 refPinnedZeroETip = default;
        bool useLinearInterTipToPinnedZeroE = false;
        if (prebondShellPinnedZeroEForLinearInterTipAngle != null)
        {
            for (int j = 0; j < startRows.Count; j++)
            {
                if (startRows[j].orb != prebondShellPinnedZeroEForLinearInterTipAngle) continue;
                Vector3 rj = startRows[j].localRot * Vector3.right;
                if (rj.sqrMagnitude > epsTip)
                {
                    refPinnedZeroETip = rj.normalized;
                    useLinearInterTipToPinnedZeroE = true;
                }
                break;
            }
        }

        for (int i = 0; i < startRows.Count; i++)
        {
            var row = startRows[i];
            if (row.orb == null) continue;
            if (!endById.TryGetValue(row.orb.GetInstanceID(), out var end)) continue;
            Transform pr = row.orb.transform.parent;
            bool nucleusNonBond =
                row.orb.Bond == null
                && pr != null
                && pr.GetComponent<AtomFunction>() != null;

            if (nucleusNonBond)
            {
                var atom = pr.GetComponent<AtomFunction>();
                float m0 = row.localPos.magnitude;
                float m1 = end.lp.magnitude;
                if (atom == null || (m0 < epsMag && m1 < epsMag))
                {
                    row.orb.transform.localRotation = Quaternion.Slerp(row.localRot, end.lr, s);
                    row.orb.transform.localPosition = Vector3.Lerp(row.localPos, end.lp, s);
                }
                else
                {
                    Vector3 tipStart = (row.localRot * Vector3.right).normalized;
                    Vector3 tipEnd = (end.lr * Vector3.right).normalized;
                    if (tipStart.sqrMagnitude < epsTip || tipEnd.sqrMagnitude < epsTip)
                    {
                        row.orb.transform.localRotation = Quaternion.Slerp(row.localRot, end.lr, s);
                        row.orb.transform.localPosition = LerpLocalOrbitalOffsetSpherical(row.localPos, end.lp, s);
                    }
                    else
                    {
                        Vector3 tipS;
                        if (useLinearInterTipToPinnedZeroE
                            && prebondShellPinnedZeroEForLinearInterTipAngle != null
                            && row.orb != prebondShellPinnedZeroEForLinearInterTipAngle
                            && refPinnedZeroETip.sqrMagnitude > epsTip)
                        {
                            float tArc = PrebondShellTipSlerpTForLinearAngleToRef(
                                refPinnedZeroETip, tipStart, tipEnd, s);
                            tipS = Vector3.Slerp(tipStart, tipEnd, tArc);
                        }
                        else
                            tipS = Vector3.Slerp(tipStart, tipEnd, s);
                        if (tipS.sqrMagnitude < epsTip)
                            tipS = tipEnd;
                        else
                            tipS.Normalize();
                        Quaternion rotHint = Quaternion.Slerp(row.localRot, end.lr, s);
                        var (_, rotCanon) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                            tipS, atom.BondRadius, rotHint);
                        row.orb.transform.localRotation = rotCanon;
                        float m = Mathf.Lerp(m0, m1, s);
                        float radialSign = 1f;
                        if (m0 > epsMag && tipStart.sqrMagnitude > epsTip
                            && Vector3.Dot(row.localPos.normalized, tipStart) < 0f)
                            radialSign = -1f;
                        // Obtuse offset vs target (e.g. prebond 0e snap vs shell end ~180° in H31): tip×m would hug +tip while
                        // centroids lie on opposite hemispheres — blend offset on the sphere + magnitude (see LerpOrbitalLocalOffsetAvoidThroughNucleus).
                        if (m0 > epsMag && m1 > epsMag
                            && Vector3.Dot(row.localPos.normalized, end.lp.normalized) < 0f)
                            row.orb.transform.localPosition = LerpOrbitalLocalOffsetAvoidThroughNucleus(row.localPos, end.lp, s);
                        else
                            row.orb.transform.localPosition = tipS * (m * radialSign);
                    }
                }
            }
            else
            {
                bool freezeSubstituentSigmaBondOrb =
                    freezeNonFormingBondSigmaInOrbitalDragPrebond
                    && row.orb.Bond is CovalentBond cbSigma
                    && row.orb.transform.parent == cbSigma.transform
                    && !((cbSigma.AtomA == sigmaFormationPrebondGuideAtom
                            && cbSigma.AtomB == sigmaFormationPrebondNonGuideAtom)
                        || (cbSigma.AtomA == sigmaFormationPrebondNonGuideAtom
                            && cbSigma.AtomB == sigmaFormationPrebondGuideAtom));
                if (freezeSubstituentSigmaBondOrb)
                {
                    row.orb.transform.localRotation = row.localRot;
                    row.orb.transform.localPosition = row.localPos;
                }
                else
                {
                    Quaternion lerpedRot = Quaternion.Slerp(row.localRot, end.lr, s);
                    row.orb.transform.localRotation = lerpedRot;
                    row.orb.transform.localPosition = Vector3.Lerp(row.localPos, end.lp, s);
                }
            }
        }
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

            int sigmaDragSavedNonGuideOpElectrons = nonGuide == atomA ? orbA.ElectronCount : orbB.ElectronCount;
            int sigmaDragSavedGuideOpElectrons = guide == atomA ? orbA.ElectronCount : orbB.ElectronCount;

            Vector3 n0 = nonGuide.transform.position;
            Vector3 pivot0W = n0;
            float bl = DefaultSigmaBondLengthForPair(guide, nonGuide);
            ElectronOrbitalFunction guideOpForPlacement = guide == atomA ? orbA : orbB;
            Vector3 nTarget = ElectronRedistributionOrchestrator.ComputeNonGuideNucleusTargetAlongGuideOpHead(
                guide, nonGuide, guideOpForPlacement, bl);
            Vector3 deltaTotal = nTarget - n0;

            // σ phase-1 (per-orbital / predictive) bond δ + tip dirs — mirror guide phase 3 prelude; set after refresh below.
            List<CovalentBond> phase1NgSigmaBonds = null;
            Quaternion[] phase1NgD0 = null, phase1NgD1 = null, phase1NgWr0 = null, phase1NgWr1 = null;
            int phase1NSig = 0;
            bool phase1SubstituentFragmentMotion = false;
            List<AtomFunction> phase1SigmaNeighbors = null;
            Vector3[] phase1OldSigmaDirWorld = null;
            Vector3[] phase1NewSigmaDirWorld = null;
            Vector3[] phase1InterpDirs = null;
            HashSet<AtomFunction> phase1FragAtoms = null;
            Dictionary<AtomFunction, Vector3> phase1FragmentStartWorld = null;
            float phase1MaxSigmaBondDeltaDegLog = 0f;
            // Pre-Refresh σ δ / world rot (before hybrid refresh + rebaseline). Rebaseline keeps phase1NgD0 for phase-3 lerp;
            // substituent fragment span uses this vs post-shell D1/Wr1 (rebaseline zeros Angle(rebaselineD0,D1)).
            Quaternion[] phase1NgD0ForFragmentSpan = null;
            Quaternion[] phase1NgWr0ForFragmentSpan = null;

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

            // Phase 1 pre-bond: non-guide shell. One 0e and one 2e on the forming pair (either side): same predictive path — rigid prebond,
            // re-snapshot, RefreshSigmaBond with guide op refLocal + per-orbital lerp. If only one polarity ran before, the other fell through
            // to unified shell and wrong template (logs: usePerOrbitalPhase1 false when guide 0e / non-guide 2e).
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

            bool usePerOrbitalPhase1 = nonGuideOpForAnim != null && guideOpForPlacement != null
                && nonGuide.AtomicNumber > 1
                && ((nonGuideOpForAnim.ElectronCount == 0 && guideOpForPlacement.ElectronCount == 2)
                    || (nonGuideOpForAnim.ElectronCount == 2 && guideOpForPlacement.ElectronCount == 0));

            Quaternion qFullShell;
            Dictionary<int, (Vector3 lp, Quaternion lr)> shellEndById = null;

            if (usePerOrbitalPhase1)
            {
                phase1NgSigmaBonds = new List<CovalentBond>();
                foreach (var cb in nonGuide.CovalentBonds)
                {
                    if (cb != null && cb.IsSigmaBondLine() && cb.Orbital != null)
                        phase1NgSigmaBonds.Add(cb);
                }
                int nNgSig = phase1NgSigmaBonds.Count;

                ElectronRedistributionOrchestrator.RunSigmaFormation12PrebondNonGuideHybridOnly(
                    atomA, atomB, orbA, orbB, guideOrb);
                snapBeforeNG.Clear();
                nonGuide.SnapshotAllBondedOrbitalLocalTransforms(snapBeforeNG, nonGuideOpForAnim);
                worldBeforeNG.Clear();
                nonGuide.CaptureAllBondedOrbitalWorldTransforms(worldBeforeNG, nonGuideOpForAnim);
                if (nonGuideOpForAnim != null)
                {
                    wrotBeforeOp = nonGuideOpForAnim.transform.rotation;
                    foundWrotBeforeOp = true;
                }

                phase1NgD0 = new Quaternion[nNgSig];
                phase1NgD1 = new Quaternion[nNgSig];
                phase1NgWr0 = new Quaternion[nNgSig];
                phase1NgWr1 = new Quaternion[nNgSig];
                for (int bi = 0; bi < nNgSig; bi++)
                {
                    var cb = phase1NgSigmaBonds[bi];
                    cb.CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(cb.Orbital.transform.rotation);
                    phase1NgD0[bi] = cb.GetOrbitalRedistributionWorldDeltaForDiagnostics();
                    phase1NgWr0[bi] = cb.Orbital.transform.rotation;
                }

                phase1NgD0ForFragmentSpan = new Quaternion[nNgSig];
                phase1NgWr0ForFragmentSpan = new Quaternion[nNgSig];
                for (int bi = 0; bi < nNgSig; bi++)
                {
                    phase1NgD0ForFragmentSpan[bi] = phase1NgD0[bi];
                    phase1NgWr0ForFragmentSpan[bi] = phase1NgWr0[bi];
                }

                nonGuide.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(
                    guide,
                    null,
                    null,
                    orbitalDragSigmaPhase3RegularGuide: true,
                    sigmaFormationPrebond: true,
                    sigmaFormationPrebondZeroEOperationOrb: nonGuideOpForAnim,
                    sigmaFormationPrebondGuideOperationOrb: guideOpForPlacement);

                // Re-baseline phase-1 σ δ/world tip from post–prebond-refresh poses. Initial Commit above ran before
                // Refresh; Restore+SetOrbitalRedistributionWorldDeltaForPhase3Lerp(phase1NgD0) must not reapply that
                // stale ~160° δ after TryMatch/Sync.
                for (int bi = 0; bi < nNgSig; bi++)
                {
                    var cb = phase1NgSigmaBonds[bi];
                    if (cb?.Orbital == null) continue;
                    cb.CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(cb.Orbital.transform.rotation);
                    phase1NgD0[bi] = cb.GetOrbitalRedistributionWorldDeltaForDiagnostics();
                    phase1NgWr0[bi] = cb.Orbital.transform.rotation;
                }

                var snapEndNG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                nonGuide.SnapshotAllBondedOrbitalLocalTransforms(snapEndNG, nonGuideOpForAnim);
                shellEndById = new Dictionary<int, (Vector3, Quaternion)>(snapEndNG.Count);
                foreach (var row in snapEndNG)
                {
                    if (row.orb == null) continue;
                    shellEndById[row.orb.GetInstanceID()] = (row.localPos, row.localRot);
                }

                // 0e shell end stays snapEndNG (post–RefreshSigmaBondOrbitalHybridAlignment, pinReservedDir + canonical slot).
                // Overwriting shellEnd with snapBeforeNG left localPosition off hybrid axis vs lones (H54 pos≠tip) while H25 was correct.

                for (int bi = 0; bi < nNgSig; bi++)
                {
                    var cb = phase1NgSigmaBonds[bi];
                    if (cb?.Orbital == null) continue;
                    phase1NgD1[bi] = cb.GetOrbitalRedistributionWorldDeltaForDiagnostics();
                    phase1NgWr1[bi] = cb.Orbital.transform.rotation;
                }

                // Same motion paradigm as guide phase 3: no qFullShell on atoms; substituents + cylinders follow tip/dir rigid step.
                qFullShell = Quaternion.identity;

                AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapBeforeNG);
                for (int bi = 0; bi < nNgSig; bi++)
                {
                    var cb = phase1NgSigmaBonds[bi];
                    if (cb?.Orbital == null) continue;
                    cb.UpdateBondTransformToCurrentAtoms();
                    cb.SetOrbitalRedistributionWorldDeltaForPhase3Lerp(phase1NgD0[bi]);
                }

                // Guide phase 3 analog: fixed initial σ directions from pre-approach layout; σ-tip / δ spans drive substituent rigid rotation about the (moving) non-guide pivot.
                if (phase1NgSigmaBonds.Count > 0)
                {
                    phase1NSig = phase1NgSigmaBonds.Count;
                    phase1SigmaNeighbors = new List<AtomFunction>(phase1NSig);
                    phase1OldSigmaDirWorld = new Vector3[phase1NSig];
                    phase1NewSigmaDirWorld = new Vector3[phase1NSig];
                    var phase1FullTipRotDirWorld = new Vector3[phase1NSig];
                    phase1InterpDirs = new Vector3[phase1NSig];
                    var deltaSpanDeg = new float[phase1NSig];
                    var includeForFragment = new bool[phase1NSig];
                    float maxDeltaDeg = -1f;
                    Vector3 ngpos0 = n0;
                    const int formingBondIdPhase1 = 0;
                    for (int i = 0; i < phase1NSig; i++)
                    {
                        var cb = phase1NgSigmaBonds[i];
                        var nbr = cb != null ? (cb.AtomA == nonGuide ? cb.AtomB : cb.AtomA) : null;
                        phase1SigmaNeighbors.Add(nbr);
                        phase1OldSigmaDirWorld[i] = nbr != null && initialWorld.TryGetValue(nbr, out var pn)
                            ? (pn - ngpos0).normalized
                            : Vector3.right;
                        deltaSpanDeg[i] = Quaternion.Angle(phase1NgD0ForFragmentSpan[i], phase1NgD1[i]);
                        if (deltaSpanDeg[i] > maxDeltaDeg)
                            maxDeltaDeg = deltaSpanDeg[i];
                        phase1FullTipRotDirWorld[i] = phase1OldSigmaDirWorld[i];
                        phase1NewSigmaDirWorld[i] = phase1OldSigmaDirWorld[i];
                    }
                    phase1MaxSigmaBondDeltaDegLog = maxDeltaDeg >= 0f ? maxDeltaDeg : 0f;

                    const float minDeltaForFragmentDeg = 0.75f;
                    const float deltaTieEpsDeg = 0.15f;
                    for (int i = 0; i < phase1NSig; i++)
                    {
                        bool isForming = phase1NgSigmaBonds[i] != null
                            && phase1NgSigmaBonds[i].GetInstanceID() == formingBondIdPhase1;
                        bool tiesMaxDelta = maxDeltaDeg >= minDeltaForFragmentDeg
                            && deltaSpanDeg[i] >= minDeltaForFragmentDeg
                            && Mathf.Abs(deltaSpanDeg[i] - maxDeltaDeg) <= deltaTieEpsDeg;
                        includeForFragment[i] = tiesMaxDelta
                            || (isForming && deltaSpanDeg[i] >= minDeltaForFragmentDeg)
                            || (deltaSpanDeg[i] >= minDeltaForFragmentDeg);

                        Vector3 tip0w = (phase1NgWr0ForFragmentSpan[i] * Vector3.right).normalized;
                        Vector3 tip1w = (phase1NgWr1[i] * Vector3.right).normalized;
                        if (tip0w.sqrMagnitude < 1e-12f || tip1w.sqrMagnitude < 1e-12f)
                        {
                            phase1FullTipRotDirWorld[i] = phase1OldSigmaDirWorld[i];
                            phase1NewSigmaDirWorld[i] = phase1OldSigmaDirWorld[i];
                        }
                        else
                        {
                            Vector3 towardTip1 = Vector3.Dot(tip1w, phase1OldSigmaDirWorld[i]) < 0f
                                ? (-tip1w).normalized
                                : tip1w.normalized;
                            phase1FullTipRotDirWorld[i] = towardTip1;
                            float angFull = includeForFragment[i]
                                ? Vector3.Angle(phase1OldSigmaDirWorld[i], phase1FullTipRotDirWorld[i])
                                : 0f;
                            float angCap = Mathf.Min(angFull, deltaSpanDeg[i]);
                            if (angFull < 1e-3f || angCap < 1e-3f)
                                phase1NewSigmaDirWorld[i] = phase1OldSigmaDirWorld[i];
                            else
                            {
                                phase1NewSigmaDirWorld[i] = Vector3.RotateTowards(
                                    phase1OldSigmaDirWorld[i],
                                    phase1FullTipRotDirWorld[i],
                                    angCap * Mathf.Deg2Rad,
                                    0f).normalized;
                            }
                        }
                    }

                    for (int i = 0; i < phase1NSig; i++)
                    {
                        if (Vector3.Angle(phase1OldSigmaDirWorld[i], phase1NewSigmaDirWorld[i]) >= 0.75f)
                        {
                            phase1SubstituentFragmentMotion = true;
                            break;
                        }
                    }

                    if (phase1SubstituentFragmentMotion && nonGuide != null)
                    {
                        phase1FragAtoms = new HashSet<AtomFunction>();
                        phase1FragmentStartWorld = new Dictionary<AtomFunction, Vector3>();
                        for (int i = 0; i < phase1NSig; i++)
                        {
                            var nbr = phase1SigmaNeighbors[i];
                            if (nbr == null) continue;
                            foreach (var a in nonGuide.GetAtomsOnSideOfSigmaBond(nbr))
                            {
                                if (a == null || a == nonGuide) continue;
                                if (phase1FragAtoms.Add(a))
                                    phase1FragmentStartWorld[a] = a.transform.position;
                            }
                        }
                    }
                }
            }
            else
            {
                ElectronRedistributionOrchestrator.RunSigmaFormation12PrebondNonGuideHybridOnly(atomA, atomB, orbA, orbB, guideOrb);
                Quaternion wrotAfterOp = nonGuideOpForAnim != null ? nonGuideOpForAnim.transform.rotation : Quaternion.identity;
                qFullShell = wrotAfterOp * Quaternion.Inverse(wrotBeforeOp);
                AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapBeforeNG);
            }

            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
            try
            {
                phase1WorldBeforeShell = worldBeforeNG;
                phase1ApplyShellPosesInLateUpdate = true;
                phase1ApplyPerOrbitalLocalShell = usePerOrbitalPhase1;
                float p1 = Mathf.Max(1e-5f, phase1Sec);
                float e1 = 0f;
                // First loop iteration uses s=0 without consuming time so the visible pose matches the t=0
                // shell (same as snapBeforeNG / approach start). Otherwise e1+=dt runs first and the first
                // rendered frame is already at smoothstep(dt/p1)>0 — reads as a teleport at animation start.
                bool phase1NeedInitialSZeroFrame = true;
                while (e1 < p1)
                {
                    float s;
                    if (phase1NeedInitialSZeroFrame)
                    {
                        phase1NeedInitialSZeroFrame = false;
                        s = 0f;
                    }
                    else
                    {
                        e1 += Time.deltaTime;
                        float u = Mathf.Clamp01(e1 / p1);
                        s = u * u * (3f - 2f * u);
                    }
                    Vector3 off = deltaTotal * s;
                    Quaternion qS = Quaternion.Slerp(Quaternion.identity, qFullShell, s);

                    if (usePerOrbitalPhase1 && shellEndById != null)
                    {
                        foreach (var a in toMove)
                        {
                            if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                            if (!initialWorldRot.TryGetValue(a, out var r0))
                                r0 = a.transform.rotation;
                            // Substituent fragment atoms: baseline after prebond refresh (phase1FragmentStartWorld),
                            // not initialWorld from before RunSigmaFormation12 / hybrid refresh — mismatch caused a
                            // per-frame snap when the loop overwrote p0+off with pStart+off before rigid targets.
                            Vector3 baseW = p0;
                            if (phase1SubstituentFragmentMotion && phase1FragmentStartWorld != null
                                && phase1FragmentStartWorld.TryGetValue(a, out var pFrag))
                                baseW = pFrag;
                            a.transform.SetPositionAndRotation(baseW + off, r0);
                        }

                        ApplySigmaLerpStepPhase3(
                            snapBeforeNG,
                            shellEndById,
                            s,
                            nonGuideOpForAnim,
                            guide,
                            nonGuide);

                        if (phase1SubstituentFragmentMotion && nonGuide != null && phase1FragAtoms != null
                            && phase1FragmentStartWorld != null && phase1NSig > 0 && phase1InterpDirs != null
                            && phase1OldSigmaDirWorld != null && phase1NewSigmaDirWorld != null
                            && phase1SigmaNeighbors != null)
                        {
                            Vector3 pivotW = nonGuide.transform.position;
                            for (int i = 0; i < phase1NSig; i++)
                                phase1InterpDirs[i] = Vector3
                                    .Slerp(phase1OldSigmaDirWorld[i], phase1NewSigmaDirWorld[i], s).normalized;

                            SigmaBreakPureRedistribution.BuildSigmaNeighborTargetsWithFragmentRigidRotation(
                                pivotW,
                                phase1SigmaNeighbors,
                                phase1OldSigmaDirWorld,
                                phase1InterpDirs,
                                nonGuide,
                                out var targets);
                            if (targets != null)
                            {
                                foreach (var (a, tw) in targets)
                                {
                                    if (a != null) a.transform.position = tw;
                                }
                            }
                        }

                        if (nonGuide != null)
                        {
                            var molPhase1 = nonGuide.GetConnectedMolecule();
                            if (molPhase1 != null && molPhase1.Count > 0)
                                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molPhase1);
                        }
                    }
                    else
                    {
                        foreach (var a in toMove)
                        {
                            if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                            if (!initialWorldRot.TryGetValue(a, out var r0))
                                r0 = a.transform.rotation;
                            a.transform.SetPositionAndRotation(
                                pivot0W + off + qS * (p0 - pivot0W),
                                qS * r0);
                        }
                    }

                    phase1Pivot0W = pivot0W;
                    phase1Off = off;
                    phase1QS = usePerOrbitalPhase1 && shellEndById != null ? Quaternion.identity : qS;
                    yield return null;
                }

                if (usePerOrbitalPhase1 && shellEndById != null)
                {
                    foreach (var a in toMove)
                    {
                        if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                        if (!initialWorldRot.TryGetValue(a, out var r0End))
                            r0End = a.transform.rotation;
                        Vector3 baseEndW = p0;
                        if (phase1SubstituentFragmentMotion && phase1FragmentStartWorld != null
                            && phase1FragmentStartWorld.TryGetValue(a, out var pFragEnd))
                            baseEndW = pFragEnd;
                        a.transform.SetPositionAndRotation(baseEndW + deltaTotal, r0End);
                    }

                    ApplySigmaLerpStepPhase3(
                        snapBeforeNG,
                        shellEndById,
                        1f,
                        nonGuideOpForAnim,
                        guide,
                        nonGuide);

                    if (phase1SubstituentFragmentMotion && nonGuide != null && phase1FragAtoms != null
                        && phase1FragmentStartWorld != null && phase1NSig > 0 && phase1InterpDirs != null
                        && phase1OldSigmaDirWorld != null && phase1NewSigmaDirWorld != null
                        && phase1SigmaNeighbors != null)
                    {
                        Vector3 pivotW = nonGuide.transform.position;
                        for (int i = 0; i < phase1NSig; i++)
                            phase1InterpDirs[i] = phase1NewSigmaDirWorld[i];

                        SigmaBreakPureRedistribution.BuildSigmaNeighborTargetsWithFragmentRigidRotation(
                            pivotW,
                            phase1SigmaNeighbors,
                            phase1OldSigmaDirWorld,
                            phase1InterpDirs,
                            nonGuide,
                            out var targetsFinal);
                        if (targetsFinal != null)
                        {
                            foreach (var (a, tw) in targetsFinal)
                            {
                                if (a != null) a.transform.position = tw;
                            }
                        }
                    }

                    if (nonGuide != null)
                    {
                        var molEndPhase1 = nonGuide.GetConnectedMolecule();
                        if (molEndPhase1 != null && molEndPhase1.Count > 0)
                            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molEndPhase1);
                    }

                    if (phase1NgSigmaBonds != null && phase1NgD1 != null)
                    {
                        for (int bi = 0; bi < phase1NgSigmaBonds.Count; bi++)
                        {
                            var cb = phase1NgSigmaBonds[bi];
                            if (cb?.Orbital == null || bi >= phase1NgD1.Length) continue;
                            cb.SetOrbitalRedistributionWorldDeltaForPhase3Lerp(phase1NgD1[bi]);
                        }
                        var molD1 = nonGuide != null ? nonGuide.GetConnectedMolecule() : null;
                        if (molD1 != null && molD1.Count > 0)
                            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molD1);
                    }
                }
                else
                {
                    foreach (var a in toMove)
                    {
                        if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
                        if (!initialWorldRot.TryGetValue(a, out var r0End))
                            r0End = a.transform.rotation;
                        a.transform.SetPositionAndRotation(
                            pivot0W + deltaTotal + qFullShell * (p0 - pivot0W),
                            qFullShell * r0End);
                    }
                }
            }
            finally
            {
                phase1ApplyShellPosesInLateUpdate = false;
                phase1WorldBeforeShell = null;
                phase1ApplyPerOrbitalLocalShell = false;
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
                var guideSigmaBonds = new List<CovalentBond>();
                foreach (var cb in guide.CovalentBonds)
                {
                    if (cb != null && cb.IsSigmaBondLine() && cb.Orbital != null)
                        guideSigmaBonds.Add(cb);
                }

                var d0 = new Quaternion[guideSigmaBonds.Count];
                var wr0 = new Quaternion[guideSigmaBonds.Count];
                for (int i = 0; i < guideSigmaBonds.Count; i++)
                {
                    var cb = guideSigmaBonds[i];
                    cb.CommitSigmaRedistributionDeltaFromWorldOrbitalRotation(cb.Orbital.transform.rotation);
                    d0[i] = cb.GetOrbitalRedistributionWorldDeltaForDiagnostics();
                    wr0[i] = cb.Orbital.transform.rotation;
                }

                var snapBeforeG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                guide.SnapshotNucleusParentedOrbitalLocalTransforms(snapBeforeG);

                bool skipPhase3Lerp = sigmaDragSavedNonGuideOpElectrons == 0
                    && sigmaDragSavedGuideOpElectrons == 2
                    && guide.OrbitalDragSigmaGuidePhase3ConformationAlreadyIdeal(nonGuide, bond);

                guide.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(
                    nonGuide,
                    bond,
                    null,
                    orbitalDragSigmaPhase3RegularGuide: true);

                var d1 = new Quaternion[guideSigmaBonds.Count];
                var wr1 = new Quaternion[guideSigmaBonds.Count];
                for (int i = 0; i < guideSigmaBonds.Count; i++)
                {
                    var cb = guideSigmaBonds[i];
                    d1[i] = cb.GetOrbitalRedistributionWorldDeltaForDiagnostics();
                    wr1[i] = cb.Orbital.transform.rotation;
                }

                var snapAfterG = new List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)>();
                guide.SnapshotNucleusParentedOrbitalLocalTransforms(snapAfterG);

                if (!skipPhase3Lerp)
                {
                    AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapBeforeG);

                    for (int i = 0; i < guideSigmaBonds.Count; i++)
                    {
                        var cb = guideSigmaBonds[i];
                        if (cb?.Orbital == null) continue;
                        cb.UpdateBondTransformToCurrentAtoms();
                        cb.SetOrbitalRedistributionWorldDeltaForPhase3Lerp(d0[i]);
                    }
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());

                    SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, true);
                    try
                    {
                        yield return StartCoroutine(CoPhase3GuideNucleusAndBondLerp(
                            guide,
                            bond != null ? bond.GetInstanceID() : 0,
                            snapBeforeG,
                            snapAfterG,
                            guideSigmaBonds,
                            d0,
                            d1,
                            wr0,
                            wr1,
                            phase3Sec));
                    }
                    finally
                    {
                        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, false);
                    }
                }
                else
                {
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }

                for (int i = 0; i < guideSigmaBonds.Count; i++)
                {
                    var cb = guideSigmaBonds[i];
                    if (cb?.Orbital == null) continue;
                    cb.UpdateBondTransformToCurrentAtoms();
                    cb.SetOrbitalRedistributionWorldDeltaForPhase3Lerp(d1[i]);
                }
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());

                editModeManager.FinishSigmaBondInstantTail(
                    atomA,
                    atomB,
                    skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
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
    /// Guide phase 3: lone pairs lerp in nucleus locals (σ-break non-bond analog); substituents use
    /// <see cref="SigmaBreakPureRedistribution.BuildSigmaNeighborTargetsWithFragmentRigidRotation"/> about the guide (cylinders follow atom motion).
    /// Bond δ stays at d0 (set before this coroutine) during the lerp; <paramref name="d1"/> after.
    /// </summary>
    IEnumerator CoPhase3GuideNucleusAndBondLerp(
        AtomFunction guide,
        int formingBondId,
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> snapBefore,
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> snapAfter,
        List<CovalentBond> guideSigmaBonds,
        Quaternion[] d0,
        Quaternion[] d1,
        Quaternion[] wr0,
        Quaternion[] wr1,
        float duration)
    {
        var endByIdNucleus = new Dictionary<int, (Vector3 lp, Quaternion lr)>();
        foreach (var e in snapAfter)
        {
            if (e.orb == null || guide == null || e.orb.transform.parent != guide.transform) continue;
            endByIdNucleus[e.orb.GetInstanceID()] = (e.localPos, e.localRot);
        }

        var molForBondLines = guide != null ? guide.GetConnectedMolecule() : null;

        int nSig = guideSigmaBonds.Count;
        var sigmaNeighbors = new List<AtomFunction>(nSig);
        var includeForFragment = new bool[nSig];
        var oldSigmaDirWorld = new Vector3[nSig];
        var newSigmaDirWorld = new Vector3[nSig];
        var fullTipRotDirWorld = new Vector3[nSig];
        var interpDirs = new Vector3[nSig];
        var deltaSpanDeg = new float[nSig];
        float maxDeltaDeg = -1f;
        Vector3 gpos0 = guide != null ? guide.transform.position : Vector3.zero;
        for (int i = 0; i < nSig; i++)
        {
            var cb = guideSigmaBonds[i];
            var nbr = cb != null ? (cb.AtomA == guide ? cb.AtomB : cb.AtomA) : null;
            sigmaNeighbors.Add(nbr);
            oldSigmaDirWorld[i] = nbr != null
                ? (nbr.transform.position - gpos0).normalized
                : Vector3.right;
            deltaSpanDeg[i] = Quaternion.Angle(d0[i], d1[i]);
            if (deltaSpanDeg[i] > maxDeltaDeg)
                maxDeltaDeg = deltaSpanDeg[i];
            fullTipRotDirWorld[i] = oldSigmaDirWorld[i];
            newSigmaDirWorld[i] = oldSigmaDirWorld[i];
        }

        // Use σ bond δ from diagnostics: any leg whose δ ties the global max (within ε) participates in fragment
        // targeting and tip capping — not only the single max-δ index. Otherwise equivalent legs (e.g. three −H on CH₃) can
        // share ~identical δ but only one got includeForFragment and FromToRotation stayed identity on the others.
        const float minDeltaForFragmentDeg = 0.75f;
        const float deltaTieEpsDeg = 0.15f;
        for (int i = 0; i < nSig; i++)
        {
            bool isForming = guideSigmaBonds[i] != null && guideSigmaBonds[i].GetInstanceID() == formingBondId;
            bool tiesMaxDelta = maxDeltaDeg >= minDeltaForFragmentDeg
                && deltaSpanDeg[i] >= minDeltaForFragmentDeg
                && Mathf.Abs(deltaSpanDeg[i] - maxDeltaDeg) <= deltaTieEpsDeg;
            // Any leg with meaningful δ participates — not only legs tying global max (hetero substituents can have very different δ spans).
            includeForFragment[i] = tiesMaxDelta
                || (isForming && deltaSpanDeg[i] >= minDeltaForFragmentDeg)
                || (deltaSpanDeg[i] >= minDeltaForFragmentDeg);

            Vector3 tip0w = (wr0[i] * Vector3.right).normalized;
            Vector3 tip1w = (wr1[i] * Vector3.right).normalized;
            if (tip0w.sqrMagnitude < 1e-12f || tip1w.sqrMagnitude < 1e-12f)
            {
                fullTipRotDirWorld[i] = oldSigmaDirWorld[i];
                newSigmaDirWorld[i] = oldSigmaDirWorld[i];
            }
            else
            {
                // Use the post-refresh σ tip direction with sign chosen toward the bonded neighbor.
                // This avoids 180° branch ambiguity from tip-frame delta transport.
                Vector3 towardTip1 = Vector3.Dot(tip1w, oldSigmaDirWorld[i]) < 0f
                    ? (-tip1w).normalized
                    : tip1w.normalized;
                fullTipRotDirWorld[i] = towardTip1;
                float angFull = includeForFragment[i]
                    ? Vector3.Angle(oldSigmaDirWorld[i], fullTipRotDirWorld[i])
                    : 0f;
                float angCap = Mathf.Min(angFull, deltaSpanDeg[i]);
                if (angFull < 1e-3f || angCap < 1e-3f)
                    newSigmaDirWorld[i] = oldSigmaDirWorld[i];
                else
                {
                    newSigmaDirWorld[i] = Vector3.RotateTowards(
                        oldSigmaDirWorld[i],
                        fullTipRotDirWorld[i],
                        angCap * Mathf.Deg2Rad,
                        0f).normalized;
                }
            }
        }

        bool substituentFragmentMotion = false;
        for (int i = 0; i < nSig; i++)
        {
            if (Vector3.Angle(oldSigmaDirWorld[i], newSigmaDirWorld[i]) >= 0.75f)
            {
                substituentFragmentMotion = true;
                break;
            }
        }

        var fragAtoms = new HashSet<AtomFunction>();
        var fragmentStartWorld = new Dictionary<AtomFunction, Vector3>();
        if (substituentFragmentMotion && guide != null)
        {
            for (int i = 0; i < nSig; i++)
            {
                var nbr = sigmaNeighbors[i];
                if (nbr == null) continue;
                foreach (var a in guide.GetAtomsOnSideOfSigmaBond(nbr))
                {
                    if (a == null || a == guide) continue;
                    if (fragAtoms.Add(a))
                        fragmentStartWorld[a] = a.transform.position;
                }
            }
        }

        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float smooth = Mathf.Clamp01(elapsed / duration);
            smooth = smooth * smooth * (3f - 2f * smooth);

            ApplySigmaLerpStepPhase3(snapBefore, endByIdNucleus, smooth);

            if (substituentFragmentMotion && guide != null)
            {
                foreach (var a in fragAtoms)
                {
                    if (fragmentStartWorld.TryGetValue(a, out var pStart))
                        a.transform.position = pStart;
                }

                Vector3 pivotW = guide.transform.position;
                for (int i = 0; i < nSig; i++)
                    interpDirs[i] = Vector3.Slerp(oldSigmaDirWorld[i], newSigmaDirWorld[i], smooth).normalized;

                SigmaBreakPureRedistribution.BuildSigmaNeighborTargetsWithFragmentRigidRotation(
                    pivotW,
                    sigmaNeighbors,
                    oldSigmaDirWorld,
                    interpDirs,
                    guide,
                    out var targets);
                if (targets != null)
                {
                    foreach (var (a, tw) in targets)
                    {
                        if (a != null) a.transform.position = tw;
                    }
                }
            }

            if (molForBondLines != null)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);

            yield return null;
        }

        ApplySigmaLerpStepPhase3(snapBefore, endByIdNucleus, 1f);

        if (substituentFragmentMotion)
        {
            if (guide != null)
            {
                foreach (var a in fragAtoms)
                {
                    if (fragmentStartWorld.TryGetValue(a, out var pStart))
                        a.transform.position = pStart;
                }

                Vector3 pivotW = guide.transform.position;
                for (int i = 0; i < nSig; i++)
                    interpDirs[i] = newSigmaDirWorld[i];
                SigmaBreakPureRedistribution.BuildSigmaNeighborTargetsWithFragmentRigidRotation(
                    pivotW,
                    sigmaNeighbors,
                    oldSigmaDirWorld,
                    interpDirs,
                    guide,
                    out var targetsFinal);
                if (targetsFinal != null)
                {
                    foreach (var (a, tw) in targetsFinal)
                    {
                        if (a != null) a.transform.position = tw;
                    }
                }
            }
        }

        if (molForBondLines != null)
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);

        for (int bi = 0; bi < guideSigmaBonds.Count; bi++)
        {
            var cb = guideSigmaBonds[bi];
            if (cb == null) continue;
            cb.SetOrbitalRedistributionWorldDeltaForPhase3Lerp(d1[bi]);
        }
        if (molForBondLines != null)
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);

        AtomFunction.RestoreNucleusParentedOrbitalLocalTransforms(snapAfter);
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
