using System.Collections;
using UnityEngine;

/// <summary>
/// Stepped bond-formation debug: pause at three template phases when <see cref="SteppedModeEnabled"/> is true.
/// UI toggle sets <see cref="SteppedModeEnabled"/>; <see cref="RequestAdvance"/> continues to the next phase.
/// Turning stepped mode off while waiting calls <see cref="OnSteppedModeDisabled"/> so bonding continues.
/// </summary>
public static class BondFormationDebugController
{
    /// <summary>Bound from HUD toggle. When false during <see cref="WaitPhase"/>, wait ends immediately.</summary>
    public static bool SteppedModeEnabled { get; set; }

    /// <summary>
    /// When non-zero, <see cref="OrbitalRedistribution"/> emits extra <c>H31-focus-orbital-*</c> NDJSON lines for this
    /// <see cref="UnityEngine.Object.GetInstanceID"/> (e.g. match “Selected Orbital” in stepped debug description). Set to 0 to disable.
    /// </summary>
    public static int FocusOrbitalInstanceId { get; set; }

    static bool _pendingAdvance;
    static bool _waiting;

    public static bool IsWaitingForPhase => _waiting;

    public static void RequestAdvance() => _pendingAdvance = true;

    /// <summary>Call when the user turns the debug toggle off while a phase wait is active.</summary>
    public static void OnSteppedModeDisabled()
    {
        FocusOrbitalInstanceId = 0;
        if (_waiting)
            _pendingAdvance = true;
    }

    /// <param name="phase">1 = template created, 2 = joint / vertex-0 alignment resolved, 3 = after further rotation (pre-apply).</param>
    /// <param name="phaseLabelOverride">When non-null, shown on the Next row instead of the default 1/3–3/3 labels.</param>
    public static IEnumerator WaitPhase(int phase, string phaseLabelOverride = null)
    {
        if (!SteppedModeEnabled) yield break;
        _waiting = true;
        _pendingAdvance = false;
        BondFormationDebugHud.Instance?.SetPhaseWaiting(phase, true, phaseLabelOverride);
        while (SteppedModeEnabled && !_pendingAdvance)
            yield return null;
        _waiting = false;
        _pendingAdvance = false;
        BondFormationDebugHud.Instance?.SetPhaseWaiting(phase, false, phaseLabelOverride);
    }
}
