using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// σ / π formation from <b>orbital drag</b> or the same pipeline <b>without animation</b> (<see cref="TryBeginOrbitalDragSigmaFormation"/> /
/// <see cref="TryBeginOrbitalDragPiFormation"/> with <c>animate: false</c>): σ runs phase 1 pre-bond (non-guide fragment or cyclic targets),
/// then bond formation (cylinder + orbital→line), then post-bond guide hybrid lerp. π uses the same three phases with
/// <b>no</b> rigid fragment translation in phase 1 (σ already links the pair).
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

    void Awake()
    {
        if (editModeManager == null)
            editModeManager = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
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
        bool allowSteppedDebug,
        bool dualAcyclicPhase1RedistributionOnBothOps)
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
            disablePhase1Redistribution,
            dualAcyclicPhase1RedistributionOnBothOps);

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
        if (dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null)
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, true);
        try
        {
            yield return StartCoroutine(
                CoExecutePhase1ParallelAnimations(tracks, mol, dur, null, null));
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
            if (dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null)
                SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, false);
        }
    }

    /// <summary>σ phase 1 with no coroutine yields (for <see cref="RunOrbitalDragSigmaFormationThreePhaseImmediate"/>).</summary>
    static void RunOrbitalDragSigmaPhase1PrebondSynchronously(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        float phase1Sec,
        bool disableCyclicSigmaPhase1RedistributionDebug,
        bool dualAcyclicPhase1RedistributionOnBothOps)
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
            disablePhase1Redistribution,
            dualAcyclicPhase1RedistributionOnBothOps);

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        if (dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null)
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, true);
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
            if (dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null)
                SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(guide, false);
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

    /// <summary>
    /// Non-mutating peek at <see cref="AtomFunction.GetLoneOrbitalForBondFormation"/> selection (1e preferred, else best 2e by dot)
    /// so we can branch dual phase-1 without changing electron counts.
    /// </summary>
    /// <summary>
    /// World stem from nucleus to OP mesh center, expressed in <paramref name="atom"/> local (for phase-1 guide-axis / template alignment).
    /// </summary>
    static bool TryBondFormationOpStemInAtomLocal(
        AtomFunction atom,
        ElectronOrbitalFunction op,
        out Vector3 dirLocal)
    {
        dirLocal = Vector3.zero;
        if (atom == null || op == null) return false;
        Vector3 w = op.transform.position - atom.transform.position;
        if (w.sqrMagnitude < 1e-14f) return false;
        dirLocal = atom.transform.InverseTransformDirection(w.normalized).normalized;
        return true;
    }

    static ElectronOrbitalFunction PeekBondFormationLoneAlong(AtomFunction atom, Vector3 preferredDirectionWorld)
    {
        if (atom == null) return null;
        var one = atom.GetLoneOrbitalWithOneElectron(preferredDirectionWorld);
        if (one != null) return one;
        Vector3 dirNorm = preferredDirectionWorld.sqrMagnitude >= 0.01f ? preferredDirectionWorld.normalized : Vector3.right;
        ElectronOrbitalFunction best = null;
        float bestDot = -2f;
        foreach (var orb in atom.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 2) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), dirNorm);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = orb;
            }
        }
        return best;
    }

    /// <summary>
    /// When both forming σ lobes are each nucleus’s bond-formation pick along the internuclear axis, run acyclic phase-1
    /// redistribution on <b>both</b> centers (guide as pivot in the second track) and skip legacy phase-3 guide-only lerp.
    /// Disabled when a cyclic σ phase-1 context applies (pivot semantics differ).
    /// </summary>
    static bool ShouldDualAtomAcyclicPhase1PrebondForFormingSigmaOps(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp)
    {
        if (guide == null || nonGuide == null || guideOp == null || nonGuideOp == null) return false;
        Vector3 w = nonGuide.transform.position - guide.transform.position;
        if (w.sqrMagnitude < 1e-14f) return false;
        var peekGuide = PeekBondFormationLoneAlong(guide, w);
        var peekNon = PeekBondFormationLoneAlong(nonGuide, -w);
        bool result = peekGuide != null && peekNon != null
            && ReferenceEquals(peekGuide, guideOp) && ReferenceEquals(peekNon, nonGuideOp);
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H_peek",
            "SigmaBondFormation.ShouldDualAtomAcyclicPhase1PrebondForFormingSigmaOps",
            "dual_phase1_gate",
            "{"
            + "\"result\":" + (result ? "1" : "0")
            + ",\"guideId\":" + guide.GetInstanceID().ToString()
            + ",\"nonGuideId\":" + nonGuide.GetInstanceID().ToString()
            + ",\"guideOpId\":" + guideOp.GetInstanceID().ToString()
            + ",\"nonGuideOpId\":" + nonGuideOp.GetInstanceID().ToString()
            + ",\"peekGuideId\":" + (peekGuide != null ? peekGuide.GetInstanceID().ToString() : "0")
            + ",\"peekNonId\":" + (peekNon != null ? peekNon.GetInstanceID().ToString() : "0")
            + ",\"eqGuidePeek\":" + (peekGuide != null && ReferenceEquals(peekGuide, guideOp) ? "1" : "0")
            + ",\"eqNonPeek\":" + (peekNon != null && ReferenceEquals(peekNon, nonGuideOp) ? "1" : "0")
            + ",\"wSqr\":" + ProjectAgentDebugLog.JsonFloatInvariant(w.sqrMagnitude)
            + "}",
            "pre-fix");
        // #endregion
        return result;
    }

    /// <summary>
    /// π orbital-drag dual phase-1: do <b>not</b> reuse <see cref="ShouldDualAtomAcyclicPhase1PrebondForFormingSigmaOps"/> — that uses
    /// internuclear <see cref="PeekBondFormationLoneAlong"/>, which favors σ-axis lobes; π forming lobes lie ⟂ σ so peek mismatches
    /// one end (runtime: <c>eqNonPeek=0</c> while forming <c>nonGuideOp</c> is the p lobe).
    /// When acyclic, accept both forming OPs as long as they are still lone shells on their nuclei (prebond, no bond yet).
    /// </summary>
    static bool ShouldDualAtomAcyclicPhase1PrebondForPiFormingOps(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp)
    {
        bool result = guide != null && nonGuide != null && guideOp != null && nonGuideOp != null
            && guideOp.Bond == null
            && nonGuideOp.Bond == null
            && guideOp.transform.parent == guide.transform
            && nonGuideOp.transform.parent == nonGuide.transform;
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H_pi_gate",
            "SigmaBondFormation.ShouldDualAtomAcyclicPhase1PrebondForPiFormingOps",
            "dual_pi_gate",
            "{\"result\":" + (result ? "1" : "0") + "}",
            "post-fix");
        // #endregion
        return result;
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
        bool disablePhase1Redistribution,
        bool dualAcyclicPhase1RedistributionOnBothOps)
    {
        int extraRedist = !disablePhase1Redistribution && dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null ? 1 : 0;
        // #region agent log
        if (extraRedist > 0)
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_track",
                "SigmaBondFormation.BuildPhase1ParallelAnimationList",
                "sigma_phase1_second_redist_track_added",
                "{\"extraRedist\":1}",
                "pre-fix");
        // #endregion
        var list = new List<Phase1ParallelTrack>(1 + (disablePhase1Redistribution ? 0 : 1 + extraRedist));
        list.Add(BuildPhase1AtomFragmentApproachAnimation(
            nonGuide, initialWorld, toMove, deltaTotal, nTarget, molForBondLines, cyclicContext));
        if (!disablePhase1Redistribution)
        {
            list.Add(BuildPhase1OrbitalRedistributeForSigmaFormationPhase1(
                guide, nonGuide, guideOp, nonGuideOp, cyclicContext));
            if (dualAcyclicPhase1RedistributionOnBothOps && cyclicContext == null)
                list.Add(BuildPhase1OrbitalRedistributeForSigmaFormationPhase1GuidePivotPrebond(
                    guide, nonGuide, guideOp, nonGuideOp));
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
            // (and mis-trigger OP preassign steal). See H12 vs H6 in cyclic NDJSON.
            Vector3 pivotFinal = finalWorldByAtom.TryGetValue(nonGuideAtom, out Vector3 pFin) ? pFin : nonGuideAtom.transform.position;
            Vector3 guideFinal = finalWorldByAtom.TryGetValue(guideAtom, out Vector3 gFin) ? gFin : guideAtom.transform.position;
            Vector3 guideLegWorld = guideFinal - pivotFinal;
            if (guideLegWorld.sqrMagnitude > 1e-12f)
            {
                finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(guideLegWorld.normalized).normalized;
            }
        }
        else if (guideAtom != null && guideOp != null && nonGuideAtom != null && nonGuideOp != null)
        {
            // Acyclic σ on non-guide pivot: template guide-axis must anti-align with the partner’s forming OP stem in world
            // (each lobe points into the bond gap; local guide-dir matches opposite of guide’s OP axis).
            if (!TryBondFormationOpStemInAtomLocal(guideAtom, guideOp, out Vector3 guideStemLocal))
                finalDirectionForGuideOrbital = Vector3.zero;
            else
            {
                Vector3 guideStemW = guideAtom.transform.TransformDirection(guideStemLocal.normalized);
                if (guideStemW.sqrMagnitude < 1e-14f)
                    finalDirectionForGuideOrbital = Vector3.zero;
                else
                    finalDirectionForGuideOrbital = nonGuideAtom.transform.InverseTransformDirection(-guideStemW.normalized).normalized;
            }
        }

        // #region agent log
        if (guideAtom != null && nonGuideAtom != null && guideOp != null && nonGuideOp != null)
        {
            Vector3 stemW = nonGuideOp.transform.position - nonGuideAtom.transform.position;
            Vector3 toGuideOpW = guideOp.transform.position - nonGuideAtom.transform.position;
            Vector3 gStemW = guideOp.transform.position - guideAtom.transform.position;
            float stemS = stemW.sqrMagnitude;
            float toGS = toGuideOpW.sqrMagnitude;
            float gStemS = gStemW.sqrMagnitude;
            float dotStemToGuideOp = stemS > 1e-12f && toGS > 1e-12f
                ? Vector3.Dot(stemW.normalized, toGuideOpW.normalized)
                : -2f;
            float dotProvToGuideOp = -2f;
            float dotProvAntiGuideStem = -2f;
            if (finalDirectionForGuideOrbital.sqrMagnitude > 1e-12f && toGS > 1e-12f)
            {
                Vector3 provW = nonGuideAtom.transform.TransformDirection(finalDirectionForGuideOrbital.normalized);
                if (provW.sqrMagnitude > 1e-12f)
                {
                    dotProvToGuideOp = Vector3.Dot(provW.normalized, toGuideOpW.normalized);
                    if (gStemS > 1e-12f)
                        dotProvAntiGuideStem = Vector3.Dot(provW.normalized, (-gStemW.normalized));
                }
            }
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_sigma_nguide_toward_guideop",
                "SigmaBondFormation.BuildPhase1OrbitalRedistributeForSigmaFormationPhase1",
                "sigma_p1_nonGuide_provided_dir_vs_guideop",
                "{"
                + "\"cyclicCtx\":" + (redistCycleContext != null ? "1" : "0")
                + ",\"dotStemToGuideOp\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotStemToGuideOp)
                + ",\"dotProvToGuideOp\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotProvToGuideOp)
                + ",\"dotProvAntiGuideStem\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotProvAntiGuideStem)
                + ",\"pivotId\":" + nonGuideAtom.GetInstanceID().ToString()
                + ",\"partnerId\":" + guideAtom.GetInstanceID().ToString()
                + "}",
                "post-fix");
        }
        // #endregion

        var animation = OrbitalRedistribution.BuildOrbitalRedistribution(
            nonGuideAtom,
            guideAtom,
            guideOp,
            nonGuideOp,
            guideOrbitalPredetermined: null,
            finalDirectionForGuideOrbital,
            isBondingEvent: true,
            cyclicContext: redistCycleContext);
        Debug.Log("[σ-p1-redist] phase1OrbitalRedistTrack guide=" + guideAtom.GetInstanceID() + " nonGuide=" + nonGuideAtom.GetInstanceID()
            + " cyclicCtx=" + (redistCycleContext != null ? "1" : "0")
            + " finalGuideDirMag2=" + finalDirectionForGuideOrbital.sqrMagnitude.ToString("G4"));
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-115e1e.log",
            "115e1e",
            "H_phase1_track",
            "SigmaBondFormation.BuildPhase1OrbitalRedistributeForSigmaFormationPhase1",
            "phase1OrbitalRedistTrack",
            "{"
            + "\"guideId\":" + guideAtom.GetInstanceID().ToString()
            + ",\"nonGuideId\":" + nonGuideAtom.GetInstanceID().ToString()
            + ",\"cyclicCtx\":" + (redistCycleContext != null ? "1" : "0")
            + ",\"finalGuideDirMag2\":" + ProjectAgentDebugLog.JsonFloatInvariant(finalDirectionForGuideOrbital.sqrMagnitude)
            + "}",
            "pre-fix");
        // #endregion
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => animation?.Apply(s),
            FinalizeAfterTimeline = () => { }
        };
    }

    /// <summary>
    /// Second σ phase-1 redistribution lane: <paramref name="guide"/> as predictive pivot (prebond), partner’s forming lobe as guide ref — mirrors
    /// <see cref="BuildPhase1OrbitalRedistributeForSigmaFormationPhase1"/> with roles swapped. Acyclic only; no cyclic context.
    /// </summary>
    static Phase1ParallelTrack BuildPhase1OrbitalRedistributeForSigmaFormationPhase1GuidePivotPrebond(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp)
    {
        Vector3 finalDirectionForGuideOrbital = Vector3.zero;
        // Second lane pivots on guide: guide-dir must anti-align with the partner’s forming σ stem in world.
        if (nonGuide != null && nonGuideOp != null
            && TryBondFormationOpStemInAtomLocal(nonGuide, nonGuideOp, out Vector3 nonStemLocal))
        {
            Vector3 nonStemW = nonGuide.transform.TransformDirection(nonStemLocal.normalized);
            if (nonStemW.sqrMagnitude >= 1e-14f)
                finalDirectionForGuideOrbital = guide.transform.InverseTransformDirection(-nonStemW.normalized).normalized;
        }

        var animation = OrbitalRedistribution.BuildOrbitalRedistribution(
            guide,
            nonGuide,
            nonGuideOp,
            guideOp,
            guideOrbitalPredetermined: null,
            finalDirectionForGuideOrbital,
            isBondingEvent: true,
            cyclicContext: null);
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => animation?.Apply(s),
            FinalizeAfterTimeline = () => { }
        };
    }

    /// <summary>
    /// π phase 1: same redistribution as σ phase 1, but <see cref="OrbitalRedistribution.BuildOrbitalRedistribution"/> guide axis uses
    /// pivot nucleus → σ bond partner (guide atom), i.e. the internuclear leg the OP π bond forms across, in <paramref name="nonGuideAtom"/> local.
    /// Acyclic π uses a single redistribution build with pivot <paramref name="nonGuideAtom"/> (not a second guide-pivot lane).
    /// </summary>
    static Phase1ParallelTrack BuildPhase1OrbitalRedistributeForPiFormationPhase1(
        AtomFunction guideAtom,
        AtomFunction nonGuideAtom,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        OrbitalRedistribution.CyclicRedistributionContext cyclicContext,
        Vector3 finalDirectionForGuideOrbital)
    {
        // #region agent log
        if (guideAtom != null && nonGuideAtom != null && guideOp != null && nonGuideOp != null)
        {
            Vector3 stemW = nonGuideOp.transform.position - nonGuideAtom.transform.position;
            Vector3 toGuideOpW = guideOp.transform.position - nonGuideAtom.transform.position;
            float stemS = stemW.sqrMagnitude;
            float toGS = toGuideOpW.sqrMagnitude;
            float dotStemToGuideOp = stemS > 1e-12f && toGS > 1e-12f
                ? Vector3.Dot(stemW.normalized, toGuideOpW.normalized)
                : -2f;
            float dotProvToGuideOp = -2f;
            if (finalDirectionForGuideOrbital.sqrMagnitude > 1e-12f && toGS > 1e-12f)
            {
                Vector3 provW = nonGuideAtom.transform.TransformDirection(finalDirectionForGuideOrbital.normalized);
                if (provW.sqrMagnitude > 1e-12f)
                    dotProvToGuideOp = Vector3.Dot(provW.normalized, toGuideOpW.normalized);
            }
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_pi_nguide_toward_guideop",
                "SigmaBondFormation.BuildPhase1OrbitalRedistributeForPiFormationPhase1",
                "pi_p1_nonGuide_provided_dir_vs_guideop",
                "{"
                + "\"cyclicCtx\":" + (cyclicContext != null ? "1" : "0")
                + ",\"dotStemToGuideOp\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotStemToGuideOp)
                + ",\"dotProvToGuideOp\":" + ProjectAgentDebugLog.JsonFloatInvariant(dotProvToGuideOp)
                + ",\"pivotId\":" + nonGuideAtom.GetInstanceID().ToString()
                + ",\"partnerId\":" + guideAtom.GetInstanceID().ToString()
                + "}",
                "debug-probe");
        }
        // #endregion

        var animation = OrbitalRedistribution.BuildOrbitalRedistribution(
            nonGuideAtom,
            guideAtom,
            guideOp,
            nonGuideOp,
            guideOrbitalPredetermined: null,
            finalDirectionForGuideOrbital: finalDirectionForGuideOrbital,
            isBondingEvent: true,
            cyclicContext: cyclicContext,
            formingPiPrecursorAlignTrigonalPartnerPlane: true);
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => animation?.Apply(s),
            FinalizeAfterTimeline = () => { }
        };
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
    /// <param name="debugPiPhase1SourceAtom">When non-null with <paramref name="debugPiPhase1TargetAtom"/>, logs per-track nucleus deltas on the last timeline frame (π phase-1 triage only).</param>
    static IEnumerator CoExecutePhase1ParallelAnimations(
        IReadOnlyList<Phase1ParallelTrack> tracks,
        ICollection<AtomFunction> molForBondLines,
        float dur,
        AtomFunction debugPiPhase1SourceAtom = null,
        AtomFunction debugPiPhase1TargetAtom = null)
    {
        if (tracks == null || tracks.Count == 0)
            yield break;

        if (dur < 1e-5f)
        {
            ExecutePhase1ParallelAnimationsImmediate(tracks, molForBondLines, dur);
            yield break;
        }

        float t = 0f;
        bool piParallelTrackMotionLogged = false;
        while (true)
        {
            float u = Mathf.Clamp01(t / dur);
            float s = u * u * (3f - 2f * u);
            bool lastFrame = u >= 1f - 1e-6f;
            if (lastFrame
                && debugPiPhase1SourceAtom != null
                && debugPiPhase1TargetAtom != null
                && !piParallelTrackMotionLogged)
            {
                piParallelTrackMotionLogged = true;
                for (int i = 0; i < tracks.Count; i++)
                {
                    Vector3 srcBefore = debugPiPhase1SourceAtom.transform.position;
                    Vector3 tgtBefore = debugPiPhase1TargetAtom.transform.position;
                    tracks[i].ApplySmoothStep?.Invoke(s);
                    float dSrc = Vector3.Distance(srcBefore, debugPiPhase1SourceAtom.transform.position);
                    float dTgt = Vector3.Distance(tgtBefore, debugPiPhase1TargetAtom.transform.position);
                    // #region agent log
                    ProjectAgentDebugLog.AppendDebugModeNdjson(
                        "debug-446955.log",
                        "446955",
                        "H_pi_track_step",
                        "SigmaBondFormation.CoExecutePhase1ParallelAnimations",
                        "pi_phase1_parallel_track_nucleus_step_at_end",
                        "{"
                        + "\"trackIdx\":" + i.ToString()
                        + ",\"trackCount\":" + tracks.Count.ToString()
                        + ",\"dSrc\":" + ProjectAgentDebugLog.JsonFloatInvariant(dSrc)
                        + ",\"dTgt\":" + ProjectAgentDebugLog.JsonFloatInvariant(dTgt)
                        + ",\"s\":" + ProjectAgentDebugLog.JsonFloatInvariant(s)
                        + "}",
                        "debug-probe");
                    // #endregion
                }
            }
            else
            {
                for (int i = 0; i < tracks.Count; i++)
                    tracks[i].ApplySmoothStep?.Invoke(s);
            }

            if (molForBondLines != null && molForBondLines.Count > 0)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
            yield return null;
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
            float blDualProbe = DefaultSigmaBondLengthForPair(guide, nonGuide);
            var cyclicCtxForDualProbe = TryBuildCyclicPhase1Context(guide, nonGuide, blDualProbe);
            bool dualAcyclicSigmaPhase1BothOps = cyclicCtxForDualProbe == null
                && ShouldDualAtomAcyclicPhase1PrebondForFormingSigmaOps(
                    guide, nonGuide, guideOpPhase1, nonGuideOpPhase1);
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_sigma_ctx",
                "SigmaBondFormation.CoOrbitalDragSigmaFormationThreePhase",
                "sigma_dual_and_cyclic_probe",
                "{"
                + "\"dualAcyclicSigmaPhase1BothOps\":" + (dualAcyclicSigmaPhase1BothOps ? "1" : "0")
                + ",\"cyclicCtxProbeNull\":" + (cyclicCtxForDualProbe == null ? "1" : "0")
                + ",\"prebondCycleCandidate\":" + (prebondCycleCandidate ? "1" : "0")
                + "}",
                "pre-fix");
            // #endregion
            yield return StartCoroutine(CoOrbitalDragSigmaPhase1Prebond(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                animate ? phase1Sec : 0f,
                allowSteppedDebug: animate,
                dualAcyclicSigmaPhase1BothOps));

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
            // Omit when acyclic dual phase-1 already redistributed the guide nucleus predictively with the non-guide OP.
            bool sigmaPhase3WillRun = guide.AtomicNumber > 1 && phase3Sec > 1e-5f && !prebondCycleCandidate && !dualAcyclicSigmaPhase1BothOps;
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_sigma_p3",
                "SigmaBondFormation.CoOrbitalDragSigmaFormationThreePhase",
                "sigma_phase3_branch",
                "{"
                + "\"sigmaPhase3WillRun\":" + (sigmaPhase3WillRun ? "1" : "0")
                + ",\"dualAcyclicSigmaPhase1BothOps\":" + (dualAcyclicSigmaPhase1BothOps ? "1" : "0")
                + ",\"prebondCycleCandidate\":" + (prebondCycleCandidate ? "1" : "0")
                + "}",
                "pre-fix");
            // #endregion
            if (sigmaPhase3WillRun)
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
            float blDualProbeImm = DefaultSigmaBondLengthForPair(guide, nonGuide);
            var cyclicCtxForDualProbeImm = TryBuildCyclicPhase1Context(guide, nonGuide, blDualProbeImm);
            bool dualAcyclicSigmaPhase1BothOpsImm = cyclicCtxForDualProbeImm == null
                && ShouldDualAtomAcyclicPhase1PrebondForFormingSigmaOps(
                    guide, nonGuide, guideOpPhase1, nonGuideOpPhase1);
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_sigma_imm",
                "SigmaBondFormation.RunOrbitalDragSigmaFormationThreePhaseImmediate",
                "sigma_dual_immediate",
                "{"
                + "\"dualAcyclicSigmaPhase1BothOpsImm\":" + (dualAcyclicSigmaPhase1BothOpsImm ? "1" : "0")
                + ",\"cyclicCtxProbeImmNull\":" + (cyclicCtxForDualProbeImm == null ? "1" : "0")
                + "}",
                "pre-fix");
            // #endregion
            RunOrbitalDragSigmaPhase1PrebondSynchronously(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                0f,
                debugDisableCyclicSigmaPhase1Redistribution,
                dualAcyclicSigmaPhase1BothOpsImm);

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

            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f && !prebondCycleCandidate && !dualAcyclicSigmaPhase1BothOpsImm)
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
        if (editModeManager == null)
        {
            Debug.LogWarning("[pi-drag] SigmaBondFormation: no EditModeManager reference; cannot run π formation pipeline.");
            return false;
        }
        if (!animate)
            return RunOrbitalDragPiFormationThreePhaseImmediate(
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                redistributionGuideTieBreakDraggedOrbital);
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
        bool dualAcyclicPiPhase1BothOps)
    {
        if (guide == null || nonGuide == null)
            yield break;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        Vector3 sourceStart = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStart = targetAtom != null ? targetAtom.transform.position : Vector3.zero;
        Vector3 guideStart = guide.transform.position;
        Vector3 nonGuideStart = nonGuide.transform.position;

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
        float piGuideSqrBefore = piGuideDirLocal.sqrMagnitude;
        bool piGuideAcyclicFillApplied = false;
        if (piPhase1CyclicContext == null && piGuideSqrBefore < 1e-12f)
        {
            piGuideDirLocal = ComputePiPerpendicularGuideDirectionLocal(nonGuide, guide, nonGuideOp);
            piGuideAcyclicFillApplied = true;
        }
        // #region agent log
        if (piPhase1CyclicContext == null)
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_piGuideAcyclic",
                "SigmaBondFormation.CoOrbitalDragPiPhase1RedistributionOnly",
                "pi_guide_dir_acyclic",
                "{"
                + "\"fillApplied\":" + (piGuideAcyclicFillApplied ? "1" : "0")
                + ",\"sqrBefore\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideSqrBefore)
                + ",\"sqrAfter\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideDirLocal.sqrMagnitude)
                + "}",
                "post-fix");
        // #endregion

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

        var tracks = new List<Phase1ParallelTrack>(1);
        Phase1ParallelTrack piPhase1RedistributionTrack = BuildPhase1OrbitalRedistributeForPiFormationPhase1(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            piPhase1CyclicContext,
            piGuideDirLocal);
        if (piPhase1RedistributionTrack != null)
            tracks.Add(piPhase1RedistributionTrack);

        Phase1ParallelTrack piCylinderTrack = BuildPhase1PiCylinderTrackFromOpFinalPose(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBetween,
            molForBondLines);
        if (piCylinderTrack != null)
            tracks.Add(piCylinderTrack);
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H9",
            "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
            "pi_phase1_track_composition",
            "{"
            + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
            + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
            + ",\"guideId\":" + (guide != null ? guide.GetInstanceID().ToString() : "0")
            + ",\"guideIsSource\":" + (ReferenceEquals(guide, sourceAtom) ? "1" : "0")
            + ",\"hasPiCylinderTrack\":" + (piCylinderTrack != null ? "1" : "0")
            + ",\"hasPhase1RedistributionTrack\":" + (piPhase1RedistributionTrack != null ? "1" : "0")
            + ",\"piRedistLaneCount\":" + (tracks.Count - (piCylinderTrack != null ? 1 : 0)).ToString()
            + ",\"phase1Sec\":" + ProjectAgentDebugLog.JsonFloatInvariant(phase1Sec)
            + "}",
            "hypothesis-run");
        // #endregion

        float absDotPiGuideVsSigma = -1f;
        if (guide != null && nonGuide != null && piGuideDirLocal.sqrMagnitude > 1e-14f)
        {
            Vector3 sigW = guide.transform.position - nonGuide.transform.position;
            if (sigW.sqrMagnitude > 1e-14f)
            {
                Vector3 piGuideW = nonGuide.transform.TransformDirection(piGuideDirLocal.normalized);
                absDotPiGuideVsSigma = Mathf.Abs(Vector3.Dot(piGuideW.normalized, sigW.normalized));
            }
        }
        Vector3 sourceOpPosBefore = sourceOrbital != null ? sourceOrbital.transform.position : Vector3.zero;
        Vector3 targetOpPosBefore = targetOrbital != null ? targetOrbital.transform.position : Vector3.zero;
        Vector3 sourceOpDirBefore = sourceOrbital != null
            ? OrbitalAngleUtility.GetOrbitalDirectionWorld(sourceOrbital.transform)
            : Vector3.zero;
        Vector3 targetOpDirBefore = targetOrbital != null
            ? OrbitalAngleUtility.GetOrbitalDirectionWorld(targetOrbital.transform)
            : Vector3.zero;
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H_pi_p1_axes",
            "SigmaBondFormation.CoOrbitalDragPiPhase1RedistributionOnly",
            "pi_phase1_final_dir_geometry",
            "{"
            + "\"hasCyclic\":" + (piPhase1CyclicContext != null ? "1" : "0")
            + ",\"dualSecondLane\":0"
            + ",\"piGuideMag2\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideDirLocal.sqrMagnitude)
            + ",\"absDotPiGuideVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(absDotPiGuideVsSigma)
            + "}",
            "debug-probe");
        // #endregion

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
            yield return StartCoroutine(
                CoExecutePhase1ParallelAnimations(
                    tracks,
                    molForBondLines,
                    Mathf.Max(0f, phase1Sec),
                    sourceAtom,
                    targetAtom));
            if (sourceAtom != null
                && targetAtom != null
                && sourceOrbital != null
                && TryComputePlaneNormalForPiProbe(sourceAtom, targetAtom, out var sourcePlaneN))
            {
                Vector3 sourceOpDir = (sourceOrbital.transform.position - sourceAtom.transform.position).normalized;
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
                    "pi_plusz_vs_phase1_plane_probe_source",
                    "{"
                    + "\"atomId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"absDotOpVsPlaneNormal\":" + ProjectAgentDebugLog.JsonFloatInvariant(Mathf.Abs(Vector3.Dot(sourceOpDir, sourcePlaneN)))
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            else if (sourceAtom != null && targetAtom != null)
            {
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
                    "pi_plusz_vs_phase1_plane_probe_source_missing_plane",
                    "{"
                    + "\"atomId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"hasPlane\":0"
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            if (sourceAtom != null
                && targetAtom != null
                && targetOrbital != null
                && TryComputePlaneNormalForPiProbe(targetAtom, sourceAtom, out var targetPlaneN))
            {
                Vector3 targetOpDir = (targetOrbital.transform.position - targetAtom.transform.position).normalized;
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
                    "pi_plusz_vs_phase1_plane_probe_target",
                    "{"
                    + "\"atomId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"absDotOpVsPlaneNormal\":" + ProjectAgentDebugLog.JsonFloatInvariant(Mathf.Abs(Vector3.Dot(targetOpDir, targetPlaneN)))
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            else if (sourceAtom != null && targetAtom != null)
            {
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
                    "pi_plusz_vs_phase1_plane_probe_target_missing_plane",
                    "{"
                    + "\"atomId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"hasPlane\":0"
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H1",
                "SigmaBondFormation.cs:CoOrbitalDragPiPhase1RedistributionOnly",
                "pi_phase1_atom_motion_probe",
                "{"
                + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
                + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
                + ",\"sourceMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceAtom != null ? Vector3.Distance(sourceStart, sourceAtom.transform.position) : 0f)
                + ",\"targetMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetAtom != null ? Vector3.Distance(targetStart, targetAtom.transform.position) : 0f)
                + ",\"guideMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(guide != null ? Vector3.Distance(guideStart, guide.transform.position) : 0f)
                + ",\"nonGuideMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(nonGuide != null ? Vector3.Distance(nonGuideStart, nonGuide.transform.position) : 0f)
                + ",\"sourceOpMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceOrbital != null ? Vector3.Distance(sourceOpPosBefore, sourceOrbital.transform.position) : 0f)
                + ",\"targetOpMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetOrbital != null ? Vector3.Distance(targetOpPosBefore, targetOrbital.transform.position) : 0f)
                + ",\"srcOpDirDeltaDeg\":" + ProjectAgentDebugLog.JsonFloatInvariant(
                    sourceOrbital != null && sourceOpDirBefore.sqrMagnitude > 1e-14f
                        ? Vector3.Angle(sourceOpDirBefore, OrbitalAngleUtility.GetOrbitalDirectionWorld(sourceOrbital.transform))
                        : -1f)
                + ",\"tgtOpDirDeltaDeg\":" + ProjectAgentDebugLog.JsonFloatInvariant(
                    targetOrbital != null && targetOpDirBefore.sqrMagnitude > 1e-14f
                        ? Vector3.Angle(targetOpDirBefore, OrbitalAngleUtility.GetOrbitalDirectionWorld(targetOrbital.transform))
                        : -1f)
                + ",\"trackCount\":" + tracks.Count.ToString()
                + ",\"hasCyclic\":" + (piPhase1CyclicContext != null ? "1" : "0")
                + ",\"dualSecondLane\":0"
                + ",\"absDotPiGuideVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(absDotPiGuideVsSigma)
                + "}",
                "debug-probe");
            // #endregion
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
    }

    static void RunOrbitalDragPiPhase1RedistributionSynchronously(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ICollection<AtomFunction> molForBondLines,
        bool dualAcyclicPiPhase1BothOps)
    {
        if (guide == null || nonGuide == null)
            return;
        OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
        Vector3 sourceStart = sourceAtom != null ? sourceAtom.transform.position : Vector3.zero;
        Vector3 targetStart = targetAtom != null ? targetAtom.transform.position : Vector3.zero;
        Vector3 guideStart = guide.transform.position;
        Vector3 nonGuideStart = nonGuide.transform.position;

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
        float piGuideSqrBeforeSync = piGuideDirLocal.sqrMagnitude;
        bool piGuideAcyclicFillAppliedSync = false;
        if (piPhase1CyclicContext == null && piGuideSqrBeforeSync < 1e-12f)
        {
            piGuideDirLocal = ComputePiPerpendicularGuideDirectionLocal(nonGuide, guide, nonGuideOp);
            piGuideAcyclicFillAppliedSync = true;
        }
        // #region agent log
        if (piPhase1CyclicContext == null)
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_piGuideAcyclic",
                "SigmaBondFormation.RunOrbitalDragPiPhase1RedistributionSynchronously",
                "pi_guide_dir_acyclic_sync",
                "{"
                + "\"fillApplied\":" + (piGuideAcyclicFillAppliedSync ? "1" : "0")
                + ",\"sqrBefore\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideSqrBeforeSync)
                + ",\"sqrAfter\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideDirLocal.sqrMagnitude)
                + "}",
                "post-fix");
        // #endregion

        ClearPiPhase1RedistributeTemplatePreviewVisuals();

        var tracks = new List<Phase1ParallelTrack>(1);
        Phase1ParallelTrack piPhase1RedistributionTrack = BuildPhase1OrbitalRedistributeForPiFormationPhase1(
            guide,
            nonGuide,
            guideOp,
            nonGuideOp,
            piPhase1CyclicContext,
            piGuideDirLocal);
        if (piPhase1RedistributionTrack != null)
            tracks.Add(piPhase1RedistributionTrack);

        Phase1ParallelTrack piCylinderTrack = BuildPhase1PiCylinderTrackFromOpFinalPose(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBetween,
            molForBondLines);
        if (piCylinderTrack != null)
            tracks.Add(piCylinderTrack);
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H9",
            "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
            "pi_phase1_track_composition_sync",
            "{"
            + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
            + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
            + ",\"guideId\":" + (guide != null ? guide.GetInstanceID().ToString() : "0")
            + ",\"guideIsSource\":" + (ReferenceEquals(guide, sourceAtom) ? "1" : "0")
            + ",\"hasPiCylinderTrack\":" + (piCylinderTrack != null ? "1" : "0")
            + ",\"hasPhase1RedistributionTrack\":" + (piPhase1RedistributionTrack != null ? "1" : "0")
            + ",\"piRedistLaneCount\":" + (tracks.Count - (piCylinderTrack != null ? 1 : 0)).ToString()
            + "}",
            "hypothesis-run");
        // #endregion

        float absDotPiGuideVsSigmaSync = -1f;
        if (guide != null && nonGuide != null && piGuideDirLocal.sqrMagnitude > 1e-14f)
        {
            Vector3 sigW = guide.transform.position - nonGuide.transform.position;
            if (sigW.sqrMagnitude > 1e-14f)
            {
                Vector3 piGuideW = nonGuide.transform.TransformDirection(piGuideDirLocal.normalized);
                absDotPiGuideVsSigmaSync = Mathf.Abs(Vector3.Dot(piGuideW.normalized, sigW.normalized));
            }
        }
        Vector3 sourceOpPosBeforeSync = sourceOrbital != null ? sourceOrbital.transform.position : Vector3.zero;
        Vector3 targetOpPosBeforeSync = targetOrbital != null ? targetOrbital.transform.position : Vector3.zero;
        Vector3 sourceOpDirBeforeSync = sourceOrbital != null
            ? OrbitalAngleUtility.GetOrbitalDirectionWorld(sourceOrbital.transform)
            : Vector3.zero;
        Vector3 targetOpDirBeforeSync = targetOrbital != null
            ? OrbitalAngleUtility.GetOrbitalDirectionWorld(targetOrbital.transform)
            : Vector3.zero;
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H_pi_p1_axes",
            "SigmaBondFormation.RunOrbitalDragPiPhase1RedistributionSynchronously",
            "pi_phase1_final_dir_geometry_sync",
            "{"
            + "\"hasCyclic\":" + (piPhase1CyclicContext != null ? "1" : "0")
            + ",\"dualSecondLane\":0"
            + ",\"piGuideMag2\":" + ProjectAgentDebugLog.JsonFloatInvariant(piGuideDirLocal.sqrMagnitude)
            + ",\"absDotPiGuideVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(absDotPiGuideVsSigmaSync)
            + "}",
            "debug-probe");
        // #endregion

        SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, true);
        try
        {
            ExecutePhase1ParallelAnimationsImmediate(tracks, molForBondLines, 0f);
            if (sourceAtom != null
                && targetAtom != null
                && sourceOrbital != null
                && TryComputePlaneNormalForPiProbe(sourceAtom, targetAtom, out var sourcePlaneN))
            {
                Vector3 sourceOpDir = (sourceOrbital.transform.position - sourceAtom.transform.position).normalized;
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
                    "pi_plusz_vs_phase1_plane_probe_source_sync",
                    "{"
                    + "\"atomId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"absDotOpVsPlaneNormal\":" + ProjectAgentDebugLog.JsonFloatInvariant(Mathf.Abs(Vector3.Dot(sourceOpDir, sourcePlaneN)))
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            else if (sourceAtom != null && targetAtom != null)
            {
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
                    "pi_plusz_vs_phase1_plane_probe_source_missing_plane_sync",
                    "{"
                    + "\"atomId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"hasPlane\":0"
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            if (sourceAtom != null
                && targetAtom != null
                && targetOrbital != null
                && TryComputePlaneNormalForPiProbe(targetAtom, sourceAtom, out var targetPlaneN))
            {
                Vector3 targetOpDir = (targetOrbital.transform.position - targetAtom.transform.position).normalized;
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
                    "pi_plusz_vs_phase1_plane_probe_target_sync",
                    "{"
                    + "\"atomId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"absDotOpVsPlaneNormal\":" + ProjectAgentDebugLog.JsonFloatInvariant(Mathf.Abs(Vector3.Dot(targetOpDir, targetPlaneN)))
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            else if (sourceAtom != null && targetAtom != null)
            {
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H16",
                    "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
                    "pi_plusz_vs_phase1_plane_probe_target_missing_plane_sync",
                    "{"
                    + "\"atomId\":" + targetAtom.GetInstanceID().ToString()
                    + ",\"partnerId\":" + sourceAtom.GetInstanceID().ToString()
                    + ",\"hasPlane\":0"
                    + "}",
                    "hypothesis-run");
                // #endregion
            }
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H1",
                "SigmaBondFormation.cs:RunOrbitalDragPiPhase1RedistributionSynchronously",
                "pi_phase1_atom_motion_probe_sync",
                "{"
                + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
                + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
                + ",\"sourceMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceAtom != null ? Vector3.Distance(sourceStart, sourceAtom.transform.position) : 0f)
                + ",\"targetMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetAtom != null ? Vector3.Distance(targetStart, targetAtom.transform.position) : 0f)
                + ",\"guideMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(Vector3.Distance(guideStart, guide.transform.position))
                + ",\"nonGuideMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(Vector3.Distance(nonGuideStart, nonGuide.transform.position))
                + ",\"sourceOpMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceOrbital != null ? Vector3.Distance(sourceOpPosBeforeSync, sourceOrbital.transform.position) : 0f)
                + ",\"targetOpMove\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetOrbital != null ? Vector3.Distance(targetOpPosBeforeSync, targetOrbital.transform.position) : 0f)
                + ",\"srcOpDirDeltaDeg\":" + ProjectAgentDebugLog.JsonFloatInvariant(
                    sourceOrbital != null && sourceOpDirBeforeSync.sqrMagnitude > 1e-14f
                        ? Vector3.Angle(sourceOpDirBeforeSync, OrbitalAngleUtility.GetOrbitalDirectionWorld(sourceOrbital.transform))
                        : -1f)
                + ",\"tgtOpDirDeltaDeg\":" + ProjectAgentDebugLog.JsonFloatInvariant(
                    targetOrbital != null && targetOpDirBeforeSync.sqrMagnitude > 1e-14f
                        ? Vector3.Angle(targetOpDirBeforeSync, OrbitalAngleUtility.GetOrbitalDirectionWorld(targetOrbital.transform))
                        : -1f)
                + ",\"trackCount\":" + tracks.Count.ToString()
                + ",\"hasCyclic\":" + (piPhase1CyclicContext != null ? "1" : "0")
                + ",\"dualSecondLane\":0"
                + ",\"absDotPiGuideVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(absDotPiGuideVsSigmaSync)
                + "}",
                "debug-probe");
            // #endregion
        }
        finally
        {
            SetSuppressSigmaPrebondBondFrameOrbitalPoseOnAtomBonds(nonGuide, false);
        }
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
                cyclicContext: cyclicContext,
                formingPiPrecursorAlignTrigonalPartnerPlane: true);
        }
        finally
        {
            // capture closed below
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
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H3",
            "SigmaBondFormation.cs:ComputePiPerpendicularGuideDirectionLocal",
            "computed_pi_perpendicular_dir",
            "{"
            + "\"nonGuideId\":" + nonGuideAtom.GetInstanceID().ToString()
            + ",\"guideId\":" + guideAtom.GetInstanceID().ToString()
            + ",\"dotNormalVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(Vector3.Dot(normalWorld, sigmaAxisWorld))
            + ",\"dotPlaneRefVsSigma\":" + ProjectAgentDebugLog.JsonFloatInvariant(Vector3.Dot(planeRefWorld, sigmaAxisWorld))
            + ",\"usedNonGuideOpSign\":" + (nonGuideOp != null ? "1" : "0")
            + "}",
            "pre-fix");
        // #endregion
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
        Vector3 frozenBondTargetPos = default;
        Vector3 frozenSourceTargetPos = default;
        Vector3 frozenTargetTargetPos = default;
        Quaternion frozenSourceTargetRot = default;
        Quaternion frozenTargetTargetRot = default;
        bool frozenSkipCylinderLerp = false;
        bool prepareFailLogged = false;
        bool endPivotDriftLogged = false;
        Vector3 sourceNucleus0 = default;
        Vector3 targetNucleus0 = default;
        float bondTargetAlphaOnSigma0 = 0.5f;
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
                {
                    // #region agent log
                    if (!prepareFailLogged)
                    {
                        prepareFailLogged = true;
                        ProjectAgentDebugLog.AppendDebugModeNdjson(
                            "debug-446955.log",
                            "446955",
                            "H_pi_cyl_prepare",
                            "SigmaBondFormation.BuildPhase1PiCylinderTrackFromOpFinalPose",
                            "pi_phase1_cylinder_prepare_failed",
                            "{"
                            + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
                            + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
                            + "}",
                            "debug-probe");
                    }
                    // #endregion
                    return;
                }

                if (!initialized)
                {
                    initialized = true;
                    sourceStart = cylPrepared.SourceOrbStart;
                    targetStart = cylPrepared.TargetOrbStart;
                    // Snapshot σ/π cylinder lerp endpoints once: recomputing those every frame while nuclei or the σ line
                    // move during parallel redistribution makes the lerp chase a drifting endpoint (runaway bondTargetPos).
                    // Arc pivots are not snapshotted — they follow nuclei each frame (see ApplySmoothStep below).
                    frozenBondTargetPos = cylPrepared.BondTargetPos;
                    frozenSourceTargetPos = cylPrepared.SourceTargetPos;
                    frozenTargetTargetPos = cylPrepared.TargetTargetPos;
                    frozenSourceTargetRot = cylPrepared.SourceTargetRot;
                    frozenTargetTargetRot = cylPrepared.TargetTargetRot;
                    frozenSkipCylinderLerp = cylPrepared.SkipCylinderLerp;
                    sourceNucleus0 = sourceAtom != null ? sourceAtom.transform.position : frozenSourceTargetPos;
                    targetNucleus0 = targetAtom != null ? targetAtom.transform.position : frozenTargetTargetPos;
                    Vector3 sigmaEdge0 = targetNucleus0 - sourceNucleus0;
                    float sigmaEdgeLen2 = sigmaEdge0.sqrMagnitude;
                    if (sigmaEdgeLen2 > 1e-10f)
                    {
                        bondTargetAlphaOnSigma0 = Mathf.Clamp01(
                            Vector3.Dot(frozenBondTargetPos - sourceNucleus0, sigmaEdge0) / sigmaEdgeLen2);
                    }
                    else
                        bondTargetAlphaOnSigma0 = 0.5f;
                    // #region agent log
                    ProjectAgentDebugLog.AppendDebugModeNdjson(
                        "debug-446955.log",
                        "446955",
                        "H_pi_cyl_freeze",
                        "SigmaBondFormation.BuildPhase1PiCylinderTrackFromOpFinalPose",
                        "pi_phase1_cylinder_targets_frozen",
                        "{"
                        + "\"bondTgtX\":" + ProjectAgentDebugLog.JsonFloatInvariant(frozenBondTargetPos.x)
                        + ",\"srcTgtX\":" + ProjectAgentDebugLog.JsonFloatInvariant(frozenSourceTargetPos.x)
                        + "}",
                        "post-fix");
                    // #endregion
                }

                Vector3 deltaSrc = sourceAtom != null
                    ? sourceAtom.transform.position - sourceNucleus0
                    : Vector3.zero;
                Vector3 deltaTgt = targetAtom != null
                    ? targetAtom.transform.position - targetNucleus0
                    : Vector3.zero;
                Vector3 srcNNow = sourceAtom != null ? sourceAtom.transform.position : sourceNucleus0 + deltaSrc;
                Vector3 tgtNNow = targetAtom != null ? targetAtom.transform.position : targetNucleus0 + deltaTgt;
                Vector3 bondTargetCoMoved = srcNNow + bondTargetAlphaOnSigma0 * (tgtNNow - srcNNow);

                // Frozen world targets from t≈0, rigidly co-moved with each nucleus so parallel redistribution
                // translations do not leave hundreds of units between pivots and arc endpoints (see NDJSON d*StartN).
                cylPrepared.SourceOrbStart = (sourceStart.pos + deltaSrc, sourceStart.rot);
                cylPrepared.TargetOrbStart = (targetStart.pos + deltaTgt, targetStart.rot);
                cylPrepared.BondTargetPos = bondTargetCoMoved;
                cylPrepared.SourceTargetPos = frozenSourceTargetPos + deltaSrc;
                cylPrepared.TargetTargetPos = frozenTargetTargetPos + deltaTgt;
                cylPrepared.SourceTargetRot = frozenSourceTargetRot;
                cylPrepared.TargetTargetRot = frozenTargetTargetRot;
                // Arc pivots track live nuclei: phase-1 redistribution moves atoms each frame; freezing pivots
                // while keeping frozen endpoints made LerpOrbitalMeshCenterOnNucleusPivotArc use a stale center
                // (hundreds of world units of drift in logs) and drove huge OP motion / axis swings.
                cylPrepared.SourceArcPivot = sourceAtom != null
                    ? sourceAtom.transform.position
                    : cylPrepared.SourceOrbStart.pos;
                cylPrepared.TargetArcPivot = targetAtom != null
                    ? targetAtom.transform.position
                    : cylPrepared.TargetOrbStart.pos;
                cylPrepared.SkipCylinderLerp = frozenSkipCylinderLerp;
                ElectronOrbitalFunction.ApplyBondFormationCylinderPoseForSmoothStep(
                    cylPrepared, sourceOrbital, targetOrbital, s);
                // #region agent log
                if (initialized && !endPivotDriftLogged && s >= 0.999f && sourceAtom != null && targetAtom != null)
                {
                    endPivotDriftLogged = true;
                    float driftSrc = Vector3.Distance(cylPrepared.SourceArcPivot, sourceAtom.transform.position);
                    float driftTgt = Vector3.Distance(cylPrepared.TargetArcPivot, targetAtom.transform.position);
                    Vector3 srcN = sourceAtom.transform.position;
                    Vector3 tgtN = targetAtom.transform.position;
                    Vector3 dS = srcN - sourceNucleus0;
                    Vector3 dT = tgtN - targetNucleus0;
                    float dSrcStartN = Vector3.Distance(sourceStart.pos + dS, srcN);
                    float dFrozenSrcEndN = Vector3.Distance(frozenSourceTargetPos + dS, srcN);
                    float dTgtStartN = Vector3.Distance(targetStart.pos + dT, tgtN);
                    float dFrozenTgtEndN = Vector3.Distance(frozenTargetTargetPos + dT, tgtN);
                    ProjectAgentDebugLog.AppendDebugModeNdjson(
                        "debug-446955.log",
                        "446955",
                        "H_pi_cyl_pivot_drift",
                        "SigmaBondFormation.BuildPhase1PiCylinderTrackFromOpFinalPose",
                        "pi_phase1_cylinder_pivot_vs_nucleus_end",
                        "{"
                        + "\"driftSrc\":" + ProjectAgentDebugLog.JsonFloatInvariant(driftSrc)
                        + ",\"driftTgt\":" + ProjectAgentDebugLog.JsonFloatInvariant(driftTgt)
                        + ",\"dSrcStartN\":" + ProjectAgentDebugLog.JsonFloatInvariant(dSrcStartN)
                        + ",\"dFrozenSrcEndN\":" + ProjectAgentDebugLog.JsonFloatInvariant(dFrozenSrcEndN)
                        + ",\"dTgtStartN\":" + ProjectAgentDebugLog.JsonFloatInvariant(dTgtStartN)
                        + ",\"dFrozenTgtEndN\":" + ProjectAgentDebugLog.JsonFloatInvariant(dFrozenTgtEndN)
                        + "}",
                        "debug-probe");
                }
                // #endregion
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
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H5",
            "SigmaBondFormation.cs:CoOrbitalDragPiFormationThreePhase",
            "pi_three_phase_entry",
            "{"
            + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
            + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
            + ",\"animate\":" + (animate ? "1" : "0")
            + "}",
            "pre-fix");
        // #endregion
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

            var sigmaBetweenPiDualProbe = TryFindSigmaBondBetween(sourceAtom, targetAtom);
            TryBuildPiPhase1CyclicRedistributionContext(
                sourceAtom,
                targetAtom,
                guide,
                nonGuide,
                sigmaBetweenPiDualProbe,
                out var piCyclicCtxDualProbe,
                out _,
                out _,
                out _,
                out _);
            bool dualAcyclicPiPhase1BothOps = piCyclicCtxDualProbe == null
                && ShouldDualAtomAcyclicPhase1PrebondForPiFormingOps(
                    guide, nonGuide, guideOpPhase1, nonGuideOpPhase1);
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_pi_ctx",
                "SigmaBondFormation.CoOrbitalDragPiFormationThreePhase",
                "pi_dual_and_cyclic_probe",
                "{"
                + "\"dualAcyclicPiPhase1BothOps\":" + (dualAcyclicPiPhase1BothOps ? "1" : "0")
                + ",\"piCyclicCtxProbeNull\":" + (piCyclicCtxDualProbe == null ? "1" : "0")
                + "}",
                "post-fix");
            // #endregion

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
                dualAcyclicPiPhase1BothOps));

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

            bool piPhase3WillRun = guide.AtomicNumber > 1 && phase3Sec > 1e-5f && bond != null && !dualAcyclicPiPhase1BothOps;
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H_pi_p3",
                "SigmaBondFormation.CoOrbitalDragPiFormationThreePhase",
                "pi_phase3_branch",
                "{"
                + "\"piPhase3WillRun\":" + (piPhase3WillRun ? "1" : "0")
                + ",\"dualAcyclicPiPhase1BothOps\":" + (dualAcyclicPiPhase1BothOps ? "1" : "0")
                + "}",
                "pre-fix");
            // #endregion
            if (piPhase3WillRun)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H7",
                    "SigmaBondFormation.cs:CoOrbitalDragPiFormationThreePhase",
                    "pi_phase3_guide_selection",
                    "{"
                    + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
                    + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
                    + ",\"guideId\":" + (guide != null ? guide.GetInstanceID().ToString() : "0")
                    + ",\"nonGuideId\":" + (nonGuide != null ? nonGuide.GetInstanceID().ToString() : "0")
                    + ",\"guideIsSource\":" + (ReferenceEquals(guide, sourceAtom) ? "1" : "0")
                    + ",\"phase3Built\":" + (phase3GuideRedistribution != null ? "1" : "0")
                    + "}",
                    "pre-fix");
                // #endregion
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
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H5",
                "SigmaBondFormation.cs:CoOrbitalDragPiFormationThreePhase",
                "pi_three_phase_exit_motion",
                "{"
                + "\"sourceMoveTotal\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceAtom != null ? Vector3.Distance(sourceStartAll, sourceAtom.transform.position) : 0f)
                + ",\"targetMoveTotal\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetAtom != null ? Vector3.Distance(targetStartAll, targetAtom.transform.position) : 0f)
                + "}",
                "pre-fix");
            // #endregion
        }
        finally
        {
            OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
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
        // #region agent log
        ProjectAgentDebugLog.AppendDebugModeNdjson(
            "debug-446955.log",
            "446955",
            "H5",
            "SigmaBondFormation.cs:RunOrbitalDragPiFormationThreePhaseImmediate",
            "pi_three_phase_immediate_entry",
            "{"
            + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
            + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
            + "}",
            "pre-fix");
        // #endregion
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
            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : sourceOrbital;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(sourceAtom, targetAtom, guideOrb, out var guide, out var nonGuide);

            if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide))
            {
                RunOrbitalDragPiFormationLegacyFallbackImmediate(sourceAtom, targetAtom, sourceOrbital, targetOrbital);
                return true;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == sourceAtom ? sourceOrbital : targetOrbital;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == sourceAtom ? sourceOrbital : targetOrbital;

            var sigmaBetweenPiDualProbeImm = TryFindSigmaBondBetween(sourceAtom, targetAtom);
            TryBuildPiPhase1CyclicRedistributionContext(
                sourceAtom,
                targetAtom,
                guide,
                nonGuide,
                sigmaBetweenPiDualProbeImm,
                out var piCyclicCtxDualProbeImm,
                out _,
                out _,
                out _,
                out _);
            bool dualAcyclicPiPhase1BothOpsImm = piCyclicCtxDualProbeImm == null
                && ShouldDualAtomAcyclicPhase1PrebondForPiFormingOps(
                    guide, nonGuide, guideOpPhase1, nonGuideOpPhase1);

            RunOrbitalDragPiPhase1RedistributionSynchronously(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                molForBondLines,
                dualAcyclicPiPhase1BothOpsImm);

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

            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f && bond != null && !dualAcyclicPiPhase1BothOpsImm)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                // #region agent log
                ProjectAgentDebugLog.AppendDebugModeNdjson(
                    "debug-446955.log",
                    "446955",
                    "H7",
                    "SigmaBondFormation.cs:RunOrbitalDragPiFormationThreePhaseImmediate",
                    "pi_phase3_guide_selection_immediate",
                    "{"
                    + "\"sourceId\":" + (sourceAtom != null ? sourceAtom.GetInstanceID().ToString() : "0")
                    + ",\"targetId\":" + (targetAtom != null ? targetAtom.GetInstanceID().ToString() : "0")
                    + ",\"guideId\":" + (guide != null ? guide.GetInstanceID().ToString() : "0")
                    + ",\"nonGuideId\":" + (nonGuide != null ? nonGuide.GetInstanceID().ToString() : "0")
                    + ",\"guideIsSource\":" + (ReferenceEquals(guide, sourceAtom) ? "1" : "0")
                    + ",\"phase3Built\":" + (phase3GuideRedistribution != null ? "1" : "0")
                    + "}",
                    "pre-fix");
                // #endregion
                if (phase3GuideRedistribution != null)
                {
                    phase3GuideRedistribution.Apply(1f);
                    AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(guide.GetConnectedMolecule());
                }

            }

            editModeManager.FinishSigmaBondInstantTail(
                sourceAtom,
                targetAtom,
                skipHydrogenSigmaNeighborSnapAfterOrbitalDragThreePhase: true);
            // #region agent log
            ProjectAgentDebugLog.AppendDebugModeNdjson(
                "debug-446955.log",
                "446955",
                "H5",
                "SigmaBondFormation.cs:RunOrbitalDragPiFormationThreePhaseImmediate",
                "pi_three_phase_immediate_exit_motion",
                "{"
                + "\"sourceMoveTotal\":" + ProjectAgentDebugLog.JsonFloatInvariant(sourceAtom != null ? Vector3.Distance(sourceStartAll, sourceAtom.transform.position) : 0f)
                + ",\"targetMoveTotal\":" + ProjectAgentDebugLog.JsonFloatInvariant(targetAtom != null ? Vector3.Distance(targetStartAll, targetAtom.transform.position) : 0f)
                + "}",
                "pre-fix");
            // #endregion
            return true;
        }
        finally
        {
            OrbitalRedistribution.ClearPiPhase1PrecursorTrigonalTemplatePlane();
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
            editModeManager?.RefreshSelectedMoleculeAfterBondChange();
        }
    }


}
