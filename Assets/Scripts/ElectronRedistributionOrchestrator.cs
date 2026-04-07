using UnityEngine;

/// <summary>
/// Bond-event electron redistribution orchestration (σ formation v1: formation σ-hybrid / VSEPR refresh on <see cref="AtomFunction"/>).
/// </summary>
public static class ElectronRedistributionOrchestrator
{
    /// <summary>
    /// Unit vector from <paramref name="atom"/> nucleus toward <paramref name="orb"/> (world positions).
    /// Same for 0e/1e/2e; not tied to guide/non-guide. Degenerate offset falls back to local +X.
    /// </summary>
    static Vector3 SigmaLobeUnitDirectionFromAtom(ElectronOrbitalFunction orb, AtomFunction atom)
    {
        if (orb == null) return Vector3.right;
        if (atom != null)
        {
            Vector3 v = orb.transform.position - atom.transform.position;
            if (v.sqrMagnitude > 1e-10f)
                return v.normalized;
        }
        return OrbitalAngleUtility.GetOrbitalDirectionWorld(orb.transform);
    }

    /// <summary>
    /// Unit axis for the guide atom’s in-operation σ lobe: <see cref="SigmaLobeUnitDirectionFromAtom"/> for that lobe.
    /// Phase-1: non-guide nucleus at <c>guidePos + u × bondLength</c>; prebond non-guide lobe targets <c>-u</c>.
    /// </summary>
    static Vector3 NormalizedGuideSigmaOpHeadForDrag(
        ElectronOrbitalFunction guideOp,
        AtomFunction guideAtom,
        Vector3 nonGuidePosW)
    {
        AtomFunction guideResolved = guideAtom;
        if (guideResolved == null && guideOp != null && guideOp.transform.parent != null)
            guideResolved = guideOp.transform.parent.GetComponent<AtomFunction>();

        Vector3 guidePosW;
        if (guideResolved != null)
            guidePosW = guideResolved.transform.position;
        else if (guideOp != null && guideOp.transform.parent != null)
        {
            var p = guideOp.transform.parent.GetComponent<AtomFunction>();
            guidePosW = p != null ? p.transform.position : guideOp.transform.position;
        }
        else
            guidePosW = guideOp != null ? guideOp.transform.position : Vector3.zero;

        Vector3 towardNg = nonGuidePosW - guidePosW;
        float towardMag2 = towardNg.sqrMagnitude;
        if (guideOp != null)
        {
            Vector3 gh = SigmaLobeUnitDirectionFromAtom(guideOp, guideResolved);
            if (gh.sqrMagnitude > 1e-10f)
                return gh.normalized;
        }
        return towardMag2 > 1e-10f ? towardNg.normalized : Vector3.right;
    }

    /// <summary>When true, resolve guide atoms and log only — no hybrid refresh. Default off so σ formation runs hybrid alignment.</summary>
    public static bool DryRunLogOnly = false;

    public enum BondRedistributionEventId
    {
        SigmaFormation12 = 12,
        PiFormation11 = 11,
        PiBreak21 = 21,
        SigmaBreak22 = 22
    }

    /// <param name="draggedOrbitalForGuideTieBreak">Orbital used with <see cref="ElectronRedistributionGuide.ResolveGuideAtomForPair"/> mass tie-break; usually the lobe the user dragged (see EditModeManager σ formation).</param>
    public static void RunElectronRedistributionForBondEvent(
        BondRedistributionEventId eventId,
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction draggedOrbitalForGuideTieBreak,
        CovalentBond operationBondOrNull = null)
    {
        if (atomA == null || atomB == null) return;

        ElectronRedistributionGuide.ResolveGuideAtomForPair(
            atomA, atomB, draggedOrbitalForGuideTieBreak, out var guide, out var nonGuide);

        if (DryRunLogOnly || operationBondOrNull == null)
            return;

        if (eventId == BondRedistributionEventId.SigmaFormation12)
            ExecuteSigmaFormation12HybridAlignment(nonGuide, guide, operationBondOrNull);
    }

    /// <summary>Non-guide nucleus target: guide position + guide σ op head × bond length (orbital-drag σ phase 1).</summary>
    public static Vector3 ComputeNonGuideNucleusTargetAlongGuideOpHead(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        float bondLength)
    {
        Vector3 g = guide.transform.position;
        Vector3 n0 = nonGuide.transform.position;
        Vector3 u = NormalizedGuideSigmaOpHeadForDrag(guideOp, guide, n0);
        return g + u * bondLength;
    }

    /// <summary>
    /// Orbital-drag σ phase 1 (pre-bond): rigid world rotation on the non-guide nucleus so its in-op lobe axis
    /// (nucleus→orbital) matches <c>−</c> the guide’s in-op axis; δ applies to every nucleus-parented orbital on that atom.
    /// </summary>
    public static void RunSigmaFormation12PrebondNonGuideHybridOnly(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction draggedOrbitalForGuideTieBreak)
    {
        if (atomA == null || atomB == null || orbA == null || orbB == null) return;
        ElectronRedistributionGuide.ResolveGuideAtomForPair(
            atomA, atomB, draggedOrbitalForGuideTieBreak, out var guide, out var nonGuide);
        if (nonGuide == null || guide == null) return;

        ElectronOrbitalFunction nonGuideOp = nonGuide == atomA ? orbA : orbB;
        ElectronOrbitalFunction guideOp = guide == atomA ? orbA : orbB;
        if (nonGuideOp == null || guideOp == null) return;

        Vector3 g = guide.transform.position;
        Vector3 nn = nonGuide.transform.position;
        Vector3 guideHead = NormalizedGuideSigmaOpHeadForDrag(guideOp, guide, nn);
        Vector3 desiredNonGuideHead = -guideHead;

        Vector3 currentHead = SigmaLobeUnitDirectionFromAtom(nonGuideOp, nonGuide);
        if (currentHead.sqrMagnitude < 1e-10f)
        {
            Vector3 towardGuide = g - nn;
            if (towardGuide.sqrMagnitude < 1e-10f) return;
            currentHead = towardGuide.normalized;
        }
        else
            currentHead.Normalize();

        float alignDeg = Vector3.Angle(currentHead, desiredNonGuideHead);
        if (alignDeg < 0.02f)
            return;

        Quaternion delta = Quaternion.FromToRotation(currentHead, desiredNonGuideHead);
        nonGuide.ApplyRigidWorldRotationToNucleusParentedOrbitals(delta, nonGuideOp);
    }

    /// <summary>
    /// Post-bond σ-12 hybrid on the <b>guide</b> only. Prefer <see cref="RunElectronRedistributionForBondEvent"/> with
    /// <see cref="CovalentBond.SkipNonGuideExecuteSigmaFormation12HybridPass"/> true (orbital-drag phase 3 / instant postbond).
    /// </summary>
    public static void RunSigmaFormation12PostbondGuideHybridOnly(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction draggedOrbitalForGuideTieBreak,
        CovalentBond bond)
    {
        if (atomA == null || atomB == null || bond == null) return;
        bool savedSkip = bond.SkipNonGuideExecuteSigmaFormation12HybridPass;
        bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;
        try
        {
            RunElectronRedistributionForBondEvent(
                BondRedistributionEventId.SigmaFormation12,
                atomA,
                atomB,
                draggedOrbitalForGuideTieBreak,
                bond);
        }
        finally
        {
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = savedSkip;
        }
    }

    /// <summary>
    /// v1: two endpoint passes only (non-guide then guide), not a second full-molecule sweep — avoids double-applying hybrid refresh.
    /// Skips Z==1: hydrogen does not run full formation hybrid alignment here.
    /// </summary>
    static void ExecuteSigmaFormation12HybridAlignment(
        AtomFunction nonGuide,
        AtomFunction guide,
        CovalentBond bond)
    {
        void RefreshIfHeavy(AtomFunction center, AtomFunction partner)
        {
            if (center == null || partner == null)
                return;

            if (center.AtomicNumber <= 1)
                return;

            center.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(partner, bond, null);
        }

        if (bond == null || !bond.SkipNonGuideExecuteSigmaFormation12HybridPass)
            RefreshIfHeavy(nonGuide, guide);
        RefreshIfHeavy(guide, nonGuide);
    }
}
