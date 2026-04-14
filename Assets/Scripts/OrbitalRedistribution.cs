using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Phase-1 sigma-formation orbital redistribution planner (standalone, does not call existing redistribution pipelines).
/// </summary>
public static class OrbitalRedistribution
{
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
            isBondingEvent: isBondingEvent);
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
            for (int i = 0; i < assignments.Count; i++)
            {
                Assignment a = assignments[i];
                if (a.Group?.Orbital == null) continue;
                Vector3 targetWorld = atom.transform.TransformDirection(a.TargetDirLocal).normalized;
                Vector3 dirWorld = Vector3.Slerp(a.StartDirWorld, targetWorld, s).normalized;
                if (dirWorld.sqrMagnitude < 1e-12f) dirWorld = targetWorld;

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
                        a.Bond.UpdateBondTransformToCurrentAtoms();
                    }
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
        bool isBondingEvent = true)
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
            if (orb == null) continue;
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
            if (orb == null || orb.Bond != null || orb.transform.parent != atom.transform) continue;
            var row = new GroupEntry
            {
                Kind = "nonbond",
                Orbital = orb,
                MassWeight = Mathf.Max(1f, atom.AtomicNumber),
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
        GroupEntry guideGroup = GetGuideGroup(
            guideOrbitalPredetermined, bondingGroups, nonbondingOccupied, emptyOrbitals, atomOrbitalOp);

        int nVseprGroup = bondingGroups.Count + nonbondingOccupied.Count;
        var finalDirectionsTemplate = BuildFinalDirectionsTemplate(nVseprGroup);
        var groupsForMatching = new List<GroupEntry>(bondingGroups.Count + nonbondingOccupied.Count);
        groupsForMatching.AddRange(bondingGroups);
        groupsForMatching.AddRange(nonbondingOccupied);

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
        Quaternion qAlign = finalDirectionsTemplate.Count > 0
            ? Quaternion.FromToRotation(finalDirectionsTemplate[0], guideDirLocal)
            : Quaternion.identity;
        var alignedTemplate = new List<Vector3>(finalDirectionsTemplate.Count);
        for (int i = 0; i < finalDirectionsTemplate.Count; i++)
            alignedTemplate.Add((qAlign * finalDirectionsTemplate[i]).normalized);

        int[] bestPerm = FindBestCombinationVSEPRGroupToFinalDirection(groupsForMatching, alignedTemplate, atom, guideGroup);
        var finalDirectionsEmptyOrbital = BuildFinalDirectionsEmptyOrbitalTemplate(alignedTemplate);
        var animation = BuildRedistributionAnimation(
            groupsForMatching,
            emptyOrbitals,
            alignedTemplate,
            finalDirectionsEmptyOrbital,
            bestPerm,
            atom,
            guideOrbitalPredetermined,
            atomMoveAnimation,
            visitedAtoms,
            isBondingEvent);

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
                guideOrbitalPredetermined,
                atomMoveAnimation,
                visitedAtoms,
                isBondingEvent);
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
        ElectronOrbitalFunction guideOrbitalPredetermined,
        System.Func<float, Vector3> atomMoveAnimation,
        HashSet<AtomFunction> visitedAtoms,
        bool isBondingEvent)
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
            if (g == null || g.Orbital == null || g.Orbital == guideOrbitalPredetermined) continue;
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
                    Vector3 childGuideDirWorld = atom.transform.TransformDirection((-alignedTemplate[ti]).normalized);
                    Vector3 childGuideDirLocal = adjacentAtom.transform.InverseTransformDirection(childGuideDirWorld).normalized;
                    var childAnim = BuildOrbitalRedistribution(
                        adjacentAtom,
                        atom,
                        guideOrbitalPredetermined: g.Orbital,
                        finalDirectionForGuideOrbital: childGuideDirLocal,
                        atomMoveAnimation: atomMoveAnimation,
                        visitedAtoms: visitedAtoms,
                        isBondingEvent: isBondingEvent);
                    anim.AddChild(childAnim);
                }

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
        GroupEntry guideGroup)
    {
        int n = groups != null ? groups.Count : 0;
        if (n == 0 || alignedTemplate == null || alignedTemplate.Count < n)
            return Array.Empty<int>();

        var used = new bool[alignedTemplate.Count];
        var cur = new int[n];
        var best = new int[n];
        float bestCost = float.PositiveInfinity;
        int guideIdx = -1;
        if (guideGroup != null)
        {
            for (int i = 0; i < n; i++)
            {
                if (!ReferenceEquals(groups[i], guideGroup)) continue;
                guideIdx = i;
                break;
            }
        }

        void Dfs(int idx, float costSoFar)
        {
            if (idx >= n)
            {
                if (costSoFar < bestCost)
                {
                    bestCost = costSoFar;
                    for (int k = 0; k < n; k++) best[k] = cur[k];
                }
                return;
            }

            GroupEntry g = groups[idx];
            Vector3 gLocal = atom.transform.InverseTransformDirection(g.CurrentDirWorld).normalized;
            int tStart = 0;
            int tEnd = alignedTemplate.Count - 1;
            if (guideIdx >= 0 && idx == guideIdx)
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
                cur[idx] = t;
                Dfs(idx + 1, next);
                used[t] = false;
            }
        }

        Dfs(0, 0f);
        return best;
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

        int opEmptyIdx = -1;
        for (int i = 0; i < emptyOrbitals.Count; i++)
        {
            if (!ReferenceEquals(emptyOrbitals[i].Orbital, atomOrbitalOp)) continue;
            opEmptyIdx = i;
            break;
        }
        if (opEmptyIdx < 0)
            return;

        GroupEntry moved = emptyOrbitals[opEmptyIdx];
        moved.Kind = isBondingEvent
            ? "nonbond-op-empty-counted-bonding"
            : "nonbond-op-empty-counted-breaking";
        moved.MassWeight = isBondingEvent
            ? ComputeAtomComponentMass(guideAtom)
            : Mathf.Max(1f, atom.AtomicNumber);
        if (isBondingEvent)
        {
            nonbondingOccupied.Add(moved);
            emptyOrbitals.RemoveAt(opEmptyIdx);
        }
        else
        {
            // Breaking event: keep released empty in emptyOrbitals so empty-target assignment can rotate it.
            moved.Kind = "empty-op-released-breaking";
        }
    }

    static float ComputeBondGroupMassWeight(AtomFunction center, CovalentBond bond)
    {
        if (center == null || bond == null) return 1f;
        var other = bond.AtomA == center ? bond.AtomB : bond.AtomA;
        if (other == null) return 1f;
        return ComputeAtomComponentMass(other);
    }

    static float ComputeAtomComponentMass(AtomFunction start)
    {
        if (start == null) return 1f;
        var component = start.GetConnectedMolecule();
        if (component == null || component.Count == 0)
            return Mathf.Max(1f, start.AtomicNumber);
        float total = 0f;
        foreach (var a in component)
        {
            if (a == null) continue;
            total += Mathf.Max(1f, a.AtomicNumber);
        }
        return Mathf.Max(1f, total);
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
