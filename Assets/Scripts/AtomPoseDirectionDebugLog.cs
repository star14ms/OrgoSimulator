using UnityEngine;

/// <summary>Former triage hooks for atom/orbital pose. Stubs: all entry points are no-ops.</summary>
public static class AtomPoseDirectionDebugLog
{
    public static bool LogCarbonPosRotDirectionProbe;
    public static bool LogBondBreakReleasedOrbitalPoseProbe;

    public static void LogCarbonSpawn(AtomFunction atom, string source) { }

    public static void LogCarbonCarbonSigmaBeforeBond(AtomFunction atomA, AtomFunction atomB, string source) { }

    public static void LogBondBreakOrbitalPose(
        string phase,
        string hypothesisId,
        int bondInstanceId,
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbReturnedSide,
        ElectronOrbitalFunction orbOtherSide,
        Vector3? capturedWorldPos,
        Quaternion? capturedWorldRot,
        bool userDragCylinderBreak,
        bool skipRedistributionCoro)
    { }

    public static void LogBondBreakOtherSideSlotPick(
        string phase,
        int bondInstanceId,
        AtomFunction returnOrbitalTo,
        AtomFunction otherAtom,
        ElectronOrbitalFunction returnedOrbital,
        ElectronOrbitalFunction otherSideOrbital,
        Vector3 dirToReturnWorld,
        Vector3 slotLocalPos,
        Quaternion slotLocalRot)
    { }

    public static void LogBondBreakOtherSideOverwriteVsChosenSlot(
        string phase,
        int bondInstanceId,
        AtomFunction otherAtom,
        AtomFunction returnOrbitalTo,
        ElectronOrbitalFunction otherSideOrbital,
        Quaternion chosenSlotLocalRot)
    { }

    public static void LogBondBreakCaptureTargetState(
        string phase,
        int bondInstanceId,
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction bondOrbital,
        Vector3 targetWorldPos,
        Quaternion targetWorldRot,
        bool orbitalRotationFlipped,
        Quaternion orbitalRedistributionWorldDelta)
    { }

    public static void LogBondBreakTargetVsChosenSlot(
        string phase,
        int bondInstanceId,
        AtomFunction otherAtom,
        AtomFunction returnOrbitalTo,
        Quaternion targetWorldRot,
        Quaternion chosenSlotLocalRot,
        bool orbitalRotationFlipped)
    { }
}
