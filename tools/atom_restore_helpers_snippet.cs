    Vector3 ResolveReferenceBondDirectionLocal(float? piBondAngleOverride, Vector3? refBondWorldDirection, ElectronOrbitalFunction bondBreakGuideOrbital = null)
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (refBondWorldDirection.HasValue)
        {
            var w = refBondWorldDirection.Value;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w.normalized).normalized;
        }
        if (bondBreakGuideOrbital != null)
        {
            var t = OrbitalTipLocalDirection(bondBreakGuideOrbital);
            if (t.sqrMagnitude > 1e-8f) return t.normalized;
        }
        if (piBondAngleOverride.HasValue)
        {
            float a = NormalizeAngleTo360(piBondAngleOverride.Value) * Mathf.Deg2Rad;
            var worldXy = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            return transform.InverseTransformDirection(worldXy).normalized;
        }
        if (covalentBonds.Count > 0)
        {
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var w = (other.transform.position - transform.position).normalized;
                if (w.sqrMagnitude >= 0.01f)
                    return transform.InverseTransformDirection(w).normalized;
            }
        }
        return Vector3.right;
    }

    Vector3 RedistributeReferenceDirectionLocalForTargets(AtomFunction newBondPartner)
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (newBondPartner != null)
        {
            var w = (newBondPartner.transform.position - transform.position).normalized;
            if (w.sqrMagnitude >= 0.01f)
                return transform.InverseTransformDirection(w).normalized;
        }
        if (covalentBonds.Count > 0)
        {
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var w = (other.transform.position - transform.position).normalized;
                if (w.sqrMagnitude >= 0.01f)
                    return transform.InverseTransformDirection(w).normalized;
            }
        }
        return Vector3.right;
    }

    static Vector3 OrbitalTipLocalDirection(ElectronOrbitalFunction orb) =>
        (orb.transform.localRotation * Vector3.right).normalized;

    /// <summary>Lobe +X as a unit vector in <b>this nucleus</b> local space (non-bond: parent is nucleus; σ on bond: world tip → nucleus local).</summary>
    Vector3 OrbitalTipDirectionInNucleusLocal(ElectronOrbitalFunction orb)
    {
        if (orb == null) return Vector3.right;
        if (orb.transform.parent == transform)
            return OrbitalTipLocalDirection(orb).normalized;
        Vector3 tipW = orb.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

    /// <summary>σ bond-break pin lobe tip in <b>this nucleus</b> local space (+X along lobe), even if the orbital is still parented under a bond.</summary>
    Vector3 ExBondPinOrbitalTipLocalOnNucleus(ElectronOrbitalFunction pinOrb)
    {
        if (pinOrb == null) return Vector3.forward;
        if (pinOrb.transform.parent == transform)
            return OrbitalTipLocalDirection(pinOrb).normalized;
        Vector3 tipW = pinOrb.transform.TransformDirection(Vector3.right);
        if (tipW.sqrMagnitude < 1e-10f) return Vector3.forward;
        return transform.InverseTransformDirection(tipW.normalized).normalized;
    }

    List<Vector3> CollectUniqueOrbitalDirections(float toleranceDeg)
    {
        var bondDirs = new List<Vector3>();
        AppendSigmaBondDirectionsLocalDistinctNeighbors(bondDirs);
        var mergedBond = MergeDirectionsWithinTolerance(bondDirs, toleranceDeg);

        var loneDirs = new List<Vector3>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != transform) continue;
            loneDirs.Add(OrbitalTipLocalDirection(orb));
        }
        var result = new List<Vector3>(mergedBond);
        result.AddRange(loneDirs);
        return result;
    }

    /// <summary>Lexicographic order on normalized components so sorts and ties are reproducible across frames.</summary>
    static int CompareVectorsStable(Vector3 a, Vector3 b)
    {
        a = a.normalized;
        b = b.normalized;
        int c = a.x.CompareTo(b.x);
        if (c != 0) return c;
        c = a.y.CompareTo(b.y);
        if (c != 0) return c;
        return a.z.CompareTo(b.z);
    }

    static int ComparePermutationLex(int[] a, int[] b)
    {
        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            int c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Length.CompareTo(b.Length);
    }

    static List<Vector3> MergeDirectionsWithinTolerance(List<Vector3> dirs, float toleranceDeg)
    {
        if (dirs.Count == 0) return new List<Vector3>();
        var sorted = new List<Vector3>(dirs);
        sorted.Sort(CompareVectorsStable);
        dirs = sorted;
        var used = new bool[dirs.Count];
        var result = new List<Vector3>();
        for (int i = 0; i < dirs.Count; i++)
        {
            if (used[i]) continue;
            Vector3 acc = dirs[i].normalized;
            int count = 1;
            used[i] = true;
            for (int j = i + 1; j < dirs.Count; j++)
            {
                if (used[j]) continue;
                if (Vector3.Angle(acc, dirs[j]) <= toleranceDeg)
                {
                    acc += dirs[j].normalized;
                    count++;
                    used[j] = true;
                }
            }
            result.Add((acc / count).normalized);
        }
        return result;
    }

    static int FindClosestDirectionIndex(List<Vector3> dirs, Vector3 target, float _)
    {
        target = target.normalized;
        int best = 0;
        float bestAng = 360f;
        for (int i = 0; i < dirs.Count; i++)
        {
            float ang = Vector3.Angle(dirs[i], target);
            if (ang < bestAng)
            {
                bestAng = ang;
                best = i;
            }
        }
        return best;
    }

    static List<(Vector3 oldDir, Vector3 newDir)> FindBestDirectionMapping(List<Vector3> oldDirs, List<Vector3> newDirs)
    {
        if (oldDirs.Count != newDirs.Count || oldDirs.Count == 0) return null;
        var indices = Enumerable.Range(0, newDirs.Count).ToArray();
        List<(Vector3, Vector3)> bestMapping = null;
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float angleEps = 1e-3f;

        foreach (var perm in Permutations(indices))
        {
            float total = 0f;
            for (int i = 0; i < oldDirs.Count; i++)
                total += Vector3.Angle(oldDirs[i], newDirs[perm[i]]);
            bool better = bestMapping == null
                || total < bestTotal - angleEps
                || (Mathf.Abs(total - bestTotal) <= angleEps && ComparePermutationLex(perm, bestPerm) < 0);

            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
                bestMapping = new List<(Vector3, Vector3)>();
                for (int i = 0; i < oldDirs.Count; i++)
                    bestMapping.Add((oldDirs[i], newDirs[perm[i]]));
            }
        }
        return bestMapping;
    }

    /// <summary>Work-plane angles in [0,360): min of |o−t|, |o−(t−360)|, |o−(t+360)| — same idea as comparing raw separation vs shifting the target by one turn before summing over a permutation.</summary>
    static float PlanarAbsDeltaMinRawMinus360Plus360PairDeg(float oDeg, float tDeg)
    {
        float o = NormalizeAngleTo360(oDeg);
        float t = NormalizeAngleTo360(tDeg);
        return Mathf.Min(
            Mathf.Abs(o - t),
            Mathf.Abs(o - (t - 360f)),
            Mathf.Abs(o - (t + 360f)));
    }

    /// <summary>For one assignment permutation: Σ|o−t|, Σ|o−(t−360)|, Σ|o−(t+360)| with o,t∈[0,360); take the smallest total (then compare across permutations).</summary>
    static float PlanarAnglePermutationCostMinOfThreeSums(IReadOnlyList<float> oldDeg, IReadOnlyList<float> targetDeg, int[] perm)
    {
        int n = oldDeg.Count;
        float s0 = 0f, s1 = 0f, s2 = 0f;
        for (int i = 0; i < n; i++)
        {
            float o = NormalizeAngleTo360(oldDeg[i]);
            float t = NormalizeAngleTo360(targetDeg[perm[i]]);
            s0 += Mathf.Abs(o - t);
            s1 += Mathf.Abs(o - (t - 360f));
            s2 += Mathf.Abs(o - (t + 360f));
        }
        return Mathf.Min(s0, s1, s2);
    }

    static float Atan2DegXY(Vector3 v) => Mathf.Atan2(v.y, v.x) * Mathf.Rad2Deg;

    static float QuaternionSlotCostOnly(ElectronOrbitalFunction orb, Vector3 targetDirLocal, float bondRadius)
    {
        if (orb == null) return 0f;
        var (_, r) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(targetDirLocal.normalized, bondRadius, orb.transform.localRotation);
        return Quaternion.Angle(orb.transform.localRotation, r);
    }

    static float QuaternionSlotCostOnlyFromSlotRot(ElectronOrbitalFunction orb, Quaternion slotLocalRot, float bondRadius)
    {
        if (orb == null) return 0f;
        Vector3 d = (slotLocalRot * Vector3.right).normalized;
        return QuaternionSlotCostOnly(orb, d, bondRadius);
    }

    /// <summary>Matches 2D formation: planar wrap via ±360° shifts; full 3D uses cone angle.</summary>
    static float FormationStyleTipToTipCost(Vector3 oldTipLocal, Vector3 newTipLocal)
    {
        oldTipLocal.Normalize();
        newTipLocal.Normalize();
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return Vector3.Angle(oldTipLocal, newTipLocal);
        return PlanarAbsDeltaMinRawMinus360Plus360PairDeg(Atan2DegXY(oldTipLocal), Atan2DegXY(newTipLocal));
    }

    /// <summary>σ redistribution / bond break: cost for assigning a physical lobe to a target hybrid direction — same ingredients as formation (shortest planar angle when 2D) plus in-plane quaternion move after canonical slot.</summary>
    static float FormationStyleOrbitalToDirSlotCost(ElectronOrbitalFunction orb, Vector3 targetDirLocal, float bondRadius)
    {
        if (orb == null) return 0f;
        targetDirLocal.Normalize();
        Vector3 ot = OrbitalTipLocalDirection(orb).normalized;
        float dirCost = FormationStyleTipToTipCost(ot, targetDirLocal);
        var (_, r) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(targetDirLocal, bondRadius, orb.transform.localRotation);
        return dirCost + Quaternion.Angle(orb.transform.localRotation, r);
    }

    static float FormationStyleOrbitalToSlotPoseCost(ElectronOrbitalFunction orb, Quaternion slotLocalRot)
    {
        if (orb == null) return 0f;
        Vector3 nt = (slotLocalRot * Vector3.right).normalized;
        Vector3 ot = OrbitalTipLocalDirection(orb).normalized;
        float dirCost = FormationStyleTipToTipCost(ot, nt);
        return dirCost + Quaternion.Angle(orb.transform.localRotation, slotLocalRot);
    }

    /// <returns>perm where orb <c>i</c> is assigned target direction <c>targetDirs[perm[i]]</c>.</returns>
    /// <param name="tipSpaceNucleus">When set, old tip directions for cost are expressed in this nucleus's local space (σ orbitals on bonds). Quaternion slot cost is skipped for bond-parented orbitals (targets are nucleus-local).</param>
    static int[] FindBestOrbitalToTargetDirsPermutation(List<ElectronOrbitalFunction> orbs, List<Vector3> targetDirs, float bondRadius, AtomFunction tipSpaceNucleus = null)
    {
        int n = orbs.Count;
        if (n != targetDirs.Count || n == 0) return null;
        int[] idx = Enumerable.Range(0, n).ToArray();
        float bestTotal = float.MaxValue;
        int[] bestPerm = null;
        const float eps = 1e-3f;
        bool full3D = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        var oldDeg = new List<float>(n);
        var tgtDeg = new List<float>(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 ot = tipSpaceNucleus != null
                ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                : OrbitalTipLocalDirection(orbs[i]).normalized;
            oldDeg.Add(Atan2DegXY(ot.normalized));
        }
        for (int i = 0; i < n; i++)
            tgtDeg.Add(Atan2DegXY(targetDirs[i].normalized));

        foreach (var perm in Permutations(idx))
        {
            float total;
            float quatSum = 0f;
            for (int i = 0; i < n; i++)
            {
                if (tipSpaceNucleus != null && orbs[i] != null && orbs[i].Bond != null)
                    continue;
                quatSum += QuaternionSlotCostOnly(orbs[i], targetDirs[perm[i]], bondRadius);
            }
            if (full3D)
            {
                float coneSum = 0f;
                for (int i = 0; i < n; i++)
                {
                    Vector3 td = targetDirs[perm[i]].normalized;
                    Vector3 ot = tipSpaceNucleus != null
                        ? tipSpaceNucleus.OrbitalTipDirectionInNucleusLocal(orbs[i])
                        : OrbitalTipLocalDirection(orbs[i]).normalized;
                    coneSum += Vector3.Angle(ot, td);
                }
                total = coneSum + quatSum;
            }
            else
                total = PlanarAnglePermutationCostMinOfThreeSums(oldDeg, tgtDeg, perm) + quatSum;

            bool better = bestPerm == null
                || total < bestTotal - eps
                || (Mathf.Abs(total - bestTotal) <= eps && ComparePermutationLex(perm, bestPerm) < 0);
            if (better)
            {
                bestTotal = total;
                bestPerm = (int[])perm.Clone();
            }
        }
        return bestPerm;
    }

    /// <summary>Bond-break / break-ref redistribution: permute which physical lobe gets which computed slot so Σ∠(old tip, new tip) is minimal (pin lobe unchanged).</summary>
    void RematchRedistributeTargetSlotsMinAngularMotion(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> list, ElectronOrbitalFunction pinOrbital)
    {
        if (list == null || list.Count <= 1) return;
        var movableIdx = new List<int>();
        for (int i = 0; i < list.Count; i++)
        {
