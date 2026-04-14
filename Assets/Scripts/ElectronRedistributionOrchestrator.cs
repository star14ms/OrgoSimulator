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
    /// Unit axis along the guide’s σ lobe from the <b>guide</b> nucleus toward the operation orbital center
    /// (<see cref="SigmaLobeUnitDirectionFromAtom"/>(guideOp, guide)). Used for <see cref="ComputeNonGuideNucleusTargetAlongGuideOpHead"/>
    /// (<c>guidePos + u × bondLength</c>). Prebond hybrid <see cref="NonGuideSigmaApproachDirectionWorld"/> uses
    /// <c>guideOp.position − nonGuide.position</c> (orbital group − redistributing nucleus), not the negated guide nucleus→lobe axis.
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

    /// <summary>σ orbital-drag prebond: log guide/non-guide head angles vs internuclear ray. Default off; set true for triage.</summary>
    public static bool DebugLogSigmaPrebondHeadAngles = false;

    /// <summary>σ phase 1: append NDJSON triage for non–op shell / fragment rotation (overlap path, σ-axis rows, dir sanity). Default on; set false for quiet runs.</summary>
    public static bool DebugLogSigmaPhase1NonOpRotationNdjson = true;

    /// <summary>σ phase 2→3: NDJSON per nucleus/substituent σ orbital tip before vs after non-guide RefreshSigmaBond (line snap → phase 3). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogPhase23NucleusOrbitalTeleportNdjson = true;

    /// <summary>Cursor debug session 8de5d1: NDJSON to <c>.cursor/debug-8de5d1.log</c> — non-bond / group tip dirs + pairwise angles at σ phase boundaries. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLog8de5d1NonBondRedistDirections = true;

    /// <summary>H54: σ refresh — log when <see cref="CovalentBond.ApplySigmaOrbitalTipFromRedistribution"/> early-exits because +X already matches geometric bond axis (vs lone TryMatch snap). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogSigmaBondRefreshGeomAlignedEarlyExitNdjson = true;

    /// <summary>H55: orbital-drag σ — NDJSON probe <c>refLocal</c> / <c>vseprTemplateFirst</c> / <c>newDirs[0]</c> vs pure internuclear and negated (invert hunt). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogRefLocalGuideInvertProbeNdjson = true;

    /// <summary>H49/H50: NDJSON in ApplySigmaOrbitalTip and SyncSigmaBondOrbitalTipsFromLocks (auth vs caller internuclear ray, lock vs axis). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogApplySigmaTipInternuclearNdjson = true;

    /// <summary>H51/H52: lone TryMatch snap vs tip before/after AlignNucleusChildOrbitalLocalRotationToHybridTipFromLocalPosition in RefreshSigmaBond. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogLoneTeleportTryMatchNdjson = true;

    /// <summary>H53: NDJSON at end of TryMatchLoneOrbitalsToFreeIdealDirections — permutation index per lone vs free ideal. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogTryMatchPermNdjson = true;

    /// <summary>H60: TryMatch lone — pivot→orbital transform position vs hybrid +X vs assigned template <c>td</c> (nucleus local). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogRedistributionOrbGroupRayNdjson = true;

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
    /// World unit direction fallback when guide-lobe vs guide-nucleus ray is degenerate: partner σ <b>position</b> minus
    /// non-guide nucleus (<c>guideOp.position − nonGuide.position</c>), then internuclear toward guide. Primary prebond
    /// ref in <see cref="AtomFunction.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/> is
    /// <c>guideOp.position − guideNucleus.position</c> mapped into pivot local.
    /// </summary>
    public static Vector3 NonGuideSigmaApproachDirectionWorld(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp)
    {
        if (nonGuide == null)
            return Vector3.right;

        if (guideOp != null)
        {
            Vector3 w = guideOp.transform.position - nonGuide.transform.position;
            if (w.sqrMagnitude > 1e-10f)
                return w.normalized;
        }

        if (guide != null)
        {
            Vector3 towardGuide = guide.transform.position - nonGuide.transform.position;
            if (towardGuide.sqrMagnitude > 1e-10f)
                return towardGuide.normalized;
        }

        return Vector3.right;
    }

    /// <summary>
    /// World unit direction from non-guide nucleus toward guide: <c>normalize(guideNucleus − nonGuideNucleus)</c>.
    /// Used only to snap the <b>non-guide</b> forming σ after prebond refresh; the guide orbital group stays at drop pose.
    /// </summary>
    public static bool TryGetOrbitalDragSigmaSharedPlusXWorldFromGuideToNonGuide(
        AtomFunction guide,
        AtomFunction nonGuide,
        out Vector3 plusXWorldUnit)
    {
        plusXWorldUnit = Vector3.right;
        if (guide == null || nonGuide == null) return false;
        Vector3 gMinusN = guide.transform.position - nonGuide.transform.position;
        if (gMinusN.sqrMagnitude < 1e-16f) return false;
        plusXWorldUnit = gMinusN.normalized;
        return true;
    }

    /// <summary>
    /// Orbital-drag σ phase 1: after non-guide <see cref="AtomFunction.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute"/>,
    /// TryMatch/pin can leave the forming σ +X anti-parallel to <see cref="TryGetOrbitalDragSigmaSharedPlusXWorldFromGuideToNonGuide"/>.
    /// Snap <b>only</b> the non-guide forming op; do not rotate the guide (phase 1 policy).
    /// </summary>
    public static bool SnapNonGuideFormingSigmaHybridPlusXForOrbitalDragPrebondSharedAxis(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction nonGuideFormingOp)
    {
        if (guide == null || nonGuide == null || nonGuideFormingOp == null) return false;
        if (!TryGetOrbitalDragSigmaSharedPlusXWorldFromGuideToNonGuide(guide, nonGuide, out var desiredPlusX))
            return false;
        Vector3 plusX = nonGuideFormingOp.transform.rotation * Vector3.right;
        if (plusX.sqrMagnitude < 1e-16f) return false;
        plusX.Normalize();
        float dot = Vector3.Dot(plusX, desiredPlusX);
        if (dot > 1f - 1e-4f) return false;
        Vector3 desiredNucLocal = nonGuide.transform.InverseTransformDirection(desiredPlusX);
        if (desiredNucLocal.sqrMagnitude < 1e-16f) return false;
        desiredNucLocal.Normalize();
        float rMag = nonGuideFormingOp.transform.localPosition.sqrMagnitude > 1e-16f
            ? nonGuideFormingOp.transform.localPosition.magnitude
            : ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                desiredNucLocal, nonGuide.BondRadius).position.magnitude;
        var spec = NucleusLobeSpec.ForTryMatchSnap(desiredNucLocal, nonGuide.BondRadius, nonGuideFormingOp.transform.localRotation);
        spec.Radius = rMag;
        NucleusLobePose.ApplyToNucleusChild(nonGuide, nonGuideFormingOp, spec);
        return true;
    }

    /// <summary>
    /// Orbital-drag σ phase 1 (prebond, no <see cref="CovalentBond"/> yet): rigid world rotation of the non-guide
    /// σ prebond unified shell about the non-guide nucleus so the forming lobe axis
    /// (<see cref="SigmaLobeUnitDirectionFromAtom"/>(non-guide op, non-guide)) anti-aligns with the guide lobe axis
    /// (negated guide head). No-op if already within ~0.02° or directions degenerate. Skips hydrogen non-guide.
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
        if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide)) return;
        if (nonGuide.AtomicNumber <= 1) return;

        ElectronOrbitalFunction guideOp = guide == atomA ? orbA : orbB;
        ElectronOrbitalFunction nonGuideOp = nonGuide == atomA ? orbA : orbB;
        if (guideOp == null || nonGuideOp == null) return;

        if (DryRunLogOnly) return;

        Vector3 guideHead = SigmaLobeUnitDirectionFromAtom(guideOp, guide);
        if (guideHead.sqrMagnitude < 1e-12f) return;
        guideHead.Normalize();

        Vector3 desiredNonGuideHead = -guideHead;
        Vector3 currentNonGuideHead = SigmaLobeUnitDirectionFromAtom(nonGuideOp, nonGuide);
        if (currentNonGuideHead.sqrMagnitude < 1e-12f) return;
        currentNonGuideHead.Normalize();

        const float tolDot = 1f - 1e-4f;
        if (Vector3.Dot(currentNonGuideHead, desiredNonGuideHead) >= tolDot)
            return;

        Quaternion worldDelta = Quaternion.FromToRotation(currentNonGuideHead, desiredNonGuideHead);
        nonGuide.ApplyRigidWorldRotationToNucleusParentedOrbitals(worldDelta, nonGuideOp);

        if (DebugLogSigmaPrebondHeadAngles)
        {
            Vector3 after = SigmaLobeUnitDirectionFromAtom(nonGuideOp, nonGuide);
            float angAfter = after.sqrMagnitude > 1e-12f
                ? Vector3.Angle(after.normalized, desiredNonGuideHead)
                : -1f;
            Debug.Log(
                "[sigma-prebond-head] RunSigmaFormation12PrebondNonGuideHybridOnly guideId=" + guide.GetInstanceID()
                + " nonGuideId=" + nonGuide.GetInstanceID()
                + " angNonGuideHeadVsDesiredAfterDeg=" + angAfter.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
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
        RedistributionTetraCompareDebugLog.LogSigma12HybridPass(nonGuide, guide, bond);
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
