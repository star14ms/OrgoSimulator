using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// σ / π formation from <b>orbital drag</b>: σ runs phase 1 pre-bond (non-guide fragment translates toward guide op head target),
/// then bond formation (cylinder + orbital→line), then post-bond guide hybrid lerp. π uses the same three phases with
/// <b>no</b> rigid fragment translation in phase 1 (σ already links the pair).
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

    /// <summary>
    /// Orbital-drag σ <b>phase 1</b> (pre-bond): builds parallel tracks (fragment translation + orbital redistribution placeholder) and runs them on one timeline.
    /// </summary>
    /// <param name="guide">Guide atom from <see cref="ElectronRedistributionGuide.ResolveGuideAtomForPair"/>.</param>
    /// <param name="nonGuide">Non-guide (approaching) atom.</param>
    /// <param name="guideOp">Guide atom’s σ operation orbital.</param>
    /// <param name="nonGuideOp">Non-guide atom’s σ operation orbital.</param>
    IEnumerator CoOrbitalDragSigmaPhase1PrebondPlaceholder(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        float phase1Sec)
    {
        if (guide == null || nonGuide == null)
            yield break;

        float bl = DefaultSigmaBondLengthForPair(guide, nonGuide);
        Vector3 nTarget = Phase1NonGuideNucleusTargetOnGuideOpOutboundRay(guide, guideOp, bl);
        Vector3 deltaTotal = nTarget - nonGuide.transform.position;
        if (deltaTotal.sqrMagnitude < 1e-16f)
            yield break;

        var toMove = BuildNonGuideFragmentAtomsForApproach(guide, nonGuide);
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
            mol);

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

    /// <summary>Assembles phase-1 parallel lanes: atom fragment approach + non-guide orbital redistribution (placeholder).</summary>
    static List<Phase1ParallelTrack> BuildPhase1ParallelAnimationList(
        AtomFunction guide,
        AtomFunction nonGuide,
        ElectronOrbitalFunction guideOp,
        ElectronOrbitalFunction nonGuideOp,
        Dictionary<AtomFunction, Vector3> initialWorld,
        List<AtomFunction> toMove,
        Vector3 deltaTotal,
        Vector3 nTarget,
        ICollection<AtomFunction> molForBondLines)
    {
        var list = new List<Phase1ParallelTrack>(2);
        list.Add(BuildPhase1AtomFragmentApproachAnimation(
            nonGuide, initialWorld, toMove, deltaTotal, nTarget, molForBondLines));
        list.Add(BuildPhase1OrbitalRedistributeForSigmaFormationPhase1(
            guide, nonGuide, guideOp, nonGuideOp));
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
        ICollection<AtomFunction> molForBondLines)
    {
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => ApplyPhase1ApproachFragmentOffset(initialWorld, toMove, deltaTotal, s),
            FinalizeAfterTimeline = () =>
            {
                if (nonGuide == null) return;
                Vector3 residual = nTarget - nonGuide.transform.position;
                if (residual.sqrMagnitude > 1e-14f)
                {
                    foreach (var a in toMove)
                    {
                        if (a == null) continue;
                        a.transform.position += residual;
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
        ElectronOrbitalFunction nonGuideOp)
    {
       Vector3 finalDirectionForGuideOrbital = Vector3.zero;
        if (guideAtom != null && guideOp != null)
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
            isBondingEvent: true);
        return new Phase1ParallelTrack
        {
            ApplySmoothStep = s => animation?.Apply(s),
            FinalizeAfterTimeline = () => { }
        };
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
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].ApplySmoothStep?.Invoke(1f);
            if (molForBondLines != null && molForBondLines.Count > 0)
                AtomFunction.UpdateSigmaBondLineTransformsOnlyForAtoms(molForBondLines);
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].FinalizeAfterTimeline?.Invoke();
            yield break;
        }

        float t = 0f;
        while (true)
        {
            float u = Mathf.Clamp01(t / dur);
            float s = u * u * (3f - 2f * u);
            for (int i = 0; i < tracks.Count; i++)
                tracks[i].ApplySmoothStep?.Invoke(s);
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
    /// Orbital-drag σ: phase 1 non-guide approach toward guide op target, bond animation, then post-bond guide lerp.
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

            ElectronOrbitalFunction guideOpPhase1 = guide == atomA ? orbA : orbB;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == atomA ? orbA : orbB;
            yield return StartCoroutine(CoOrbitalDragSigmaPhase1PrebondPlaceholder(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
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
            
            // Phase 3: guide post-bond redistribution animation.
            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f)
            {
                var phase3GuideOp = bond != null && bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
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
    /// Orbital-drag π: phase 1 redistribution on the non-guide center only (no rigid fragment translation), then cylinder + orbital→line, then post-bond guide lerp.
    /// </summary>
    public bool TryBeginOrbitalDragPiFormation(
        AtomFunction sourceAtom,
        AtomFunction targetAtom,
        ElectronOrbitalFunction sourceOrbital,
        ElectronOrbitalFunction targetOrbital,
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
        if (editModeManager == null)
        {
            Debug.LogWarning("[pi-drag] SigmaBondFormation: no EditModeManager reference; cannot run π formation pipeline.");
            return false;
        }
        StartCoroutine(CoOrbitalDragPiFormationThreePhase(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            redistributionGuideTieBreakDraggedOrbital));
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
        ICollection<AtomFunction> molForBondLines)
    {
        if (guide == null || nonGuide == null)
            yield break;

        var tracks = new List<Phase1ParallelTrack>(2)
        {
            BuildPhase1OrbitalRedistributeForSigmaFormationPhase1(guide, nonGuide, guideOp, nonGuideOp)
        };

        var sigmaBetween = TryFindSigmaBondBetween(sourceAtom, targetAtom);
        Phase1ParallelTrack piCylinderTrack = BuildPhase1PiCylinderTrackFromOpFinalPose(
            sourceAtom,
            targetAtom,
            sourceOrbital,
            targetOrbital,
            sigmaBetween,
            molForBondLines);
        if (piCylinderTrack != null)
            tracks.Add(piCylinderTrack);

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
        ElectronOrbitalFunction targetOrbital)
    {
        if (sourceAtom == null || targetAtom == null || sourceOrbital == null || targetOrbital == null)
            yield break;

        int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
        int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
        AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
            sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

        sourceAtom.UnbondOrbital(sourceOrbital);
        targetAtom.UnbondOrbital(targetOrbital);

        var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
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

        yield return StartCoroutine(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
            sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, -1f, mol));

        if (bond != null)
        {
            bond.animatingOrbitalToBondPosition = false;
            yield return bond.AnimateOrbitalToLine(
                sourceOrbital.BondFormationOrbitalToLineDurationResolved, sourceOrbital, mol);
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
        ElectronOrbitalFunction redistributionGuideTieBreakDraggedOrbital)
    {
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
            var draggedLobeToRestore = redistributionGuideTieBreakDraggedOrbital != null
                ? redistributionGuideTieBreakDraggedOrbital
                : sourceOrbital;
            if (draggedLobeToRestore != null)
                draggedLobeToRestore.SnapToOriginal();
            yield return null;

            var guideOrb = redistributionGuideTieBreakDraggedOrbital != null ? redistributionGuideTieBreakDraggedOrbital : sourceOrbital;
            ElectronRedistributionGuide.ResolveGuideAtomForPair(sourceAtom, targetAtom, guideOrb, out var guide, out var nonGuide);

            if (guide == null || nonGuide == null || ReferenceEquals(guide, nonGuide))
            {
                yield return StartCoroutine(CoOrbitalDragPiFormationLegacyFallback(
                    sourceAtom, targetAtom, sourceOrbital, targetOrbital));
                yield break;
            }

            ElectronOrbitalFunction guideOpPhase1 = guide == sourceAtom ? sourceOrbital : targetOrbital;
            ElectronOrbitalFunction nonGuideOpPhase1 = nonGuide == sourceAtom ? sourceOrbital : targetOrbital;

            yield return StartCoroutine(CoOrbitalDragPiPhase1RedistributionOnly(
                guide,
                nonGuide,
                guideOpPhase1,
                nonGuideOpPhase1,
                sourceAtom,
                targetAtom,
                sourceOrbital,
                targetOrbital,
                phase1Sec,
                molForBondLines));

            int mergedElectrons = sourceOrbital.ElectronCount + targetOrbital.ElectronCount;
            int ecnPiEvent = AtomFunction.AllocateMoleculeEcnEventId();
            AtomFunction.LogMoleculeElectronConfigurationFromAtomUnion(
                sourceAtom, targetAtom, "beforePiBond", ecnPiEvent, null, "pi");

            sourceAtom.UnbondOrbital(sourceOrbital);
            targetAtom.UnbondOrbital(targetOrbital);

            var bond = CovalentBond.Create(sourceAtom, targetAtom, targetOrbital, targetAtom, animateOrbitalToBond: true);
            if (bond == null)
                yield break;
            bond.SkipNonGuideExecuteSigmaFormation12HybridPass = true;

            sourceOrbital.transform.SetParent(null, worldPositionStays: true);
            bond.SetOrbitalBeingFaded(sourceOrbital);
            sourceAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            targetAtom.TryTransferElectronFromLonePairToEmptyOrbitals();
            sourceAtom.RefreshCharge();
            targetAtom.RefreshCharge();

            float tCyl = Mathf.Max(0f, phase2CylinderSec);
            float tLine = Mathf.Max(0f, phase2OrbitalToLineSec);

            yield return StartCoroutine(sourceOrbital.AnimateBondFormationOperationOrbitalsTowardBondCylinder(
                sourceAtom, targetAtom, sourceOrbital, targetOrbital, bond, tCyl, molForBondLines));

            if (bond != null)
            {
                bond.animatingOrbitalToBondPosition = false;
                float lineDur = tLine > 1e-6f ? tLine : sourceOrbital.BondFormationOrbitalToLineDurationResolved;
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

            if (guide.AtomicNumber > 1 && phase3Sec > 1e-5f && bond != null)
            {
                var phase3GuideOp = bond.Orbital != null ? bond.Orbital : guideOpPhase1;
                var phase3GuideRedistribution = OrbitalRedistribution.BuildOrbitalRedistribution(
                    guide,
                    nonGuide,
                    atomOrbitalOp: phase3GuideOp,
                    isBondingEvent: true);
                if (phase3GuideRedistribution != null)
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
            foreach (var a in atomsToBlock)
                if (a != null) a.SetInteractionBlocked(false);
            editModeManager?.RefreshSelectedMoleculeAfterBondChange();
        }
    }


}
