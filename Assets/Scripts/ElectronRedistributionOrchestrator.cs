using UnityEngine;

/// <summary>
/// Bond-event electron redistribution orchestration (σ formation v1: formation σ-hybrid / VSEPR refresh on <see cref="AtomFunction"/>).
/// </summary>
public static class ElectronRedistributionOrchestrator
{
    /// <summary>
    /// Unit bond axis from guide σ op +X; if +X points away from the partner (dot &lt; 0 vs guide→non-guide), use −+X so the bond pocket faces the non-guide. Same rule 0e/1e/2e.
    /// </summary>
    static Vector3 NormalizedGuideSigmaOpHeadForDrag(
        ElectronOrbitalFunction guideOp,
        Vector3 guidePosW,
        Vector3 nonGuidePosW)
    {
        Vector3 towardNg = nonGuidePosW - guidePosW;
        float towardMag2 = towardNg.sqrMagnitude;
        if (guideOp != null)
        {
            Vector3 gh = OrbitalAngleUtility.GetOrbitalDirectionWorld(guideOp.transform);
            if (gh.sqrMagnitude > 1e-10f)
            {
                gh.Normalize();
                if (towardMag2 > 1e-10f && Vector3.Dot(gh, towardNg) < 0f)
                    gh = -gh;
                return gh;
            }
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
        Vector3 u = NormalizedGuideSigmaOpHeadForDrag(guideOp, g, n0);
        return g + u * bondLength;
    }

    /// <summary>
    /// Orbital-drag σ phase 1 (pre-bond): one world rigid rotation on the non-guide nucleus so the op head (+X) is opposite the guide op head;
    /// the same δ applies to every nucleus-parented orbital on that atom.
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
        Vector3 guideHead = NormalizedGuideSigmaOpHeadForDrag(guideOp, g, nn);
        Vector3 desiredNonGuideHead = -guideHead;

        Vector3 currentHead = OrbitalAngleUtility.GetOrbitalDirectionWorld(nonGuideOp.transform);
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
    /// Orbital-drag σ phase 3 (post-bond): hybrid alignment on the <b>guide</b> center only (bond exists).
    /// </summary>
    public static void RunSigmaFormation12PostbondGuideHybridOnly(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction draggedOrbitalForGuideTieBreak,
        CovalentBond bond)
    {
        if (atomA == null || atomB == null || bond == null) return;
        ElectronRedistributionGuide.ResolveGuideAtomForPair(
            atomA, atomB, draggedOrbitalForGuideTieBreak, out var guide, out var nonGuide);
        if (guide == null || nonGuide == null || guide.AtomicNumber <= 1) return;
        guide.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(nonGuide, bond, null);
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
