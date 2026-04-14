using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// σ formation from <b>orbital drag</b>: phase 1 pre-bond (placeholder — reimplement in <see cref="CoOrbitalDragSigmaPhase1PrebondPlaceholder"/>),
/// then bond formation (cylinder + orbital→line), then post-bond guide hybrid lerp.
/// Independent of edit mode; add this component to the scene (e.g. next to <see cref="EditModeManager"/>).
/// Timings are read from the gesture <see cref="ElectronOrbitalFunction"/> (the dragged lobe when available).
/// </summary>
[DefaultExecutionOrder(100)]
public class SigmaBondFormation : MonoBehaviour
{
    [SerializeField] EditModeManager editModeManager;

    void Awake()
    {
        if (editModeManager == null)
            editModeManager = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
    }

    /// <summary>
    /// Ensures a scene runner exists for orbital-drag σ (pre-bond placeholder + bond + post-bond guide). If none, adds this component to the
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
    /// Nucleus-parented lerp for guide post-bond redistribution (matches σ-break <see cref="SigmaBreakPureRedistribution.MotionPlan.NonBondLerp"/>).
    /// For lone orbitals on a nucleus: slerp hybrid +X directions in parent space (not quaternion slerp * right, which diverges under roll),
    /// rebuild rotation via <see cref="ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection"/> for twist continuity,
    /// and place centroid on the same axis with magnitude lerped from start/end and start hemisphere sign (± vs +X) for back-side 0e pins.
    /// </summary>
    static void ApplySigmaLerpStepPhase3(
        List<(ElectronOrbitalFunction orb, Vector3 localPos, Quaternion localRot)> startRows,
        Dictionary<int, (Vector3 lp, Quaternion lr)> endById,
        float s)
    {
        if (startRows == null) return;
        const float epsMag = 1e-8f;
        const float epsTip = 1e-12f;

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
                        Vector3 tipS = Vector3.Slerp(tipStart, tipEnd, s);
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
                Quaternion lerpedRot = Quaternion.Slerp(row.localRot, end.lr, s);
                row.orb.transform.localRotation = lerpedRot;
                row.orb.transform.localPosition = Vector3.Lerp(row.localPos, end.lp, s);
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
    /// Orbital-drag σ <b>phase 1</b> (pre-bond): empty placeholder — implement non-guide approach / shell / fragment motion here incrementally.
    /// <paramref name="phase1Sec"/> is read from the gesture orbital; use it when you add timed pre-bond animation.
    /// </summary>
    IEnumerator CoOrbitalDragSigmaPhase1PrebondPlaceholder(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital,
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOrb,
        float phase1Sec)
    {
        if (atomA == null || atomB == null || guide == null || nonGuide == null)
            yield break;

        _ = orbA;
        _ = orbB;
        _ = redistributionGuideTieBreakDraggedOrbital;
        _ = guideOrb;
        _ = phase1Sec;

        yield break;
    }

    /// <summary>
    /// True if the orbital-drag σ coroutine was started. When <see cref="editModeManager"/> is null, returns false.
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

    /// <summary>
    /// Orbital-drag σ: phase 1 pre-bond (see <see cref="CoOrbitalDragSigmaPhase1PrebondPlaceholder"/>), bond animation, then post-bond guide lerp.
    /// </summary>
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
            // Snap to pre-drag locals, then wait one frame so bond animation reads nuclei and orbitals from the restored configuration.
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

            yield return StartCoroutine(CoOrbitalDragSigmaPhase1PrebondPlaceholder(
                atomA,
                atomB,
                orbA,
                orbB,
                redistributionGuideTieBreakDraggedOrbital,
                guide,
                nonGuide,
                guideOrb,
                phase1Sec));

            // Phase 2: bond creation + cylinder + orbital→line. Durations from sigmaFormationPhase2* (resolved, non-negative).

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
    /// While the guide post-bond hybrid lerp runs, suppress bond-frame σ orbital pose snaps so animation owns world pose.
    /// </summary>
    static void SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(AtomFunction atom, bool suppress)
    {
        if (atom == null) return;
        foreach (var cb in atom.CovalentBonds)
        {
            if (cb != null) cb.suppressSigmaPrebondBondFrameOrbitalPose = suppress;
        }
    }


}
