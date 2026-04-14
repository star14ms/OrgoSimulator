using UnityEngine;

/// <summary>Former NDJSON compare hooks for redistribution. Stubs: all entry points are no-ops.</summary>
public static class RedistributionTetraCompareDebugLog
{
    public static bool LogNbTetraCompareFingerprint;

    public static void LogGuideResolve(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction draggedOrb,
        AtomFunction guide,
        AtomFunction nonGuide,
        float massA,
        float massB,
        string resolveBranch,
        AtomFunction draggedParentAtom)
    { }

    public static void LogSigma12HybridPass(
        AtomFunction nonGuide,
        AtomFunction guide,
        CovalentBond bond)
    { }

    public static void LogRefreshSigmaHybridEntry(
        AtomFunction pivot,
        AtomFunction partnerAlongNewSigmaBond,
        CovalentBond redistributionOperationBondForPredictive,
        bool orbitalDragSigmaPhase3RegularGuide,
        bool sigmaFormationPrebond,
        ElectronOrbitalFunction sigmaFormationPrebondZeroEOperationOrb,
        ElectronOrbitalFunction sigmaFormationPrebondGuideOperationOrb)
    { }

    public static void LogNgFormingPlusXVsInternuc(
        string boundary,
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction ngFormingOp,
        float? dInternucAtCoroutineStart = null,
        ElectronOrbitalFunction guideFormingOp = null)
    { }
}
