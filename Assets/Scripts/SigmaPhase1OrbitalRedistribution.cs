using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

/// <summary>
/// Phase-1 sigma-formation orbital redistribution planner (standalone, does not call existing redistribution pipelines).
/// </summary>
public static class SigmaPhase1OrbitalRedistribution
{
    /// <summary>Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogSigmaPhase1OrbitalRedistribution = true;

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
            public CovalentBond Bond;
            public Vector3 StartPivotWorld;
            public Vector3 StartNeighborDirWorld;
            public List<(AtomFunction atom, Vector3 worldPos0)> FragmentStartWorld;
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
                Bond = bond,
                StartPivotWorld = startPivotWorld,
                StartNeighborDirWorld = startNeighborDirWorld,
                FragmentStartWorld = fragmentStartWorld
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
    public static RedistributionAnimation BuildOrbitalRedistributionForSigmaBondFormation(
        AtomFunction atom,
        AtomFunction guideAtom,
        Vector3 finalDirectionForGuideOrbital,
        ElectronOrbitalFunction guideAtomOrbitalOp = null,
        ElectronOrbitalFunction atomOrbitalOp = null,
        ElectronOrbitalFunction guideOrbitalPredetermined = null,
        System.Func<float, Vector3> atomMoveAnimation = null,
        HashSet<AtomFunction> visitedAtoms = null)
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

        int opEmptyIdx = -1;
        for (int i = 0; i < emptyOrbitals.Count; i++)
        {
            if (!ReferenceEquals(emptyOrbitals[i].Orbital, atomOrbitalOp)) continue;
            opEmptyIdx = i;
            break;
        }
        if (opEmptyIdx >= 0)
        {
            GroupEntry moved = emptyOrbitals[opEmptyIdx];
            moved.Kind = "nonbond-op-empty-counted";
            // Future bonding mass: use guide molecule mass instead of current nonbonding default.
            moved.MassWeight = ComputeAtomComponentMass(guideAtom);
            nonbondingOccupied.Add(moved);
            emptyOrbitals.RemoveAt(opEmptyIdx);
        }
        GroupEntry guideGroup = GetGuideGroup(
            guideOrbitalPredetermined, bondingGroups, nonbondingOccupied, emptyOrbitals);

        int nVseprGroup = bondingGroups.Count + nonbondingOccupied.Count;
        var finalDirectionsTemplate = BuildFinalDirectionsTemplate(nVseprGroup);
        var groupsForMatching = new List<GroupEntry>(bondingGroups.Count + nonbondingOccupied.Count);
        groupsForMatching.AddRange(bondingGroups);
        groupsForMatching.AddRange(nonbondingOccupied);

        Quaternion qAlign = finalDirectionsTemplate.Count > 0
            ? Quaternion.FromToRotation(finalDirectionsTemplate[0], finalDirectionForGuideOrbital)
            : Quaternion.identity;
        var alignedTemplate = new List<Vector3>(finalDirectionsTemplate.Count);
        for (int i = 0; i < finalDirectionsTemplate.Count; i++)
            alignedTemplate.Add((qAlign * finalDirectionsTemplate[i]).normalized);

        int[] bestPerm = FindBestCombinationVSEPRGroupToFinalDirection(groupsForMatching, alignedTemplate, atom);
        var animation = BuildRedistributionAnimation(
            groupsForMatching,
            alignedTemplate,
            bestPerm,
            atom,
            guideOrbitalPredetermined,
            atomMoveAnimation,
            visitedAtoms);

        if (DebugLogSigmaPhase1OrbitalRedistribution)
        {
            int guideOrbId = guideGroup != null && guideGroup.Orbital != null ? guideGroup.Orbital.GetInstanceID() : 0;
            string guideKind = guideGroup != null ? guideGroup.Kind : "none";
            Debug.Log(
                "[sigma-phase1-redist] config atomId=" + atom.GetInstanceID()
                + " nVseprGroup=" + nVseprGroup
                + " bondGroupCount=" + bondingGroups.Count
                + " nonbondOccupiedCount=" + nonbondingOccupied.Count
                + " emptyCount=" + emptyOrbitals.Count
                + " guideGroupKind=" + guideKind
                + " guideGroupOrbId=" + guideOrbId);

            for (int i = 0; i < finalDirectionsTemplate.Count; i++)
            {
                Vector3 d = finalDirectionsTemplate[i];
                Debug.Log(
                    "[sigma-phase1-redist] templateDir atomId=" + atom.GetInstanceID()
                    + " idx=" + i
                    + " x=" + d.x.ToString("F4", CultureInfo.InvariantCulture)
                    + " y=" + d.y.ToString("F4", CultureInfo.InvariantCulture)
                    + " z=" + d.z.ToString("F4", CultureInfo.InvariantCulture));
            }

            for (int i = 0; i < finalDirectionsTemplate.Count; i++)
            {
                for (int j = i + 1; j < finalDirectionsTemplate.Count; j++)
                {
                    float ang = Vector3.Angle(finalDirectionsTemplate[i], finalDirectionsTemplate[j]);
                    Debug.Log(
                        "[sigma-phase1-redist] templatePairAngle atomId=" + atom.GetInstanceID()
                        + " i=" + i
                        + " j=" + j
                        + " deg=" + ang.ToString("F3", CultureInfo.InvariantCulture));
                }
            }

            for (int i = 0; i < alignedTemplate.Count; i++)
            {
                Vector3 d = alignedTemplate[i];
                Debug.Log(
                    "[sigma-phase1-redist] alignedDir atomId=" + atom.GetInstanceID()
                    + " idx=" + i
                    + " x=" + d.x.ToString("F4", CultureInfo.InvariantCulture)
                    + " y=" + d.y.ToString("F4", CultureInfo.InvariantCulture)
                    + " z=" + d.z.ToString("F4", CultureInfo.InvariantCulture));
            }
            if (bestPerm != null)
            {
                for (int i = 0; i < bestPerm.Length; i++)
                {
                    int ti = bestPerm[i];
                    if (ti < 0 || ti >= alignedTemplate.Count || i >= groupsForMatching.Count) continue;
                    Debug.Log(
                        "[sigma-phase1-redist] match atomId=" + atom.GetInstanceID()
                        + " groupIdx=" + i
                        + " groupKind=" + groupsForMatching[i].Kind
                        + " templateIdx=" + ti
                        + " costDirDeltaDeg=" + Vector3.Angle(
                            atom.transform.InverseTransformDirection(groupsForMatching[i].CurrentDirWorld),
                            alignedTemplate[ti]).ToString("F3", CultureInfo.InvariantCulture));
                }
            }
        }

        return animation;
    }

    static RedistributionAnimation BuildRedistributionAnimation(
        List<GroupEntry> groups,
        List<Vector3> alignedTemplate,
        int[] perm,
        AtomFunction atom,
        ElectronOrbitalFunction guideOrbitalPredetermined,
        System.Func<float, Vector3> atomMoveAnimation,
        HashSet<AtomFunction> visitedAtoms)
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
                    var childAnim = BuildOrbitalRedistributionForSigmaBondFormation(
                        adjacentAtom,
                        atom,
                        childGuideDirLocal,
                        guideAtomOrbitalOp: null,
                        atomOrbitalOp: null,
                        guideOrbitalPredetermined: g.Orbital,
                        atomMoveAnimation: atomMoveAnimation,
                        visitedAtoms: visitedAtoms);
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
        return anim;
    }

    static int[] FindBestCombinationVSEPRGroupToFinalDirection(
        List<GroupEntry> groups,
        List<Vector3> alignedTemplate,
        AtomFunction atom)
    {
        int n = groups != null ? groups.Count : 0;
        if (n == 0 || alignedTemplate == null || alignedTemplate.Count < n)
            return Array.Empty<int>();

        var used = new bool[alignedTemplate.Count];
        var cur = new int[n];
        var best = new int[n];
        float bestCost = float.PositiveInfinity;

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
            for (int t = 0; t < alignedTemplate.Count; t++)
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
        List<GroupEntry> emptyOrbitals)
    {
        GroupEntry best = null;
        float bestMass = float.NegativeInfinity;

        void Visit(List<GroupEntry> rows)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                GroupEntry row = rows[i];
                if (row == null) continue;

                if (best == null || row.MassWeight > bestMass)
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
        List<GroupEntry> emptyOrbitals)
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
        return GetHeaviestGroupOpPrioritized(bondingGroups, nonbondingOccupied, emptyOrbitals);
    }
}
