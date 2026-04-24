using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// σ / π formation from <b>orbital drag</b> or the same pipeline <b>without animation</b> (<see cref="TryBeginOrbitalDragSigmaFormation"/> /
/// <see cref="TryBeginOrbitalDragPiFormation"/> with <c>animate: false</c>): σ runs phase 1 pre-bond (non-guide fragment or cyclic targets),
/// then bond formation (cylinder + orbital→line), then post-bond guide hybrid lerp. π phase 1 (acyclic) redistributes the
/// non-guide atom first, with an optional second track on the guide when mutual-σ guide groups match; phase 3 post-bond
/// redistribution pivots on the guide when that second track did not run (same as <see cref="ElectronOrbitalFunction.FormCovalentBondPiCoroutine"/>).
/// Independent of edit mode; add this component to the scene (e.g. next to <see cref="EditModeManager"/>).
/// Timings are read from the gesture <see cref="ElectronOrbitalFunction"/> (the dragged lobe when available).
/// </summary>
[DefaultExecutionOrder(100)]
public class SigmaBondFormation : MonoBehaviour
{
    static GameObject phase1DebugTemplatePreviewRoot;
    static GameObject piPhase1DebugTemplatePreviewRoot;

    [SerializeField] EditModeManager editModeManager;
    [SerializeField] bool debugDisableCyclicSigmaPhase1Redistribution = false;
    [SerializeField] bool debugShowCyclicPiPhase1RedistributeTemplates = true;

    /// <summary>Logs π phase-1 parallel track timeline (dur vs instant snap). Default on for triage; set false for quiet runs.</summary>

    void Awake()
    {
        EnsureEditModeManagerReference();
    }

    /// <summary>Resolves <see cref="editModeManager"/> when unset (e.g. runner spawned at runtime). Required for π orbital-drag three-phase.</summary>
    public void EnsureEditModeManagerReference()
    {
        if (editModeManager == null)
            editModeManager = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
    }

    /// <summary>Timing for π post-bond guide redistribution (same source as orbital-drag σ phase 3).</summary>
    public static float ResolvePiPhase3GuideSeconds(
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        var timingOrb = TimingSourceOrbital(sourceOrbital, targetOrbital, redistributionGuideTieBreakDraggedOrbital);
        return timingOrb != null ? timingOrb.SigmaFormationPhase3PostbondGuideSeconds : 1f;
    }

    /// <summary>
    /// Ensures a scene runner exists for orbital-drag σ (pre-bond approach + bond + post-bond guide). If none, adds this component to the
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

    static ElectronOrbitalFunction TimingSourceOrbital(
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        if (redistributionGuideTieBreakDraggedOrbital != null)
            return redistributionGuideTieBreakDraggedOrbital;
        return orbA != null ? orbA : orbB;
    }

    static CovalentBond TryFindSigmaBondBetween(AtomFunction a, AtomFunction b)
    {
        if (a == null || b == null) return null;
        foreach (var cb in a.CovalentBonds)
        {
            if (cb == null || !cb.IsSigmaBondLine()) continue;
            var other = cb.AtomA == a ? cb.AtomB : cb.AtomA;
            if (other == b) return cb;
        }
        return null;
    }

    static ElectronOrbitalFunction TryGetSigmaLineOrbitalBetween(AtomFunction a, AtomFunction b, ElectronOrbitalFunction fallback)
    {
        var cb = TryFindSigmaBondBetween(a, b);
        if (cb != null && cb.Orbital != null)
            return cb.Orbital;
        return fallback;
    }

    /// <summary>
    /// One parallel lane of phase 1: receives the same smoothstep <c>s</c> in <c>[0,1]</c> each frame as all other lanes.
    /// </summary>
    sealed class Phase1ParallelTrack
    {
        /// <summary>In-progress pose from fractional progress; <paramref name="smoothS"/> is smoothstep of linear <c>u</c>.</summary>
        public Action<float> ApplySmoothStep;

        /// <summary>Called once after the shared timeline completes (e.g. residual snap).</summary>
        public Action FinalizeAfterTimeline;
    }

    /// <summary>True when the optional second π phase‑1 parallel track ran (mutual σ); phase 3 is skipped in that case.</summary>
    sealed class PiPhase1GuideRedistributionDecision
    {
        public bool GuideRedistributedInPhase1;
    }

    sealed class CyclicPhase1Context
    {
        public List<AtomFunction> PathAtoms;
        public Dictionary<AtomFunction, Vector3> TargetWorldByAtom;
        public Vector3 CycleCenterWorld;
        public HashSet<AtomFunction> ChainRedistributionBlockAtoms;
        public AtomFunction NonGuideCycleNeighbor;
    }

    /// <summary>
    /// Orbital-drag σ <b>phase 1</b> (pre-bond): builds parallel tracks (fragment translation + orbital redistribution) and runs them on one timeline.
    /// </summary>
    /// <param name="guide">Guide atom from <see cref="ElectronRedistributionGuide.ResolveGuideAtomForPair"/>.</param>
    /// <param name="nonGuide">Non-guide (approaching) atom.</param>
    /// <param name="guideOp">Guide atom’s σ operation orbital.</param>
    /// <param name="nonGuideOp">Non-guide atom’s σ operation orbital.</param>
    IEnumerator CoOrbitalDragSigmaPhase1Prebond(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        float phase1Sec,
        bool allowSteppedDebug)
    {
        if (guide == null || nonGuide == null)
            yield break;

        float bl = DefaultSigmaBondLengthForPair(guide, nonGuide);
        CyclicPhase1Context cyclicContext = TryBuildCyclicPhase1Context(guide, nonGuide, bl);
        bool disablePhase1Redistribution = debugDisableCyclicSigmaPhase1Redistribution && cyclicContext != null;
        Vector3 nTarget = Phase1NonGuideNucleusTargetOnGuideOpOutboundRay(guide, guideOp, bl);
        Vector3 deltaTotal = nTarget - nonGuide.transform.position;
        if (cyclicContext == null && deltaTotal.sqrMagnitude < 1e-16f)
            yield break;

        var toMove = cyclicContext != null
            ? new List<AtomFunction>(cyclicContext.TargetWorldByAtom.Keys)
            : BuildNonGuideFragmentAtomsForApproach(guide, nonGuide);
        if (cyclicContext != null && toMove.Count == 0)
            yield break;
        var initialWorld = new Dictionary<AtomFunction, Vector3>(toMove.Count);
        foreach (var a in toMove)
        {
            if (a == null) continue;
            initialWorld[a] = a.transform.position;
        }

        var mol = nonGuide.GetConnectedMolecule();
        float dur = Mathf.Max(0f, phase1Sec);

        IReadOnlyList<Phase1ParallelTrack> tracks = BuildPhase1ParallelAnimationList(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            initialWorld,
            toMove,
            deltaTotal,
            nTarget,
            mol,
            cyclicContext,
            disablePhase1Redistribution);

        if (allowSteppedDebug && BondFormationDebugController.SteppedModeEnabled)
        {
            BuildPhase1RedistributeTemplatePreviewVisuals(
                guide,
                nonGuide,
                initialWorld,
                deltaTotal,
                cyclicContext,
                disablePhase1Redistribution);
            yield return BondFormationDebugController.WaitPhase(1);
            ClearPhase1RedistributeTemplatePreviewVisuals();
        }

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
            yield return StartCoroutine(
                CoExecutePhase1ParallelAnimations(tracks, mol, dur));
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
    }

    /// <summary>σ phase 1 with no coroutine yields (for <see cref="RunOrbitalDragSigmaFormationThreePhaseImmediate"/>).</summary>
    static void RunOrbitalDragSigmaPhase1PrebondSynchronously(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        float phase1Sec,
        bool disableCyclicSigmaPhase1RedistributionDebug)
    {
        if (guide == null || nonGuide == null)
            return;

        float bl = DefaultSigmaBondLengthForPair(guide, nonGuide);
        CyclicPhase1Context cyclicContext = TryBuildCyclicPhase1Context(guide, nonGuide, bl);
        bool disablePhase1Redistribution = disableCyclicSigmaPhase1RedistributionDebug && cyclicContext != null;
        Vector3 nTarget = Phase1NonGuideNucleusTargetOnGuideOpOutboundRay(guide, guideOp, bl);
        Vector3 deltaTotal = nTarget - nonGuide.transform.position;
        if (cyclicContext == null && deltaTotal.sqrMagnitude < 1e-16f)
            return;

        var toMove = cyclicContext != null
            ? new List<AtomFunction>(cyclicContext.TargetWorldByAtom.Keys)
            : BuildNonGuideFragmentAtomsForApproach(guide, nonGuide);
        if (cyclicContext != null && toMove.Count == 0)
            return;
        var initialWorld = new Dictionary<AtomFunction, Vector3>(toMove.Count);
        foreach (var a in toMove)
        {
            if (a == null) continue;
            initialWorld[a] = a.transform.position;
        }

        var mol = nonGuide.GetConnectedMolecule();
        float dur = Mathf.Max(0f, phase1Sec);

        IReadOnlyList<Phase1ParallelTrack> tracks = BuildPhase1ParallelAnimationList(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            initialWorld,
            toMove,
            deltaTotal,
            nTarget,
            mol,
            cyclicContext,
            disablePhase1Redistribution);

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
            if (dur >= 1e-5f)
                throw new InvalidOperationException(
                    "RunOrbitalDragSigmaPhase1PrebondSynchronously expects zero-duration phase 1 (immediate bonding).");
            ExecutePhase1ParallelAnimationsImmediate(tracks, mol, dur);
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
    }

    static void DrainCoroutineToCompletion(IEnumerator routine)
    {
        if (routine == null)
            return;
        while (routine.MoveNext())
        {
        }
    }

    /// <summary>π row only: after phase-2 cylinder completes (called from <see cref="ElectronOrbitalFunction.AnimateBondFormationOperationOrbitalsTowardBondCylinder"/> end), hide the source OP shell and suppress the bond's shared orbital mesh so π line/cylinders stay visible (<see cref="CovalentBond.SetSuppressSharedOrbitalVisualForPiHandoff"/>).</summary>
    internal static void HidePiOperationOrbitalsAfterPhase2Cylinder(CovalentBond bond, ElectronOrbitalFunction sourceOrbital)
    {
        if (bond != null && bond.GetBondIndex() > 0)
            bond.SetSuppressSharedOrbitalVisualForPiHandoff(true);
        if (sourceOrbital != null)
            sourceOrbital.SetVisualsEnabled(false);
    }

    /// <summary>Assembles phase-1 parallel lanes: atom fragment approach + non-guide orbital redistribution.</summary>
    static List<Phase1ParallelTrack> BuildPhase1ParallelAnimationList(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        Dictionary<AtomFunction, Vector3> initialWorld,
        List<AtomFunction> toMove,
        Vector3 deltaTotal,
        Vector3 nTarget,
        ICollection<AtomFunction> molForBondLines,
        CyclicPhase1Context cyclicContext,
        bool disablePhase1Redistribution)
    {
        var list = new List<Phase1ParallelTrack>(disablePhase1Redistribution ? 1 : 2);
        list.Add(BuildPhase1AtomFragmentApproachAnimation(
            nonGuide, initialWorld, toMove, deltaTotal, nTarget, molForBondLines, cyclicContext));
        if (!disablePhase1Redistribution)
        {
            list.Add(BuildPhase1OrbitalRedistributeForSigmaFormationPhase1(
                guide, nonGuide, guideOp, nonGuideOp, cyclicContext));
        }
        return list;
    }

    /// <summary>
    /// Non-guide fragment rigid translation toward <paramref name="nTarget"/> (see <see cref="ApplyPhase1ApproachFragmentOffset"/>).
    /// </summary>
    static Phase1ParallelTrack BuildPhase1AtomFragmentApproachAnimation(
        AtomFunction nonGuide,
        Dictionary<AtomFunction, Vector3> initialWorld,
        List<AtomFunction> toMove,
        Vector3 deltaTotal,
        Vector3 nTarget,
        ICollection<AtomFunction> molForBondLines,
        CyclicPhase1Context cyclicContext)
    {
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s =>
            {
                if (cyclicContext != null)
                    ApplyPhase1CyclicAtomTargets(initialWorld, cyclicContext.TargetWorldByAtom, s);
                else
                    ApplyPhase1ApproachFragmentOffset(initialWorld, toMove, deltaTotal, s);
            },
            FinalizeAfterTimeline = () =>
            {
                if (nonGuide == null) return;
                if (cyclicContext != null)
                {
                    foreach (var kv in cyclicContext.TargetWorldByAtom)
                    {
                        if (kv.Key == null) continue;
                        kv.Key.transform.position = kv.Value;
                    }
                }
                else
                {
                    Vector3 residual = nTarget - nonGuide.transform.position;
                    if (residual.sqrMagnitude > 1e-14f)
                    {
                        foreach (var a in toMove)
                        {
                            if (a == null) continue;
                            a.transform.position += residual;
                        }
                    }
                }
                if (molForBondLines != null && molForBondLines.Count > 0)
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
            }
        };
    }

    /// <summary>Builds phase-1 orbital redistribution track on the moving non-guide atom.</summary>
    static Phase1ParallelTrack BuildPhase1OrbitalRedistributeForSigmaFormationPhase1(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        CyclicPhase1Context cyclicContext)
    {
        Vector3 finalDirectionForGuideOrbital = Vector3.zero;

        OrbitalRedistribution.CyclicRedistributionContext redistCycleContext = null;
        if (cyclicContext != null && guideAtom != null && nonGuideAtom != null)
        {
            var finalWorldByAtom = new Dictionary<AtomFunction, Vector3>();
            for (int i = 0; i < cyclicContext.PathAtoms.Count; i++)
            {
                AtomFunction a = cyclicContext.PathAtoms[i];
                if (a == null) continue;
                if (cyclicContext.TargetWorldByAtom.TryGetValue(a, out Vector3 target))
                    finalWorldByAtom[a] = target;
                else
                    finalWorldByAtom[a] = a.transform.position;
            }

            redistCycleContext = new OrbitalRedistribution.CyclicRedistributionContext
            {
                PivotAtom = nonGuideAtom,
                CycleNeighborA = guideAtom,
                CycleNeighborB = cyclicContext.NonGuideCycleNeighbor,
                FinalWorldByAtom = finalWorldByAtom,
                CycleCenterWorld = cyclicContext.CycleCenterWorld,
                ChainRedistributionBlockedAtoms = cyclicContext.ChainRedistributionBlockAtoms,
                PivotSigmaOp = nonGuideOp,
                GuideSigmaOp = guideOp
            };

            // Match preview / cyclic template: internuclear axis is pivot final → guide final in world.
            // The guide-OP inbound ray (-(guideOp - guideNucleus)) can align with the wrong cycle edge
            // and mis-trigger OP preassign steal.
            Vector3 pivotFinal = finalWorldByAtom.TryGetValue(nonGuideAtom, out Vector3 pFin) ? pFin : nonGuideAtom.transform.position;
            Vector3 guideFinal = finalWorldByAtom.TryGetValue(guideAtom, out Vector3 gFin) ? gFin : guideAtom.transform.position;
            Vector3 guideLegWorld = guideFinal - pivotFinal;
            if (guideLegWorld.sqrMagnitude > 1e-12f)
            {
                finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(guideLegWorld.normalized).normalized;
            }
        }
        else if (guideAtom != null && guideOp != null && nonGuideAtom != null)
        {
            Vector3 guideOpFromGuide = guideOp.transform.position - guideAtom.transform.position;
            Vector3 invGuideOpWorld = (-guideOpFromGuide).normalized;
            finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(invGuideOpWorld).normalized;
        }

        var animation = OrbitalRedistribution.BuildOrbitalRedistribution(
            nonGuideAtom,
            guideAtom,
            guideOp,
            nonGuideOp,
            guideOrbitalPredetermined: null,
            finalDirectionForGuideOrbital,
            isBondingEvent: true,
            cyclicContext: redistCycleContext);
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => animation?.Apply(s),
            FinalizeAfterTimeline = () => { }
        };
    }

    /// <summary>
    /// π phase 1 primary track: acyclic π redistributes the <b>non-guide</b> atom first; cyclic π keeps the prior
    /// <see cref="OrbitalRedistribution.CyclicRedistributionContext"/> on the <b>non-guide</b> pivot. Optional mutual-σ track
    /// complements on the <b>guide</b> pivot when acyclic (<see cref="BuildPhase1OrbitalRedistributeForPiFormationGuideAtomPhase1"/>).
    /// </summary>
    static Phase1ParallelTrack BuildPhase1OrbitalRedistributeForPiFormationPhase1(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        OrbitalRedistribution.CyclicRedistributionContext cyclicContext,
        Vector3 finalDirectionForGuideOrbital,
        out OrbitalRedistribution.RedistributionAnimation piOpAnimForPostCylinder,
        Vector3? sharedPiNormalPlusZWorld = null)
    {
        piOpAnimForPostCylinder = null;
        OrbitalRedistribution.RedistributionAnimation piOpAnim = null;
        OrbitalRedistribution.RedistributionAnimation animation;
        if (cyclicContext != null)
        {
            if (sharedPiNormalPlusZWorld.HasValue
                && nonGuideOp != null
                && sharedPiNormalPlusZWorld.Value.sqrMagnitude > 1e-16f)
            {
                TryBuildPiPrebondOpPhase1TrigonalInPlaneRedistributionAnimation(
                    nonGuideAtom,
                    nonGuideOp,
                    sharedPiNormalPlusZWorld.Value,
                    out piOpAnim);
            }

            animation = OrbitalRedistribution.BuildOrbitalRedistribution(
                nonGuideAtom,
                guideAtom,
                guideOp,
                nonGuideOp,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: finalDirectionForGuideOrbital,
                isBondingEvent: true,
                cyclicContext: cyclicContext);
        }
        else
        {
            if (sharedPiNormalPlusZWorld.HasValue
                && nonGuideOp != null
                && sharedPiNormalPlusZWorld.Value.sqrMagnitude > 1e-16f)
            {
                TryBuildPiPrebondOpPhase1TrigonalInPlaneRedistributionAnimation(
                    nonGuideAtom,
                    nonGuideOp,
                    sharedPiNormalPlusZWorld.Value,
                    out piOpAnim);
            }

            animation = OrbitalRedistribution.BuildOrbitalRedistribution(
                nonGuideAtom,
                guideAtom,
                guideOp,
                nonGuideOp,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: Vector3.zero,
                isBondingEvent: true,
                cyclicContext: null);
        }

        piOpAnimForPostCylinder = piOpAnim;
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s =>
            {
                animation?.Apply(s);
                piOpAnim?.Apply(s);
            },
            FinalizeAfterTimeline = () => { }
        };
    }

    /// <summary>
    /// π phase 1 optional second track when <see cref="OrbitalRedistribution.BothPiPairGuideGroupsAreMutualInterAtomSigma"/>:
    /// complements the primary track on the <b>guide</b> pivot (primary is always non-guide for π phase 1).
    /// </summary>
    static Phase1ParallelTrack BuildPhase1OrbitalRedistributeForPiFormationGuideAtomPhase1(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        OrbitalRedistribution.CyclicRedistributionContext piPhase1CyclicContext,
        Vector3 finalDirectionForGuideOrbital,
        out OrbitalRedistribution.RedistributionAnimation piOpAnimForPostCylinder,
        Vector3? sharedPiNormalPlusZWorld = null)
    {
        piOpAnimForPostCylinder = null;
        OrbitalRedistribution.RedistributionAnimation piOpAnim = null;
        OrbitalRedistribution.RedistributionAnimation animation;
        if (piPhase1CyclicContext != null)
        {
            if (sharedPiNormalPlusZWorld.HasValue
                && guideOp != null
                && sharedPiNormalPlusZWorld.Value.sqrMagnitude > 1e-16f)
            {
                TryBuildPiPrebondOpPhase1TrigonalInPlaneRedistributionAnimation(
                    guideAtom,
                    guideOp,
                    sharedPiNormalPlusZWorld.Value,
                    out piOpAnim);
            }

            animation = OrbitalRedistribution.BuildOrbitalRedistribution(
                guideAtom,
                nonGuideAtom,
                nonGuideOp,
                guideOp,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: Vector3.zero,
                isBondingEvent: true,
                cyclicContext: null);
        }
        else
        {
            if (sharedPiNormalPlusZWorld.HasValue
                && guideOp != null
                && sharedPiNormalPlusZWorld.Value.sqrMagnitude > 1e-16f)
            {
                TryBuildPiPrebondOpPhase1TrigonalInPlaneRedistributionAnimation(
                    guideAtom,
                    guideOp,
                    sharedPiNormalPlusZWorld.Value,
                    out piOpAnim);
            }

            animation = OrbitalRedistribution.BuildOrbitalRedistribution(
                guideAtom,
                nonGuideAtom,
                nonGuideOp,
                guideOp,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: finalDirectionForGuideOrbital,
                isBondingEvent: true,
                cyclicContext: null);
        }

        piOpAnimForPostCylinder = piOpAnim;
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s =>
            {
                animation?.Apply(s);
                piOpAnim?.Apply(s);
            },
            FinalizeAfterTimeline = () => { }
        };
    }

    /// <summary>
    /// π prebond phase-1: shared world +z direction of the forming p orbital (trigonal plane ⟂ this), written to
    /// both atoms’ <see cref="AtomFunction.PiPOrbitalDirectionSlots"/> via <see cref="AtomFunction.SetPiPrebondPOrbitalDirectionsWorld"/>.
    /// Uses a committed π row when present; else <c>Cross(σ, g)</c> from the guide’s guide-group, then the <b>non-guide</b>’s
    /// guide-group when the guide’s <c>g ∥ σ</c>; then OP in-plane rays; then any axis ⟂ σ so a slot is almost always defined.
    /// </summary>
    static bool TryComputeSharedPiPrebondPOrbitalPlusZWorld(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        out Vector3 sharedPiNormalPlusZWorld)
    {
        sharedPiNormalPlusZWorld = Vector3.zero;
        if (guideAtom == null || nonGuideAtom == null)
            return false;

        Vector3 sigmaAxisWorld = (guideAtom.transform.position - nonGuideAtom.transform.position).normalized;
        if (sigmaAxisWorld.sqrMagnitude < 1e-12f)
            return false;

        Vector3 piNormal = Vector3.zero;
        bool resolvedPiGuideGroup = OrbitalRedistribution.TryGetGuideGroupOrbitalForPiPrebondReference(
            guideAtom,
            nonGuideAtom,
            guideOp,
            out ElectronOrbitalFunction piGuideGroupOrbital);
        if (nonGuideAtom.TryGetLatestPiPOrbitalPlusZWorldTowardPartner(guideAtom, out var prevPlusZ, out _)
            || guideAtom.TryGetLatestPiPOrbitalPlusZWorldTowardPartner(nonGuideAtom, out prevPlusZ, out _))
        {
            Vector3 p1InPlane = Vector3.ProjectOnPlane(prevPlusZ, sigmaAxisWorld);
            if (p1InPlane.sqrMagnitude > 1e-10f)
            {
                p1InPlane.Normalize();
                piNormal = Vector3.Cross(sigmaAxisWorld, p1InPlane);
                if (piNormal.sqrMagnitude > 1e-20f)
                    piNormal.Normalize();
            }
        }
        if (piNormal.sqrMagnitude < 1e-16f)
        {
            Vector3 uInPlanePerpSigma = Vector3.zero;
            bool anchoredGuideGroupCross = false;

            // First π: +z is ⊥ σ and ⊥ guide-group lobe direction g: piNormal = normalize(Cross(σ, g)).
            // When g ∥ σ, Cross is degenerate — fall back to OP in-plane rays, then axis helpers.
            if (resolvedPiGuideGroup && piGuideGroupOrbital != null)
            {
                Vector3 gdir = OrbitalAngleUtility.GetOrbitalDirectionWorld(piGuideGroupOrbital.transform);
                if (gdir.sqrMagnitude < 1e-16f)
                    gdir = piGuideGroupOrbital.transform.position - guideAtom.transform.position;
                if (gdir.sqrMagnitude > 1e-16f)
                {
                    gdir.Normalize();
                    Vector3 crossSg = Vector3.Cross(sigmaAxisWorld, gdir);
                    const float crossEpsSq = 1e-22f;
                    if (crossSg.sqrMagnitude > crossEpsSq)
                    {
                        piNormal = crossSg.normalized;
                        anchoredGuideGroupCross = true;
                    }
                }
            }

            // When the guide-side group is ∥ σ, the non-guide’s guide group (e.g. C–O) may still give a usable Cross(σ, g).
            if (!anchoredGuideGroupCross
                && OrbitalRedistribution.TryGetGuideGroupOrbitalForPiPrebondReference(
                    nonGuideAtom, guideAtom, nonGuideOp, out ElectronOrbitalFunction ngRefOrb)
                && ngRefOrb != null)
            {
                Vector3 gdirN = OrbitalAngleUtility.GetOrbitalDirectionWorld(ngRefOrb.transform);
                if (gdirN.sqrMagnitude < 1e-16f)
                    gdirN = ngRefOrb.transform.position - nonGuideAtom.transform.position;
                if (gdirN.sqrMagnitude > 1e-16f)
                {
                    gdirN.Normalize();
                    Vector3 crossNg = Vector3.Cross(sigmaAxisWorld, gdirN);
                    const float crossEpsSqNg = 1e-22f;
                    if (crossNg.sqrMagnitude > crossEpsSqNg)
                    {
                        piNormal = crossNg.normalized;
                        anchoredGuideGroupCross = true; // skip OP projection: any VSEPR-group cross succeeded
                    }
                }
            }

            if (!anchoredGuideGroupCross)
            {
                if (nonGuideOp != null)
                {
                    uInPlanePerpSigma = Vector3.ProjectOnPlane(nonGuideOp.transform.position - nonGuideAtom.transform.position, sigmaAxisWorld).normalized;
                }
                if (uInPlanePerpSigma.sqrMagnitude < 1e-12f && guideOp != null)
                {
                    uInPlanePerpSigma = Vector3.ProjectOnPlane(guideOp.transform.position - guideAtom.transform.position, sigmaAxisWorld).normalized;
                }
                if (uInPlanePerpSigma.sqrMagnitude < 1e-12f)
                {
                    Vector3 arb = Mathf.Abs(sigmaAxisWorld.y) < 0.92f ? Vector3.up : Vector3.right;
                    uInPlanePerpSigma = Vector3.ProjectOnPlane(arb, sigmaAxisWorld);
                    if (uInPlanePerpSigma.sqrMagnitude < 1e-12f)
                        uInPlanePerpSigma = Vector3.ProjectOnPlane(Vector3.forward, sigmaAxisWorld);
                    if (uInPlanePerpSigma.sqrMagnitude < 1e-12f)
                        return false;
                    uInPlanePerpSigma.Normalize();
                }

                piNormal = Vector3.Cross(sigmaAxisWorld, uInPlanePerpSigma);
                if (piNormal.sqrMagnitude < 1e-16f)
                    piNormal = Vector3.Cross(sigmaAxisWorld, Vector3.up);
                if (piNormal.sqrMagnitude < 1e-16f)
                    piNormal = Vector3.Cross(sigmaAxisWorld, Vector3.right);
                if (piNormal.sqrMagnitude < 1e-16f)
                    return false;
                piNormal.Normalize();
            }
        }

        // If π normal is still (near-)zero or never normalized, σ × arbitrary ensures Upsert receives |+z|² ≫ 1e-18.
        if (piNormal.sqrMagnitude < 1e-12f)
        {
            Vector3 arb = Mathf.Abs(sigmaAxisWorld.y) < 0.92f ? Vector3.up : Vector3.right;
            Vector3 uPerp = Vector3.ProjectOnPlane(arb, sigmaAxisWorld);
            if (uPerp.sqrMagnitude < 1e-12f)
                uPerp = Vector3.ProjectOnPlane(Vector3.forward, sigmaAxisWorld);
            if (uPerp.sqrMagnitude < 1e-12f)
            {
                sharedPiNormalPlusZWorld = Vector3.zero;
                return false;
            }
            uPerp.Normalize();
            piNormal = Vector3.Cross(sigmaAxisWorld, uPerp);
            if (piNormal.sqrMagnitude < 1e-16f)
            {
                sharedPiNormalPlusZWorld = Vector3.zero;
                return false;
            }
        }
        if (piNormal.sqrMagnitude > 1e-20f)
            piNormal = piNormal.normalized;
        else
        {
            sharedPiNormalPlusZWorld = Vector3.zero;
            return false;
        }

        Vector3 ResolveOpDirWorld(ElectronOrbitalFunction op, AtomFunction owner)
        {
            if (op == null || owner == null)
                return Vector3.zero;
            Vector3 d = OrbitalAngleUtility.GetOrbitalDirectionWorld(op.transform);
            if (d.sqrMagnitude < 1e-16f)
                d = op.transform.position - owner.transform.position;
            if (d.sqrMagnitude < 1e-16f)
                return Vector3.zero;
            return d.normalized;
        }

        // Pick one shared sign (+z or -z) for both atoms by maximizing the summed OP alignment:
        // Dot(op1,+z)+Dot(op2,+z) vs Dot(op1,-z)+Dot(op2,-z).
        Vector3 guideOpDir = ResolveOpDirWorld(guideOp, guideAtom);
        Vector3 nonGuideOpDir = ResolveOpDirWorld(nonGuideOp, nonGuideAtom);
        float scorePlus = 0f;
        float dotGuidePlus = 0f;
        float dotNonGuidePlus = 0f;
        if (guideOpDir.sqrMagnitude > 1e-16f)
        {
            dotGuidePlus = Vector3.Dot(guideOpDir, piNormal);
            scorePlus += dotGuidePlus;
        }
        if (nonGuideOpDir.sqrMagnitude > 1e-16f)
        {
            dotNonGuidePlus = Vector3.Dot(nonGuideOpDir, piNormal);
            scorePlus += dotNonGuidePlus;
        }
        if (scorePlus < 0f)
            piNormal = -piNormal;
        sharedPiNormalPlusZWorld = piNormal;
        return true;
    }

    /// <summary>
    /// π phase 1 OP-only: <paramref name="sharedPiNormalPlusZWorld"/> is the shared +z/−z axis (trigonal plane normal).
    /// Final nucleus-local target is +z or −z in world, whichever has larger dot with the OP’s current hybrid +X
    /// (fallback: nucleus → OP offset).
    /// </summary>
    static bool TryBuildPiPrebondOpPhase1TrigonalInPlaneRedistributionAnimation(
        AtomFunction atom,
        ElectronOrbitalFunction op,
        Vector3 sharedPiNormalPlusZWorld,
        out OrbitalRedistribution.RedistributionAnimation opAnimation)
    {
        opAnimation = null;
        if (atom == null || op == null || sharedPiNormalPlusZWorld.sqrMagnitude < 1e-16f)
            return false;

        Vector3 plusW = sharedPiNormalPlusZWorld.normalized;

        Vector3 curWorld = OrbitalAngleUtility.GetOrbitalDirectionWorld(op.transform);
        if (curWorld.sqrMagnitude < 1e-18f)
        {
            Vector3 radialWorld = op.transform.position - atom.transform.position;
            curWorld = radialWorld.sqrMagnitude > 1e-18f ? radialWorld.normalized : Vector3.right;
        }
        curWorld.Normalize();

        Vector3 targetWorld = plusW;
        Vector3 targetLocal = atom.transform.InverseTransformDirection(targetWorld).normalized;
        if (targetLocal.sqrMagnitude < 1e-18f)
            return false;

        Vector3 startWorld = OrbitalAngleUtility.GetOrbitalDirectionWorld(op.transform);
        if (startWorld.sqrMagnitude < 1e-18f)
        {
            Vector3 radialWorld = op.transform.position - atom.transform.position;
            startWorld = radialWorld.sqrMagnitude > 1e-18f ? radialWorld.normalized : Vector3.right;
        }

        float radialMag = (op.transform.position - atom.transform.position).magnitude;
        float radius = radialMag > 1e-6f ? radialMag : Mathf.Max(0.01f, atom.BondRadius);

        opAnimation = new OrbitalRedistribution.RedistributionAnimation(atom);
        opAnimation.AddOrbitalTarget(op, startWorld, radius, targetLocal, isEmptyGroup: true);

        return true;
    }

    /// <summary>Applies phase-1 tracks at completion in one step (smoothstep 1 + finalize). Use only when <paramref name="dur"/> is ~0.</summary>
    static void ExecutePhase1ParallelAnimationsImmediate(
        IReadOnlyList<Phase1ParallelTrack> tracks,
        ICollection<AtomFunction> molForBondLines,
        float dur)
    {
        if (tracks == null || tracks.Count == 0)
            return;
        if (dur >= 1e-5f)
            throw new InvalidOperationException(
                "ExecutePhase1ParallelAnimationsImmediate requires dur < 1e-5f; use CoExecutePhase1ParallelAnimations as a coroutine for animated phase 1.");

        for (int i = 0; i < tracks.Count; i++)
            tracks[i].ApplySmoothStep?.Invoke(1f);
        if (molForBondLines != null && molForBondLines.Count > 0)
            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
        for (int i = 0; i < tracks.Count; i++)
            tracks[i].FinalizeAfterTimeline?.Invoke();
    }

    /// <summary>Runs all <paramref name="tracks"/> with the same smoothstep timeline; updates σ line visuals once per frame after all applies.</summary>
    static IEnumerator CoExecutePhase1ParallelAnimations(
        IReadOnlyList<Phase1ParallelTrack> tracks,
        ICollection<AtomFunction> molForBondLines,
        float dur)
    {
        if (tracks == null || tracks.Count == 0)
            yield break;

        if (dur < 1e-5f)
        {
            ExecutePhase1ParallelAnimationsImmediate(tracks, molForBondLines, dur);
            yield break;
        }

        float t = 0f;
        int frameCount = 0;
        while (true)
        {
            float u = Mathf.Clamp01(t / dur);
            float s = u * u * (3f - 2f * u);
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].ApplySmoothStep?.Invoke(s);
            if (molForBondLines != null && molForBondLines.Count > 0)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
            yield return null;
            frameCount++;
            if (u >= 1f - 1e-6f)
                                break;
            t += Time.deltaTime;
        }

        for (int i = 0; i < tracks.Count; i++)
            tracks[i].FinalizeAfterTimeline?.Invoke();
            }

    /// <summary>
    /// σ phase 1 target for the <b>non-guide</b> nucleus: guide nucleus + <paramref name="bondLength"/> along the guide σ op outbound axis
    /// (from guide nucleus toward the op orbital’s world position; if that offset is degenerate, hybrid +X in world).
    /// </summary>
    static Vector3 Phase1NonGuideNucleusTargetOnGuideOpOutboundRay(
        AtomFunction guide,
        ElectronOrbitalFunction guideOp,
        float bondLength)
    {
        if (guide == null)
            return Vector3.zero;
        Vector3 g = guide.transform.position;
        if (guideOp == null)
            return g + Vector3.right * bondLength;

        Vector3 towardOpCenter = guideOp.transform.position - g;
        if (towardOpCenter.sqrMagnitude > 1e-10f)
            return g + towardOpCenter.normalized * bondLength;

        Vector3 u = OrbitalAngleUtility.GetOrbitalDirectionWorld(guideOp.transform);
        return g + u * bondLength;
    }

    static void ApplyPhase1ApproachFragmentOffset(
        Dictionary<AtomFunction, Vector3> initialWorld,
        List<AtomFunction> toMove,
        Vector3 deltaTotal,
        float s)
    {
                    Vector3 off = deltaTotal * s;
                    foreach (var a in toMove)
                    {
                        if (a == null || !initialWorld.TryGetValue(a, out var p0)) continue;
            a.transform.position = p0 + off;
        }
    }

    static void ApplyPhase1CyclicAtomTargets(
        Dictionary<AtomFunction, Vector3> initialWorld,
        Dictionary<AtomFunction, Vector3> targetWorldByAtom,
        float s)
    {
        if (initialWorld == null || targetWorldByAtom == null) return;
        foreach (var kv in targetWorldByAtom)
        {
            if (kv.Key == null) continue;
            if (!initialWorld.TryGetValue(kv.Key, out Vector3 p0)) continue;
            kv.Key.transform.position = Vector3.Lerp(p0, kv.Value, s);
        }
    }

    /// <summary>
    /// Atoms that move in σ phase 1 approach. If the guide is not in the same molecule as non-guide, the whole non-guide component moves.
    /// If both are in one molecule, only the non-guide <b>branch</b> (reachable without crossing the guide atom) moves with the approach.
    /// </summary>
    static List<AtomFunction> BuildNonGuideFragmentAtomsForApproach(AtomFunction guide, AtomFunction nonGuide)
    {
        var mol = nonGuide.GetConnectedMolecule();
        if (mol == null || mol.Count == 0)
            return new List<AtomFunction> { nonGuide };
        bool guideInMol = false;
        foreach (var a in mol)
        {
            if (a != null && a == guide)
            {
                guideInMol = true;
                break;
            }
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

    static CyclicPhase1Context TryBuildCyclicPhase1Context(AtomFunction guide, AtomFunction nonGuide, float bondLength)
    {
        if (guide == null || nonGuide == null) return null;
        if (TryFindSigmaBondBetween(guide, nonGuide) != null) return null;

        var mol = guide.GetConnectedMolecule();
        if (mol == null || !mol.Contains(nonGuide)) return null;

        if (!TryBuildShortestSigmaPath(guide, nonGuide, out var pathAtoms)) return null;
        int ringSize = pathAtoms.Count;
        if (ringSize < 3 || ringSize > 6) return null;
        if (pathAtoms[0] != guide || pathAtoms[ringSize - 1] != nonGuide) return null;
        if (ringSize < 2 || pathAtoms[1] == null || pathAtoms[ringSize - 2] == null) return null;

        var template = BuildCycleTemplateVertexAnchored(ringSize, bondLength);
        if (template == null || template.Length != ringSize) return null;

        Vector3 guidePos = guide.transform.position;
        Vector3 templateEdge = template[1] - template[0];
        Vector3 worldEdge = pathAtoms[1].transform.position - guidePos;
        if (templateEdge.sqrMagnitude < 1e-10f || worldEdge.sqrMagnitude < 1e-10f) return null;

        Quaternion align = Quaternion.FromToRotation(templateEdge.normalized, worldEdge.normalized);

        // After pinning C1->C2, add one twist around that axis so C3 lands as close as possible.
        Vector3 axis = worldEdge.normalized;
        if (ringSize >= 3 && pathAtoms[2] != null)
        {
            Vector3 alignedTemplateC3Dir = align * (template[2] - template[0]).normalized;
            Vector3 currentC3Dir = (pathAtoms[2].transform.position - guidePos).normalized;

            Vector3 tplInPlane = Vector3.ProjectOnPlane(alignedTemplateC3Dir, axis).normalized;
            Vector3 curInPlane = Vector3.ProjectOnPlane(currentC3Dir, axis).normalized;
            if (tplInPlane.sqrMagnitude > 1e-10f && curInPlane.sqrMagnitude > 1e-10f)
            {
                float twistDeg = Vector3.SignedAngle(tplInPlane, curInPlane, axis);
                Quaternion twist = Quaternion.AngleAxis(twistDeg, axis);
                align = twist * align;
            }
        }

        var targetsByAtom = new Dictionary<AtomFunction, Vector3>();
        Vector3 centerAccum = Vector3.zero;
        int centerCount = 0;
        for (int i = 0; i < ringSize; i++)
        {
            Vector3 world = guidePos + align * (template[i] - template[0]);
            centerAccum += world;
            centerCount++;
            // Keep guide + immediate guide neighbor fixed; animate the rest of the path.
            if (i >= 2)
                targetsByAtom[pathAtoms[i]] = world;
        }

        return new CyclicPhase1Context
        {
            PathAtoms = pathAtoms,
            TargetWorldByAtom = targetsByAtom,
            CycleCenterWorld = centerCount > 0 ? centerAccum / centerCount : guidePos,
            ChainRedistributionBlockAtoms = new HashSet<AtomFunction>(pathAtoms),
            NonGuideCycleNeighbor = pathAtoms[ringSize - 2]
        };
    }

    static bool TryBuildShortestSigmaPath(AtomFunction start, AtomFunction end, out List<AtomFunction> path, CovalentBond excludeSigmaBond = null)
    {
        path = null;
        if (start == null || end == null) return false;
        if (start == end)
        {
            path = new List<AtomFunction> { start };
            return true;
        }

        var parent = new Dictionary<AtomFunction, AtomFunction>();
        var queue = new Queue<AtomFunction>();
        queue.Enqueue(start);
        parent[start] = null;

        while (queue.Count > 0)
        {
            AtomFunction cur = queue.Dequeue();
            if (cur == null) continue;
            if (cur == end) break;
            for (int bi = 0; bi < cur.CovalentBonds.Count; bi++)
            {
                var cb = cur.CovalentBonds[bi];
                if (cb == null || !cb.IsSigmaBondLine()) continue;
                if (excludeSigmaBond != null && ReferenceEquals(cb, excludeSigmaBond)) continue;
                AtomFunction next = cb.AtomA == cur ? cb.AtomB : cb.AtomA;
                if (next == null || parent.ContainsKey(next)) continue;
                parent[next] = cur;
                queue.Enqueue(next);
            }
        }

        if (!parent.ContainsKey(end)) return false;
        var rev = new List<AtomFunction>();
        AtomFunction node = end;
        while (node != null)
        {
            rev.Add(node);
            node = parent[node];
        }
        rev.Reverse();
        path = rev;
        return true;
    }

    static Vector3[] BuildCycleTemplateVertexAnchored(int ringSize, float bondLength)
    {
        if (ringSize < 3 || ringSize > 6) return null;
        switch (ringSize)
        {
            case 3:
                return BuildPuckeredCyclopropaneTemplate(bondLength);
            case 4:
                return BuildPuckeredCyclobutaneTemplate(bondLength);
            case 5:
                return BuildEnvelopeCyclopentaneTemplate(bondLength);
            case 6:
                return BuildChairCyclohexaneTemplate(bondLength);
            default:
                return null;
        }
    }

    static void RescaleRingEdges(Vector3[] pts, float targetEdge)
    {
        if (pts == null || pts.Length < 2) return;
        int n = pts.Length;
        float sum = 0f;
        for (int i = 0; i < n; i++)
            sum += Vector3.Distance(pts[i], pts[(i + 1) % n]);
        float avg = sum / n;
        if (avg < 1e-6f) return;
        float s = targetEdge / avg;
        for (int i = 0; i < n; i++)
            pts[i] *= s;
    }

    static Vector3[] BuildPuckeredCyclopropaneTemplate(float bondLength)
    {
        float r = bondLength / (2f * Mathf.Sin(Mathf.PI / 3f));
        float lift = bondLength * 0.14f;
        var pts = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            float ang = (360f * i / 3f - 90f) * Mathf.Deg2Rad;
            float z = i == 0 ? lift : (i == 1 ? -lift * 0.45f : -lift * 0.55f);
            pts[i] = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, bondLength);
        return pts;
    }

    static Vector3[] BuildPuckeredCyclobutaneTemplate(float bondLength)
    {
        float r = bondLength * 0.74f;
        float d = bondLength * 0.22f;
        var pts = new Vector3[4];
        float[] zs = { d, -d, d, -d };
        for (int i = 0; i < 4; i++)
        {
            float ang = (45f + 90f * i) * Mathf.Deg2Rad;
            pts[i] = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, zs[i]);
        }
        RescaleRingEdges(pts, bondLength);
        return pts;
    }

    static Vector3[] BuildEnvelopeCyclopentaneTemplate(float bondLength)
    {
        const int n = 5;
        float r = bondLength / (2f * Mathf.Sin(Mathf.PI / n));
        float flap = bondLength * 0.35f;
        float ripple = bondLength * 0.08f;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float ang = (360f * i / n - 90f) * Mathf.Deg2Rad;
            float z = (i == 2 ? flap : 0f) + Mathf.Sin(i * 2f * Mathf.PI / n) * ripple;
            pts[i] = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, bondLength);
        return pts;
    }

    static Vector3[] BuildChairCyclohexaneTemplate(float bondLength)
    {
        float r = bondLength * 0.72f;
        float h = r * Mathf.Sqrt(1f / 32f);
        var pts = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float ang = (60f * i - 90f) * Mathf.Deg2Rad;
            float z = i % 2 == 0 ? h : -h;
            pts[i] = new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, bondLength);
        return pts;
    }

    static bool TryGetPrebondCycleSize(AtomFunction guide, AtomFunction nonGuide, out int cycleSize)
    {
        cycleSize = 0;
        if (guide == null || nonGuide == null) return false;
        if (TryFindSigmaBondBetween(guide, nonGuide) != null) return false;
        if (!TryBuildShortestSigmaPath(guide, nonGuide, out var pathAtoms) || pathAtoms == null) return false;
        cycleSize = pathAtoms.Count;
        return cycleSize >= 3 && cycleSize <= 6;
    }

    /// <summary>
    /// True when this σ-line bond lies on a simple ring of size 3–6: with this bond excluded from traversal, the
    /// endpoints are still connected by a shortest σ-only path; <paramref name="ringSize"/> is that path’s vertex count
    /// (including both endpoints), i.e. the ring size.
    /// </summary>
    public static bool TryGetCyclicSigmaBondBreakRingSize(CovalentBond sigmaBond, out int ringSize)
    {
        ringSize = 0;
        if (sigmaBond == null || !sigmaBond.IsSigmaBondLine()) return false;
        var a = sigmaBond.AtomA;
        var b = sigmaBond.AtomB;
        if (a == null || b == null) return false;
        if (!TryBuildShortestSigmaPath(a, b, out var pathAtoms, sigmaBond) || pathAtoms == null) return false;
        ringSize = pathAtoms.Count;
        return ringSize >= 3 && ringSize <= 6;
    }

    /// <summary>Shortest σ-line path between endpoints (e.g. along a ring after the direct bond was removed).</summary>
    public static bool TryGetSigmaShortestPathBetween(AtomFunction start, AtomFunction end, out List<AtomFunction> path)
    {
        return TryBuildShortestSigmaPath(start, end, out path, excludeSigmaBond: null);
    }

    static void BuildPhase1RedistributeTemplatePreviewVisuals(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        Dictionary<AtomFunction, Vector3> initialWorld,
        Vector3 deltaTotal,
        CyclicPhase1Context cyclicContext,
        bool disablePhase1Redistribution)
    {
        ClearPhase1RedistributeTemplatePreviewVisuals();
        if (disablePhase1Redistribution)
            return;

        OrbitalRedistribution.BeginDebugTemplateCapture();
        try
        {
            Vector3 finalDirectionForGuideOrbital = Vector3.zero;
            OrbitalRedistribution.CyclicRedistributionContext redistCycleContext = null;
            if (cyclicContext != null && guideAtom != null && nonGuideAtom != null)
            {
                var finalWorldByAtom = new Dictionary<AtomFunction, Vector3>();
                for (int i = 0; i < cyclicContext.PathAtoms.Count; i++)
                {
                    AtomFunction a = cyclicContext.PathAtoms[i];
                    if (a == null) continue;
                    if (cyclicContext.TargetWorldByAtom.TryGetValue(a, out Vector3 target))
                        finalWorldByAtom[a] = target;
                    else
                        finalWorldByAtom[a] = a.transform.position;
                }

                redistCycleContext = new OrbitalRedistribution.CyclicRedistributionContext
                {
                    PivotAtom = nonGuideAtom,
                    CycleNeighborA = guideAtom,
                    CycleNeighborB = cyclicContext.NonGuideCycleNeighbor,
                    FinalWorldByAtom = finalWorldByAtom,
                    CycleCenterWorld = cyclicContext.CycleCenterWorld,
                    ChainRedistributionBlockedAtoms = cyclicContext.ChainRedistributionBlockAtoms
                };

                Vector3 pivotFinal = finalWorldByAtom.TryGetValue(nonGuideAtom, out Vector3 p) ? p : nonGuideAtom.transform.position;
                Vector3 guideFinal = finalWorldByAtom.TryGetValue(guideAtom, out Vector3 g) ? g : guideAtom.transform.position;
                Vector3 guideFinalDirWorld = (guideFinal - pivotFinal).normalized;
                if (guideFinalDirWorld.sqrMagnitude > 1e-12f)
                    finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(guideFinalDirWorld).normalized;
            }

            var _ = OrbitalRedistribution.BuildOrbitalRedistribution(
                nonGuideAtom,
                guideAtom,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: finalDirectionForGuideOrbital,
                cyclicContext: redistCycleContext,
                isBondingEvent: true);
        }
        finally
        {
            // capture closed below
        }

        var snapshots = OrbitalRedistribution.EndDebugTemplateCapture();
        if (snapshots == null || snapshots.Count == 0)
            return;

        phase1DebugTemplatePreviewRoot = new GameObject("Phase1RedistributeTemplatePreview");
        if (phase1DebugTemplatePreviewRoot.GetComponent<BondFormationTemplatePreviewInput>() == null)
            phase1DebugTemplatePreviewRoot.AddComponent<BondFormationTemplatePreviewInput>();
        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            if (snap == null) continue;
            if (snap.Atom == null || snap.TemplateLocal == null || snap.TemplateLocal.Count == 0) continue;
            Vector3 center = ResolvePhase1FinalCenterWorld(snap.Atom, initialWorld, deltaTotal, cyclicContext);
            float len = Mathf.Max(0.125f, snap.Atom.BondRadius * 0.55f);
            for (int di = 0; di < snap.TemplateLocal.Count; di++)
            {
                Vector3 dirWorld = snap.Atom.transform.TransformDirection(snap.TemplateLocal[di]).normalized;
                if (dirWorld.sqrMagnitude < 1e-12f) continue;
                Vector3 end = center + dirWorld * len;
                ElectronOrbitalFunction linkedOrbital = FindAssignedOrbitalForTemplateIndex(snap, di);
                CreateDebugTemplateCylinder(
                    phase1DebugTemplatePreviewRoot.transform,
                    center,
                    end,
                    snap.Atom.GetInstanceID(),
                    di,
                    linkedOrbital);
            }
        }
    }

    static Vector3 ResolvePhase1FinalCenterWorld(
        AtomFunction atom,
        Dictionary<AtomFunction, Vector3> initialWorld,
        Vector3 deltaTotal,
        CyclicPhase1Context cyclicContext)
    {
        if (atom == null) return Vector3.zero;
        if (cyclicContext != null
            && cyclicContext.TargetWorldByAtom != null
            && cyclicContext.TargetWorldByAtom.TryGetValue(atom, out Vector3 cycTarget))
            return cycTarget;
        if (initialWorld != null && initialWorld.TryGetValue(atom, out Vector3 p0))
            return p0 + deltaTotal;
        return atom.transform.position;
    }

    static void CreateDebugTemplateCylinder(
        Transform parent,
        Vector3 startWorld,
        Vector3 endWorld,
        int atomId,
        int dirIndex,
        ElectronOrbitalFunction linkedOrbital)
    {
        Vector3 v = endWorld - startWorld;
        float len = v.magnitude;
        if (len < 1e-6f) return;
        var go = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        go.name = "Phase1Tpl_A" + atomId + "_D" + dirIndex;
        go.transform.SetParent(parent, worldPositionStays: true);
        go.transform.position = (startWorld + endWorld) * 0.5f;
        go.transform.rotation = Quaternion.FromToRotation(Vector3.up, v.normalized);
        float r = Mathf.Clamp(len * 0.06f, 0.015f, 0.06f);
        go.transform.localScale = new Vector3(r, len * 0.5f, r);
        var col = go.GetComponent<Collider>();
        if (col != null) col.isTrigger = true;
        var renderer = go.GetComponent<Renderer>();
        if (renderer != null)
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.color = new Color(0.20f, 0.90f, 1f, 0.92f);
                renderer.sharedMaterial = mat;
            }
        }
        var pick = go.AddComponent<BondFormationTemplatePreviewPick>();
        pick.SetDescription("Final template A=" + atomId + " dir=" + dirIndex);
        pick.SetLinkedOrbital(linkedOrbital, renderer != null ? new[] { renderer } : null);
    }

    static ElectronOrbitalFunction FindAssignedOrbitalForTemplateIndex(
        OrbitalRedistribution.DebugTemplateSnapshot snapshot,
        int templateIndex)
    {
        if (snapshot == null || snapshot.GroupAssignments == null) return null;
        for (int i = 0; i < snapshot.GroupAssignments.Count; i++)
        {
            var row = snapshot.GroupAssignments[i];
            if (row.templateIndex == templateIndex)
                return row.orbital;
        }
        return null;
    }

    static void ClearPhase1RedistributeTemplatePreviewVisuals()
    {
        if (phase1DebugTemplatePreviewRoot != null)
        {
            UnityEngine.Object.Destroy(phase1DebugTemplatePreviewRoot);
            phase1DebugTemplatePreviewRoot = null;
        }
    }

    /// <summary>
    /// True if the σ formation pipeline was started (animated coroutine) or completed (immediate). When <see cref="editModeManager"/> is null, returns false.
    /// </summary>
    /// <param name="animate">When false, runs the same phases as orbital-drag σ without animation or frame waits (library / tooling).</param>
    public bool TryBeginOrbitalDragSigmaFormation(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital,
        bool animate = true)
    {
        if (editModeManager == null)
        {
            Debug.LogWarning("[sigma-drag] SigmaBondFormation: no EditModeManager reference; cannot run σ formation pipeline.");
            return false;
        }
        if (!animate)
            return RunOrbitalDragSigmaFormationThreePhaseImmediate(
                atomA,
                atomB,
                orbA,
                orbB,
                redistributionGuideTieBreakDraggedOrbital);
        StartCoroutine(CoOrbitalDragSigmaFormationThreePhase(
            atomA,
            atomB,
            orbA,
            orbB,
            redistributionGuideTieBreakDraggedOrbital,
            animate: true));
        return true;
    }

    /// <summary>
    /// Orbital-drag σ: phase 1 non-guide approach toward guide op target, bond animation, then post-bond guide lerp.
    /// </summary>
    IEnumerator CoOrbitalDragSigmaFormationThreePhase(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital,
        bool animate)
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
            if (animate)
            {
                // Pointer-up can still leave the dragged lobe at the drop world pose in the same frame StartCoroutine runs.
                // Snap to pre-drag locals, then wait one frame so bond animation reads nuclei and orbitals from the restored configuration.
                var draggedLobeToRestore = redistributionGuideTieBreakDraggedOrbital != null
                    ? redistributionGuideTieBreakDraggedOrbital
                    : orbA;
                if (draggedLobeToRestore != null)
                    draggedLobeToRestore.SnapToOriginal();
                yield return null;
            }

            int merged = orbA.ElectronCount + orbB.ElectronCount;
            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : orbA;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(atomA, atomB, guideOrb, out var guide, out var nonGuide);
            bool prebondCycleCandidate = TryGetPrebondCycleSize(guide, nonGuide, out _);

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
                    orbitalDragPostbondGuideHybridLerp: animate,
                    redistributionGuideTieBreakDraggedOrbital,
                    phase3GuideLerpSecondsOverride: animate ? phase3Sec : null,
                    skipHydrogenSigmaNeighborSnapAfterTail: !animate);
                yield break;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == atomA ? orbA : orbB;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == atomA ? orbA : orbB;
            yield return StartCoroutine(CoOrbitalDragSigmaPhase1Prebond(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                animate ? phase1Sec : 0f,
                allowSteppedDebug: animate));

            // Phase 2: bond creation + cylinder + orbital→line. Durations from sigmaFormationPhase2* (resolved, non-negative).

            atomA.UnbondOrbital(orbA);
            atomB.UnbondOrbital(orbB);
            var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: animate);
            if (bond == null)
                yield break;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            orbA.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(orbB);
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            float tCyl = Mathf.Max(0f, animate ? phase2CylinderSec : 0f);
            float tLine = Mathf.Max(0f, animate ? phase2OrbitalToLineSec : 0f);

            yield return StartCoroutine(orbA.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                atomA, atomB, orbB, orbA, bond, tCyl));

            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(tLine, orbB);
            orbA.ElectronCount = merged;
            atomA.RefreshCharge();
            atomB.RefreshCharge();
            
            // Phase 3: guide post-bond redistribution. Omit for ring-closure σ (prebond cycle): phase 1–2 already set geometry.
            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f && !prebondCycleCandidate)
            {
                var phase3GuideOp = bond != null && bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
                {
                    if (animate)
                    {
                        float t3 = 0f;
                        while (t3 < phase3Sec)
                        {
                            t3 += Time.deltaTime;
                            float s3 = Mathf.Clamp01(t3 / phase3Sec);
                            float smooth3 = s3 * s3 * (3f - 2f * s3);
                            phase3GuideRedistribution.Apply(smooth3);
                            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                            yield return null;
                        }
                    }

                    phase3GuideRedistribution.Apply(1f);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }

            }

            editModeManager.FinishSigmaBondInstantTail(
                atomA,
                atomB,
                skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
        }
        finally
        {
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
        }
    }

    bool RunOrbitalDragSigmaFormationThreePhaseImmediate(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        var timingOrb = TimingSourceOrbital(orbA, orbB, redistributionGuideTieBreakDraggedOrbital);
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
            int merged = orbA.ElectronCount + orbB.ElectronCount;
            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : orbA;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(atomA, atomB, guideOrb, out var guide, out var nonGuide);
            bool prebondCycleCandidate = TryGetPrebondCycleSize(guide, nonGuide, out _);

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
                    orbitalDragPostbondGuideHybridLerp: false,
                    redistributionGuideTieBreakDraggedOrbital,
                    phase3GuideLerpSecondsOverride: null,
                    skipHydrogenSigmaNeighborSnapAfterTail: true);
                return true;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == atomA ? orbA : orbB;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == atomA ? orbA : orbB;
            RunOrbitalDragSigmaPhase1PrebondSynchronously(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                0f,
                debugDisableCyclicSigmaPhase1Redistribution);

            atomA.UnbondOrbital(orbA);
            atomB.UnbondOrbital(orbB);
            var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
            if (bond == null)
                return false;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            orbA.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(orbB);
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            DrainCoroutineToCompletion(orbA.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                atomA, atomB, orbB, orbA, bond, 0f));
            bond.animatingOrbitalToBondPosition = false;
            DrainCoroutineToCompletion(bond.AnimateOrbitalToLine(0f, orbB));
            orbA.ElectronCount = merged;
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f && !prebondCycleCandidate)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
                {
                    phase3GuideRedistribution.Apply(1f);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }

            }

            editModeManager.FinishSigmaBondInstantTail(
                atomA,
                atomB,
                skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
            return true;
        }
        finally
        {
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
        }
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

    /// <summary>
    /// True if the π formation pipeline was started (animated) or completed (immediate). When <see cref="editModeManager"/> is null, returns false.
    /// </summary>
    /// <param name="animate">When false, runs the same phases as orbital-drag π without animation or frame waits.</param>
    public bool TryBeginOrbitalDragPiFormation(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital,
        bool animate = true)
    {
        EnsureEditModeManagerReference();
                if (editModeManager == null)
        {
            Debug.LogWarning("[pi-drag] SigmaBondFormation: no EditModeManager reference; cannot run π formation pipeline.");
                        return false;
        }
        if (!animate)
        {
            bool imm = RunOrbitalDragPiFormationThreePhaseImmediate(
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                redistributionGuideTieBreakDraggedOrbital);
                        return imm;
        }
        StartCoroutine(CoOrbitalDragPiFormationThreePhase(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            redistributionGuideTieBreakDraggedOrbital,
            animate: true));
                return true;
    }

    /// <summary>π phase 1: orbital redistribution + cylinder lerp toward π line pose on existing σ (parallel timeline).</summary>
    IEnumerator CoOrbitalDragPiPhase1RedistributionOnly(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        float phase1Sec,
        ICollection<AtomFunction> molForBondLines,
        bool buildPhase1TemplatePreviews,
        PiPhase1GuideRedistributionDecision guideDecision = null)
    {
        if (guide == null || nonGuide == null)
            yield break;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        guide.RemovePiPOrbitalDirectionsForPartnerLine(nonGuide, AtomFunction.PiPOrbitalPrebondLineIndex);
        nonGuide.RemovePiPOrbitalDirectionsForPartnerLine(guide, AtomFunction.PiPOrbitalPrebondLineIndex);
        Vector3 sourceStart = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStart = targetAtom != null ? targetAtom.transform.position : Vector3.zero;

        var sigmaBetween = TryFindSigmaBondBetween(sourceAtom, targetAtom);
        TryBuildPiPhase1CyclicRedistributionContext(
            sourceAtom,
            targetAtom,
            guide,
            nonGuide,
            sigmaBetween,
            out var piPhase1CyclicContext,
            out _,
            out var piTargetC2,
            out var piTargetC3,
            out var piGuideDirLocal);
        Vector3 perpendicularGuideDirLocalCandidate = ComputePiPerpendicularGuideDirectionLocal(nonGuide, guide, nonGuideOp);
        bool hasPerpendicularPiGuideDirCandidate = perpendicularGuideDirLocalCandidate.sqrMagnitude > 1e-12f;
        
        if (buildPhase1TemplatePreviews
            && debugShowCyclicPiPhase1RedistributeTemplates
            && sigmaBetween != null
            && TryBuildShortestSigmaPath(sourceAtom, targetAtom, out var ringPath, sigmaBetween)
            && ringPath != null
            && ringPath.Count >= 4)
        {
            BuildPiPhase1RedistributeTemplatePreviewVisuals(
                guide,
                nonGuide,
                guideOp,
                nonGuideOp,
                piPhase1CyclicContext,
                piGuideDirLocal);
        }
        else
        {
            ClearPiPhase1RedistributeTemplatePreviewVisuals();
        }

        bool runGuideInPhase1 = OrbitalRedistribution.BothPiPairGuideGroupsAreMutualInterAtomSigma(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            isBondingEvent: true);
        if (guideDecision != null)
            guideDecision.GuideRedistributedInPhase1 = runGuideInPhase1;

        Vector3 sharedPiNormalPlusZWorld = Vector3.zero;
        bool hasSharedPiPPlusZ = TryComputeSharedPiPrebondPOrbitalPlusZWorld(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            out sharedPiNormalPlusZWorld);
        if (hasSharedPiPPlusZ)
        {
            Vector3 minusZ = -sharedPiNormalPlusZWorld;
            guide.SetPiPrebondPOrbitalDirectionsWorld(nonGuide, sharedPiNormalPlusZWorld, minusZ);
            nonGuide.SetPiPrebondPOrbitalDirectionsWorld(guide, sharedPiNormalPlusZWorld, minusZ);
        }

        var tracks = new List<Phase1ParallelTrack>(runGuideInPhase1 ? 4 : 3);
        OrbitalRedistribution.RedistributionAnimation piOpNonGuidePostCylinder = null;
        OrbitalRedistribution.RedistributionAnimation piOpGuidePostCylinder = null;
        Phase1ParallelTrack piPhase1RedistributionTrack = BuildPhase1OrbitalRedistributeForPiFormationPhase1(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            piPhase1CyclicContext,
            piGuideDirLocal,
            out piOpNonGuidePostCylinder,
            hasSharedPiPPlusZ ? sharedPiNormalPlusZWorld : (Vector3?)null);
        if (piPhase1RedistributionTrack != null)
            tracks.Add(piPhase1RedistributionTrack);
        if (runGuideInPhase1)
        {
            Phase1ParallelTrack piPhase1GuideTrack = BuildPhase1OrbitalRedistributeForPiFormationGuideAtomPhase1(
                guide,
                nonGuide,
                guideOp,
                nonGuideOp,
                piPhase1CyclicContext,
                piGuideDirLocal,
                out piOpGuidePostCylinder,
                hasSharedPiPPlusZ ? sharedPiNormalPlusZWorld : (Vector3?)null);
            if (piPhase1GuideTrack != null)
                tracks.Add(piPhase1GuideTrack);
        }

        Phase1ParallelTrack piCylinderTrack = BuildPhase1PiCylinderTrackFromOpFinalPose(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBetween,
            molForBondLines);
        if (piCylinderTrack != null)
            tracks.Add(piCylinderTrack);
        if (piCylinderTrack != null
            && (piOpNonGuidePostCylinder != null || piOpGuidePostCylinder != null))
        {
            tracks.Add(new Phase1ParallelTrack
            {
                ApplySmoothStep = s =>
                {
                    piOpNonGuidePostCylinder?.Apply(s);
                    piOpGuidePostCylinder?.Apply(s);
                },
                FinalizeAfterTimeline = () => { }
            });
        }

        Vector3 guideOpStartWorld = guideOp != null ? guideOp.transform.position : Vector3.zero;
        Vector3 guideAtomStartWorld = guide != null ? guide.transform.position : Vector3.zero;
        Vector3 nonGuideOpStartWorld = nonGuideOp != null ? nonGuideOp.transform.position : Vector3.zero;
        Vector3 nonGuideAtomStartWorld = nonGuide != null ? nonGuide.transform.position : Vector3.zero;
        var phase1MolAtomStartWorld = new Dictionary<int, Vector3>();
        if (molForBondLines != null)
        {
            foreach (var a in molForBondLines)
            {
                if (a == null) continue;
                int id = a.GetInstanceID();
                if (!phase1MolAtomStartWorld.ContainsKey(id))
                    phase1MolAtomStartWorld[id] = a.transform.position;
            }
        }
        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
                        yield return StartCoroutine(
                CoExecutePhase1ParallelAnimations(tracks, molForBondLines, Mathf.Max(0f, phase1Sec)));
                    }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
                    }

    /// <summary>π phase-1 redistribution (non-guide + optional guide when OP-path); also shared by legacy π gesture coroutine when the three-phase runner cannot start.</summary>
    public static bool RunOrbitalDragPiPhase1RedistributionSynchronously(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ICollection<AtomFunction> molForBondLines)
    {
        if (guide == null || nonGuide == null)
            return false;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        guide.RemovePiPOrbitalDirectionsForPartnerLine(nonGuide, AtomFunction.PiPOrbitalPrebondLineIndex);
        nonGuide.RemovePiPOrbitalDirectionsForPartnerLine(guide, AtomFunction.PiPOrbitalPrebondLineIndex);
        Vector3 sourceStart = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStart = targetAtom != null ? targetAtom.transform.position : Vector3.zero;

        var sigmaBetween = TryFindSigmaBondBetween(sourceAtom, targetAtom);
        TryBuildPiPhase1CyclicRedistributionContext(
            sourceAtom,
            targetAtom,
            guide,
            nonGuide,
            sigmaBetween,
            out var piPhase1CyclicContext,
            out _,
            out var piTargetC2,
            out var piTargetC3,
            out var piGuideDirLocal);
        Vector3 perpendicularGuideDirLocalCandidate = ComputePiPerpendicularGuideDirectionLocal(nonGuide, guide, nonGuideOp);
        bool hasPerpendicularPiGuideDirCandidate = perpendicularGuideDirLocalCandidate.sqrMagnitude > 1e-12f;
        
        ClearPiPhase1RedistributeTemplatePreviewVisuals();

        bool runGuideInPhase1 = OrbitalRedistribution.BothPiPairGuideGroupsAreMutualInterAtomSigma(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            isBondingEvent: true);

        Vector3 sharedPiNormalPlusZWorld = Vector3.zero;
        bool hasSharedPiPPlusZ = TryComputeSharedPiPrebondPOrbitalPlusZWorld(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            out sharedPiNormalPlusZWorld);
        if (hasSharedPiPPlusZ)
        {
            Vector3 minusZ = -sharedPiNormalPlusZWorld;
            guide.SetPiPrebondPOrbitalDirectionsWorld(nonGuide, sharedPiNormalPlusZWorld, minusZ);
            nonGuide.SetPiPrebondPOrbitalDirectionsWorld(guide, sharedPiNormalPlusZWorld, minusZ);
        }

        var tracks = new List<Phase1ParallelTrack>(runGuideInPhase1 ? 4 : 3);
        OrbitalRedistribution.RedistributionAnimation piOpNonGuidePostCylinder = null;
        OrbitalRedistribution.RedistributionAnimation piOpGuidePostCylinder = null;
        Phase1ParallelTrack piPhase1RedistributionTrack = BuildPhase1OrbitalRedistributeForPiFormationPhase1(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            piPhase1CyclicContext,
            piGuideDirLocal,
            out piOpNonGuidePostCylinder,
            hasSharedPiPPlusZ ? sharedPiNormalPlusZWorld : (Vector3?)null);
        if (piPhase1RedistributionTrack != null)
            tracks.Add(piPhase1RedistributionTrack);
        if (runGuideInPhase1)
        {
            Phase1ParallelTrack piPhase1GuideTrack = BuildPhase1OrbitalRedistributeForPiFormationGuideAtomPhase1(
                guide,
                nonGuide,
                guideOp,
                nonGuideOp,
                piPhase1CyclicContext,
                piGuideDirLocal,
                out piOpGuidePostCylinder,
                hasSharedPiPPlusZ ? sharedPiNormalPlusZWorld : (Vector3?)null);
            if (piPhase1GuideTrack != null)
                tracks.Add(piPhase1GuideTrack);
        }

        Phase1ParallelTrack piCylinderTrack = BuildPhase1PiCylinderTrackFromOpFinalPose(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBetween,
            molForBondLines);
        if (piCylinderTrack != null)
            tracks.Add(piCylinderTrack);
        if (piCylinderTrack != null
            && (piOpNonGuidePostCylinder != null || piOpGuidePostCylinder != null))
        {
            tracks.Add(new Phase1ParallelTrack
            {
                ApplySmoothStep = s =>
                {
                    piOpNonGuidePostCylinder?.Apply(s);
                    piOpGuidePostCylinder?.Apply(s);
                },
                FinalizeAfterTimeline = () => { }
            });
        }

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
            ExecutePhase1ParallelAnimationsImmediate(tracks, molForBondLines, 0f);
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
        return runGuideInPhase1;
    }

    static bool TryComputePlaneNormalForPiProbe(AtomFunction centerAtom, AtomFunction bondedPartnerToExclude, out Vector3 normalWorld)
    {
        normalWorld = Vector3.zero;
        if (centerAtom == null)
            return false;

        Vector3 a = Vector3.zero;
        Vector3 b = Vector3.zero;
        int picked = 0;
        foreach (var cb in centerAtom.CovalentBonds)
        {
            if (cb == null || !cb.IsSigmaBondLine())
                continue;
            AtomFunction other = cb.AtomA == centerAtom ? cb.AtomB : cb.AtomA;
            if (other == null || other == bondedPartnerToExclude)
                continue;
            Vector3 v = other.transform.position - centerAtom.transform.position;
            if (v.sqrMagnitude < 1e-10f)
                continue;
            if (picked == 0) a = v.normalized;
            else if (picked == 1)
            {
                b = v.normalized;
                break;
            }
            picked++;
        }
        if (picked < 2)
            return false;
        normalWorld = Vector3.Cross(a, b);
        if (normalWorld.sqrMagnitude < 1e-10f)
            return false;
        normalWorld.Normalize();
        return true;
    }

    static void BuildPiPhase1RedistributeTemplatePreviewVisuals(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        OrbitalRedistribution.CyclicRedistributionContext cyclicContext,
        Vector3 finalDirectionForGuideOrbital)
    {
        ClearPiPhase1RedistributeTemplatePreviewVisuals();
        if (guideAtom == null || nonGuideAtom == null)
            return;

        bool appliedPreviewP = false;
        if (TryComputeSharedPiPrebondPOrbitalPlusZWorld(
                guideAtom,
                nonGuideAtom,
                guideOp,
                nonGuideOp,
                out Vector3 pzW)
            && pzW.sqrMagnitude > 1e-16f)
        {
            Vector3 mz = -pzW;
            guideAtom.SetPiPrebondPOrbitalDirectionsWorld(nonGuideAtom, pzW, mz);
            nonGuideAtom.SetPiPrebondPOrbitalDirectionsWorld(guideAtom, pzW, mz);
            appliedPreviewP = true;
        }

        OrbitalRedistribution.BeginDebugTemplateCapture();
        try
        {
            _ = OrbitalRedistribution.BuildOrbitalRedistribution(
                nonGuideAtom,
                guideAtom,
                guideOp,
                nonGuideOp,
                guideOrbitalPredetermined: null,
                finalDirectionForGuideOrbital: finalDirectionForGuideOrbital,
                isBondingEvent: true,
                cyclicContext: cyclicContext);
        }
        finally
        {
            if (appliedPreviewP)
            {
                guideAtom.RemovePiPOrbitalDirectionsForPartnerLine(nonGuideAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
                nonGuideAtom.RemovePiPOrbitalDirectionsForPartnerLine(guideAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
            }
        }

        var snapshots = OrbitalRedistribution.EndDebugTemplateCapture();
        if (snapshots == null || snapshots.Count == 0)
            return;

        piPhase1DebugTemplatePreviewRoot = new GameObject("PiPhase1RedistributeTemplatePreview");
        if (piPhase1DebugTemplatePreviewRoot.GetComponent<BondFormationTemplatePreviewInput>() == null)
            piPhase1DebugTemplatePreviewRoot.AddComponent<BondFormationTemplatePreviewInput>();

        for (int i = 0; i < snapshots.Count; i++)
        {
            var snap = snapshots[i];
            if (snap == null || snap.Atom == null || snap.TemplateLocal == null || snap.TemplateLocal.Count == 0)
                continue;

            Vector3 center = snap.Atom.transform.position;
            float len = Mathf.Max(0.125f, snap.Atom.BondRadius * 0.55f);
            for (int di = 0; di < snap.TemplateLocal.Count; di++)
            {
                Vector3 dirWorld = snap.Atom.transform.TransformDirection(snap.TemplateLocal[di]).normalized;
                if (dirWorld.sqrMagnitude < 1e-12f) continue;
                Vector3 end = center + dirWorld * len;
                ElectronOrbitalFunction linkedOrbital = FindAssignedOrbitalForTemplateIndex(snap, di);
                CreateDebugTemplateCylinder(
                    piPhase1DebugTemplatePreviewRoot.transform,
                    center,
                    end,
                    snap.Atom.GetInstanceID(),
                    di,
                    linkedOrbital);
            }
        }
    }

    static bool TryBuildPiPhase1CyclicRedistributionContext(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        CovalentBond sigmaBetween,
        out OrbitalRedistribution.CyclicRedistributionContext cyclicContext,
        out Dictionary<AtomFunction, Vector3> finalWorldByAtom,
        out Vector3 targetC2,
        out Vector3 targetC3,
        out Vector3 finalDirectionForGuideOrbital)
    {
        cyclicContext = null;
        finalWorldByAtom = null;
        targetC2 = Vector3.zero;
        targetC3 = Vector3.zero;
        finalDirectionForGuideOrbital = Vector3.zero;
        if (sourceAtom == null || targetAtom == null || guideAtom == null || nonGuideAtom == null)
            return false;
        if (sigmaBetween == null)
            return false;
        if (!TryBuildShortestSigmaPath(sourceAtom, targetAtom, out var ringPath, sigmaBetween))
            return false;
        if (ringPath == null || ringPath.Count < 4)
            return false;

        AtomFunction c2 = sourceAtom;
        AtomFunction c3 = targetAtom;
        AtomFunction c1 = ringPath[1];
        AtomFunction c4 = ringPath[ringPath.Count - 2];
        if (c1 == null || c4 == null || c1 == c2 || c1 == c3 || c4 == c2 || c4 == c3 || c1 == c4)
            return false;

        var cycleAtoms = new List<AtomFunction>(ringPath.Count + 1) { sourceAtom };
        for (int i = 1; i < ringPath.Count; i++)
            cycleAtoms.Add(ringPath[i]);

        if (!TryComputePiCyclicCoplanarTargets(
            cycleAtoms,
            c1.transform.position,
            c2.transform.position,
            c3.transform.position,
            c4.transform.position,
            out targetC2,
            out targetC3))
            return false;

        finalWorldByAtom = new Dictionary<AtomFunction, Vector3>(cycleAtoms.Count);
        for (int i = 0; i < cycleAtoms.Count; i++)
        {
            AtomFunction a = cycleAtoms[i];
            if (a == null) continue;
            Vector3 w = a.transform.position;
            if (a == c2) w = targetC2;
            else if (a == c3) w = targetC3;
            finalWorldByAtom[a] = w;
        }

        AtomFunction cycleNeighborB = null;
        if (ReferenceEquals(nonGuideAtom, sourceAtom) && ringPath.Count >= 2)
            cycleNeighborB = ringPath[1];
        else if (ReferenceEquals(nonGuideAtom, targetAtom) && ringPath.Count >= 2)
            cycleNeighborB = ringPath[ringPath.Count - 2];
        if (cycleNeighborB == null)
            cycleNeighborB = c1;

        Vector3 centerAccum = Vector3.zero;
        int centerCount = 0;
        foreach (var kv in finalWorldByAtom)
        {
            centerAccum += kv.Value;
            centerCount++;
        }
        Vector3 cycleCenterWorld = centerCount > 0 ? centerAccum / centerCount : nonGuideAtom.transform.position;

        if (finalWorldByAtom.TryGetValue(nonGuideAtom, out Vector3 nonGuideFinal)
            && finalWorldByAtom.TryGetValue(guideAtom, out Vector3 guideFinal))
        {
            Vector3 leg = guideFinal - nonGuideFinal;
            if (leg.sqrMagnitude > 1e-12f)
                finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(leg.normalized).normalized;
        }

        cyclicContext = new OrbitalRedistribution.CyclicRedistributionContext
        {
            PivotAtom = nonGuideAtom,
            CycleNeighborA = guideAtom,
            CycleNeighborB = cycleNeighborB,
            FinalWorldByAtom = finalWorldByAtom,
            CycleCenterWorld = cycleCenterWorld,
            ChainRedistributionBlockedAtoms = new HashSet<AtomFunction>(finalWorldByAtom.Keys)
        };
        return true;
    }

    static Vector3 ComputePiPerpendicularGuideDirectionLocal(
        AtomFunction nonGuideAtom,
        AtomFunction guideAtom,
        ElectronOrbitalFunction nonGuideOp)
    {
        if (nonGuideAtom == null || guideAtom == null)
            return Vector3.zero;
        Vector3 sigmaAxisWorld = guideAtom.transform.position - nonGuideAtom.transform.position;
        if (sigmaAxisWorld.sqrMagnitude < 1e-12f)
            return Vector3.zero;
        sigmaAxisWorld.Normalize();

        Vector3 planeRefWorld = Vector3.zero;
        const float sigmaAxisMaxDeg = 18f;
        foreach (var cb in guideAtom.CovalentBonds)
        {
            if (cb == null || cb.Orbital == null || !cb.IsSigmaBondLine()) continue;
            AtomFunction other = cb.AtomA == guideAtom ? cb.AtomB : cb.AtomA;
            if (other == null || other == nonGuideAtom) continue;
            Vector3 dw = (cb.Orbital.transform.position - guideAtom.transform.position).normalized;
            Vector3 projected = Vector3.ProjectOnPlane(dw, sigmaAxisWorld);
            if (projected.sqrMagnitude < 1e-10f) continue;
            if (Vector3.Angle(dw, -sigmaAxisWorld) < sigmaAxisMaxDeg) continue;
            planeRefWorld = projected.normalized;
            break;
        }

        if (planeRefWorld.sqrMagnitude < 1e-12f)
        {
            foreach (var orb in guideAtom.BondedOrbitals)
            {
                if (orb == null || orb.Bond != null || orb.transform.parent != guideAtom.transform) continue;
                if (orb.ElectronCount <= 0) continue;
                Vector3 dw = (orb.transform.position - guideAtom.transform.position).normalized;
                Vector3 projected = Vector3.ProjectOnPlane(dw, sigmaAxisWorld);
                if (projected.sqrMagnitude < 1e-10f) continue;
                planeRefWorld = projected.normalized;
                break;
            }
        }

        if (planeRefWorld.sqrMagnitude < 1e-12f)
            planeRefWorld = Vector3.ProjectOnPlane(nonGuideAtom.transform.up, sigmaAxisWorld).normalized;
        if (planeRefWorld.sqrMagnitude < 1e-12f)
            planeRefWorld = Vector3.ProjectOnPlane(nonGuideAtom.transform.right, sigmaAxisWorld).normalized;
        if (planeRefWorld.sqrMagnitude < 1e-12f)
            return Vector3.zero;

        Vector3 normalWorld = Vector3.Cross(sigmaAxisWorld, planeRefWorld);
        if (normalWorld.sqrMagnitude < 1e-12f)
            return Vector3.zero;
        normalWorld.Normalize();

        if (nonGuideOp != null)
        {
            Vector3 opDir = (nonGuideOp.transform.position - nonGuideAtom.transform.position).normalized;
            if (opDir.sqrMagnitude > 1e-12f && Vector3.Dot(opDir, normalWorld) < 0f)
                normalWorld = -normalWorld;
        }

        Vector3 resultLocal = nonGuideAtom.transform.InverseTransformDirection(normalWorld).normalized;
        return resultLocal;
    }

    static void ClearPiPhase1RedistributeTemplatePreviewVisuals()
    {
        if (piPhase1DebugTemplatePreviewRoot != null)
        {
            UnityEngine.Object.Destroy(piPhase1DebugTemplatePreviewRoot);
            piPhase1DebugTemplatePreviewRoot = null;
        }
    }

    /// <summary>
    /// For cyclic π formation on an existing σ edge, move the operation-pair atoms (C2/C3)
    /// toward a shared plane during phase 1.
    /// Default plane is C1-C2-C3-C4 coplanarity; when a ring atom is already double-bonded,
    /// use that ring π-center local plane as the target.
    /// </summary>
    static Phase1ParallelTrack BuildPhase1PiCyclicCoplanarTrack(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        CovalentBond sigmaBetween,
        Vector3 predictedTargetC2,
        Vector3 predictedTargetC3)
    {
        if (sourceAtom == null || targetAtom == null || sigmaBetween == null || !sigmaBetween.IsSigmaBondLine())
            return null;
        if (!TryBuildShortestSigmaPath(sourceAtom, targetAtom, out var ringPath, sigmaBetween))
            return null;
        if (ringPath == null || ringPath.Count < 4)
            return null;

        AtomFunction c2 = sourceAtom;
        AtomFunction c3 = targetAtom;
        AtomFunction c1 = ringPath[1];
        AtomFunction c4 = ringPath[ringPath.Count - 2];
        if (c1 == null || c4 == null || c1 == c2 || c1 == c3 || c4 == c2 || c4 == c3 || c1 == c4)
            return null;
        var cycleAtoms = new List<AtomFunction>(ringPath.Count + 1) { sourceAtom };
        for (int i = 1; i < ringPath.Count; i++)
            cycleAtoms.Add(ringPath[i]);

        Vector3 startC2 = c2.transform.position;
        Vector3 startC3 = c3.transform.position;
        bool targetsBuilt = predictedTargetC2.sqrMagnitude > 1e-12f && predictedTargetC3.sqrMagnitude > 1e-12f;
        Vector3 targetC2 = targetsBuilt ? predictedTargetC2 : startC2;
        Vector3 targetC3 = targetsBuilt ? predictedTargetC3 : startC3;

        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s =>
            {
                if (!targetsBuilt)
                {
                    targetsBuilt = TryComputePiCyclicCoplanarTargets(
                        cycleAtoms,
                        c1.transform.position,
                        c2.transform.position,
                        c3.transform.position,
                        c4.transform.position,
                        out targetC2,
                        out targetC3);
                }
                if (!targetsBuilt)
                    return;

                c2.transform.position = Vector3.Lerp(startC2, targetC2, Mathf.Clamp01(s));
                c3.transform.position = Vector3.Lerp(startC3, targetC3, Mathf.Clamp01(s));
            },
            FinalizeAfterTimeline = () =>
            {
                if (!targetsBuilt)
                    return;
                c2.transform.position = targetC2;
                c3.transform.position = targetC3;
            }
        };
    }

    static bool TryComputePiCyclicCoplanarTargets(
        List<AtomFunction> cycleAtoms,
        Vector3 c1,
        Vector3 c2,
        Vector3 c3,
        Vector3 c4,
        out Vector3 outC2,
        out Vector3 outC3)
    {
        outC2 = c2;
        outC3 = c3;

        bool useExistingPiPlane = TryGetExistingRingPiPlane(
            cycleAtoms,
            out Vector3 planePoint,
            out Vector3 planeNormal);
        if (!useExistingPiPlane)
        {
            // Default fallback: flatten C1-C2-C3-C4 to one plane while moving C2/C3 together.
            // Build the plane through C1, C4, and the C2/C3 midpoint so projecting C2 and C3
            // sends them in opposite normal directions (symmetric coplanar settle).
            Vector3 mid23 = 0.5f * (c2 + c3);
            planePoint = c1;
            planeNormal = Vector3.Cross(c4 - c1, mid23 - c1);
            if (planeNormal.sqrMagnitude < 1e-12f)
                planeNormal = Vector3.Cross(c3 - c2, c4 - c1);
            if (planeNormal.sqrMagnitude < 1e-12f)
                planeNormal = Vector3.Cross(c2 - c1, c4 - c1);
            if (planeNormal.sqrMagnitude < 1e-12f)
                return false;
            planeNormal.Normalize();
        }

        outC2 = c2 - Vector3.Dot(c2 - planePoint, planeNormal) * planeNormal;
        outC3 = c3 - Vector3.Dot(c3 - planePoint, planeNormal) * planeNormal;
        return true;
    }

    static bool TryGetExistingRingPiPlane(
        List<AtomFunction> cycleAtoms,
        out Vector3 planePoint,
        out Vector3 planeNormal)
    {
        planePoint = Vector3.zero;
        planeNormal = Vector3.zero;
        if (cycleAtoms == null || cycleAtoms.Count < 4)
            return false;

        Vector3 cycleCenter = Vector3.zero;
        int centerCount = 0;
        for (int i = 0; i < cycleAtoms.Count; i++)
        {
            AtomFunction a = cycleAtoms[i];
            if (a == null) continue;
            cycleCenter += a.transform.position;
            centerCount++;
        }
        if (centerCount <= 0)
            return false;
        cycleCenter /= centerCount;

        int n = cycleAtoms.Count;
        for (int i = 0; i < n; i++)
        {
            AtomFunction center = cycleAtoms[i];
            if (center == null || center.AtomicNumber != 6 || center.GetPiBondCount() <= 0)
                continue;

            AtomFunction prev = cycleAtoms[(i - 1 + n) % n];
            AtomFunction next = cycleAtoms[(i + 1) % n];
            if (prev == null || next == null)
                continue;
            int bondToPrev = center.GetBondsTo(prev);
            int bondToNext = center.GetBondsTo(next);
            AtomFunction doubleBondPartner = null;
            if (bondToPrev > 1) doubleBondPartner = prev;
            else if (bondToNext > 1) doubleBondPartner = next;
            if (doubleBondPartner == null)
                continue;

            Vector3 p = center.transform.position;
            // Plane from (pi center atom, its ring double-bond partner, cycle center).
            Vector3 toPartner = doubleBondPartner.transform.position - p;
            Vector3 toCycleCenter = cycleCenter - p;
            Vector3 nrm = Vector3.Cross(toPartner, toCycleCenter);
            if (nrm.sqrMagnitude < 1e-12f)
                continue;

            planePoint = p;
            planeNormal = nrm.normalized;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds π phase-1 cylinder finalize track and prepares from runtime OP pose at phase end,
    /// so cylinder targets use redistributed operation-orbital world positions.
    /// </summary>
    static Phase1ParallelTrack BuildPhase1PiCylinderTrackFromOpFinalPose(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        CovalentBond sigmaBetween,
        ICollection<AtomFunction> molForBondLines)
    {
        if (sigmaBetween == null || sourceOrbital == null || targetOrbital == null)
            return null;

        bool initialized = false;
        (Vector3 pos, Quaternion rot) sourceStart = default;
        (Vector3 pos, Quaternion rot) targetStart = default;
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s =>
            {
                if (!ElectronOrbitalFunction.TryPreparePiPhase1CylinderFromSigmaBond(
                    sourceAtom,
                    targetAtom,
                    sourceOrbital,
                    targetOrbital,
                    sigmaBetween,
                    out var cylPrepared))
                    return;

                if (!initialized)
                {
                    initialized = true;
                    sourceStart = cylPrepared.SourceOrbStart;
                    targetStart = cylPrepared.TargetOrbStart;
                }

                // Keep a stable interpolation start through phase-1 while recomputing moving bond targets per frame.
                cylPrepared.SourceOrbStart = sourceStart;
                cylPrepared.TargetOrbStart = targetStart;
                ElectronOrbitalFunction.ApplyBondFormationCylinderPoseForSmoothStep(
                    cylPrepared, sourceOrbital, targetOrbital, s);
                if (molForBondLines != null && molForBondLines.Count > 0)
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
            },
            FinalizeAfterTimeline = () =>
            {
                if (!ElectronOrbitalFunction.TryPreparePiPhase1CylinderFromSigmaBond(
                    sourceAtom,
                    targetAtom,
                    sourceOrbital,
                    targetOrbital,
                    sigmaBetween,
                    out var cylPrepared))
                    return;

                ElectronOrbitalFunction.FinalizeBondFormationCylinderPose(
                    cylPrepared,
                    sourceAtom,
                    sourceOrbital,
                    targetOrbital,
                    sigmaBetween,
                    ElectronOrbitalFunction.BondFormationCylinderFinalizeMode.ApplyWorldTargetsOnly,
                    molForBondLines);
            }
        };
    }

    /// <summary>
    /// Instant σ/π-style guide resolution failed: run legacy π creation + animation (no phase 1 / 3 timeline).
    /// </summary>
    IEnumerator CoOrbitalDragPiFormationLegacyFallback(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        bool animate)
    {
        if (sourceAtom == null || targetAtom == null || sourceOrbital == null || targetOrbital == null)
            yield break;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        sourceAtom.RemovePiPOrbitalDirectionsForPartnerLine(targetAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
        targetAtom.RemovePiPOrbitalDirectionsForPartnerLine(sourceAtom, AtomFunction.PiPOrbitalPrebondLineIndex);

        int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
        AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
            sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: animate);
        if (bond == null)
            yield break;
        bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

        sourceOrbital.transform.SetParent(null, worldPositionStays: true);
        bond.SetOrbitalBeingFaded(sourceOrbital);
        sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        var mol = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) if (a != null) mol.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) if (a != null) mol.Add(a);

        float cylDur = animate ? -1f : 0f;
        float lineDur = animate ? sourceOrbital.BondFormationOrbitalToLineDurationResolved : 0f;
        yield return StartCoroutine(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
            sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, cylDur, mol));

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(lineDur, sourceOrbital, mol);
            targetOrbital.ElectronCount = mergedElectrons;
        }
        else
        {
            Destroy(sourceOrbital.gameObject);
        }

        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        if (bond != null)
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "afterPiBond", ecnPiEvent, bond, "pi");
    }

    void RunOrbitalDragPiFormationLegacyFallbackImmediate(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital)
    {
        if (sourceAtom == null || targetAtom == null || sourceOrbital == null || targetOrbital == null)
            return;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        sourceAtom.RemovePiPOrbitalDirectionsForPartnerLine(targetAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
        targetAtom.RemovePiPOrbitalDirectionsForPartnerLine(sourceAtom, AtomFunction.PiPOrbitalPrebondLineIndex);

        int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
        AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
            sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: false);
        if (bond == null)
            return;
        bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

        sourceOrbital.transform.SetParent(null, worldPositionStays: true);
        bond.SetOrbitalBeingFaded(sourceOrbital);
        sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();

        var mol = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) if (a != null) mol.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) if (a != null) mol.Add(a);

        DrainCoroutineToCompletion(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
            sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, 0f, mol));
        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            DrainCoroutineToCompletion(bond.AnimateOrbitalToLine(0f, sourceOrbital, mol));
            targetOrbital.ElectronCount = mergedElectrons;
        }
        else
        {
            Destroy(sourceOrbital.gameObject);
        }

        sourceAtom.RefreshCharge();
        targetAtom.RefreshCharge();
        if (bond != null)
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "afterPiBond", ecnPiEvent, bond, "pi");
    }

    IEnumerator CoOrbitalDragPiFormationThreePhase(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital,
        bool animate)
    {
        Vector3 sourceStartAll = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStartAll = targetAtom != null ? targetAtom.transform.position : Vector3.zero;
        var timingOrb = TimingSourceOrbital(sourceOrbital, targetOrbital, redistributionGuideTieBreakDraggedOrbital);
        float phase1Sec = timingOrb != null ? timingOrb.SigmaFormationPhase1PrebondSeconds : 1f;
        float phase2CylinderSec = timingOrb != null ? timingOrb.SigmaFormationPhase2CylinderSecondsResolved : 0.55f;
        float phase2OrbitalToLineSec = timingOrb != null ? timingOrb.SigmaFormationPhase2OrbitalToLineSecondsResolved : 0.45f;
        float phase3Sec = timingOrb != null ? timingOrb.SigmaFormationPhase3PostbondGuideSeconds : 1f;

        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) if (a != null) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) if (a != null) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        var molForBondLines = new List<AtomFunction>(atomsToBlock.Count);
        foreach (var a in atomsToBlock) if (a != null) molForBondLines.Add(a);

        try
        {
            EnsureEditModeManagerReference();
            if (animate)
            {
                var draggedLobeToRestore = redistributionGuideTieBreakDraggedOrbital != null
                    ? redistributionGuideTieBreakDraggedOrbital
                    : sourceOrbital;
                if (draggedLobeToRestore != null)
                    draggedLobeToRestore.SnapToOriginal();
                yield return null;
            }

            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : sourceOrbital;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(sourceAtom, targetAtom, guideOrb, out var guide, out var nonGuide);
            if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide))
            {
                yield return StartCoroutine(CoOrbitalDragPiFormationLegacyFallback(
                    sourceAtom, targetAtom, sourceOrbital, targetOrbital, animate));
                yield break;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == sourceAtom ? sourceOrbital : targetOrbital;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == sourceAtom ? sourceOrbital : targetOrbital;

            var guidePhase1Decision = new PiPhase1GuideRedistributionDecision();
            yield return StartCoroutine(CoOrbitalDragPiPhase1RedistributionOnly(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                animate ? phase1Sec : 0f,
                molForBondLines,
                buildPhase1TemplatePreviews: animate,
                guideDecision: guidePhase1Decision));
            
            int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
            int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

            sourceAtom.UnbondOrbital(sourceOrbital);
            targetAtom.UnbondOrbital(targetOrbital);

            var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: animate);
            if (bond == null)
                yield break;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            sourceOrbital.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(sourceOrbital);
            sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            sourceAtom.RefreshCharge();
            targetAtom.RefreshCharge();

            float tCyl = Mathf.Max(0f, animate ? phase2CylinderSec : 0f);
            float tLine = Mathf.Max(0f, animate ? phase2OrbitalToLineSec : 0f);

            yield return StartCoroutine(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, tCyl, molForBondLines));

            if (bond != null)
            {
                bond.animatingOrbitalToBondPosition = false;
                float lineDur = !animate ? 0f : (tLine > 1e-6f ? tLine : sourceOrbital.BondFormationOrbitalToLineDurationResolved);
                yield return bond.AnimateOrbitalToLine(lineDur, sourceOrbital, molForBondLines);
                targetOrbital.ElectronCount = mergedElectrons;
            }
            else
            {
                Destroy(sourceOrbital.gameObject);
            }

            sourceAtom.RefreshCharge();
            targetAtom.RefreshCharge();
            if (bond != null)
                AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                    sourceAtom, targetAtom, "afterPiBond", ecnPiEvent, bond, "pi");

            if (guide.AtomicNumber > 1
                && phase3Sec > 1e-5f
                && bond != null
                && !guidePhase1Decision.GuideRedistributedInPhase1)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
                {
                    if (animate)
                    {
                        float t3 = 0f;
                        while (t3 < phase3Sec)
                        {
                            t3 += Time.deltaTime;
                            float s3 = Mathf.Clamp01(t3 / phase3Sec);
                            float smooth3 = s3 * s3 * (3f - 2f * s3);
                            phase3GuideRedistribution.Apply(smooth3);
                            AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                            yield return null;
                        }
                    }

                    phase3GuideRedistribution.Apply(1f);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }
            }

            editModeManager.FinishSigmaBondInstantTail(
                sourceAtom,
                targetAtom,
                skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
        }
        finally
        {
            OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
            sourceAtom?.RemovePiPOrbitalDirectionsForPartnerLine(targetAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
            targetAtom?.RemovePiPOrbitalDirectionsForPartnerLine(sourceAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
            editModeManager?.RefreshSelectedMoleculeAfterBondChange();
        }
    }

    bool RunOrbitalDragPiFormationThreePhaseImmediate(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        Vector3 sourceStartAll = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStartAll = targetAtom != null ? targetAtom.transform.position : Vector3.zero;
        var timingOrb = TimingSourceOrbital(sourceOrbital, targetOrbital, redistributionGuideTieBreakDraggedOrbital);
        float phase3Sec = timingOrb != null ? timingOrb.SigmaFormationPhase3PostbondGuideSeconds : 1f;

        var atomsToBlock = new HashSet<AtomFunction>();
        foreach (var a in sourceAtom.GetConnectedMolecule()) if (a != null) atomsToBlock.Add(a);
        foreach (var a in targetAtom.GetConnectedMolecule()) if (a != null) atomsToBlock.Add(a);
        foreach (var a in atomsToBlock) a.SetInteractionBlocked(true);

        var molForBondLines = new List<AtomFunction>(atomsToBlock.Count);
        foreach (var a in atomsToBlock) if (a != null) molForBondLines.Add(a);
        try
        {
            EnsureEditModeManagerReference();
            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : sourceOrbital;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(sourceAtom, targetAtom, guideOrb, out var guide, out var nonGuide);
            if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide))
            {
                RunOrbitalDragPiFormationLegacyFallbackImmediate(sourceAtom, targetAtom, sourceOrbital, targetOrbital);
                return true;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == sourceAtom ? sourceOrbital : targetOrbital;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == sourceAtom ? sourceOrbital : targetOrbital;

            bool guideRedistributedInPhase1 = RunOrbitalDragPiPhase1RedistributionSynchronously(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                molForBondLines);

            int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
            int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

            sourceAtom.UnbondOrbital(sourceOrbital);
            targetAtom.UnbondOrbital(targetOrbital);

            var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: false);
            if (bond == null)
                return false;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            sourceOrbital.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(sourceOrbital);
            sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            sourceAtom.RefreshCharge();
            targetAtom.RefreshCharge();

            DrainCoroutineToCompletion(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, 0f, molForBondLines));
            if (bond != null)
            {
                bond.animatingOrbitalToBondPosition = false;
                DrainCoroutineToCompletion(bond.AnimateOrbitalToLine(0f, sourceOrbital, molForBondLines));
                targetOrbital.ElectronCount = mergedElectrons;
            }
            else
            {
                Destroy(sourceOrbital.gameObject);
            }

            sourceAtom.RefreshCharge();
            targetAtom.RefreshCharge();
            if (bond != null)
                AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                    sourceAtom, targetAtom, "afterPiBond", ecnPiEvent, bond, "pi");

            if (guide.AtomicNumber > 1
                && phase3Sec > 1e-5f
                && bond != null
                && !guideRedistributedInPhase1)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
                {
                    phase3GuideRedistribution.Apply(1f);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }

            }

            editModeManager?.FinishSigmaBondInstantTail(
                sourceAtom,
                targetAtom,
                skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
            return true;
        }
        finally
        {
            OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
            sourceAtom?.RemovePiPOrbitalDirectionsForPartnerLine(targetAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
            targetAtom?.RemovePiPOrbitalDirectionsForPartnerLine(sourceAtom, AtomFunction.PiPOrbitalPrebondLineIndex);
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
            editModeManager?.RefreshSelectedMoleculeAfterBondChange();
        }
    }


}
