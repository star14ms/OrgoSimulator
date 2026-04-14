using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Sigma bond break: pure redistribution from current geometry (no predictive formation hybrid).
/// </summary>
public static class SigmaBreakPureRedistribution
{
    const float AngleEps = 1e-3f;

    /// <summary>NDJSON / Console triage for sigma-break pure redistribution. Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogSigmaBreakPureRedist = true;

    enum GroupKind
    {
        NonBond,
        BondSigma
    }

    sealed class Group
    {
        public GroupKind Kind;
        public ElectronOrbitalFunction NonBondOrb;
        public AtomFunction SigmaNeighbor;
        public float MassWeight;

        public int StableId =>
            Kind == GroupKind.NonBond
                ? (NonBondOrb != null ? NonBondOrb.GetInstanceID() : 0)
                : (SigmaNeighbor != null ? SigmaNeighbor.GetInstanceID() : 0);
    }

    public sealed class MotionPlan
    {
        public AtomFunction Center;
        public readonly List<(ElectronOrbitalFunction orb, Vector3 localPos0, Quaternion localRot0, Vector3 localPos1, Quaternion localRot1)> NonBondLerp =
            new List<(ElectronOrbitalFunction, Vector3, Quaternion, Vector3, Quaternion)>();
        public readonly Dictionary<AtomFunction, (Vector3 w0, Vector3 w1)> AtomWorldLerp = new Dictionary<AtomFunction, (Vector3, Vector3)>();
    }

    static Vector3 TipLocal(ElectronOrbitalFunction o) =>
        o != null ? (o.transform.localRotation * Vector3.right).normalized : Vector3.zero;

    /// <summary>Build simultaneous lerp targets; returns null when this atom skips (no anti-guide, n≤1, or invalid).</summary>
    public static MotionPlan TryBuildMotionPlan(AtomFunction center, ElectronOrbitalFunction antiGuideOrbital)
    {
        if (center == null || antiGuideOrbital == null || antiGuideOrbital.ElectronCount != 0
            || antiGuideOrbital.Bond != null || antiGuideOrbital.transform.parent != center.transform)
            return null;

        var groups = BuildGroups(center, antiGuideOrbital);
        int n = groups.Count;
        if (n <= 1)
        {
            if (DebugLogSigmaBreakPureRedist)
                Debug.Log("[σ-break-pure] skip center=" + center.name + " id=" + center.GetInstanceID() + " nVsepr=" + n + " reason=nLe1");
            return null;
        }

        Vector3 gLocal = TipLocal(antiGuideOrbital);
        if (gLocal.sqrMagnitude < 1e-12f)
        {
            if (DebugLogSigmaBreakPureRedist)
                Debug.Log("[σ-break-pure] abort center=" + center.name + " id=" + center.GetInstanceID() + " reason=antiGuideTipZero");
            return null;
        }
        gLocal.Normalize();
        Vector3 gWorld = center.transform.TransformDirection(gLocal).normalized;

        var masses = groups.ConvertAll(g => Mathf.Max(1e-4f, g.MassWeight));
        var startTipsWorld = groups.ConvertAll(g => GroupTipWorld(center, g));

        List<Vector3> finalTipsWorld;
        if (n == 2 || n == 3)
            finalTipsWorld = Solve23(center, gWorld, masses, startTipsWorld);
        else if (n == 4)
            finalTipsWorld = SolveAnchored(center, gLocal, masses, startTipsWorld, 4, 0);
        else if (n == 5)
            finalTipsWorld = SolveAnchored(center, gLocal, masses, startTipsWorld, 5, 1);
        else
            finalTipsWorld = SolveAnchored(center, gLocal, masses, startTipsWorld, 6, 0);

        if (finalTipsWorld == null || finalTipsWorld.Count != n)
        {
            if (DebugLogSigmaBreakPureRedist)
                Debug.Log("[σ-break-pure] abort center=" + center.name + " id=" + center.GetInstanceID() + " reason=solveFail n=" + n);
            return null;
        }

        if (DebugLogSigmaBreakPureRedist)
            Debug.Log("[σ-break-pure] plan center=" + center.name + " id=" + center.GetInstanceID() + " n=" + n + " nonBondLerpWill=" +
                      groups.Count(g => g.Kind == GroupKind.NonBond) + " bondGroups=" + groups.Count(g => g.Kind == GroupKind.BondSigma));

        return BuildMotionPlanFromTips(center, groups, startTipsWorld, finalTipsWorld);
    }

    static List<Group> BuildGroups(AtomFunction center, ElectronOrbitalFunction antiGuideOrbital)
    {
        var list = new List<Group>();
        var orbs = center.GetComponentsInChildren<ElectronOrbitalFunction>(true);
        for (int i = 0; i < orbs.Length; i++)
        {
            var o = orbs[i];
            if (o == null || o == antiGuideOrbital || o.Bond != null || o.ElectronCount <= 0) continue;
            if (o.transform.parent != center.transform) continue;
            list.Add(new Group { Kind = GroupKind.NonBond, NonBondOrb = o, MassWeight = 1f });
        }

        var seenN = new HashSet<AtomFunction>();
        if (center.CovalentBonds != null)
        {
            foreach (var cb in center.CovalentBonds)
            {
                if (cb == null || !cb.IsSigmaBondLine() || cb.Orbital == null) continue;
                var other = cb.AtomA == center ? cb.AtomB : cb.AtomA;
                if (other == null || !seenN.Add(other)) continue;
                float mass = ElectronRedistributionGuide.SumSubstituentMassThroughSigmaEdge(center, other);
                list.Add(new Group { Kind = GroupKind.BondSigma, SigmaNeighbor = other, MassWeight = mass });
            }
        }

        list.Sort((a, b) => a.StableId.CompareTo(b.StableId));
        return list;
    }

    static Vector3 GroupTipWorld(AtomFunction center, Group g)
    {
        if (g.Kind == GroupKind.NonBond && g.NonBondOrb != null)
            return center.transform.TransformDirection(TipLocal(g.NonBondOrb)).normalized;
        if (g.Kind == GroupKind.BondSigma && g.SigmaNeighbor != null)
        {
            Vector3 d = g.SigmaNeighbor.transform.position - center.transform.position;
            return d.sqrMagnitude > 1e-14f ? d.normalized : Vector3.right;
        }
        return Vector3.right;
    }

    static Vector3 ProjectPerpTowardAntiGuide(Vector3 tipUnitWorld, Vector3 antiGuideWorldUnit, int centerLogId, int groupIdx)
    {
        float d = Vector3.Dot(tipUnitWorld, antiGuideWorldUnit);
        Vector3 proj = tipUnitWorld - d * antiGuideWorldUnit;
        if (proj.sqrMagnitude < 1e-10f)
        {
            Vector3 aux = Mathf.Abs(Vector3.Dot(antiGuideWorldUnit, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
            proj = Vector3.Cross(antiGuideWorldUnit, aux);
            if (proj.sqrMagnitude < 1e-10f) proj = Vector3.Cross(antiGuideWorldUnit, Vector3.forward);
            if (DebugLogSigmaBreakPureRedist)
                Debug.Log("[σ-break-pure] parallelTipFallback centerId=" + centerLogId + " groupIdx=" + groupIdx);
        }
        return proj.normalized;
    }

    static List<Vector3> Solve23(AtomFunction center, Vector3 gWorld, List<float> masses, List<Vector3> startTipsWorld)
    {
        int n = startTipsWorld.Count;
        var midTips = new List<Vector3>(n);
        int cid = center.GetInstanceID();
        for (int i = 0; i < n; i++)
            midTips.Add(ProjectPerpTowardAntiGuide(startTipsWorld[i], gWorld, cid, i));

        float bestTheta = 0f;
        int[] bestPerm = null;
        float bestTotalAngle = float.MaxValue;
        float bestMom = float.MaxValue;

        for (int step = 0; step < 72; step++)
        {
            float theta = step * 5f;
            var idealWorld = PlanarIdealDirsWorld(gWorld, n, theta);
            var perm = FindBestPermutationWeighted(midTips, idealWorld, masses, out float totalAng, out float mom);
            if (perm == null) continue;
            bool better = bestPerm == null
                || totalAng < bestTotalAngle - AngleEps
                || (Mathf.Abs(totalAng - bestTotalAngle) <= AngleEps && mom < bestMom - 1e-4f)
                || (Mathf.Abs(totalAng - bestTotalAngle) <= AngleEps && Mathf.Abs(mom - bestMom) <= 1e-4f && LexLess(perm, bestPerm));
            if (better)
            {
                bestTotalAngle = totalAng;
                bestMom = mom;
                bestTheta = theta;
                bestPerm = perm;
            }
        }

        if (bestPerm == null) return null;
        var idealChosen = PlanarIdealDirsWorld(gWorld, n, bestTheta);
        var result = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            result.Add(idealChosen[bestPerm[i]]);
        return result;
    }

    static List<Vector3> PlanarIdealDirsWorld(Vector3 gWorldUnit, int n, float thetaDeg)
    {
        Vector3 aux = Mathf.Abs(Vector3.Dot(gWorldUnit, Vector3.up)) < 0.9f ? Vector3.up : Vector3.right;
        Vector3 e1 = Vector3.Cross(gWorldUnit, aux);
        if (e1.sqrMagnitude < 1e-10f) e1 = Vector3.Cross(gWorldUnit, Vector3.forward);
        e1.Normalize();
        Vector3 e2 = Vector3.Cross(gWorldUnit, e1).normalized;
        float rad = thetaDeg * Mathf.Deg2Rad;
        var dirs = new List<Vector3>(n);
        if (n == 2)
        {
            Vector3 u = (Mathf.Cos(rad) * e1 + Mathf.Sin(rad) * e2).normalized;
            dirs.Add(u);
            dirs.Add((-u).normalized);
        }
        else if (n == 3)
        {
            for (int k = 0; k < 3; k++)
            {
                float a = rad + k * (120f * Mathf.Deg2Rad);
                dirs.Add((Mathf.Cos(a) * e1 + Mathf.Sin(a) * e2).normalized);
            }
        }
        return dirs;
    }

    static List<Vector3> SolveAnchored(
        AtomFunction center,
        Vector3 gLocal,
        List<float> masses,
        List<Vector3> startTipsWorld,
        int idealCount,
        int alignIdealIndexToMinusG)
    {
        int n = startTipsWorld.Count;
        if (n != idealCount) return null;

        Vector3 minusGLocal = (-gLocal).normalized;
        var idealBase = VseprLayout.GetIdealLocalDirections(idealCount);
        var rotatedLocal = new Vector3[idealCount];
        Quaternion qAlign = Quaternion.FromToRotation(idealBase[alignIdealIndexToMinusG].normalized, minusGLocal);
        for (int i = 0; i < idealCount; i++)
            rotatedLocal[i] = (qAlign * idealBase[i]).normalized;

        var idealWorld = new List<Vector3>(idealCount);
        for (int i = 0; i < idealCount; i++)
            idealWorld.Add(center.transform.TransformDirection(rotatedLocal[i]).normalized);

        Vector3 anchorTargetWorld = idealWorld[alignIdealIndexToMinusG];
        int anchorGroup = -1;
        float bestDot = -2f;
        for (int i = 0; i < n; i++)
        {
            float dot = Vector3.Dot(startTipsWorld[i].normalized, anchorTargetWorld);
            if (dot > bestDot)
            {
                bestDot = dot;
                anchorGroup = i;
            }
        }
        if (anchorGroup < 0) return null;

        var otherGroups = new List<int>();
        for (int i = 0; i < n; i++)
            if (i != anchorGroup) otherGroups.Add(i);

        int m = otherGroups.Count;
        var idx = Enumerable.Range(0, m).ToArray();
        int[] bestPermSub = null;
        float bestTotal = float.MaxValue;
        float bestMom = float.MaxValue;

        foreach (var perm in Permutations(idx))
        {
            float fullTotal = 0f;
            float fullMom = 0f;
            for (int t = 0; t < m; t++)
            {
                int groupIdx = otherGroups[t];
                int slot = SlotIndexExcluding(idealCount, alignIdealIndexToMinusG, perm[t]);
                float ang = Vector3.Angle(startTipsWorld[groupIdx], idealWorld[slot]);
                fullTotal += ang;
                fullMom += masses[groupIdx] * ang;
            }
            float angA = Vector3.Angle(startTipsWorld[anchorGroup], idealWorld[alignIdealIndexToMinusG]);
            fullTotal += angA;
            fullMom += masses[anchorGroup] * angA;

            bool better = bestPermSub == null
                || fullTotal < bestTotal - AngleEps
                || (Mathf.Abs(fullTotal - bestTotal) <= AngleEps && fullMom < bestMom - 1e-4f)
                || (Mathf.Abs(fullTotal - bestTotal) <= AngleEps && Mathf.Abs(fullMom - bestMom) <= 1e-4f && LexLess(perm, bestPermSub));
            if (better)
            {
                bestTotal = fullTotal;
                bestMom = fullMom;
                bestPermSub = (int[])perm.Clone();
            }
        }

        if (bestPermSub == null) return null;
        var final = new List<Vector3>(n);
        for (int i = 0; i < n; i++)
            final.Add(Vector3.zero);
        final[anchorGroup] = idealWorld[alignIdealIndexToMinusG];
        for (int t = 0; t < m; t++)
        {
            int groupIdx = otherGroups[t];
            int slot = SlotIndexExcluding(idealCount, alignIdealIndexToMinusG, bestPermSub[t]);
            final[groupIdx] = idealWorld[slot];
        }
        return final;
    }

    static int SlotIndexExcluding(int idealCount, int excludeSlot, int k)
    {
        int idx = 0;
        for (int s = 0; s < idealCount; s++)
        {
            if (s == excludeSlot) continue;
            if (idx == k) return s;
            idx++;
        }
        return 0;
    }

    static int[] FindBestPermutationWeighted(List<Vector3> oldDirs, List<Vector3> newDirs, List<float> masses, out float totalAngle, out float angularMomentum)
    {
        totalAngle = float.MaxValue;
        angularMomentum = float.MaxValue;
        int n = oldDirs.Count;
        if (n != newDirs.Count || n != masses.Count) return null;
        var indices = Enumerable.Range(0, n).ToArray();
        int[] best = null;
        foreach (var perm in Permutations(indices))
        {
            float ta = 0f;
            float am = 0f;
            for (int i = 0; i < n; i++)
            {
                float ang = Vector3.Angle(oldDirs[i], newDirs[perm[i]]);
                ta += ang;
                am += masses[i] * ang;
            }
            bool better = best == null
                || ta < totalAngle - AngleEps
                || (Mathf.Abs(ta - totalAngle) <= AngleEps && am < angularMomentum - 1e-4f)
                || (Mathf.Abs(ta - totalAngle) <= AngleEps && Mathf.Abs(am - angularMomentum) <= 1e-4f && LexLess(perm, best));
            if (better)
            {
                best = (int[])perm.Clone();
                totalAngle = ta;
                angularMomentum = am;
            }
        }
        return best;
    }

    static bool LexLess(int[] a, int[] b)
    {
        if (b == null) return true;
        if (a == null) return false;
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            if (a[i] != b[i]) return a[i].CompareTo(b[i]) < 0;
        }
        return a.Length.CompareTo(b.Length) < 0;
    }

    static IEnumerable<int[]> Permutations(int[] arr)
    {
        if (arr.Length == 0) { yield return arr; yield break; }
        var a = (int[])arr.Clone();
        yield return a;
        while (NextPermutation(a))
            yield return (int[])a.Clone();
    }

    static bool NextPermutation(int[] a)
    {
        int i = a.Length - 2;
        while (i >= 0 && a[i] >= a[i + 1]) i--;
        if (i < 0) return false;
        int j = a.Length - 1;
        while (a[j] <= a[i]) j--;
        (a[i], a[j]) = (a[j], a[i]);
        Array.Reverse(a, i + 1, a.Length - i - 1);
        return true;
    }

    static MotionPlan BuildMotionPlanFromTips(
        AtomFunction center,
        List<Group> groups,
        List<Vector3> startTipsWorld,
        List<Vector3> finalTipsWorld)
    {
        var plan = new MotionPlan { Center = center };
        var bondNeighbors = new List<AtomFunction>();
        var oldDirs = new List<Vector3>();
        var newDirs = new List<Vector3>();

        for (int i = 0; i < groups.Count; i++)
        {
            var g = groups[i];
            if (g.Kind == GroupKind.NonBond && g.NonBondOrb != null)
            {
                var orb = g.NonBondOrb;
                Vector3 finalLocalDir = center.transform.InverseTransformDirection(finalTipsWorld[i]).normalized;
                var (lp1, lr1) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(
                    finalLocalDir, center.BondRadius, orb.transform.localRotation);
                plan.NonBondLerp.Add((orb, orb.transform.localPosition, orb.transform.localRotation, lp1, lr1));
            }
            else if (g.Kind == GroupKind.BondSigma && g.SigmaNeighbor != null)
            {
                bondNeighbors.Add(g.SigmaNeighbor);
                oldDirs.Add(startTipsWorld[i]);
                newDirs.Add(finalTipsWorld[i]);
            }
        }

        if (bondNeighbors.Count > 0)
        {
            BuildSigmaNeighborTargetsWithFragmentRigidRotation(
                center.transform.position,
                bondNeighbors,
                oldDirs,
                newDirs,
                center,
                out var targets);
            if (targets != null)
            {
                foreach (var (atom, tw) in targets)
                {
                    if (atom == null) continue;
                    plan.AtomWorldLerp[atom] = (atom.transform.position, tw);
                }
            }
        }

        return plan;
    }

    /// <summary>
    /// Orbital-drag σ phase 3: same rigid fragment step as σ break <see cref="BuildMotionPlanFromTips"/> (pivot = redistributing nucleus).
    /// </summary>
    internal static void BuildSigmaNeighborTargetsWithFragmentRigidRotation(
        Vector3 pivotWorld,
        IReadOnlyList<AtomFunction> sigmaNeighbors,
        IReadOnlyList<Vector3> oldUnitDirs,
        IReadOnlyList<Vector3> newUnitDirs,
        AtomFunction pivot,
        out List<(AtomFunction atom, Vector3 targetWorld)> targets)
    {
        targets = new List<(AtomFunction, Vector3)>();
        int n = sigmaNeighbors.Count;
        if (pivot == null || n != oldUnitDirs.Count || n != newUnitDirs.Count) return;

        var fragments = new List<List<AtomFunction>>(n);
        for (int i = 0; i < n; i++)
            fragments.Add(pivot.GetAtomsOnSideOfSigmaBond(sigmaNeighbors[i]));

        bool overlap = false;
        var seenAcross = new HashSet<AtomFunction>();
        for (int i = 0; i < n; i++)
        {
            foreach (var a in fragments[i])
            {
                if (!seenAcross.Add(a))
                {
                    overlap = true;
                    break;
                }
            }
            if (overlap) break;
        }

        if (overlap)
        {
            // #region agent log
            if (ElectronRedistributionOrchestrator.DebugLogSigmaPhase1NonOpRotationNdjson)
            {
                ProjectAgentDebugLog.AppendCursorDebugSessionC2019eNdjson(
                    "H2",
                    "SigmaBreakPureRedistribution.cs:BuildSigmaNeighborTargetsWithFragmentRigidRotation",
                    "fragment_overlap_fallback",
                    "{\"overlap\":true,\"n\":" + n + ",\"pivotWorld\":\"" + pivotWorld.ToString("G9") + "\"}",
                    "pre-fix");
            }
            // #endregion
            for (int i = 0; i < n; i++)
            {
                var neighbor = sigmaNeighbors[i];
                float dist = Vector3.Distance(pivotWorld, neighbor.transform.position);
                targets.Add((neighbor, pivotWorld + newUnitDirs[i].normalized * dist));
            }
            return;
        }

        for (int i = 0; i < n; i++)
        {
            Vector3 o = oldUnitDirs[i].normalized;
            Vector3 nd = newUnitDirs[i].normalized;
            if (o.sqrMagnitude < 1e-12f || nd.sqrMagnitude < 1e-12f) continue;
            Quaternion R = Quaternion.FromToRotation(o, nd);
            foreach (var a in fragments[i])
            {
                Vector3 newPos = pivotWorld + R * (a.transform.position - pivotWorld);
                targets.Add((a, newPos));
            }
        }
    }
}
