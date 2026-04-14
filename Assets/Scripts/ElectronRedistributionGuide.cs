using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Guide atom (whole-molecule mass) and VSEPR domain helpers for electron redistribution.
/// See instruction/electron-redistribution-orbital-drag-events.md.
/// </summary>
public static class ElectronRedistributionGuide
{
    /// <summary>Domain counting: bonded multiply edge vs post-break released lobe split.</summary>
    public enum RedistributionDomainContext
    {
        BondedMultiplyEdgeIntact,
        PostBreakReleasedOccupiedSeparate
    }

    /// <summary>One candidate VSEPR group for guide scoring (heaviest substituent mass wins).</summary>
    public readonly struct VseprGroupMassEntry
    {
        public readonly int StableId;
        public readonly float SubstituentMassSum;
        public readonly AtomFunction SigmaNeighborOrNull;
        public readonly ElectronOrbitalFunction ReleasedOccupiedOrNull;

        public VseprGroupMassEntry(int stableId, float substituentMassSum, AtomFunction sigmaNeighborOrNull, ElectronOrbitalFunction releasedOccupiedOrNull)
        {
            StableId = stableId;
            SubstituentMassSum = substituentMassSum;
            SigmaNeighborOrNull = sigmaNeighborOrNull;
            ReleasedOccupiedOrNull = releasedOccupiedOrNull;
        }
    }

    /// <summary>IUPAC-style standard atomic weights; index 0 unused, 1..118 valid. Defaults to Z for uncommon Z.</summary>
    static readonly float[] StandardAtomicWeightByZ = BuildStandardAtomicWeights();

    static float[] BuildStandardAtomicWeights()
    {
        var w = new float[119];
        for (int z = 1; z <= 118; z++)
            w[z] = z;
        w[1] = 1.008f;
        w[2] = 4.003f;
        w[3] = 6.94f;
        w[5] = 10.81f;
        w[6] = 12.011f;
        w[7] = 14.007f;
        w[8] = 15.999f;
        w[9] = 18.998f;
        w[11] = 22.99f;
        w[12] = 24.305f;
        w[13] = 26.982f;
        w[14] = 28.085f;
        w[15] = 30.974f;
        w[16] = 32.06f;
        w[17] = 35.45f;
        w[19] = 39.098f;
        w[20] = 40.078f;
        w[26] = 55.845f;
        w[29] = 63.546f;
        w[30] = 65.38f;
        w[35] = 79.904f;
        w[53] = 126.90f;
        w[79] = 196.97f;
        w[82] = 207.2f;
        return w;
    }

    /// <summary>Relative atomic mass for element Z (1–118).</summary>
    public static float GetStandardAtomicWeight(int atomicNumber)
    {
        if (atomicNumber <= 0 || atomicNumber >= StandardAtomicWeightByZ.Length)
            return 1f;
        float m = StandardAtomicWeightByZ[atomicNumber];
        return m > 0.1f ? m : atomicNumber;
    }

    /// <summary>Sum of standard atomic weights over <see cref="AtomFunction.GetConnectedMolecule"/>.</summary>
    public static float SumAtomicMassInConnectedMolecule(AtomFunction atom)
    {
        if (atom == null) return 0f;
        float sum = 0f;
        foreach (var a in atom.GetConnectedMolecule())
        {
            if (a != null)
                sum += GetStandardAtomicWeight(a.AtomicNumber);
        }
        return sum;
    }

    /// <summary>Heavier whole-molecule side is guide; tie: dragged orbital’s atom is non-guide.</summary>
    public static void ResolveGuideAtomForPair(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction draggedOrbital,
        out AtomFunction guideAtom,
        out AtomFunction nonGuideAtom)
    {
        guideAtom = atomA;
        nonGuideAtom = atomB;
        if (atomA == null || atomB == null)
            return;
        float massA = SumAtomicMassInConnectedMolecule(atomA);
        float massB = SumAtomicMassInConnectedMolecule(atomB);
        string resolveBranch;
        AtomFunction draggedParentAtom = draggedOrbital != null
            ? (draggedOrbital.transform.parent != null
                ? draggedOrbital.transform.parent.GetComponent<AtomFunction>()
                : null)
            : null;
        if (massA > massB + 1e-4f)
        {
            guideAtom = atomA;
            nonGuideAtom = atomB;
            resolveBranch = "massA_heavier";
        }
        else if (massB > massA + 1e-4f)
        {
            guideAtom = atomB;
            nonGuideAtom = atomA;
            resolveBranch = "massB_heavier";
        }
        else
        {
            if (draggedParentAtom == atomA)
            {
                nonGuideAtom = atomA;
                guideAtom = atomB;
                resolveBranch = "tie_draggedParentIsAtomA";
            }
            else if (draggedParentAtom == atomB)
            {
                nonGuideAtom = atomB;
                guideAtom = atomA;
                resolveBranch = "tie_draggedParentIsAtomB";
            }
            else
            {
                int idA = atomA.GetInstanceID();
                int idB = atomB.GetInstanceID();
                guideAtom = idA <= idB ? atomA : atomB;
                nonGuideAtom = idA <= idB ? atomB : atomA;
                resolveBranch = draggedParentAtom == null
                    ? "tie_noDraggedParent_instanceId"
                    : "tie_draggedParentMismatch_instanceId";
            }
        }

        RedistributionTetraCompareDebugLog.LogGuideResolve(
            atomA,
            atomB,
            draggedOrbital,
            guideAtom,
            nonGuideAtom,
            massA,
            massB,
            resolveBranch,
            draggedParentAtom);
    }

    /// <summary>Sum of atomic masses in the substituent beyond the σ edge (center, neighbor) — same as fragment used in σ-relax.</summary>
    public static float SumSubstituentMassThroughSigmaEdge(AtomFunction center, AtomFunction sigmaNeighbor)
    {
        if (center == null || sigmaNeighbor == null) return 0f;
        float sum = 0f;
        foreach (var a in center.GetAtomsOnSideOfSigmaBond(sigmaNeighbor))
        {
            if (a != null)
                sum += GetStandardAtomicWeight(a.AtomicNumber);
        }
        return sum;
    }

    /// <summary>Mass of partner fragment for an incipient σ bond (before the bond exists in the graph).</summary>
    public static float SumIncipientPartnerFragmentMass(AtomFunction center, AtomFunction partner)
    {
        if (center == null || partner == null) return 0f;
        return SumSubstituentMassThroughSigmaEdge(center, partner);
    }

    /// <summary>
    /// Enumerate VSEPR groups for guide scoring: one entry per distinct σ neighbor (mass = substituent sum);
    /// multiply bonds to same neighbor share one σ neighbor key. Optional released occupied lobe (post-break) is separate.
    /// If <paramref name="incipientSigmaPartner"/> is set (σ formation before bond exists), adds a candidate group for that fragment mass.
    /// </summary>
    public static void EnumerateVseprGroupMassEntries(
        AtomFunction center,
        RedistributionDomainContext ctx,
        ElectronOrbitalFunction releasedOccupiedAfterBreakOrNull,
        AtomFunction incipientSigmaPartner,
        List<VseprGroupMassEntry> outEntries)
    {
        if (center == null || outEntries == null) return;
        outEntries.Clear();
        int nextId = 0;
        var seenNeighbor = new HashSet<AtomFunction>();

        if (incipientSigmaPartner != null
            && ctx == RedistributionDomainContext.BondedMultiplyEdgeIntact
            && center.GetBondsTo(incipientSigmaPartner) < 1)
        {
            float mInc = SumIncipientPartnerFragmentMass(center, incipientSigmaPartner);
            outEntries.Add(new VseprGroupMassEntry(nextId++, mInc, incipientSigmaPartner, null));
        }

        foreach (var cb in center.CovalentBonds)
        {
            if (cb == null || !cb.IsSigmaBondLine()) continue;
            var other = cb.AtomA == center ? cb.AtomB : cb.AtomA;
            if (other == null || !seenNeighbor.Add(other)) continue;
            if (incipientSigmaPartner != null && other == incipientSigmaPartner) continue;
            float m = SumSubstituentMassThroughSigmaEdge(center, other);
            outEntries.Add(new VseprGroupMassEntry(nextId++, m, other, null));
        }

        foreach (var o in center.GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (o == null || o.Bond != null) continue;
            if (o.transform.parent != center.transform) continue;
            if (o.ElectronCount <= 0) continue;
            bool isReleased = ctx == RedistributionDomainContext.PostBreakReleasedOccupiedSeparate
                && releasedOccupiedAfterBreakOrNull != null && ReferenceEquals(o, releasedOccupiedAfterBreakOrNull);
            if (isReleased)
                outEntries.Add(new VseprGroupMassEntry(nextId++, 0f, null, o));
            else
                outEntries.Add(new VseprGroupMassEntry(nextId++, 0f, null, null));
        }

        if (ctx == RedistributionDomainContext.PostBreakReleasedOccupiedSeparate
            && releasedOccupiedAfterBreakOrNull != null
            && releasedOccupiedAfterBreakOrNull.ElectronCount > 0
            && releasedOccupiedAfterBreakOrNull.transform.parent == center.transform
            && releasedOccupiedAfterBreakOrNull.Bond == null)
        {
            bool already = false;
            foreach (var e in outEntries)
            {
                if (e.ReleasedOccupiedOrNull != null && ReferenceEquals(e.ReleasedOccupiedOrNull, releasedOccupiedAfterBreakOrNull))
                {
                    already = true;
                    break;
                }
            }
            if (!already)
                outEntries.Add(new VseprGroupMassEntry(nextId++, 0f, null, releasedOccupiedAfterBreakOrNull));
        }
    }

    /// <summary>Pick heaviest-mass group; tie-break lower StableId.</summary>
    public static bool TryGetHeaviestVseprGroupMassEntry(IReadOnlyList<VseprGroupMassEntry> entries, out VseprGroupMassEntry best)
    {
        best = default;
        if (entries == null || entries.Count == 0) return false;
        best = entries[0];
        for (int i = 1; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.SubstituentMassSum > best.SubstituentMassSum + 1e-6f)
                best = e;
            else if (Mathf.Abs(e.SubstituentMassSum - best.SubstituentMassSum) <= 1e-6f && e.StableId < best.StableId)
                best = e;
        }
        return true;
    }

    /// <summary>
    /// Split redistribute tuples into occupied then empty (build order).
    /// </summary>
    public static void SplitRedistributeTargetsOccupiedThenEmpty(
        IReadOnlyList<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> all,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> occupiedOut,
        List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> emptyOut)
    {
        occupiedOut?.Clear();
        emptyOut?.Clear();
        if (all == null) return;
        foreach (var row in all)
        {
            if (row.orb == null) continue;
            if (row.orb.ElectronCount > 0)
                occupiedOut?.Add(row);
            else
                emptyOut?.Add(row);
        }
    }
}
