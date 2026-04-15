using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase-1 sigma-formation orbital redistribution planner (standalone, does not call existing redistribution pipelines).
/// </summary>
public static class OrbitalRedistribution
{
    public sealed class DebugTemplateSnapshot
    {
        public AtomFunction Atom;
        public List<Vector3> TemplateLocal;
        public List<(ElectronOrbitalFunction orbital, int templateIndex)> GroupAssignments;
    }

    static bool _debugCaptureTemplates;
    static readonly List<DebugTemplateSnapshot> _debugCapturedTemplates = new List<DebugTemplateSnapshot>();

    public static void BeginDebugTemplateCapture()
    {
        _debugCapturedTemplates.Clear();
        _debugCaptureTemplates = true;
    }

    public static List<DebugTemplateSnapshot> EndDebugTemplateCapture()
    {
        _debugCaptureTemplates = false;
        return new List<DebugTemplateSnapshot>(_debugCapturedTemplates);
    }

    static void TryCaptureDebugTemplate(
        AtomFunction atom,
        List<Vector3> alignedTemplate,
        List<GroupEntry> groupsForMatching,
        int[] bestPerm)
    {
        if (!_debugCaptureTemplates || atom == null || alignedTemplate == null || alignedTemplate.Count == 0)
            return;
        var assigns = new List<(ElectronOrbitalFunction orbital, int templateIndex)>();
        if (groupsForMatching != null && bestPerm != null)
        {
            int n = Mathf.Min(groupsForMatching.Count, bestPerm.Length);
            for (int i = 0; i < n; i++)
            {
                var g = groupsForMatching[i];
                if (g == null || g.Orbital == null) continue;
                int ti = bestPerm[i];
                assigns.Add((g.Orbital, ti));
            }
        }
        _debugCapturedTemplates.Add(new DebugTemplateSnapshot
        {
            Atom = atom,
            TemplateLocal = new List<Vector3>(alignedTemplate),
            GroupAssignments = assigns
        });
    }

    public sealed class CyclicRedistributionContext
    {
        public AtomFunction PivotAtom;
        public AtomFunction CycleNeighborA;
        public AtomFunction CycleNeighborB;
        public Dictionary<AtomFunction, Vector3> FinalWorldByAtom;
        public Vector3 CycleCenterWorld;
        public HashSet<AtomFunction> ChainRedistributionBlockedAtoms;
        /// <summary>σ OP on <see cref="PivotAtom"/> (approaching atom) for this closure.</summary>
        public ElectronOrbitalFunction PivotSigmaOp;
        /// <summary>σ OP on <see cref="CycleNeighborA"/> (guide) for this closure.</summary>
        public ElectronOrbitalFunction GuideSigmaOp;
    }


    /// <summary>
    /// Simple entry: build redistribution for <paramref name="nonGuideAtom"/> using <paramref name="guideAtom"/> as guide.
    /// </summary>
    public static RedistributionAnimation BuildOrbitalRedistribution(
        AtomFunction nonGuideAtom,
        AtomFunction guideAtom,
        bool isBondingEvent = true)
    {
        return BuildOrbitalRedistribution(
            nonGuideAtom,
            guideAtom,
            guideAtomOrbitalOp: null,
            atomOrbitalOp: null,
            guideOrbitalPredetermined: null,
            finalDirectionForGuideOrbital: Vector3.zero,
            atomMoveAnimation: null,
            visitedAtoms: null,
            isBondingEvent: isBondingEvent,
            cyclicContext: null);
    }

    /// <summary>
    /// Debug helper: resolves the current guide orbital group for an atom
    /// using the same group-building/selection policy as redistribution.
    /// </summary>
    public static bool TryGetGuideOrbitalForDebug(AtomFunction atom, out ElectronOrbitalFunction guideOrbital)
    {
        guideOrbital = null;
        if (atom == null) return false;

        var bondingGroups = new List<GroupEntry>();
        foreach (var cb in atom.CovalentBonds)
        {
            if (cb == null) continue;
            var orb = cb.Orbital;
            if (orb == null || !cb.IsSigmaBondLine()) continue;
            Vector3 dir = orb.transform.position - atom.transform.position;
            float radius = dir.magnitude;
            bondingGroups.Add(new GroupEntry
            {
                Kind = "bond",
                Bond = cb,
                Orbital = orb,
                MassWeight = ComputeBondGroupMassWeight(atom, cb),
                CurrentDirWorld = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.right,
                Radius = radius > 1e-5f ? radius : atom.BondRadius
            });
        }

        var nonbondingOccupied = new List<GroupEntry>();
        var emptyOrbitals = new List<GroupEntry>();
        foreach (var orb in atom.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != atom.transform) continue;
            var row = new GroupEntry
            {
                Kind = "nonbond",
                Orbital = orb,
                MassWeight = Mathf.Max(1e-3f, ElectronRedistributionGuide.GetStandardAtomicWeight(atom.AtomicNumber)),
                CurrentDirWorld = (orb.transform.position - atom.transform.position).normalized,
                Radius = (orb.transform.position - atom.transform.position).magnitude
            };
            if (orb.ElectronCount > 0) nonbondingOccupied.Add(row);
            else emptyOrbitals.Add(row);
        }

        GroupEntry guideGroup = GetGuideGroup(
            guideOrbitalPredetermined: null,
            bondingGroups: bondingGroups,
            nonbondingOccupied: nonbondingOccupied,
            emptyOrbitals: emptyOrbitals,
            atomOrbitalOp: null);
        if (guideGroup == null || guideGroup.Orbital == null) return false;
        guideOrbital = guideGroup.Orbital;
        return true;
    }

    sealed class GroupEntry
    {
        public string Kind;
        public ElectronOrbitalFunction Orbital;
        public CovalentBond Bond;
        public float MassWeight;
        public Vector3 CurrentDirWorld;
        public float Radius;
    }

    public sealed class RedistributionAnimation
    {
        sealed class Assignment
        {
            public GroupEntry Group;
            public Vector3 StartDirWorld;
            public Vector3 TargetDirLocal;
            public float Radius;
            public bool IsBondGroup;
            public bool IsEmptyGroup;
            public CovalentBond Bond;
            public Vector3 StartPivotWorld;
            public Vector3 StartNeighborDirWorld;
            public List<(AtomFunction atom, Vector3 worldPos0)> FragmentStartWorld;
            public Vector3 StartLocalPos;
        }

        readonly List<Assignment> assignments = new List<Assignment>();
        readonly AtomFunction atom;
        readonly List<RedistributionAnimation> childAnimations = new List<RedistributionAnimation>();

        public RedistributionAnimation(AtomFunction atomRef)
        {
            atom = atomRef;
        }

        public void AddOrbitalTarget(
            ElectronOrbitalFunction orbital,
            Vector3 startDirWorld,
            float radius,
            Vector3 targetDirLocal,
            bool isBondGroup = false,
            bool isEmptyGroup = false,
            CovalentBond bond = null,
            Vector3 startPivotWorld = default,
            Vector3 startNeighborDirWorld = default,
            List<(AtomFunction atom, Vector3 worldPos0)> fragmentStartWorld = null)
        {
            if (orbital == null) return;
            Vector3 start = startDirWorld.sqrMagnitude > 1e-12f ? startDirWorld.normalized : Vector3.right;
            assignments.Add(new Assignment
            {
                Group = new GroupEntry { Orbital = orbital },
                StartDirWorld = start,
                TargetDirLocal = targetDirLocal.sqrMagnitude > 1e-12f ? targetDirLocal.normalized : Vector3.right,
                Radius = Mathf.Max(0.01f, radius),
                IsBondGroup = isBondGroup,
                IsEmptyGroup = isEmptyGroup,
                Bond = bond,
                StartPivotWorld = startPivotWorld,
                StartNeighborDirWorld = startNeighborDirWorld,
                FragmentStartWorld = fragmentStartWorld,
                StartLocalPos = atom != null
                    ? atom.transform.InverseTransformDirection(start).normalized * Mathf.Max(0.01f, radius)
                    : Vector3.zero
            });
        }

        public void AddChild(RedistributionAnimation child)
        {
            if (child == null) return;
            childAnimations.Add(child);
        }

        public void Apply(float smoothS)
        {
            if (atom == null) return;
            float s = Mathf.Clamp01(smoothS);
            int n = assignments.Count;
            // Rigid rotation about the nucleus that maps the σ internuclear axis from its start pose to the
            // slerp target. π orbitals on the same partner pair use this so π lobes + cylinders stay aligned with σ.
            var sigmaRigidByPartnerId = new Dictionary<int, Quaternion>(n);
            for (int i = 0; i < n; i++)
            {
                Assignment a = assignments[i];
                if (a.Group?.Orbital == null) continue;
                if (a.Bond == null || !a.IsBondGroup || !a.Bond.IsSigmaBondLine()) continue;
                Vector3 targetWorld = atom.transform.TransformDirection(a.TargetDirLocal).normalized;
                Vector3 dirSigma = Vector3.Slerp(a.StartDirWorld, targetWorld, s).normalized;
                if (dirSigma.sqrMagnitude < 1e-12f) dirSigma = targetWorld;
                Vector3 startAxis = a.StartNeighborDirWorld.sqrMagnitude > 1e-12f
                    ? a.StartNeighborDirWorld.normalized
                    : a.StartDirWorld.normalized;
                AtomFunction partner = a.Bond.AtomA == atom ? a.Bond.AtomB : a.Bond.AtomA;
                if (partner != null)
                    sigmaRigidByPartnerId[partner.GetInstanceID()] = Quaternion.FromToRotation(startAxis, dirSigma);
            }

            for (int i = 0; i < n; i++)
            {
                Assignment a = assignments[i];
                if (a.Group?.Orbital == null) continue;
                Vector3 targetWorld = atom.transform.TransformDirection(a.TargetDirLocal).normalized;
                Vector3 dirWorld = Vector3.Slerp(a.StartDirWorld, targetWorld, s).normalized;
                if (dirWorld.sqrMagnitude < 1e-12f) dirWorld = targetWorld;

                if (a.Bond != null && a.IsBondGroup && !a.Bond.IsSigmaBondLine())
                {
                    AtomFunction partner = a.Bond.AtomA == atom ? a.Bond.AtomB : a.Bond.AtomA;
                    if (partner != null && sigmaRigidByPartnerId.TryGetValue(partner.GetInstanceID(), out Quaternion qRigid))
                    {
                        Vector3 startRay = a.StartDirWorld.sqrMagnitude > 1e-12f ? a.StartDirWorld.normalized : Vector3.right;
                        dirWorld = (qRigid * startRay).normalized;
                        if (dirWorld.sqrMagnitude < 1e-12f) dirWorld = targetWorld;
                    }
                }

                ElectronOrbitalFunction orb = a.Group.Orbital;
                if (orb.Bond == null && orb.transform.parent == atom.transform)
                {
                    Vector3 dirLocal = atom.transform.InverseTransformDirection(dirWorld).normalized;
                    var (_, rotLocal) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                        dirLocal, atom.BondRadius, orb.transform.localRotation);
                    orb.transform.localRotation = rotLocal;
                    orb.transform.localPosition = dirLocal * a.Radius;
                }
                else
                {
                    Vector3 from = orb.transform.rotation * Vector3.right;
                    if (from.sqrMagnitude < 1e-12f) from = dirWorld;
                    Quaternion delta = Quaternion.FromToRotation(from.normalized, dirWorld);
                    orb.transform.rotation = delta * orb.transform.rotation;
                    orb.transform.position = atom.transform.position + dirWorld * a.Radius;

                    if (a.IsBondGroup && a.Bond != null && a.FragmentStartWorld != null && a.FragmentStartWorld.Count > 0)
                    {
                        Vector3 startNbr = a.StartNeighborDirWorld.sqrMagnitude > 1e-12f
                            ? a.StartNeighborDirWorld.normalized
                            : dirWorld;
                        Quaternion qFrag = Quaternion.FromToRotation(startNbr, dirWorld);
                        for (int fi = 0; fi < a.FragmentStartWorld.Count; fi++)
                        {
                            var row = a.FragmentStartWorld[fi];
                            if (row.atom == null) continue;
                            Vector3 rel0 = row.worldPos0 - a.StartPivotWorld;
                            row.atom.transform.position = atom.transform.position + qFrag * rel0;
                        }
                    }

                    if (a.IsBondGroup && a.Bond != null)
                        a.Bond.UpdateBondTransformToCurrentAtoms();
                }
            }

            for (int ci = 0; ci < childAnimations.Count; ci++)
                childAnimations[ci]?.Apply(s);
        }
    }

    /// <summary>
    /// Build redistribution plan inputs for non-guide phase 1. Empty op orbital (0e) is counted as occupied nonbonding for VSEPR group count.
    /// </summary>
    public static RedistributionAnimation BuildOrbitalRedistribution(
        AtomFunction atom,
        AtomFunction guideAtom,
        ElectronOrbitalFunction guideAtomOrbitalOp = null,
        ElectronOrbitalFunction atomOrbitalOp = null,
        ElectronOrbitalFunction guideOrbitalPredetermined = null,
        Vector3 finalDirectionForGuideOrbital = default,
        System.Func<float, Vector3> atomMoveAnimation = null,
        HashSet<AtomFunction> visitedAtoms = null,
        bool isBondingEvent = true,
        CyclicRedistributionContext cyclicContext = null)
    {
        if (atom == null) return new RedistributionAnimation(null);
        _ = atomMoveAnimation;
        if (visitedAtoms == null)
            visitedAtoms = new HashSet<AtomFunction>();
        if (!visitedAtoms.Add(atom))
            return new RedistributionAnimation(atom);

        var bondingGroups = new List<GroupEntry>();
        foreach (var cb in atom.CovalentBonds)
        {
            if (cb == null) continue;
            var orb = cb.Orbital;
            if (orb == null || !cb.IsSigmaBondLine()) continue;
            Vector3 dir = orb.transform.position - atom.transform.position;
            float radius = dir.magnitude;
            var row = new GroupEntry
            {
                Kind = "bond",
                Bond = cb,
                Orbital = orb,
                MassWeight = ComputeBondGroupMassWeight(atom, cb),
                CurrentDirWorld = dir.sqrMagnitude > 1e-12f ? dir.normalized : Vector3.right,
                Radius = radius > 1e-5f ? radius : atom.BondRadius
            };
            bondingGroups.Add(row);
        }

        var nonbondingOccupied = new List<GroupEntry>();
        var emptyOrbitals = new List<GroupEntry>();
        foreach (var orb in atom.BondedOrbitals)
        {
            if (orb == null)
                continue;
            bool inSigmaBondGroup = false;
            for (int bi = 0; bi < bondingGroups.Count; bi++)
            {
                if (bondingGroups[bi] != null && ReferenceEquals(bondingGroups[bi].Orbital, orb))
                {
                    inSigmaBondGroup = true;
                    break;
                }
            }
            if (inSigmaBondGroup)
                continue;

            bool incipientTrackedSigmaOp = isBondingEvent
                && ReferenceEquals(orb, atomOrbitalOp)
                && orb.ElectronCount == 0;
            if (orb.Bond != null && !incipientTrackedSigmaOp)
                continue;
            // σ OP can be parented off the nucleus; still counts as a VSEPR domain for matching.
            if (orb.transform.parent != atom.transform
                && !(isBondingEvent && ReferenceEquals(orb, atomOrbitalOp)))
                continue;
            var row = new GroupEntry
            {
                Kind = "nonbond",
                Orbital = orb,
                MassWeight = Mathf.Max(1e-3f, ElectronRedistributionGuide.GetStandardAtomicWeight(atom.AtomicNumber)),
                CurrentDirWorld = (orb.transform.position - atom.transform.position).normalized,
                Radius = (orb.transform.position - atom.transform.position).magnitude
            };
            if (orb.ElectronCount > 0) nonbondingOccupied.Add(row);
            else emptyOrbitals.Add(row);
        }

        ApplyEventSpecificGroupAdjustments(
            atom,
            guideAtom,
            atomOrbitalOp,
            nonbondingOccupied,
            emptyOrbitals,
            isBondingEvent);
        EnsureCyclicClosureSigmaOpCountedForMatching(
            atom,
            atomOrbitalOp,
            cyclicContext,
            bondingGroups,
            nonbondingOccupied);
        GroupEntry guideGroup = GetGuideGroup(
            guideOrbitalPredetermined, bondingGroups, nonbondingOccupied, emptyOrbitals, atomOrbitalOp);

        int nVseprGroup = bondingGroups.Count + nonbondingOccupied.Count;
        var groupsForMatching = new List<GroupEntry>(bondingGroups.Count + nonbondingOccupied.Count);
        groupsForMatching.AddRange(bondingGroups);
        groupsForMatching.AddRange(nonbondingOccupied);

        var cyclicAssignmentFix = new Dictionary<int, int>();
        bool hasCyclicTemplate = TryBuildCyclicFinalDirectionsTemplate(
            atom,
            nVseprGroup,
            groupsForMatching,
            cyclicContext,
            out var cyclicTemplate,
            cyclicAssignmentFix);
        if (hasCyclicTemplate && atomOrbitalOp != null)
        {
            Vector3 opBondDirLocal = TryGetCyclicClosureBondDirLocal(atom, cyclicContext);
            if (opBondDirLocal.sqrMagnitude < 1e-12f)
                opBondDirLocal = finalDirectionForGuideOrbital;
            if (opBondDirLocal.sqrMagnitude > 1e-12f)
            {
                TryApplyCyclicOpBondSitePreassign(
                    atom,
                    groupsForMatching,
                    cyclicTemplate,
                    cyclicAssignmentFix,
                    atomOrbitalOp,
                    opBondDirLocal);
            }
        }
        var finalDirectionsTemplate = hasCyclicTemplate
            ? cyclicTemplate
            : BuildFinalDirectionsTemplate(nVseprGroup);

        if (TryHandleBreakingReleasedEmptySpecialCase(
            atom,
            atomOrbitalOp,
            isBondingEvent,
            bondingGroups,
            groupsForMatching,
            nVseprGroup,
            guideOrbitalPredetermined,
            atomMoveAnimation,
            visitedAtoms,
            out var breakingSpecialAnimation))
        {
            return breakingSpecialAnimation;
        }

        bool usedProvidedGuideDirection = finalDirectionForGuideOrbital.sqrMagnitude > 1e-12f;
        Vector3 guideDirLocal = usedProvidedGuideDirection
            ? finalDirectionForGuideOrbital.normalized
            : (guideGroup != null && guideGroup.CurrentDirWorld.sqrMagnitude > 1e-12f
                ? atom.transform.InverseTransformDirection(guideGroup.CurrentDirWorld).normalized
                : Vector3.right);
        List<Vector3> alignedTemplate = hasCyclicTemplate
            ? new List<Vector3>(finalDirectionsTemplate)
            : AlignFinalDirectionsTemplateToGuide(
                finalDirectionsTemplate,
                guideDirLocal,
                atom,
                guideAtom,
                nVseprGroup);
        GroupEntry guideGroupForMatching = hasCyclicTemplate ? null : guideGroup;

        int[] bestPerm = FindBestCombinationVSEPRGroupToFinalDirection(
            groupsForMatching,
            alignedTemplate,
            atom,
            guideGroupForMatching,
            cyclicAssignmentFix,
            disableLegacyGuidePreassign: cyclicContext != null);
        TryCaptureDebugTemplate(atom, alignedTemplate, groupsForMatching, bestPerm);
        var finalDirectionsEmptyOrbital = BuildFinalDirectionsEmptyOrbitalTemplate(alignedTemplate);
        var animation = BuildRedistributionAnimation(
            groupsForMatching,
            emptyOrbitals,
            alignedTemplate,
            finalDirectionsEmptyOrbital,
            bestPerm,
            atom,
            atomOrbitalOp,
            guideOrbitalPredetermined,
            atomMoveAnimation,
            visitedAtoms,
            isBondingEvent,
            cyclicContext);

        return animation;
    }

    static bool TryHandleBreakingReleasedEmptySpecialCase(
        AtomFunction atom,
        ElectronOrbitalFunction atomOrbitalOp,
        bool isBondingEvent,
        List<GroupEntry> bondingGroups,
        List<GroupEntry> groupsForMatching,
        int nVseprGroup,
        ElectronOrbitalFunction guideOrbitalPredetermined,
        System.Func<float, Vector3> atomMoveAnimation,
        HashSet<AtomFunction> visitedAtoms,
        out RedistributionAnimation animation)
    {
        animation = null;
        if (atom == null || isBondingEvent || atomOrbitalOp == null) return false;
        if (bondingGroups != null && bondingGroups.Count > 0) return false;
        if (groupsForMatching == null || groupsForMatching.Count == 0) return false;

        int releasedIdx = -1;
        for (int i = 0; i < groupsForMatching.Count; i++)
        {
            if (ReferenceEquals(groupsForMatching[i]?.Orbital, atomOrbitalOp))
            {
                releasedIdx = i;
                break;
            }
        }
        if (releasedIdx < 0) return false;

        if (nVseprGroup == 1)
        {
            animation = new RedistributionAnimation(atom);
            return true;
        }

        if (nVseprGroup == 2 || nVseprGroup == 3)
        {
            Vector3 releasedLocal = atom.transform.InverseTransformDirection(
                groupsForMatching[releasedIdx].CurrentDirWorld).normalized;
            if (releasedLocal.sqrMagnitude < 1e-12f) releasedLocal = Vector3.right;

            var forcedTargets = new List<Vector3>(groupsForMatching.Count);
            for (int i = 0; i < groupsForMatching.Count; i++) forcedTargets.Add(Vector3.zero);
            forcedTargets[releasedIdx] = releasedLocal;

            Vector3 basePerp = Vector3.Cross(
                releasedLocal,
                Mathf.Abs(Vector3.Dot(releasedLocal, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right).normalized;
            if (basePerp.sqrMagnitude < 1e-12f) basePerp = Vector3.up;

            int placedOther = 0;
            for (int i = 0; i < groupsForMatching.Count; i++)
            {
                if (i == releasedIdx) continue;
                Vector3 curLocal = atom.transform.InverseTransformDirection(groupsForMatching[i].CurrentDirWorld).normalized;
                Vector3 perp = Vector3.ProjectOnPlane(curLocal, releasedLocal).normalized;
                if (perp.sqrMagnitude < 1e-12f)
                    perp = placedOther == 0 ? basePerp : -basePerp;
                if (nVseprGroup == 3 && placedOther == 1)
                    perp = -forcedTargets[FirstOtherIndex(forcedTargets, releasedIdx)];
                forcedTargets[i] = perp.normalized;
                placedOther++;
            }

            int[] identityPerm = new int[groupsForMatching.Count];
            for (int i = 0; i < identityPerm.Length; i++) identityPerm[i] = i;
            animation = BuildRedistributionAnimation(
                groupsForMatching,
                emptyOrbitals: null,
                forcedTargets,
                finalDirectionsEmptyOrbital: null,
                identityPerm,
                atom,
                atomOrbitalOp,
                guideOrbitalPredetermined,
                atomMoveAnimation,
                visitedAtoms,
                isBondingEvent,
                cyclicContext: null);
            return true;
        }

        // TODO: breaking special case for nVseprGroup == 4 and nVseprGroup == 5.
        return false;
    }

    static int FirstOtherIndex(List<Vector3> forcedTargets, int releasedIdx)
    {
        for (int i = 0; i < forcedTargets.Count; i++)
        {
            if (i == releasedIdx) continue;
            if (forcedTargets[i].sqrMagnitude > 1e-12f) return i;
        }
        return 0;
    }

    static RedistributionAnimation BuildRedistributionAnimation(
        List<GroupEntry> groups,
        List<GroupEntry> emptyOrbitals,
        List<Vector3> alignedTemplate,
        List<Vector3> finalDirectionsEmptyOrbital,
        int[] perm,
        AtomFunction atom,
        ElectronOrbitalFunction atomOrbitalOp,
        ElectronOrbitalFunction guideOrbitalPredetermined,
        System.Func<float, Vector3> atomMoveAnimation,
        HashSet<AtomFunction> visitedAtoms,
        bool isBondingEvent,
        CyclicRedistributionContext cyclicContext)
    {
        _ = atomMoveAnimation;
        var anim = new RedistributionAnimation(atom);
        if (groups == null || alignedTemplate == null || perm == null) return anim;
        int n = Mathf.Min(groups.Count, perm.Length);
        for (int i = 0; i < n; i++)
        {
            int ti = perm[i];
            if (ti < 0 || ti >= alignedTemplate.Count) continue;
            GroupEntry g = groups[i];
            if (g == null || g.Orbital == null) continue;
            if (g.Orbital == guideOrbitalPredetermined) continue;
            if (g.Kind == "bond" && g.Bond != null)
            {
                AtomFunction adjacentAtom = g.Bond.AtomA == atom ? g.Bond.AtomB : g.Bond.AtomA;
                var fragStart = new List<(AtomFunction atom, Vector3 worldPos0)>();
                Vector3 startPivot = atom.transform.position;
                Vector3 startNbrDir = g.CurrentDirWorld;
                if (adjacentAtom != null)
                {
                    List<AtomFunction> fragment = atom.GetAtomsOnSideOfSigmaBond(adjacentAtom);
                    for (int fi = 0; fi < fragment.Count; fi++)
                    {
                        var fa = fragment[fi];
                        if (fa == null) continue;
                        fragStart.Add((fa, fa.transform.position));
                    }
                    startNbrDir = (adjacentAtom.transform.position - atom.transform.position).normalized;
                    bool isOpPath = IsOperationPathGroup(atom, g, atomOrbitalOp);
                    if (!isOpPath)
                    {
                        Vector3 childGuideDirWorld = atom.transform.TransformDirection((-alignedTemplate[ti]).normalized);
                        Vector3 childGuideDirLocal = adjacentAtom.transform.InverseTransformDirection(childGuideDirWorld).normalized;
                        var childAnim = BuildOrbitalRedistribution(
                            adjacentAtom,
                            atom,
                            guideAtomOrbitalOp: null,
                            atomOrbitalOp: ResolveCyclicClosureSigmaOpForAtom(adjacentAtom, cyclicContext),
                            guideOrbitalPredetermined: g.Orbital,
                            finalDirectionForGuideOrbital: childGuideDirLocal,
                            atomMoveAnimation: atomMoveAnimation,
                            visitedAtoms: visitedAtoms,
                            isBondingEvent: isBondingEvent,
                            cyclicContext: cyclicContext);
                        anim.AddChild(childAnim);
                   
                    }
                }

                bool isCycleBlocked = cyclicContext != null
                    && cyclicContext.ChainRedistributionBlockedAtoms != null
                    && cyclicContext.ChainRedistributionBlockedAtoms.Contains(adjacentAtom);
                if (!isCycleBlocked)
                {
                    anim.AddOrbitalTarget(
                        g.Orbital,
                        g.CurrentDirWorld,
                        g.Radius,
                        alignedTemplate[ti],
                        isBondGroup: true,
                        bond: g.Bond,
                        startPivotWorld: startPivot,
                        startNeighborDirWorld: startNbrDir,
                        fragmentStartWorld: fragStart);
                }
            }
            else
            {
                anim.AddOrbitalTarget(g.Orbital, g.CurrentDirWorld, g.Radius, alignedTemplate[ti]);
            }
        }

        AppendEmptyOrbitalAssignments(
            anim,
            atom,
            emptyOrbitals,
            groups,
            alignedTemplate,
            perm,
            finalDirectionsEmptyOrbital);

        return anim;
    }

    static bool IsOperationPathGroup(AtomFunction atom, GroupEntry group, ElectronOrbitalFunction atomOrbitalOp)
    {
        if (group == null || atomOrbitalOp == null) return false;
        if (ReferenceEquals(group.Orbital, atomOrbitalOp)) return true;
        if (atom == null || group.Bond == null || atomOrbitalOp.Bond == null) return false;

        AtomFunction groupOther = group.Bond.AtomA == atom ? group.Bond.AtomB : group.Bond.AtomA;
        AtomFunction opOther = atomOrbitalOp.Bond.AtomA == atom ? atomOrbitalOp.Bond.AtomB : atomOrbitalOp.Bond.AtomA;
        if (groupOther == null || opOther == null) return false;
        return groupOther == opOther;
    }

    static void AppendEmptyOrbitalAssignments(
        RedistributionAnimation anim,
        AtomFunction atom,
        List<GroupEntry> emptyOrbitals,
        List<GroupEntry> groupsForMatching,
        List<Vector3> alignedTemplate,
        int[] perm,
        List<Vector3> finalDirectionsEmptyOrbital)
    {
        if (anim == null || atom == null || emptyOrbitals == null || emptyOrbitals.Count == 0) return;
        if (finalDirectionsEmptyOrbital == null || finalDirectionsEmptyOrbital.Count == 0) return;

        var occupiedNonEmpty = new List<Vector3>();
        if (groupsForMatching != null && alignedTemplate != null && perm != null)
        {
            int n = Mathf.Min(groupsForMatching.Count, perm.Length);
            for (int i = 0; i < n; i++)
            {
                int ti = perm[i];
                if (ti < 0 || ti >= alignedTemplate.Count) continue;
                occupiedNonEmpty.Add(alignedTemplate[ti].normalized);
            }
        }

        var remainingDirs = new List<Vector3>();
        for (int i = 0; i < finalDirectionsEmptyOrbital.Count; i++)
        {
            Vector3 d = finalDirectionsEmptyOrbital[i].normalized;
            bool overlapsOccupied = false;
            for (int j = 0; j < occupiedNonEmpty.Count; j++)
            {
                if (Vector3.Angle(d, occupiedNonEmpty[j]) < 8f)
                {
                    overlapsOccupied = true;
                    break;
                }
            }
            if (!overlapsOccupied)
                remainingDirs.Add(d);
        }
        if (remainingDirs.Count == 0) return;

        var remainingEmpty = new List<GroupEntry>(emptyOrbitals);
        Vector3? forcedOppositeOf = null;

        while (remainingEmpty.Count > 0 && remainingDirs.Count > 0)
        {
            int dirIdx = SelectNextEmptyDirectionIndex(remainingDirs, occupiedNonEmpty, forcedOppositeOf);
            if (dirIdx < 0 || dirIdx >= remainingDirs.Count) break;
            Vector3 targetDir = remainingDirs[dirIdx].normalized;

            int orbIdx = FindClosestEmptyOrbitalIndex(atom, remainingEmpty, targetDir);
            if (orbIdx < 0) break;
            GroupEntry e = remainingEmpty[orbIdx];
            if (e?.Orbital != null)
            {
                anim.AddOrbitalTarget(e.Orbital, e.CurrentDirWorld, e.Radius, targetDir, isEmptyGroup: true);
            }

            remainingEmpty.RemoveAt(orbIdx);
            remainingDirs.RemoveAt(dirIdx);
            forcedOppositeOf = targetDir;
        }
    }

    static int SelectNextEmptyDirectionIndex(
        List<Vector3> remainingDirs,
        List<Vector3> occupiedNonEmpty,
        Vector3? forcedOppositeOf)
    {
        if (remainingDirs == null || remainingDirs.Count == 0) return -1;

        int forced = -1;
        if (forcedOppositeOf.HasValue)
        {
            Vector3 desired = (-forcedOppositeOf.Value).normalized;
            float bestDot = 0.985f;
            for (int i = 0; i < remainingDirs.Count; i++)
            {
                float d = Vector3.Dot(remainingDirs[i].normalized, desired);
                if (d > bestDot)
                {
                    bestDot = d;
                    forced = i;
                }
            }
            if (forced >= 0) return forced;
        }

        int oppToOccupied = -1;
        float bestOpp = 0.985f;
        if (occupiedNonEmpty != null && occupiedNonEmpty.Count > 0)
        {
            for (int i = 0; i < remainingDirs.Count; i++)
            {
                Vector3 d = remainingDirs[i].normalized;
                for (int j = 0; j < occupiedNonEmpty.Count; j++)
                {
                    float opp = Vector3.Dot(d, (-occupiedNonEmpty[j]).normalized);
                    if (opp > bestOpp)
                    {
                        bestOpp = opp;
                        oppToOccupied = i;
                    }
                }
            }
        }
        if (oppToOccupied >= 0) return oppToOccupied;

        return UnityEngine.Random.Range(0, remainingDirs.Count);
    }

    static int FindClosestEmptyOrbitalIndex(
        AtomFunction atom,
        List<GroupEntry> emptyOrbitals,
        Vector3 targetDirLocal)
    {
        if (atom == null || emptyOrbitals == null || emptyOrbitals.Count == 0) return -1;
        int best = -1;
        float bestDeg = float.PositiveInfinity;
        for (int i = 0; i < emptyOrbitals.Count; i++)
        {
            GroupEntry e = emptyOrbitals[i];
            if (e?.Orbital == null) continue;
            Vector3 curLocal = atom.transform.InverseTransformDirection(e.CurrentDirWorld).normalized;
            float deg = Vector3.Angle(curLocal, targetDirLocal.normalized);
            if (deg < bestDeg)
            {
                bestDeg = deg;
                best = i;
            }
        }
        return best;
    }

    static List<Vector3> BuildFinalDirectionsEmptyOrbitalTemplate(List<Vector3> finalDirectionsTemplate)
    {
        var outDirs = new List<Vector3>();
        if (finalDirectionsTemplate == null || finalDirectionsTemplate.Count == 0) return outDirs;
        int nVseprGroup = finalDirectionsTemplate.Count;

        if (nVseprGroup == 1 || nVseprGroup == 2)
        {
            var oct = new List<Vector3>
            {
                Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back
            };
            Quaternion q = Quaternion.FromToRotation(Vector3.right, finalDirectionsTemplate[0].normalized);
            for (int i = 0; i < oct.Count; i++)
            {
                Vector3 d = (q * oct[i]).normalized;
                bool overlaps = false;
                for (int j = 0; j < finalDirectionsTemplate.Count; j++)
                {
                    if (Vector3.Angle(d, finalDirectionsTemplate[j].normalized) < 8f)
                    {
                        overlaps = true;
                        break;
                    }
                }
                if (!overlaps) outDirs.Add(d);
            }
            return outDirs;
        }

        if (nVseprGroup == 3)
        {
            Vector3 a = finalDirectionsTemplate[0].normalized;
            Vector3 b = finalDirectionsTemplate[1].normalized;
            Vector3 c = finalDirectionsTemplate[2].normalized;
            Vector3 n = Vector3.Cross(b - a, c - a).normalized;
            if (n.sqrMagnitude < 1e-12f) n = Vector3.forward;
            outDirs.Add(n);
            outDirs.Add(-n);
            return outDirs;
        }

        // TODO: nVseprGroup == 4 and nVseprGroup == 5 empty-orbital templates.
        return outDirs;
    }

    static int[] FindBestCombinationVSEPRGroupToFinalDirection(
        List<GroupEntry> groups,
        List<Vector3> alignedTemplate,
        AtomFunction atom,
        GroupEntry guideGroup,
        Dictionary<int, int> fixedGroupTemplateAssignments = null,
        bool disableLegacyGuidePreassign = false)
    {
        int n = groups != null ? groups.Count : 0;
        if (n == 0 || alignedTemplate == null || alignedTemplate.Count < n)
            return Array.Empty<int>();

        var used = new bool[alignedTemplate.Count];
        var cur = new int[n];
        var best = new int[n];
        float bestCost = float.PositiveInfinity;
        int guideIdx = -1;
        if (!disableLegacyGuidePreassign && guideGroup != null)
        {
            for (int i = 0; i < n; i++)
            {
                if (!ReferenceEquals(groups[i], guideGroup)) continue;
                guideIdx = i;
                break;
            }
        }

        if (fixedGroupTemplateAssignments != null)
        {
            var fixedTargets = new HashSet<int>();
            foreach (var kv in fixedGroupTemplateAssignments)
            {
                int gi = kv.Key;
                int ti = kv.Value;
                if (gi < 0 || gi >= n || ti < 0 || ti >= alignedTemplate.Count)
                    return Array.Empty<int>();
                if (!fixedTargets.Add(ti))
                    return Array.Empty<int>();
            }
        }

        var dfsOrder = new int[n];
        int orderPos = 0;
        if (fixedGroupTemplateAssignments != null && fixedGroupTemplateAssignments.Count > 0)
        {
            var fixedKeys = new List<int>(fixedGroupTemplateAssignments.Keys);
            fixedKeys.Sort();
            for (int i = 0; i < fixedKeys.Count; i++)
                dfsOrder[orderPos++] = fixedKeys[i];
        }
        for (int i = 0; i < n; i++)
        {
            if (fixedGroupTemplateAssignments != null && fixedGroupTemplateAssignments.ContainsKey(i))
                continue;
            dfsOrder[orderPos++] = i;
        }

        void Dfs(int depth, float costSoFar)
        {
            if (depth >= n)
            {
                if (costSoFar < bestCost)
                {
                    bestCost = costSoFar;
                    for (int k = 0; k < n; k++) best[k] = cur[k];
                }
                return;
            }

            int gi = dfsOrder[depth];
            GroupEntry g = groups[gi];
            Vector3 gLocal = g.CurrentDirWorld.sqrMagnitude > 1e-12f
                ? atom.transform.InverseTransformDirection(g.CurrentDirWorld).normalized
                : Vector3.right;
            int tStart = 0;
            int tEnd = alignedTemplate.Count - 1;
            if (fixedGroupTemplateAssignments != null && fixedGroupTemplateAssignments.TryGetValue(gi, out int forcedTemplateIndex))
            {
                tStart = forcedTemplateIndex;
                tEnd = forcedTemplateIndex;
            }
            else if (guideIdx >= 0 && gi == guideIdx)
            {
                tStart = 0;
                tEnd = 0;
            }
            for (int t = tStart; t <= tEnd; t++)
            {
                if (used[t]) continue;
                Vector3 td = alignedTemplate[t];
                float dirDelta = (td - gLocal).sqrMagnitude;
                float add = Mathf.Max(1f, g.MassWeight) * dirDelta;
                float next = costSoFar + add;
                if (next >= bestCost) continue;
                used[t] = true;
                cur[gi] = t;
                Dfs(depth + 1, next);
                used[t] = false;
            }
        }

        Dfs(0, 0f);
        return best;
    }

    /// <summary>
    /// Cyclic template skips guide alignment; pin the in-operation (forming σ) row to the
    /// cycle-edge template slot that matches <paramref name="finalDirectionForGuideOrbital"/>
    /// so prebond OP does not collapse onto a rest direction. If that slot is already fixed,
    /// OP takes it when it aligns at least as well with the bond axis as the occupant (plus
    /// a small bias so the forming orbital wins ties).
    /// </summary>
    static void TryApplyCyclicOpBondSitePreassign(
        AtomFunction atom,
        List<GroupEntry> groupsForMatching,
        List<Vector3> cyclicTemplate,
        Dictionary<int, int> cyclicAssignmentFix,
        ElectronOrbitalFunction atomOrbitalOp,
        Vector3 finalDirectionForGuideOrbital)
    {
        if (atom == null
            || groupsForMatching == null
            || cyclicTemplate == null
            || cyclicAssignmentFix == null
            || atomOrbitalOp == null)
            return;
        if (finalDirectionForGuideOrbital.sqrMagnitude < 1e-12f || cyclicTemplate.Count < 2)
            return;

        Vector3 bondDir = finalDirectionForGuideOrbital.normalized;
        int opRow = -1;
        for (int i = 0; i < groupsForMatching.Count; i++)
        {
            if (ReferenceEquals(groupsForMatching[i]?.Orbital, atomOrbitalOp))
            {
                opRow = i;
                break;
            }
        }
        if (opRow < 0)
            return;

        int bondSlotCount = Mathf.Min(2, cyclicTemplate.Count);
        int bestT = 0;
        float bestAxisDot = float.NegativeInfinity;
        for (int ti = 0; ti < bondSlotCount; ti++)
        {
            Vector3 tv = cyclicTemplate[ti];
            if (tv.sqrMagnitude < 1e-12f)
                continue;
            float d = Vector3.Dot(tv.normalized, bondDir);
            if (d > bestAxisDot)
            {
                bestAxisDot = d;
                bestT = ti;
            }
        }

        float RowBondDot(int row)
        {
            if (row < 0 || row >= groupsForMatching.Count)
                return -1f;
            GroupEntry g = groupsForMatching[row];
            if (g == null)
                return -1f;
            if (g.CurrentDirWorld.sqrMagnitude < 1e-12f)
            {
                if (ReferenceEquals(g.Orbital, atomOrbitalOp))
                    return Mathf.Max(0f, bestAxisDot);
                return -1f;
            }
            Vector3 loc = atom.transform.InverseTransformDirection(g.CurrentDirWorld).normalized;
            return Vector3.Dot(loc, bondDir);
        }

        if (cyclicAssignmentFix.TryGetValue(opRow, out int existingT) && existingT == bestT)
            return;

        int occupant = -1;
        foreach (var kv in cyclicAssignmentFix)
        {
            if (kv.Value == bestT)
            {
                occupant = kv.Key;
                break;
            }
        }

        if (occupant == opRow)
            return;

        float opScore = RowBondDot(opRow);
        if (ReferenceEquals(groupsForMatching[opRow]?.Orbital, atomOrbitalOp))
        {
            // Lobe pivot→center can be opposite the internuclear closure axis; do not block steal on negative dot.
            if (opScore < 0f)
                opScore = Mathf.Max(opScore, bestAxisDot);
            opScore += 0.02f;
        }

        if (occupant < 0)
        {
            cyclicAssignmentFix[opRow] = bestT;
            return;
        }

        float occScore = RowBondDot(occupant);
        if (opScore >= occScore)
        {
            cyclicAssignmentFix.Remove(occupant);
            cyclicAssignmentFix[opRow] = bestT;
        }
    }

    static bool TryBuildCyclicFinalDirectionsTemplate(
        AtomFunction atom,
        int nVseprGroup,
        List<GroupEntry> groupsForMatching,
        CyclicRedistributionContext cyclicContext,
        out List<Vector3> template,
        Dictionary<int, int> fixedAssignments)
    {
        template = null;
        if (fixedAssignments == null) return false;
        fixedAssignments.Clear();
        if (atom == null || cyclicContext == null)
            return false;
        if (groupsForMatching == null || nVseprGroup < 3 || nVseprGroup > 4) return false;

        if (!TryResolveCycleContributorNeighborsFromContext(
            atom, cyclicContext, out AtomFunction neighborA, out AtomFunction neighborB))
            return false;

        Vector3 pivotWorld = GetFinalWorldForAtom(cyclicContext, atom);
        Vector3 nAWorld = GetFinalWorldForAtom(cyclicContext, neighborA);
        Vector3 nBWorld = GetFinalWorldForAtom(cyclicContext, neighborB);
        Vector3 dirAWorld = (nAWorld - pivotWorld).normalized;
        Vector3 dirBWorld = (nBWorld - pivotWorld).normalized;
        if (dirAWorld.sqrMagnitude < 1e-12f || dirBWorld.sqrMagnitude < 1e-12f) return false;

        Vector3 dirA = atom.transform.InverseTransformDirection(dirAWorld).normalized;
        Vector3 dirB = atom.transform.InverseTransformDirection(dirBWorld).normalized;
        if (dirA.sqrMagnitude < 1e-12f || dirB.sqrMagnitude < 1e-12f) return false;

        int idxA = ResolveCycleContributorGroupIndex(
            groupsForMatching,
            atom,
            neighborA,
            dirA,
            excludeGroupIndex: -1);
        int idxB = ResolveCycleContributorGroupIndex(
            groupsForMatching,
            atom,
            neighborB,
            dirB,
            excludeGroupIndex: idxA);
        if (idxA < 0 || idxB < 0 || idxA == idxB) return false;

        template = new List<Vector3>(nVseprGroup) { dirA, dirB };
        fixedAssignments[idxA] = 0;
        fixedAssignments[idxB] = 1;

        Vector3 centerToPivotWorld = pivotWorld - cyclicContext.CycleCenterWorld;
        Vector3 outwardWorld = centerToPivotWorld.sqrMagnitude > 1e-12f
            ? centerToPivotWorld.normalized
            : -(dirAWorld + dirBWorld).normalized;
        if (outwardWorld.sqrMagnitude < 1e-12f)
            outwardWorld = Vector3.Cross(dirAWorld, dirBWorld).normalized;
        if (outwardWorld.sqrMagnitude < 1e-12f)
            outwardWorld = Vector3.up;

        Vector3 outward = atom.transform.InverseTransformDirection(outwardWorld).normalized;
        if (outward.sqrMagnitude < 1e-12f) outward = Vector3.up;

        if (nVseprGroup == 3)
        {
            Vector3 planeNormal = Vector3.Cross(dirA, dirB).normalized;
            if (planeNormal.sqrMagnitude < 1e-12f)
                planeNormal = Vector3.Cross(dirA, Vector3.up).normalized;
            if (planeNormal.sqrMagnitude < 1e-12f)
                planeNormal = Vector3.forward;

            Vector3 inPlaneOutward = Vector3.ProjectOnPlane(outward, planeNormal).normalized;
            if (inPlaneOutward.sqrMagnitude < 1e-12f)
                inPlaneOutward = Vector3.ProjectOnPlane(-(dirA + dirB), planeNormal).normalized;
            if (inPlaneOutward.sqrMagnitude < 1e-12f)
                inPlaneOutward = Vector3.Cross(planeNormal, dirA).normalized;
            template.Add(inPlaneOutward.sqrMagnitude > 1e-12f ? inPlaneOutward : Vector3.up);
            return true;
        }

        // Use the contributor-plane normal first so the two non-cycle directions span
        // above/below the cycle contributor plane (up/down), not lateral left/right.
        Vector3 perp = Vector3.Cross(dirA, dirB).normalized;
        if (perp.sqrMagnitude > 1e-12f)
            perp = Vector3.ProjectOnPlane(perp, outward).normalized;
        if (perp.sqrMagnitude < 1e-12f)
            perp = Vector3.Cross(outward, dirA + dirB).normalized;
        if (perp.sqrMagnitude < 1e-12f)
            perp = Vector3.Cross(outward, Mathf.Abs(Vector3.Dot(outward, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right).normalized;
        if (perp.sqrMagnitude < 1e-12f)
            perp = Vector3.forward;

        const float halfTetraAngleDeg = 54.73561f;
        float c = Mathf.Cos(halfTetraAngleDeg * Mathf.Deg2Rad);
        float s = Mathf.Sin(halfTetraAngleDeg * Mathf.Deg2Rad);
        Vector3 rest0 = (outward * c + perp * s).normalized;
        Vector3 rest1 = (outward * c - perp * s).normalized;
        template.Add(rest0.sqrMagnitude > 1e-12f ? rest0 : outward);
        template.Add(rest1.sqrMagnitude > 1e-12f ? rest1 : -outward);
        return true;
    }

    static bool TryResolveCycleContributorNeighborsFromContext(
        AtomFunction atom,
        CyclicRedistributionContext cyclicContext,
        out AtomFunction neighborA,
        out AtomFunction neighborB)
    {
        neighborA = null;
        neighborB = null;
        if (atom == null || cyclicContext == null || cyclicContext.FinalWorldByAtom == null)
            return false;
        if (!cyclicContext.FinalWorldByAtom.ContainsKey(atom))
            return false;

        var cycleNeighbors = new List<AtomFunction>(2);
        foreach (var cb in atom.CovalentBonds)
        {
            if (cb == null || !cb.IsSigmaBondLine()) continue;
            AtomFunction other = cb.AtomA == atom ? cb.AtomB : cb.AtomA;
            if (other == null) continue;
            if (!cyclicContext.FinalWorldByAtom.ContainsKey(other)) continue;
            cycleNeighbors.Add(other);
        }

        if (cycleNeighbors.Count >= 2)
        {
            neighborA = cycleNeighbors[0];
            neighborB = cycleNeighbors[1];
            return true;
        }

        // If only one cycle sigma neighbor exists (common at prebond terminal contributors),
        // complete the pair by nearest cycle atom from final geometry.
        if (cycleNeighbors.Count == 1)
        {
            AtomFunction bondedNeighbor = cycleNeighbors[0];
            AtomFunction nearestOther = null;
            float bestSq = float.PositiveInfinity;
            Vector3 atomW = GetFinalWorldForAtom(cyclicContext, atom);
            foreach (var kv in cyclicContext.FinalWorldByAtom)
            {
                AtomFunction cand = kv.Key;
                if (cand == null || cand == atom || cand == bondedNeighbor) continue;
                float d2 = (kv.Value - atomW).sqrMagnitude;
                if (d2 < bestSq)
                {
                    bestSq = d2;
                    nearestOther = cand;
                }
            }
            if (nearestOther != null)
            {
                neighborA = bondedNeighbor;
                neighborB = nearestOther;
                return true;
            }
        }

        // Legacy fallback only when it is a valid non-self pair.
        if (cyclicContext.CycleNeighborA != null
            && cyclicContext.CycleNeighborB != null
            && cyclicContext.CycleNeighborA != atom
            && cyclicContext.CycleNeighborB != atom
            && cyclicContext.CycleNeighborA != cyclicContext.CycleNeighborB)
        {
            neighborA = cyclicContext.CycleNeighborA;
            neighborB = cyclicContext.CycleNeighborB;
            return true;
        }

        return false;
    }

    static int FindBondGroupIndexForNeighbor(List<GroupEntry> groups, AtomFunction atom, AtomFunction neighbor)
    {
        if (groups == null || atom == null || neighbor == null) return -1;
        for (int i = 0; i < groups.Count; i++)
        {
            GroupEntry g = groups[i];
            if (g == null || g.Kind != "bond" || g.Bond == null) continue;
            AtomFunction other = g.Bond.AtomA == atom ? g.Bond.AtomB : g.Bond.AtomA;
            if (other == neighbor) return i;
        }
        return -1;
    }

    static int ResolveCycleContributorGroupIndex(
        List<GroupEntry> groups,
        AtomFunction atom,
        AtomFunction neighbor,
        Vector3 expectedDirLocal,
        int excludeGroupIndex)
    {
        if (groups == null || atom == null) return -1;

        // Primary: true bonded contributor row for this neighbor.
        int bondedIdx = FindBondGroupIndexForNeighbor(groups, atom, neighbor);
        if (bondedIdx >= 0 && bondedIdx != excludeGroupIndex)
            return bondedIdx;

        // Prebond cyclic phase can represent one contributor as nonbond.
        // Fallback: pick the row whose current direction best matches the expected
        // final contributor direction (neighborFinal - atomFinal).
        int bestIdx = -1;
        float bestAng = float.PositiveInfinity;
        for (int i = 0; i < groups.Count; i++)
        {
            if (i == excludeGroupIndex) continue;
            GroupEntry g = groups[i];
            if (g == null) continue;
            Vector3 gLocal = atom.transform.InverseTransformDirection(g.CurrentDirWorld).normalized;
            if (gLocal.sqrMagnitude < 1e-12f) continue;
            float ang = Vector3.Angle(gLocal, expectedDirLocal);
            if (ang < bestAng)
            {
                bestAng = ang;
                bestIdx = i;
            }
        }
        return bestIdx;
    }

    static Vector3 GetFinalWorldForAtom(CyclicRedistributionContext ctx, AtomFunction atom)
    {
        if (atom == null) return Vector3.zero;
        if (ctx != null && ctx.FinalWorldByAtom != null && ctx.FinalWorldByAtom.TryGetValue(atom, out Vector3 w))
            return w;
        return atom.transform.position;
    }

    /// <summary>
    /// Closure σ axis in <paramref name="atom"/> local space for the pivot↔guide endpoints only.
    /// Chain recursion passes a different logical guide; this matches the real closing leg from <see cref="FinalWorldByAtom"/>.
    /// </summary>
    static Vector3 TryGetCyclicClosureBondDirLocal(AtomFunction atom, CyclicRedistributionContext ctx)
    {
        if (atom == null || ctx == null || ctx.FinalWorldByAtom == null)
            return Vector3.zero;
        if (ctx.PivotAtom == null || ctx.CycleNeighborA == null)
            return Vector3.zero;
        if (ReferenceEquals(atom, ctx.PivotAtom))
        {
            Vector3 pivotW = GetFinalWorldForAtom(ctx, ctx.PivotAtom);
            Vector3 guideW = GetFinalWorldForAtom(ctx, ctx.CycleNeighborA);
            Vector3 leg = guideW - pivotW;
            if (leg.sqrMagnitude < 1e-12f)
                return Vector3.zero;
            return atom.transform.InverseTransformDirection(leg.normalized).normalized;
        }
        if (ReferenceEquals(atom, ctx.CycleNeighborA))
        {
            Vector3 pivotW = GetFinalWorldForAtom(ctx, ctx.PivotAtom);
            Vector3 guideW = GetFinalWorldForAtom(ctx, atom);
            Vector3 leg = pivotW - guideW;
            if (leg.sqrMagnitude < 1e-12f)
                return Vector3.zero;
            return atom.transform.InverseTransformDirection(leg.normalized).normalized;
        }
        return Vector3.zero;
    }

    static ElectronOrbitalFunction ResolveCyclicClosureSigmaOpForAtom(
        AtomFunction adjacentAtom,
        CyclicRedistributionContext ctx)
    {
        if (adjacentAtom == null || ctx == null)
            return null;
        if (ReferenceEquals(adjacentAtom, ctx.PivotAtom))
            return ctx.PivotSigmaOp;
        if (ReferenceEquals(adjacentAtom, ctx.CycleNeighborA))
            return ctx.GuideSigmaOp;
        return null;
    }

    /// <summary>
    /// Pivot / closure-guide σ OP may not appear in <see cref="AtomFunction.BondedOrbitals"/> with the same
    /// reference as <see cref="CyclicRedistributionContext.PivotSigmaOp"/> / <c>GuideSigmaOp</c>, or may be
    /// omitted from bond rows. Add one matching <see cref="GroupEntry"/> so VSEPR count and perm length match
    /// tetrahedral closure (four group→template pairs).
    /// </summary>
    static void EnsureCyclicClosureSigmaOpCountedForMatching(
        AtomFunction atom,
        ElectronOrbitalFunction atomOrbitalOp,
        CyclicRedistributionContext cyclicContext,
        List<GroupEntry> bondingGroups,
        List<GroupEntry> nonbondingOccupied)
    {
        if (atom == null || atomOrbitalOp == null || cyclicContext == null || nonbondingOccupied == null)
            return;
        if (!ReferenceEquals(atom, cyclicContext.PivotAtom)
            && !ReferenceEquals(atom, cyclicContext.CycleNeighborA))
            return;

        if (bondingGroups != null)
        {
            for (int i = 0; i < bondingGroups.Count; i++)
            {
                if (bondingGroups[i] != null && ReferenceEquals(bondingGroups[i].Orbital, atomOrbitalOp))
                    return;
            }
        }
        for (int i = 0; i < nonbondingOccupied.Count; i++)
        {
            if (nonbondingOccupied[i] != null && ReferenceEquals(nonbondingOccupied[i].Orbital, atomOrbitalOp))
                return;
        }

        Vector3 bondDirLocal = TryGetCyclicClosureBondDirLocal(atom, cyclicContext);
        Vector3 opRayWorld = atomOrbitalOp.transform.position - atom.transform.position;
        // Start pose for Slerp must be the real lobe direction, not the closure axis (same as target → no spin).
        Vector3 dirWorld = opRayWorld.sqrMagnitude > 1e-12f
            ? opRayWorld.normalized
            : (bondDirLocal.sqrMagnitude > 1e-12f
                ? atom.transform.TransformDirection(bondDirLocal).normalized
                : Vector3.right);
        float opRadius = Mathf.Max(0.01f, opRayWorld.magnitude);

        AtomFunction partner = ReferenceEquals(atom, cyclicContext.PivotAtom)
            ? cyclicContext.CycleNeighborA
            : cyclicContext.PivotAtom;
        float massW = partner != null
            ? ComputeAtomComponentMass(partner)
            : Mathf.Max(1e-3f, ElectronRedistributionGuide.GetStandardAtomicWeight(atom.AtomicNumber));

        nonbondingOccupied.Add(new GroupEntry
        {
            Kind = "nonbond-op-empty-counted-bonding",
            Orbital = atomOrbitalOp,
            MassWeight = Mathf.Max(1e-3f, massW),
            CurrentDirWorld = dirWorld,
            Radius = opRadius
        });
    }

    static GroupEntry GetHeaviestGroupOpPrioritized(
        List<GroupEntry> bondingGroups,
        List<GroupEntry> nonbondingOccupied,
        List<GroupEntry> emptyOrbitals,
        ElectronOrbitalFunction atomOrbitalOp)
    {
        GroupEntry best = null;
        float bestMass = float.NegativeInfinity;

        void Visit(List<GroupEntry> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                GroupEntry row = rows[i];
                if (row == null) continue;

                if (best == null || row.MassWeight > bestMass || (row.Orbital == atomOrbitalOp && row.MassWeight >= bestMass))
                {
                    best = row;
                    bestMass = row.MassWeight;
                }
            }
        }

        Visit(bondingGroups);
        Visit(nonbondingOccupied);
        Visit(emptyOrbitals);

        bool IsSameBondAxisAsOp(GroupEntry row)
        {
            if (row == null || atomOrbitalOp == null) return false;
            if (ReferenceEquals(row.Orbital, atomOrbitalOp)) return true;
            if (row.Bond == null || atomOrbitalOp.Bond == null) return false;
            AtomFunction rA = row.Bond.AtomA;
            AtomFunction rB = row.Bond.AtomB;
            AtomFunction oA = atomOrbitalOp.Bond.AtomA;
            AtomFunction oB = atomOrbitalOp.Bond.AtomB;
            return (rA == oA && rB == oB) || (rA == oB && rB == oA);
        }

        float opPathBestMass = float.NegativeInfinity;
        GroupEntry opPathBest = null;
        void VisitOpPath(List<GroupEntry> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                GroupEntry row = rows[i];
                if (row == null || !IsSameBondAxisAsOp(row)) continue;
                if (opPathBest == null || row.MassWeight > opPathBestMass)
                {
                    opPathBest = row;
                    opPathBestMass = row.MassWeight;
                }
            }
        }

        VisitOpPath(bondingGroups);
        VisitOpPath(nonbondingOccupied);
        VisitOpPath(emptyOrbitals);
        return best;
    }

    static void ApplyEventSpecificGroupAdjustments(
        AtomFunction atom,
        AtomFunction guideAtom,
        ElectronOrbitalFunction atomOrbitalOp,
        List<GroupEntry> nonbondingOccupied,
        List<GroupEntry> emptyOrbitals,
        bool isBondingEvent)
    {
        if (atom == null || atomOrbitalOp == null || emptyOrbitals == null || nonbondingOccupied == null)
            return;

        bool isPiBondFormationEvent = isBondingEvent && guideAtom != null && atom.GetBondsTo(guideAtom) > 0;
        if (isPiBondFormationEvent)
        {
            for (int i = nonbondingOccupied.Count - 1; i >= 0; i--)
            {
                if (nonbondingOccupied[i] != null && ReferenceEquals(nonbondingOccupied[i].Orbital, atomOrbitalOp))
                {
                    nonbondingOccupied.RemoveAt(i);
                    break;
                }
            }
        }

        int opEmptyIdx = -1;
        for (int i = 0; i < emptyOrbitals.Count; i++)
        {
            if (!ReferenceEquals(emptyOrbitals[i].Orbital, atomOrbitalOp)) continue;
            opEmptyIdx = i;
            break;
        }
        if (opEmptyIdx < 0)
            return;

        bool addOpEmptyAsOccupiedForBonding = isBondingEvent && !isPiBondFormationEvent;

        GroupEntry moved = emptyOrbitals[opEmptyIdx];
        moved.Kind = addOpEmptyAsOccupiedForBonding
            ? "nonbond-op-empty-counted-bonding"
            : "nonbond-op-empty-preserved";
        moved.MassWeight = addOpEmptyAsOccupiedForBonding
            ? ComputeAtomComponentMass(guideAtom)
            : Mathf.Max(1e-3f, ElectronRedistributionGuide.GetStandardAtomicWeight(atom.AtomicNumber));
        if (isBondingEvent)
        {
            if (addOpEmptyAsOccupiedForBonding)
                nonbondingOccupied.Add(moved);
            emptyOrbitals.RemoveAt(opEmptyIdx);
        }
        else
        {
            // π bonding and breaking: keep OP empty in emptyOrbitals so empty-target assignment can rotate it.
            moved.Kind = isBondingEvent
                ? "empty-op-preserved-pi-bonding"
                : "empty-op-released-breaking";
        }
    }

    static float ComputeBondGroupMassWeight(AtomFunction center, CovalentBond bond)
    {
        if (center == null || bond == null) return 1f;
        var other = bond.AtomA == center ? bond.AtomB : bond.AtomA;
        if (other == null) return 1f;
        // Same substituent fragment and standard atomic weights as <see cref="ElectronRedistributionGuide.SumSubstituentMassThroughSigmaEdge"/>.
        float m = ElectronRedistributionGuide.SumSubstituentMassThroughSigmaEdge(center, other);
        return Mathf.Max(1e-3f, m);
    }

    static float ComputeAtomComponentMass(AtomFunction start)
    {
        if (start == null) return 1f;
        var component = start.GetConnectedMolecule();
        if (component == null || component.Count == 0)
            return Mathf.Max(1e-3f, ElectronRedistributionGuide.GetStandardAtomicWeight(start.AtomicNumber));
        float total = 0f;
        foreach (var a in component)
        {
            if (a == null) continue;
            total += ElectronRedistributionGuide.GetStandardAtomicWeight(a.AtomicNumber);
        }
        return Mathf.Max(1e-3f, total);
    }

    /// <summary>
    /// σ bond lines + occupied nonbonding domains (same basis as <see cref="BuildOrbitalRedistribution"/> group lists,
    /// without event-specific empty-op reclassification). Used for tetrahedral–tetrahedral stagger checks.
    /// </summary>
    static int CountVseprGroupsForAtom(AtomFunction a)
    {
        if (a == null) return 0;
        int bonds = 0;
        foreach (var cb in a.CovalentBonds)
        {
            if (cb == null || cb.Orbital == null || !cb.IsSigmaBondLine()) continue;
            bonds++;
        }
        int nonbondingOccupied = 0;
        foreach (var orb in a.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != a.transform) continue;
            if (orb.ElectronCount > 0) nonbondingOccupied++;
        }
        return bonds + nonbondingOccupied;
    }

    static bool HasPiBondBetweenAtoms(AtomFunction a, AtomFunction b)
    {
        if (a == null || b == null) return false;
        foreach (var cb in a.CovalentBonds)
        {
            if (cb == null || cb.IsSigmaBondLine()) continue;
            AtomFunction other = cb.AtomA == a ? cb.AtomB : cb.AtomA;
            if (other == b) return true;
        }
        return false;
    }

    /// <summary>
    /// Maps canonical template vertex 0 onto <paramref name="guideDirLocal"/>, then when both centers are tetrahedral,
    /// rotates around that axis so the template is staggered (60°) relative to the guide atom’s real substituent clock.
    /// </summary>
    static List<Vector3> AlignFinalDirectionsTemplateToGuide(
        List<Vector3> finalDirectionsTemplate,
        Vector3 guideDirLocal,
        AtomFunction nonGuideAtom,
        AtomFunction guideAtom,
        int nVseprGroup)
    {
        var aligned = new List<Vector3>();
        if (finalDirectionsTemplate == null || finalDirectionsTemplate.Count == 0)
            return aligned;

        Vector3 axisLocal = guideDirLocal.sqrMagnitude > 1e-12f ? guideDirLocal.normalized : Vector3.right;
        Quaternion qAlign = Quaternion.FromToRotation(finalDirectionsTemplate[0], axisLocal);

        Quaternion qTotal = qAlign;
        if (nVseprGroup == 4
            && guideAtom != null
            && CountVseprGroupsForAtom(guideAtom) == 4
            && finalDirectionsTemplate.Count >= 2
            && TryGetGuideTetrahedralStaggerReferenceInNonGuideLocal(
                nonGuideAtom, guideAtom, axisLocal, out Vector3 refInPlaneLocal))
        {
            Vector3 v1 = (qAlign * finalDirectionsTemplate[1]).normalized;
            Vector3 aLocal = Vector3.ProjectOnPlane(v1, axisLocal);
            if (aLocal.sqrMagnitude > 1e-10f && refInPlaneLocal.sqrMagnitude > 1e-10f)
            {
                aLocal.Normalize();
                refInPlaneLocal.Normalize();
                float eclipseToRefDeg = Vector3.SignedAngle(aLocal, refInPlaneLocal, axisLocal);
                const float staggerDihedralDegrees = 60f;
                float twistDeg = eclipseToRefDeg + staggerDihedralDegrees;
                Quaternion qTwist = Quaternion.AngleAxis(twistDeg, axisLocal);
                qTotal = qTwist * qAlign;
            }
        }
        else if (nVseprGroup == 3
            && guideAtom != null
            && CountVseprGroupsForAtom(guideAtom) == 3
            && HasPiBondBetweenAtoms(nonGuideAtom, guideAtom)
            && finalDirectionsTemplate.Count >= 2
            && TryGetGuideTetrahedralStaggerReferenceInNonGuideLocal(
                nonGuideAtom, guideAtom, axisLocal, out Vector3 trigonalRefInPlaneLocal))
        {
            Vector3 v1 = (qAlign * finalDirectionsTemplate[1]).normalized;
            Vector3 aLocal = Vector3.ProjectOnPlane(v1, axisLocal);
            if (aLocal.sqrMagnitude > 1e-10f && trigonalRefInPlaneLocal.sqrMagnitude > 1e-10f)
            {
                aLocal.Normalize();
                trigonalRefInPlaneLocal.Normalize();
                float eclipseToRefDeg = Vector3.SignedAngle(aLocal, trigonalRefInPlaneLocal, axisLocal);
                Quaternion qTwist = Quaternion.AngleAxis(eclipseToRefDeg, axisLocal);
                qTotal = qTwist * qAlign;
            }
        }

        for (int i = 0; i < finalDirectionsTemplate.Count; i++)
            aligned.Add((qTotal * finalDirectionsTemplate[i]).normalized);
        return aligned;
    }

    /// <summary>
    /// One substituent direction on the guide atom, projected into the plane perpendicular to the non-guide→guide axis,
    /// expressed in non-guide local space — for aligning stagger vs the guide’s actual conformation.
    /// </summary>
    static bool TryGetGuideTetrahedralStaggerReferenceInNonGuideLocal(
        AtomFunction nonGuideAtom,
        AtomFunction guideAtom,
        Vector3 axisLocalNonGuideTowardGuide,
        out Vector3 refInPlaneLocal)
    {
        refInPlaneLocal = default;
        if (nonGuideAtom == null || guideAtom == null) return false;
        Vector3 axisWorld = nonGuideAtom.transform.TransformDirection(axisLocalNonGuideTowardGuide).normalized;
        if (axisWorld.sqrMagnitude < 1e-12f) return false;

        Vector3 towardNonGuideWorld = (nonGuideAtom.transform.position - guideAtom.transform.position).normalized;
        if (towardNonGuideWorld.sqrMagnitude < 1e-12f) return false;

        const float sigmaAxisMaxDeg = 18f;

        foreach (var cb in guideAtom.CovalentBonds)
        {
            if (cb == null || cb.Orbital == null || !cb.IsSigmaBondLine()) continue;
            Vector3 dw = (cb.Orbital.transform.position - guideAtom.transform.position).normalized;
            if (Vector3.Angle(dw, towardNonGuideWorld) < sigmaAxisMaxDeg)
                continue;
            Vector3 pw = Vector3.ProjectOnPlane(dw, axisWorld);
            if (pw.sqrMagnitude < 1e-8f) continue;
            refInPlaneLocal = nonGuideAtom.transform.InverseTransformDirection(pw.normalized).normalized;
            return true;
        }

        foreach (var orb in guideAtom.BondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != guideAtom.transform) continue;
            if (orb.ElectronCount <= 0) continue;
            Vector3 dw = (orb.transform.position - guideAtom.transform.position).normalized;
            Vector3 pw = Vector3.ProjectOnPlane(dw, axisWorld);
            if (pw.sqrMagnitude < 1e-8f) continue;
            refInPlaneLocal = nonGuideAtom.transform.InverseTransformDirection(pw.normalized).normalized;
            return true;
        }

        return false;
    }

    static List<Vector3> BuildFinalDirectionsTemplate(int nVseprGroup)
    {
        var dirs = new List<Vector3>();
        if (nVseprGroup <= 0) return dirs;

        if (nVseprGroup == 1)
        {
            dirs.Add(Vector3.right);
            return dirs;
        }

        if (nVseprGroup == 2)
        {
            dirs.Add(Vector3.right);
            dirs.Add(Vector3.left);
            return dirs;
        }

        if (nVseprGroup == 3)
        {
            dirs.Add(Vector3.right);
            dirs.Add(new Vector3(-0.5f, 0.8660254f, 0f).normalized);
            dirs.Add(new Vector3(-0.5f, -0.8660254f, 0f).normalized);
            return dirs;
        }

        if (nVseprGroup == 4)
        {
            dirs.Add(Vector3.right);
            const float x = -1f / 3f;
            float r = Mathf.Sqrt(8f / 9f);
            dirs.Add(new Vector3(x, r, 0f).normalized);
            dirs.Add(new Vector3(x, -0.5f * r, 0.8660254f * r).normalized);
            dirs.Add(new Vector3(x, -0.5f * r, -0.8660254f * r).normalized);
            return dirs;
        }

        // Fallback for >4 groups: deterministic spread with vertex 0 fixed to +X.
        dirs.Add(Vector3.right);
        for (int i = 1; i < nVseprGroup; i++)
        {
            float t = i / (float)(nVseprGroup - 1);
            float z = 1f - 2f * t;
            float radial = Mathf.Sqrt(Mathf.Max(0f, 1f - z * z));
            float phi = i * 2.39996323f; // golden angle
            float y = Mathf.Cos(phi) * radial;
            float x = Mathf.Sin(phi) * radial;
            Vector3 v = new Vector3(x, y, z).normalized;
            if (v.sqrMagnitude < 1e-8f) v = Vector3.up;
            dirs.Add(v);
        }
        return dirs;
    }

    static GroupEntry GetGuideGroup(
        ElectronOrbitalFunction guideOrbitalPredetermined,
        List<GroupEntry> bondingGroups,
        List<GroupEntry> nonbondingOccupied,
        List<GroupEntry> emptyOrbitals,
        ElectronOrbitalFunction atomOrbitalOp)
    {
        if (guideOrbitalPredetermined != null)
        {
            for (int i = 0; i < bondingGroups.Count; i++)
                if (bondingGroups[i].Orbital == guideOrbitalPredetermined) return bondingGroups[i];
            for (int i = 0; i < nonbondingOccupied.Count; i++)
                if (nonbondingOccupied[i].Orbital == guideOrbitalPredetermined) return nonbondingOccupied[i];
            for (int i = 0; i < emptyOrbitals.Count; i++)
                if (emptyOrbitals[i].Orbital == guideOrbitalPredetermined) return emptyOrbitals[i];
        }
        return GetHeaviestGroupOpPrioritized(bondingGroups, nonbondingOccupied, emptyOrbitals, atomOrbitalOp);
    }
}
