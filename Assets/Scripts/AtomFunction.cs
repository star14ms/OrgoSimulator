using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.Linq;
using TMPro;

public class AtomFunction : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [SerializeField] float bondRadius = 0.8f;
    [SerializeField] int atomicNumber = 1;
    [SerializeField] int charge;
    [SerializeField] [Range(0.05f, 1f)] float atomBodyAlpha = 0.42f;
    [SerializeField] ElectronOrbitalFunction orbitalPrefab;
    readonly List<ElectronOrbitalFunction> bondedOrbitals = new List<ElectronOrbitalFunction>();
    readonly List<CovalentBond> covalentBonds = new List<CovalentBond>();

    bool isBeingHeld;
    Vector3 dragOffset;
    Vector3 atomDragPlanePoint;
    Vector3 atomDragPlaneNormal;
    bool atomDragPlaneValid;
    HashSet<AtomFunction> moleculeAtoms;
    Transform elementLabelTransform;

    public float BondRadius => bondRadius;
    public ElectronOrbitalFunction OrbitalPrefab => orbitalPrefab;
    public int BondedOrbitalCount => bondedOrbitals.Count;
    public int AtomicNumber { get => atomicNumber; set => atomicNumber = Mathf.Clamp(value, 1, 118); }
    public int Charge { get => charge; set { charge = value; RefreshChargeLabel(); } }

    public bool CanAcceptOrbital() => bondedOrbitals.Count < GetOrbitalSlotCount();

    public int GetBondsTo(AtomFunction other)
    {
        if (other == null) return 0;
        int count = 0;
        foreach (var b in covalentBonds)
        {
            if (b == null) continue;
            var o = b.AtomA == this ? b.AtomB : b.AtomA;
            if (o == other) count++;
        }
        return count;
    }

    public int GetPiBondCount()
    {
        var counted = new HashSet<(int, int)>();
        int pi = 0;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            int idA = GetInstanceID();
            int idB = other.GetInstanceID();
            var pair = (Mathf.Min(idA, idB), Mathf.Max(idA, idB));
            if (counted.Contains(pair)) continue;
            counted.Add(pair);
            pi += Mathf.Max(0, GetBondsTo(other) - 1);
        }
        return pi;
    }

    public int GetOrbitalSlotCount()
    {
        if (atomicNumber == 2) return 1; // He: 1s² only
        int group = GetGroupFromAtomicNumber(atomicNumber);
        if (group == 1) return 1;
        if (group == 2) return 2;
        if (group == 3 || group == 13) return 3;
        // Period 3+ N-group / O-group: expanded octet so σ + π (e.g. SO₃, PO₄) can form after four σ bonds.
        if (atomicNumber > 10 && (group == 15 || group == 16)) return 6;
        return 4;
    }

    public static float[] GetSlotAnglesForCount(int n)
    {
        if (n <= 0) return System.Array.Empty<float>();
        if (n == 1) return new[] { 0f };
        var angles = new float[n];
        for (int i = 0; i < n; i++)
            angles[i] = 360f * i / n;
        return angles;
    }

    public void RegisterBond(CovalentBond bond)
    {
        if (bond != null && !covalentBonds.Contains(bond))
            covalentBonds.Add(bond);
    }

    public void UnregisterBond(CovalentBond bond)
    {
        covalentBonds.Remove(bond);
    }

    public IReadOnlyList<CovalentBond> CovalentBonds => covalentBonds;

    /// <summary>Returns the angle (degrees) to the first bond partner for use as redistribution origin. Null if no bonds.</summary>
    public float? GetPrimaryBondDirectionAngle()
    {
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var dir = (other.transform.position - transform.position).normalized;
            if (dir.sqrMagnitude >= 0.01f)
                return Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
        }
        return null;
    }

    public void OnElectronRemoved()
    {
        RefreshCharge();
    }

    public void OnElectronAdded()
    {
        RefreshCharge();
    }

    /// <summary>Computes oxidation state using electronegativity: bonding electrons assigned to the more electronegative atom; equal EN splits 50-50.</summary>
    public int ComputeCharge()
    {
        int valence = GetValenceFromGroup(GetGroupFromAtomicNumber(atomicNumber));
        if (atomicNumber == 2) valence = 2;

        int electronsOwned = 0;

        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null) continue;
            electronsOwned += orb.ElectronCount;
        }

        foreach (var b in covalentBonds)
        {
            if (b == null || b.Orbital == null) continue;
            electronsOwned += b.GetElectronsOwnedBy(this);
        }

        return valence - electronsOwned;
    }

    public static float GetElectronegativity(int z)
    {
        if (z <= 0 || z > ElectronegativityPauling.Length) return 2f;
        return ElectronegativityPauling[z - 1];
    }

    static readonly float[] ElectronegativityPauling =
    {
        2.20f, 2.0f, 0.98f, 1.57f, 2.04f, 2.55f, 3.04f, 3.44f, 3.98f, 2.0f,
        0.93f, 1.31f, 1.61f, 1.90f, 2.19f, 2.58f, 3.16f, 2.0f,
        0.82f, 1.00f, 1.36f, 1.54f, 1.63f, 1.66f, 1.55f, 1.83f, 1.88f, 1.91f, 1.90f, 1.65f, 1.81f, 2.01f, 2.18f, 2.55f, 2.96f, 3.00f,
        0.82f, 0.95f, 1.22f, 1.33f, 1.60f, 2.16f, 1.90f, 2.20f, 2.28f, 2.20f, 1.93f, 1.69f, 1.78f, 1.96f, 2.05f, 2.10f, 2.66f, 2.60f,
        0.79f, 0.89f,
        1.10f, 1.12f, 1.13f, 1.14f, 1.15f, 1.17f, 1.15f, 1.20f, 1.15f, 1.22f, 1.23f, 1.24f, 1.25f, 1.15f, 1.27f,
        1.30f, 1.50f, 2.36f, 1.90f, 2.20f, 2.20f, 2.28f, 2.54f, 2.00f, 1.62f, 2.33f, 2.02f, 2.00f, 2.20f, 2.20f,
        0.70f, 0.90f,
        1.10f, 1.30f, 1.50f, 1.38f, 1.36f, 1.28f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f,
        1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f, 1.30f
    };

    public void RefreshCharge()
    {
        charge = ComputeCharge();
        RefreshChargeLabel();
    }

    public void RefreshChargeLabel()
    {
        var chargeLabel = transform.Find("ElementLabel/ChargeLabel");
        if (chargeLabel == null) return;
        var tmp = chargeLabel.GetComponent<TMP_Text>();
        if (tmp == null) return;
        int abs = Mathf.Abs(charge);
        tmp.text = charge > 0 ? (abs == 1 ? "+" : charge + "+") : charge < 0 ? (abs == 1 ? "-" : abs + "-") : "";
        chargeLabel.gameObject.SetActive(charge != 0);
    }

    /// <summary>Block or unblock pointer interaction on this atom and all its orbitals and electrons. Used during bond formation.</summary>
    public void SetInteractionBlocked(bool blocked)
    {
        var col = GetComponent<Collider>();
        var col2D = GetComponent<Collider2D>();
        if (col != null) col.enabled = !blocked;
        if (col2D != null) col2D.enabled = !blocked;

        foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
        {
            if (orb.transform.parent != transform) continue;
            orb.SetPointerBlocked(blocked);
            orb.SetPhysicsEnabled(!blocked);
            foreach (var e in orb.GetComponentsInChildren<ElectronFunction>())
            {
                e.SetPointerBlocked(blocked);
            }
        }
    }

    public void BondOrbital(ElectronOrbitalFunction orbital)
    {
        if (CanAcceptOrbital() && !bondedOrbitals.Contains(orbital))
            bondedOrbitals.Add(orbital);
    }

    public void UnbondOrbital(ElectronOrbitalFunction orbital)
    {
        bondedOrbitals.Remove(orbital);
    }

    /// <summary>Returns true if orbitals are not on ideal VSEPR directions (3D) or evenly spaced in the XY plane (2D).</summary>
    public bool HasInconsistentOrbitalAngles()
    {
        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return false;

        float tolerance = 360f / (2f * slotCount);
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return HasInconsistentOrbitalDirections3D(tolerance);

        var angles = CollectUniqueOrbitalAngles(tolerance);
        if (angles.Count <= 1) return false;

        var sorted = angles.Select(NormalizeAngleTo360).OrderBy(a => a).ToList();
        float expectedStep = 360f / sorted.Count;
        for (int i = 0; i < sorted.Count; i++)
        {
            float next = i + 1 < sorted.Count ? sorted[i + 1] : sorted[0] + 360f;
            float diff = next - sorted[i];
            if (diff < 0) diff += 360f;
            if (Mathf.Abs(diff - expectedStep) > tolerance) return true;
        }
        return false;
    }

    bool HasInconsistentOrbitalDirections3D(float toleranceDeg)
    {
        var dirs = CollectUniqueOrbitalDirections(toleranceDeg);
        if (dirs.Count <= 1) return false;

        Vector3 refLocal = ResolveReferenceBondDirectionLocal(null, null);
        var idealRaw = VseprLayout.GetIdealLocalDirections(dirs.Count);
        var idealAligned = VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal);
        foreach (var d in dirs)
        {
            float minAng = 360f;
            foreach (var id in idealAligned)
                minAng = Mathf.Min(minAng, Vector3.Angle(d, id));
            if (minAng > toleranceDeg) return true;
        }
        return false;
    }

    /// <summary>Redistributes orbital directions when pi count or bonding changed. In 3D (perspective), uses VSEPR (linear … octahedral). <paramref name="refBondWorldDirection"/> overrides reference axis when set (e.g. bond break).</summary>
    public void RedistributeOrbitals(float? piBondAngleOverride = null, Vector3? refBondWorldDirection = null)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            RedistributeOrbitals3D(piBondAngleOverride, refBondWorldDirection);
            return;
        }

        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return;

        float tolerance = 360f / (2f * slotCount);

        // Step 1: Collect unique orbital angles (lone orbitals + bonds)
        var oldAngles = CollectUniqueOrbitalAngles(tolerance);
        if (oldAngles.Count == 0) return;

        // Step 2: Identify origin angle. Pi bond when present; else 0° (RedistributeOrbitals is used for bond break).
        float? piBondAngle = null;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var dir = (other.transform.position - transform.position).normalized;
            piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            break;
        }
        float originAngle = piBondAngle.HasValue ? NormalizeAngleTo360(piBondAngle.Value)
            : (piBondAngleOverride.HasValue ? NormalizeAngleTo360(piBondAngleOverride.Value) : 0f);

        // Step 3: Build new angle list starting from origin
        int n = oldAngles.Count;
        float step = 360f / n;
        var newAngles = new List<float>();
        for (int i = 0; i < n; i++)
        {
            float a = originAngle + i * step;
            newAngles.Add(NormalizeAngleTo360(a));
        }

        // Step 4: Remove bond slot from both lists when we have bonds (bond stays fixed). When no bonds, redistribute all.
        List<float> oldNonPi;
        List<float> newNonPi;
        if (covalentBonds.Count > 0)
        {
            float refAngle = piBondAngle.HasValue ? originAngle : (piBondAngleOverride.HasValue ? NormalizeAngleTo360(piBondAngleOverride.Value) : 0f);
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null && !piBondAngleOverride.HasValue)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var dir = (other.transform.position - transform.position).normalized;
                if (dir.sqrMagnitude >= 0.01f) refAngle = NormalizeAngleTo360(OrbitalAngleUtility.DirectionToAngleWorld(dir));
            }
            int refIdxOld = FindClosestAngleIndex(oldAngles, refAngle, tolerance);
            int refIdxNew = FindClosestAngleIndex(newAngles, refAngle, tolerance);
            oldNonPi = oldAngles.Where((_, i) => i != refIdxOld).ToList();
            newNonPi = newAngles.Where((_, i) => i != refIdxNew).ToList();
        }
        else
        {
            oldNonPi = oldAngles;
            newNonPi = newAngles;
        }

        if (oldNonPi.Count == 0) return;

        // Step 5: Optimal one-to-one matching (min total angular change)
        var bestMapping = FindBestAngleMapping(oldNonPi, newNonPi);
        if (bestMapping == null) return;

        // Step 6: Apply updates to lone orbitals (one orbital per mapping pair to avoid overlap)
        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldAngle, newAngle) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPosition(newAngle, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                float orbAngle = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
                if (AnglesWithinTolerance(orbAngle, oldAngle, tolerance))
                {
                    orb.transform.localPosition = pos;
                    orb.transform.localRotation = rot;
                    moved.Add(orb);
                    break; // One orbital per new slot to avoid overlap
                }
            }
        }
    }

    /// <summary>World σ axes toward bonded partners, in local space, merged within tolerance.</summary>
    List<Vector3> CollectSigmaBondAxesLocalMerged(float toleranceDeg)
    {
        var bondAxes = new List<Vector3>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude < 0.01f) continue;
            bondAxes.Add(transform.InverseTransformDirection(w).normalized);
        }
        return MergeDirectionsWithinTolerance(bondAxes, toleranceDeg);
    }

    /// <summary>
    /// σ bonds claim vertices of the ideal VSEPR polyhedron; lone orbitals are matched only to the remaining directions.
    /// (Mixing σ bond axes into the lone-orbital “old” pool broke stepwise H-auto methane tetrahedral layout.)
    /// </summary>
    bool TryMatchLoneOrbitalsToFreeIdealDirections(
        Vector3 refLocal,
        int slotCount,
        List<Vector3> bondAxesMerged,
        List<ElectronOrbitalFunction> loneOrbitals,
        List<Vector3> newDirsAligned,
        out List<(Vector3 oldDir, Vector3 newDir)> mapping)
    {
        mapping = null;
        var oldLone = new List<Vector3>(loneOrbitals.Count);
        foreach (var o in loneOrbitals)
            oldLone.Add(OrbitalTipLocalDirection(o));

        if (refLocal.sqrMagnitude < 1e-8f) refLocal = Vector3.right;

        bool[] idealUsed = new bool[slotCount];
        var bondOrder = new List<Vector3>(bondAxesMerged);
        bondOrder.Sort((a, b) =>
            Vector3.Dot(b.normalized, refLocal.normalized).CompareTo(Vector3.Dot(a.normalized, refLocal.normalized)));

        foreach (var bd in bondOrder)
        {
            int bestI = -1;
            float bestDot = -2f;
            for (int i = 0; i < slotCount; i++)
            {
                if (idealUsed[i]) continue;
                float d = Vector3.Dot(bd.normalized, newDirsAligned[i].normalized);
                if (d > bestDot)
                {
                    bestDot = d;
                    bestI = i;
                }
            }
            if (bestI >= 0) idealUsed[bestI] = true;
        }

        var free = new List<Vector3>();
        for (int i = 0; i < slotCount; i++)
        {
            if (!idealUsed[i])
                free.Add(newDirsAligned[i]);
        }

        if (free.Count != oldLone.Count)
            return false;

        mapping = FindBestDirectionMapping(oldLone, free);
        return mapping != null;
    }

    /// <summary>
    /// VSEPR vertex count from current σ directions (one per neighbor) plus lone lobes, clamped to [2, max valence].
    /// Prevents forcing tetrahedral (4) on sp²-like centers (e.g. C with one σ+π partner + two lone → 3 domains).
    /// </summary>
    int GetVseprSlotCount3D(int mergedSigmaAxisCount, int loneOrbitalCount)
    {
        int maxSlots = GetOrbitalSlotCount();
        int domains = mergedSigmaAxisCount + loneOrbitalCount;
        domains = Mathf.Max(domains, 2);
        return Mathf.Min(domains, maxSlots);
    }

    void RedistributeOrbitals3D(float? piBondAngleOverride, Vector3? refBondWorldDirection)
    {
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1) return;

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        Vector3 refLocal = ResolveReferenceBondDirectionLocal(piBondAngleOverride, refBondWorldDirection);

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        if (loneOrbitals.Count == 0) return;

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int slotCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);

        var idealRaw = VseprLayout.GetIdealLocalDirections(slotCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        float matchToleranceDeg = 360f / (2f * slotCount);
        if (!TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, slotCount, bondAxes, loneOrbitals, newDirs, out var bestMapping) ||
            bestMapping == null)
            return;

        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldDir, newDir) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                Vector3 oDir = OrbitalTipLocalDirection(orb);
                if (DirectionsWithinTolerance(oDir, oldDir, matchToleranceDeg))
                {
                    orb.transform.localPosition = pos;
                    orb.transform.localRotation = rot;
                    moved.Add(orb);
                    break;
                }
            }
        }
    }

    Vector3 ResolveReferenceBondDirectionLocal(float? piBondAngleOverride, Vector3? refBondWorldDirection)
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

    List<Vector3> CollectUniqueOrbitalDirections(float toleranceDeg)
    {
        var bondDirs = new List<Vector3>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var w = (other.transform.position - transform.position).normalized;
            if (w.sqrMagnitude < 0.01f) continue;
            bondDirs.Add(transform.InverseTransformDirection(w).normalized);
        }
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

    static List<Vector3> MergeDirectionsWithinTolerance(List<Vector3> dirs, float toleranceDeg)
    {
        if (dirs.Count == 0) return new List<Vector3>();
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

        foreach (var perm in Permutations(indices))
        {
            float total = 0f;
            for (int i = 0; i < oldDirs.Count; i++)
                total += Vector3.Angle(oldDirs[i], newDirs[perm[i]]);
            if (total < bestTotal)
            {
                bestTotal = total;
                bestMapping = new List<(Vector3, Vector3)>();
                for (int i = 0; i < oldDirs.Count; i++)
                    bestMapping.Add((oldDirs[i], newDirs[perm[i]]));
            }
        }
        return bestMapping;
    }

    static bool DirectionsWithinTolerance(Vector3 a, Vector3 b, float tolDeg) =>
        Vector3.Angle(a, b) <= tolDeg;

    void LogPiRedistEmptyTargets3D(int piBefore, string reason)
    {
        if (!ElectronOrbitalFunction.DebugPiOrbitalRedistribution) return;
        if (GetPiBondCount() == piBefore) return;
        ElectronOrbitalFunction.LogPiRedistDebug($"GetRedistributeTargets3D Z={atomicNumber} (id={GetInstanceID()}): 0 tween targets — {reason}");
    }

    /// <summary>Returns (orbital, targetLocalPos, targetLocalRot) for orbitals that would move during RedistributeOrbitals. Empty if pi count unchanged. For sigma bonds, pass newBondPartner to redistribute when bonding to an orbital that was already rotated.</summary>
    public List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets(int piBefore, AtomFunction newBondPartner = null)
    {
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        bool piCountChanged = GetPiBondCount() != piBefore;
        if (!piCountChanged && newBondPartner == null) return result;

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return GetRedistributeTargets3D(piBefore, newBondPartner);

        int slotCount = GetOrbitalSlotCount();
        if (slotCount <= 1) return result;

        float tolerance = 360f / (2f * slotCount);
        var oldAngles = CollectUniqueOrbitalAngles(tolerance);
        if (oldAngles.Count == 0) return result;

        float? piBondAngle = null;
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            if (GetBondsTo(other) <= 1) continue;
            var dir = (other.transform.position - transform.position).normalized;
            piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
            break;
        }
        if (!piBondAngle.HasValue && newBondPartner != null)
        {
            var dir = (newBondPartner.transform.position - transform.position).normalized;
            if (dir.sqrMagnitude >= 0.01f) piBondAngle = OrbitalAngleUtility.DirectionToAngleWorld(dir);
        }
        float originAngle = piBondAngle.HasValue ? NormalizeAngleTo360(piBondAngle.Value) : 0f;

        int n = oldAngles.Count;
        float step = 360f / n;
        var newAngles = new List<float>();
        for (int i = 0; i < n; i++)
            newAngles.Add(NormalizeAngleTo360(originAngle + i * step));

        List<float> oldNonPi;
        List<float> newNonPi;
        if (covalentBonds.Count > 0)
        {
            float refAngle = originAngle;
            var b = covalentBonds.FirstOrDefault(x => x?.AtomA != null && x?.AtomB != null);
            if (b != null)
            {
                var other = b.AtomA == this ? b.AtomB : b.AtomA;
                var dir = (other.transform.position - transform.position).normalized;
                if (dir.sqrMagnitude >= 0.01f) refAngle = NormalizeAngleTo360(OrbitalAngleUtility.DirectionToAngleWorld(dir));
            }
            int refIdxOld = FindClosestAngleIndex(oldAngles, refAngle, tolerance);
            int refIdxNew = FindClosestAngleIndex(newAngles, refAngle, tolerance);
            oldNonPi = oldAngles.Where((_, i) => i != refIdxOld).ToList();
            newNonPi = newAngles.Where((_, i) => i != refIdxNew).ToList();
        }
        else
        {
            oldNonPi = oldAngles;
            newNonPi = newAngles;
        }
        if (oldNonPi.Count == 0) return result;

        var bestMapping = FindBestAngleMapping(oldNonPi, newNonPi);
        if (bestMapping == null) return result;

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldAngle, newAngle) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPosition(newAngle, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                float orbAngle = OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform);
                if (AnglesWithinTolerance(orbAngle, oldAngle, tolerance))
                {
                    result.Add((orb, pos, rot));
                    moved.Add(orb);
                    break;
                }
            }
        }
        return result;
    }

    List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> GetRedistributeTargets3D(int piBefore, AtomFunction newBondPartner)
    {
        var result = new List<(ElectronOrbitalFunction, Vector3, Quaternion)>();
        int maxSlots = GetOrbitalSlotCount();
        if (maxSlots <= 1)
        {
            LogPiRedistEmptyTargets3D(piBefore, "slotCount≤1 (nothing to tween)");
            return result;
        }

        float mergeToleranceDeg = 360f / (2f * maxSlots);
        Vector3 refLocal = RedistributeReferenceDirectionLocalForTargets(newBondPartner);

        var loneOrbitals = bondedOrbitals.Where(orb => orb != null && orb.Bond == null).ToList();
        if (loneOrbitals.Count == 0)
        {
            LogPiRedistEmptyTargets3D(piBefore, "no lone orbitals (every child orbital is in a bond; step-2 only tweens Bond==null)");
            return result;
        }

        var bondAxes = CollectSigmaBondAxesLocalMerged(mergeToleranceDeg);
        int slotCount = GetVseprSlotCount3D(bondAxes.Count, loneOrbitals.Count);

        var idealRaw = VseprLayout.GetIdealLocalDirections(slotCount);
        var newDirs = new List<Vector3>(VseprLayout.AlignFirstDirectionTo(idealRaw, refLocal));

        float matchToleranceDeg = 360f / (2f * slotCount);
        if (!TryMatchLoneOrbitalsToFreeIdealDirections(refLocal, slotCount, bondAxes, loneOrbitals, newDirs, out var bestMapping) ||
            bestMapping == null)
        {
            LogPiRedistEmptyTargets3D(piBefore, $"VSEPR TryMatch failed (lone={loneOrbitals.Count}, vseprSlots={slotCount}, maxSlots={maxSlots}, merged σ axes={bondAxes.Count})");
            return result;
        }

        var moved = new HashSet<ElectronOrbitalFunction>();
        foreach (var (oldDir, newDir) in bestMapping)
        {
            var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(newDir, bondRadius);
            foreach (var orb in loneOrbitals)
            {
                if (moved.Contains(orb)) continue;
                if (DirectionsWithinTolerance(OrbitalTipLocalDirection(orb), oldDir, matchToleranceDeg))
                {
                    result.Add((orb, pos, rot));
                    moved.Add(orb);
                    break;
                }
            }
        }
        return result;
    }

    public void ApplyRedistributeTargets(List<(ElectronOrbitalFunction orb, Vector3 pos, Quaternion rot)> targets)
    {
        foreach (var (orb, pos, rot) in targets)
        {
            if (orb != null && orb.transform.parent == transform)
            {
                orb.transform.localPosition = pos;
                orb.transform.localRotation = rot;
            }
        }
    }

    static float NormalizeAngleTo360(float deg)
    {
        while (deg >= 360f) deg -= 360f;
        while (deg < 0f) deg += 360f;
        return deg;
    }

    static float AngularDifference(float a, float b)
    {
        float delta = Mathf.Abs(OrbitalAngleUtility.NormalizeAngle(a - b));
        return delta > 180f ? 360f - delta : delta;
    }

    static bool AnglesWithinTolerance(float a, float b, float tol)
    {
        return AngularDifference(a, b) <= tol;
    }

    List<float> CollectUniqueOrbitalAngles(float tolerance)
    {
        // Collect bond angles (merge multiple bonds to same atom - double/triple = 1 angle)
        var bondAngles = new List<float>();
        foreach (var b in covalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == this ? b.AtomB : b.AtomA;
            var dir = (other.transform.position - transform.position).normalized;
            bondAngles.Add(OrbitalAngleUtility.DirectionToAngleWorld(dir));
        }
        var mergedBondAngles = MergeAnglesWithinTolerance(bondAngles, tolerance);

        // Collect lone orbital angles - each lone orbital gets its own slot (no merging)
        // so orbitals placed at bond dir by BreakBond are correctly redistributed to non-bond slots
        var loneAngles = new List<float>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.transform.parent != transform) continue;
            loneAngles.Add(OrbitalAngleUtility.GetOrbitalAngleWorld(orb.transform));
        }

        // Combine: bond angles + lone angles. Do NOT merge - each lone orbital needs a slot
        var result = new List<float>(mergedBondAngles);
        result.AddRange(loneAngles);
        return result;
    }

    static List<float> MergeAnglesWithinTolerance(List<float> angles, float tolerance)
    {
        if (angles.Count == 0) return new List<float>();
        var result = new List<float>();
        var used = new bool[angles.Count];
        for (int i = 0; i < angles.Count; i++)
        {
            if (used[i]) continue;
            float representative = NormalizeAngleTo360(angles[i]);
            used[i] = true;
            for (int j = i + 1; j < angles.Count; j++)
            {
                if (used[j]) continue;
                float aj = NormalizeAngleTo360(angles[j]);
                if (AngularDifference(representative, aj) <= tolerance)
                    used[j] = true;
            }
            result.Add(representative);
        }
        return result;
    }

    static int FindClosestAngleIndex(List<float> angles, float target, float tolerance)
    {
        float targetNorm = NormalizeAngleTo360(target);
        int best = 0;
        float bestDelta = 360f;
        for (int i = 0; i < angles.Count; i++)
        {
            float d = AngularDifference(NormalizeAngleTo360(angles[i]), targetNorm);
            if (d < bestDelta)
            {
                bestDelta = d;
                best = i;
            }
        }
        return best;
    }

    static List<(float oldAngle, float newAngle)> FindBestAngleMapping(List<float> oldAngles, List<float> newAngles)
    {
        if (oldAngles.Count != newAngles.Count || oldAngles.Count == 0) return null;
        var indices = Enumerable.Range(0, newAngles.Count).ToArray();
        List<(float, float)> bestMapping = null;
        float bestTotal = float.MaxValue;

        foreach (var perm in Permutations(indices))
        {
            float total = 0f;
            for (int i = 0; i < oldAngles.Count; i++)
                total += AngularDifference(oldAngles[i], newAngles[perm[i]]);
            if (total < bestTotal)
            {
                bestTotal = total;
                bestMapping = new List<(float, float)>();
                for (int i = 0; i < oldAngles.Count; i++)
                    bestMapping.Add((oldAngles[i], newAngles[perm[i]]));
            }
        }
        return bestMapping;
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
        System.Array.Reverse(a, i + 1, a.Length - i - 1);
        return true;
    }

    public (Vector3 position, Quaternion rotation) GetSlotForNewOrbital(Vector3 preferredDirectionWorld, ElectronOrbitalFunction excludeOrbital = null)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
            return GetSlotForNewOrbital3D(preferredDirectionWorld, excludeOrbital);

        Vector3 localDir = transform.InverseTransformDirection(preferredDirectionWorld);
        localDir.z = 0;
        if (localDir.sqrMagnitude < 0.01f) localDir = Vector3.right;
        localDir.Normalize();
        float preferredAngle = Mathf.Atan2(localDir.y, localDir.x) * Mathf.Rad2Deg;

        int slotCount = GetOrbitalSlotCount();
        float[] slotAngles = GetSlotAnglesForCount(slotCount);
        float slotTolerance = 360f / (2f * Mathf.Max(1, slotCount));

        float bestAngle = preferredAngle;
        float bestDelta = 360f;
        foreach (float slotAngle in slotAngles)
        {
            bool occupied = false;
            foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb.transform.parent != transform) continue;
                if (orb == excludeOrbital || orb == null) continue;
                float orbAngle = ElectronOrbitalFunction.NormalizeAngle(orb.transform.localEulerAngles.z);
                float delta = Mathf.Abs(ElectronOrbitalFunction.NormalizeAngle(orbAngle - slotAngle));
                if (delta < slotTolerance || delta > 360f - slotTolerance)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
            {
                float delta = Mathf.Abs(ElectronOrbitalFunction.NormalizeAngle(slotAngle - preferredAngle));
                if (delta > 180f) delta = 360f - delta;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    bestAngle = slotAngle;
                }
            }
        }
        return ElectronOrbitalFunction.GetCanonicalSlotPosition(bestAngle, bondRadius);
    }

    (Vector3 position, Quaternion rotation) GetSlotForNewOrbital3D(Vector3 preferredDirectionWorld, ElectronOrbitalFunction excludeOrbital)
    {
        Vector3 prefLocal = transform.InverseTransformDirection(preferredDirectionWorld);
        if (prefLocal.sqrMagnitude < 1e-6f) prefLocal = Vector3.right;
        prefLocal.Normalize();

        int slotCount = GetOrbitalSlotCount();
        float slotToleranceDeg = 360f / (2f * Mathf.Max(1, slotCount));
        var slots = VseprLayout.GetIdealLocalDirections(slotCount);

        float bestDot = -2f;
        Vector3 bestDir = slots[0];
        foreach (var slot in slots)
        {
            bool occupied = false;
            foreach (var orb in GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (orb.transform.parent != transform || orb == excludeOrbital || orb == null) continue;
                if (Vector3.Angle(OrbitalTipLocalDirection(orb), slot) < slotToleranceDeg)
                {
                    occupied = true;
                    break;
                }
            }
            if (!occupied)
            {
                float dot = Vector3.Dot(prefLocal, slot);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestDir = slot;
                }
            }
        }
        return ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(bestDir, bondRadius);
    }

    public ElectronOrbitalFunction GetEmptyLoneOrbital()
    {
        foreach (var orb in bondedOrbitals)
            if (orb != null && orb.Bond == null && orb.ElectronCount == 0)
                return orb;
        return null;
    }

    public ElectronOrbitalFunction GetAvailableLoneOrbitalForBond(ElectronOrbitalFunction excludeOrbital, int sourceElectronCount, Vector3 hitPosition, Camera viewCamera = null)
    {
        var cam = viewCamera != null ? viewCamera : Camera.main;
        ElectronOrbitalFunction best = null;
        float bestScore = float.MaxValue;

        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb == excludeOrbital) continue;
            if (orb.ElectronCount + sourceElectronCount != ElectronOrbitalFunction.MaxElectrons) continue;

            bool hit3d = orb.ContainsPoint(hitPosition);
            bool hitView = cam != null && excludeOrbital != null &&
                           ElectronOrbitalFunction.OrbitalViewOverlaps(cam, excludeOrbital, orb);
            if (!hit3d && !hitView) continue;

            float score;
            if (cam == null)
                return orb;
            Vector3 sh = cam.WorldToScreenPoint(hitPosition);
            Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
            if (sh.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane)
                score = ((Vector2)sh - (Vector2)so).sqrMagnitude;
            else
                score = hit3d ? 0f : float.MaxValue;

            if (score < bestScore)
            {
                bestScore = score;
                best = orb;
            }
        }

        return best;
    }

    /// <summary>Returns a lone orbital with exactly 1 electron, preferring the one closest to preferredDirection. For programmatic bonding.</summary>
    public ElectronOrbitalFunction GetLoneOrbitalWithOneElectron(Vector3 preferredDirectionWorld)
    {
        ElectronOrbitalFunction best = null;
        float bestDot = -2f;
        Vector3 dirNorm = preferredDirectionWorld.sqrMagnitude >= 0.01f ? preferredDirectionWorld.normalized : Vector3.right;
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 1) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), dirNorm);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = orb;
            }
        }
        return best;
    }

    /// <summary>Returns a lone orbital for bond formation. Prefers 1-electron; if only 2-electron (lone pair) available, returns it and sets to 1. Used when replacing H.</summary>
    public ElectronOrbitalFunction GetLoneOrbitalForBondFormation(Vector3 preferredDirectionWorld)
    {
        var one = GetLoneOrbitalWithOneElectron(preferredDirectionWorld);
        if (one != null) return one;
        ElectronOrbitalFunction best = null;
        float bestDot = -2f;
        Vector3 dirNorm = preferredDirectionWorld.sqrMagnitude >= 0.01f ? preferredDirectionWorld.normalized : Vector3.right;
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 2) continue;
            float dot = Vector3.Dot(orb.transform.TransformDirection(Vector3.right), dirNorm);
            if (dot > bestDot)
            {
                bestDot = dot;
                best = orb;
            }
        }
        if (best != null)
            best.ElectronCount = 1;
        return best;
    }

    public bool HasEmptyLoneOrbital() => GetEmptyLoneOrbital() != null;

    /// <summary>Returns lone orbitals with 1 electron, sorted by angle (0° to 360°).</summary>
    public List<ElectronOrbitalFunction> GetLoneOrbitalsWithOneElectronSortedByAngle()
    {
        var list = new List<ElectronOrbitalFunction>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb.ElectronCount != 1) continue;
            list.Add(orb);
        }
        list.Sort((a, b) =>
        {
            float aa = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(a.transform));
            float ab = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(b.transform));
            return aa.CompareTo(ab);
        });
        return list;
    }

    /// <summary>Every orbital on this atom (lone and bond-associated), sorted by world pointing angle for stable keyboard order in 2D and 3D.</summary>
    public List<ElectronOrbitalFunction> GetAllOrbitalsSortedForArrowCycling()
    {
        var list = new List<ElectronOrbitalFunction>();
        foreach (var orb in bondedOrbitals)
        {
            if (orb != null) list.Add(orb);
        }
        list.Sort((a, b) =>
        {
            float aa = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(a.transform));
            float ab = OrbitalAngleUtility.NormalizeAngle(OrbitalAngleUtility.GetOrbitalAngleWorld(b.transform));
            int cmp = aa.CompareTo(ab);
            if (cmp != 0) return cmp;
            return a.GetInstanceID().CompareTo(b.GetInstanceID());
        });
        return list;
    }

    /// <summary>Next/previous in <see cref="GetAllOrbitalsSortedForArrowCycling"/> (wraps). Delta +1 moves forward in that list.</summary>
    public ElectronOrbitalFunction GetAdjacentOrbitalInList(ElectronOrbitalFunction current, int delta)
    {
        var list = GetAllOrbitalsSortedForArrowCycling();
        if (list.Count == 0) return null;
        if (current == null) return list[0];
        int idx = list.IndexOf(current);
        if (idx < 0) return list[0];
        int n = list.Count;
        int next = ((idx + delta) % n + n) % n;
        return list[next];
    }

    /// <summary>Orbital closest to targetAngle (0° = right). For initial selection.</summary>
    public ElectronOrbitalFunction GetOrbitalClosestToAngle(float targetAngleDeg)
    {
        return GetLoneOrbitalWithOneElectron(new Vector3(Mathf.Cos(targetAngleDeg * Mathf.Deg2Rad), Mathf.Sin(targetAngleDeg * Mathf.Deg2Rad), 0));
    }

    GameObject selectionHighlight;
    GameObject selectionHighlight3D;

    public void SetSelectionHighlight(bool on)
    {
        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            SetSelectionHighlight3D(on);
            if (selectionHighlight != null) selectionHighlight.SetActive(false);
            return;
        }

        if (selectionHighlight3D != null) selectionHighlight3D.SetActive(false);

        if (on)
        {
            if (selectionHighlight == null)
            {
                selectionHighlight = new GameObject("SelectionHighlight");
                selectionHighlight.transform.SetParent(transform);
                selectionHighlight.transform.localPosition = Vector3.zero;
                selectionHighlight.transform.localRotation = Quaternion.identity;
                selectionHighlight.transform.localScale = Vector3.one * 1.4f;
                var sr = selectionHighlight.AddComponent<SpriteRenderer>();
                sr.sprite = CreateCircleSprite();
                sr.color = new Color(0.3f, 0.9f, 0.4f, 0.6f);
                sr.sortingOrder = -1;
            }
            if (selectionHighlight != null) selectionHighlight.SetActive(true);
        }
        else if (selectionHighlight != null)
        {
            selectionHighlight.SetActive(false);
        }
    }

    void SetSelectionHighlight3D(bool on)
    {
        if (!on)
        {
            if (selectionHighlight3D != null) selectionHighlight3D.SetActive(false);
            return;
        }

        if (selectionHighlight3D != null && selectionHighlight3D.GetComponent<SpriteRenderer>() == null)
        {
            Destroy(selectionHighlight3D);
            selectionHighlight3D = null;
        }

        if (selectionHighlight3D == null)
        {
            selectionHighlight3D = new GameObject("SelectionHighlight3D");
            selectionHighlight3D.transform.SetParent(transform, false);
            selectionHighlight3D.transform.localPosition = Vector3.zero;
            selectionHighlight3D.transform.localRotation = Quaternion.identity;
            selectionHighlight3D.transform.localScale = Vector3.one;
            var sr = selectionHighlight3D.AddComponent<SpriteRenderer>();
            sr.sprite = CreateCircleSprite();
            sr.color = new Color(0.25f, 0.98f, 0.42f, 0.92f);
            sr.sortingOrder = 80;
        }

        float rWorld = GetAtomBodyRadiusWorld();
        float parentMag = Mathf.Max(
            Mathf.Abs(transform.lossyScale.x),
            Mathf.Abs(transform.lossyScale.y),
            Mathf.Abs(transform.lossyScale.z));
        parentMag = Mathf.Max(parentMag, 1e-4f);
        float diameterWorld = 2f * rWorld * 1.18f;
        float childUniform = diameterWorld / parentMag;
        selectionHighlight3D.transform.localScale = Vector3.one * childUniform;
        selectionHighlight3D.SetActive(true);
        BillboardSelectionRingTowardMainCamera(selectionHighlight3D.transform, transform.position);
    }

    static void BillboardSelectionRingTowardMainCamera(Transform ring, Vector3 worldAnchor)
    {
        var cam = Camera.main;
        if (cam == null || ring == null) return;
        Vector3 toCam = cam.transform.position - worldAnchor;
        if (toCam.sqrMagnitude < 1e-10f) return;
        ring.position = worldAnchor;
        ring.rotation = Quaternion.LookRotation(toCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
    }

    static bool TryScreenRayIntersectPlane(Camera cam, Vector2 screen, Vector3 planeNormal, Vector3 planePoint, out Vector3 hit)
    {
        hit = default;
        if (cam == null) return false;
        var ray = cam.ScreenPointToRay(screen);
        var plane = new Plane(planeNormal.normalized, planePoint);
        if (!plane.Raycast(ray, out float t)) return false;
        hit = ray.GetPoint(t);
        return true;
    }

    /// <summary>Project <paramref name="point"/> onto the plane (camera-facing “depth” sheet) through <paramref name="planePoint"/>.</summary>
    static Vector3 ProjectPointOntoViewDepthPlane(Vector3 point, Vector3 planePoint, Vector3 viewForward)
    {
        Vector3 n = viewForward.normalized;
        return point - n * Vector3.Dot(point - planePoint, n);
    }

    void SnapMoleculeToViewDepthPlane(Camera cam, Vector3 planeAnchor)
    {
        if (moleculeAtoms == null || cam == null) return;
        Vector3 n = cam.transform.forward;
        foreach (var a in moleculeAtoms)
        {
            if (a == null) continue;
            a.transform.position = ProjectPointOntoViewDepthPlane(a.transform.position, planeAnchor, n);
        }
    }

    static void ProjectMoleculeOntoPlane(HashSet<AtomFunction> atoms, Vector3 planePoint, Vector3 planeNormal)
    {
        if (atoms == null) return;
        Vector3 n = planeNormal.sqrMagnitude > 1e-10f ? planeNormal.normalized : Vector3.forward;
        foreach (var a in atoms)
        {
            if (a == null) continue;
            a.transform.position = ProjectPointOntoViewDepthPlane(a.transform.position, planePoint, n);
        }
    }

    void SnapMoleculeAfterDragStep(Camera cam)
    {
        if (moleculeAtoms == null || cam == null) return;
        var wp = MoleculeWorkPlane.Instance;
        if (wp != null)
            ProjectMoleculeOntoPlane(moleculeAtoms, wp.WorldPlanePoint, wp.WorldPlaneNormal);
        else
            SnapMoleculeToViewDepthPlane(cam, transform.position);
    }

    static Sprite CreateCircleSprite()
    {
        int size = 32;
        var tex = new Texture2D(size, size);
        tex.filterMode = FilterMode.Bilinear;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x - size * 0.5f) / (size * 0.5f);
                float dy = (y - size * 0.5f) / (size * 0.5f);
                float r = Mathf.Sqrt(dx * dx + dy * dy);
                tex.SetPixel(x, y, r >= 0.85f && r <= 1f ? Color.white : Color.clear);
            }
        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 32f);
    }

    public bool HasAvailableLoneOrbitalForBond(ElectronOrbitalFunction excludeOrbital, int sourceElectronCount)
    {
        foreach (var orb in bondedOrbitals)
        {
            if (orb == null || orb.Bond != null || orb == excludeOrbital) continue;
            if (orb.ElectronCount + sourceElectronCount == ElectronOrbitalFunction.MaxElectrons)
                return true;
        }
        return false;
    }

    public static AtomFunction FindBondPartner(AtomFunction sourceAtom, Vector3 tipPosition, ElectronOrbitalFunction sourceOrbital)
    {
        if (sourceAtom == null || sourceOrbital == null) return null;
        var cam = Camera.main;
        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        AtomFunction best = null;
        float bestMetric = float.MaxValue;

        foreach (var a in atoms)
        {
            if (a == sourceAtom) continue;
            if (!a.HasAvailableLoneOrbitalForBond(sourceOrbital, sourceOrbital.ElectronCount)) continue;

            var orb = a.GetAvailableLoneOrbitalForBond(sourceOrbital, sourceOrbital.ElectronCount, tipPosition, cam);
            if (orb == null) continue;

            float metric;
            if (cam != null)
            {
                Vector3 st = cam.WorldToScreenPoint(tipPosition);
                Vector3 so = cam.WorldToScreenPoint(orb.transform.position);
                metric = st.z >= cam.nearClipPlane && so.z >= cam.nearClipPlane
                    ? ((Vector2)st - (Vector2)so).sqrMagnitude
                    : Vector3.SqrMagnitude(a.transform.position - tipPosition);
            }
            else
                metric = Vector3.SqrMagnitude(a.transform.position - tipPosition);

            if (metric < bestMetric)
            {
                bestMetric = metric;
                best = a;
            }
        }

        return best;
    }

    public void SetupIgnoreCollisions()
    {
        SetupGlobalIgnoreCollisions();
    }

    public static void SetupGlobalIgnoreCollisions()
    {
        var atoms = Object.FindObjectsByType<AtomFunction>(FindObjectsSortMode.None);
        var orbitals = new List<ElectronOrbitalFunction>();
        var allElectrons = new List<ElectronFunction>();
        var atomColliders = new List<(Collider2D c2d, Collider c3d)>();

        foreach (var a in atoms)
        {
            var a2d = a.GetComponent<Collider2D>();
            var a3d = a.GetComponent<Collider>();
            if (a2d != null || a3d != null) atomColliders.Add((a2d, a3d));
            orbitals.AddRange(a.GetComponentsInChildren<ElectronOrbitalFunction>());
            foreach (var b in a.CovalentBonds)
                if (b?.Orbital != null && !orbitals.Contains(b.Orbital))
                    orbitals.Add(b.Orbital);
        }
        foreach (var orb in orbitals)
            allElectrons.AddRange(orb.GetComponentsInChildren<ElectronFunction>());

        foreach (var e in Object.FindObjectsByType<ElectronFunction>(FindObjectsSortMode.None))
        {
            if (e != null && !allElectrons.Contains(e))
                allElectrons.Add(e);
        }

        for (int i = 0; i < atomColliders.Count; i++)
            for (int j = i + 1; j < atomColliders.Count; j++)
                IgnoreCollisionPair(atomColliders[i], atomColliders[j]);
        foreach (var ac in atomColliders)
            foreach (var orb in orbitals)
                IgnoreCollision(ac, orb.GetComponent<Collider2D>(), orb.GetComponent<Collider>());
        foreach (var ac in atomColliders)
            foreach (var e in allElectrons)
                IgnoreCollision(ac, e.GetComponent<Collider2D>(), e.GetComponent<Collider>());
        for (int i = 0; i < orbitals.Count; i++)
        {
            for (int j = i + 1; j < orbitals.Count; j++)
                IgnoreCollision(orbitals[i], orbitals[j]);
            foreach (var e in allElectrons)
                IgnoreCollision(orbitals[i], e);
        }
        for (int i = 0; i < allElectrons.Count; i++)
            for (int j = i + 1; j < allElectrons.Count; j++)
                IgnoreCollision(allElectrons[i], allElectrons[j]);
    }

    static void IgnoreCollisionPair((Collider2D c2d, Collider c3d) a, (Collider2D c2d, Collider c3d) b)
    {
        if (a.c2d != null && b.c2d != null) Physics2D.IgnoreCollision(a.c2d, b.c2d);
        if (a.c3d != null && b.c3d != null) Physics.IgnoreCollision(a.c3d, b.c3d);
    }

    static void IgnoreCollision((Collider2D c2d, Collider c3d) a, Collider2D b2d, Collider b3d)
    {
        if (a.c2d != null && b2d != null) Physics2D.IgnoreCollision(a.c2d, b2d);
        if (a.c3d != null && b3d != null) Physics.IgnoreCollision(a.c3d, b3d);
    }

    static void IgnoreCollision(ElectronOrbitalFunction a, ElectronOrbitalFunction b)
    {
        var a2D = a.GetComponent<Collider2D>();
        var a3D = a.GetComponent<Collider>();
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    static void IgnoreCollision(ElectronOrbitalFunction orb, ElectronFunction e)
    {
        var o2D = orb.GetComponent<Collider2D>();
        var o3D = orb.GetComponent<Collider>();
        var e2D = e.GetComponent<Collider2D>();
        var e3D = e.GetComponent<Collider>();
        if (o2D != null && e2D != null) Physics2D.IgnoreCollision(o2D, e2D);
        if (o3D != null && e3D != null) Physics.IgnoreCollision(o3D, e3D);
    }

    static void IgnoreCollision(ElectronFunction a, ElectronFunction b)
    {
        var a2D = a.GetComponent<Collider2D>();
        var a3D = a.GetComponent<Collider>();
        var b2D = b.GetComponent<Collider2D>();
        var b3D = b.GetComponent<Collider>();
        if (a2D != null && b2D != null) Physics2D.IgnoreCollision(a2D, b2D);
        if (a3D != null && b3D != null) Physics.IgnoreCollision(a3D, b3D);
    }

    void Start()
    {
        EnsureCollider();
        InitializeFromAtomicNumber();
    }

    /// <summary>World-space radius of the atom body for placing labels in front of the mesh (not inside it).</summary>
    float GetAtomBodyRadiusWorld()
    {
        var sph = GetComponent<SphereCollider>();
        if (sph != null)
        {
            Vector3 ls = transform.lossyScale;
            float m = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z));
            return sph.radius * m;
        }
        var cap = GetComponent<CapsuleCollider>();
        if (cap != null)
        {
            Vector3 ls = transform.lossyScale;
            float rm = Mathf.Max(Mathf.Abs(ls.x), Mathf.Abs(ls.z));
            float hm = Mathf.Abs(ls.y);
            return Mathf.Max(cap.radius * rm, cap.height * 0.5f * hm);
        }
        var sr = GetComponent<SpriteRenderer>();
        if (sr != null && sr.sprite != null && sr.enabled)
            return Mathf.Max(sr.bounds.extents.x, sr.bounds.extents.y);

        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
            return Mathf.Max(mr.bounds.extents.x, mr.bounds.extents.y, mr.bounds.extents.z);
        return 0.42f;
    }

    void LateUpdate()
    {
        if (selectionHighlight3D != null && selectionHighlight3D.activeSelf)
            BillboardSelectionRingTowardMainCamera(selectionHighlight3D.transform, transform.position);

        if (elementLabelTransform == null)
            elementLabelTransform = transform.Find("ElementLabel");
        if (elementLabelTransform == null) return;
        var cam = Camera.main;
        if (cam == null) return;
        Vector3 labelAnchor = transform.position;
        float bodyRworld = GetAtomBodyRadiusWorld();
        float radial = bodyRworld + elementLabelBeyondBodyMargin;

        if (cam.orthographic)
        {
            // Depth offset only along view axis (keeps label centered in XY); rotation matches perspective TMP convention.
            float offset = Mathf.Min(radial, cam.orthographicSize * 0.35f);
            Vector3 f = cam.transform.forward;
            if (f.sqrMagnitude < 1e-10f) return;
            f.Normalize();
            float along = Vector3.Dot(cam.transform.position - labelAnchor, f);
            float sign = Mathf.Abs(along) < 1e-4f ? -1f : Mathf.Sign(along);
            elementLabelTransform.position = labelAnchor + f * (sign * offset);

            Vector3 lookToCam = cam.transform.position - labelAnchor;
            if (lookToCam.sqrMagnitude < 1e-10f)
                lookToCam = -f;
            else
                lookToCam.Normalize();
            elementLabelTransform.rotation = Quaternion.LookRotation(lookToCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
            return;
        }

        var toCam = cam.transform.position - labelAnchor;
        if (toCam.sqrMagnitude < 1e-10f) return;
        var toCamDir = toCam.normalized;
        float camDist = toCam.magnitude;
        float offsetPersp = Mathf.Min(radial, camDist * 0.42f);
        elementLabelTransform.position = labelAnchor + toCamDir * offsetPersp;
        // TMP world quads are authored with the visible side opposite default LookRotation(toCam).
        elementLabelTransform.rotation = Quaternion.LookRotation(toCam, cam.transform.up) * Quaternion.Euler(0f, 180f, 0f);
    }

    void EnsureCollider()
    {
        if (GetComponent<Collider>() != null || GetComponent<Collider2D>() != null) return;
        var col = gameObject.AddComponent<BoxCollider>();
        col.size = new Vector3(1f, 1f, 0.1f);
        col.isTrigger = true;
    }

    public void OnPointerDown(PointerEventData eventData)
    {
        var editMode = FindFirstObjectByType<EditModeManager>();
        if (editMode != null && editMode.EraserMode)
        {
            if (editMode.TryEraseAtomIfChainEnd(this))
                return;
            return;
        }

        isBeingHeld = true;
        moleculeAtoms = GetConnectedMolecule();

        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        // Lone atom: project onto sheet / view depth. Bonded: preserve 3D geometry — only translate the whole molecule
        // rigidly so the grabbed atom lies on the work plane (partners are not individually projected).
        if (cam != null && moleculeAtoms != null)
        {
            if (moleculeAtoms.Count == 1)
            {
                if (wp != null)
                    ProjectMoleculeOntoPlane(moleculeAtoms, wp.WorldPlanePoint, wp.WorldPlaneNormal);
                else
                    SnapMoleculeToViewDepthPlane(cam, transform.position);
            }
            else if (wp != null)
            {
                Vector3 n = wp.WorldPlaneNormal.sqrMagnitude > 1e-10f ? wp.WorldPlaneNormal.normalized : Vector3.forward;
                Vector3 projectedLead = ProjectPointOntoViewDepthPlane(transform.position, wp.WorldPlanePoint, n);
                Vector3 rigid = projectedLead - transform.position;
                if (rigid.sqrMagnitude > 1e-12f)
                {
                    foreach (var a in moleculeAtoms)
                        if (a != null) a.transform.position += rigid;
                }
            }
        }

        atomDragPlaneValid = false;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out var workHit))
        {
            atomDragPlaneNormal = wp.WorldPlaneNormal;
            atomDragPlanePoint = wp.WorldPlanePoint;
            atomDragPlaneValid = true;
            dragOffset = transform.position - workHit;
        }
        else if (cam != null && TryScreenRayIntersectPlane(cam, eventData.position, cam.transform.forward, transform.position, out var planeHit))
        {
            atomDragPlaneNormal = cam.transform.forward;
            atomDragPlanePoint = transform.position;
            atomDragPlaneValid = true;
            dragOffset = transform.position - planeHit;
        }
        else
        {
            dragOffset = transform.position - PlanarPointerInteraction.ScreenToWorldPoint(eventData.position);
        }

        if (editMode != null)
            editMode.OnAtomClicked(this);
    }

    public void OnDrag(PointerEventData eventData)
    {
        if (!isBeingHeld) return;

        Vector3 newPos;
        var cam = Camera.main;
        var wp = MoleculeWorkPlane.Instance;
        Vector3 hit = default;
        bool haveHit = false;
        if (cam != null && wp != null && wp.TryGetWorldPoint(cam, eventData.position, out hit))
            haveHit = true;
        else if (atomDragPlaneValid && cam != null &&
                 TryScreenRayIntersectPlane(cam, eventData.position, atomDragPlaneNormal, atomDragPlanePoint, out hit))
            haveHit = true;

        if (haveHit)
            newPos = hit + dragOffset;
        else
            newPos = PlanarPointerInteraction.ScreenToWorldPoint(eventData.position) + dragOffset;

        var delta = newPos - transform.position;
        // Move grabbed atom first; rest of molecule follows by the same rigid offset (preserves relative layout).
        transform.position = newPos;
        if (moleculeAtoms != null)
        {
            foreach (var a in moleculeAtoms)
            {
                if (a != this && a != null)
                    a.transform.position += delta;
            }
            // Only flatten a single atom onto the plane each step; bonded molecules stay rigid (same translation only).
            if (Camera.main != null && moleculeAtoms.Count == 1)
                SnapMoleculeAfterDragStep(Camera.main);
        }

        if (haveHit)
            dragOffset = transform.position - hit;
    }

    public HashSet<AtomFunction> GetConnectedMolecule()
    {
        var set = new HashSet<AtomFunction>();
        var queue = new Queue<AtomFunction>();
        queue.Enqueue(this);
        set.Add(this);
        while (queue.Count > 0)
        {
            var a = queue.Dequeue();
            foreach (var b in a.CovalentBonds)
            {
                if (b == null) continue;
                var other = b.AtomA == a ? b.AtomB : b.AtomA;
                if (other != null && set.Add(other))
                    queue.Enqueue(other);
            }
        }
        return set;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        if (isBeingHeld && moleculeAtoms != null)
        {
            var disposal = FindFirstObjectByType<DisposalZone>();
            if (disposal != null && disposal.ContainsScreenPoint(eventData.position))
            {
                disposal.DestroyMolecule(moleculeAtoms);
                moleculeAtoms = null;
            }
        }
        isBeingHeld = false;
        atomDragPlaneValid = false;
    }

    bool initialized;

    /// <summary>Call after setting AtomicNumber when bonds need to be formed immediately (e.g. MoleculeBuilder). Normally Start handles this.</summary>
    public void ForceInitialize() => InitializeFromAtomicNumber();

    void InitializeFromAtomicNumber()
    {
        if (initialized) return;
        initialized = true;
        int valence = GetValenceFromGroup(GetGroupFromAtomicNumber(atomicNumber));
        int valenceElectrons = Mathf.Max(0, valence - charge);
        if (atomicNumber == 2) valenceElectrons = 2; // He: only 1s²
        CreateOrbitalsWithValence(valenceElectrons);
        ApplyAtomBodyTint();
        CreateElementLabel();
        RefreshCharge();
    }

    static readonly int ShaderPropBaseColor = Shader.PropertyToID("_BaseColor");
    static readonly int ShaderPropColor = Shader.PropertyToID("_Color");
    static readonly int ShaderPropWhite = Shader.PropertyToID("_White");
    static readonly int ShaderPropRendererColor = Shader.PropertyToID("_RendererColor");

    static void SetMaterialMainTint(Material mat, Color c)
    {
        if (mat == null) return;
        if (mat.HasProperty(ShaderPropBaseColor)) mat.SetColor(ShaderPropBaseColor, c);
        else if (mat.HasProperty(ShaderPropWhite)) mat.SetColor(ShaderPropWhite, c);
        else if (mat.HasProperty(ShaderPropRendererColor)) mat.SetColor(ShaderPropRendererColor, c);
        else if (mat.HasProperty(ShaderPropColor)) mat.SetColor(ShaderPropColor, c);
    }

    static Color ContrastingLabelColor(Color opaqueElementColor)
    {
        float lum = 0.299f * opaqueElementColor.r + 0.587f * opaqueElementColor.g + 0.114f * opaqueElementColor.b;
        return lum > 0.55f ? new Color(0.12f, 0.12f, 0.18f, 1f) : Color.white;
    }

    void ApplyAtomBodyTint()
    {
        Color baseElem = PeriodicTableUI.GetAtomSphereColor(atomicNumber);
        Color body = baseElem;
        body.a = atomBodyAlpha;

        var sr = GetComponent<SpriteRenderer>();
        if (sr != null)
        {
            sr.color = body;
            return;
        }

        var mr = GetComponent<MeshRenderer>();
        if (mr != null)
        {
            var mat = mr.material;
            SetMaterialMainTint(mat, body);
        }
    }

    [SerializeField] Vector2 chargeLabelOffset = new Vector2(0.5f, 0.33f); // Fraction of elementLabelSize (0.5 = right/top edge)
    [SerializeField] Vector2 elementLabelSize = new Vector2(1f, 1f);
    [SerializeField] Vector2 chargeLabelSize = new Vector2(0.5f, 0.5f);
    [Tooltip("World units beyond the atom body radius along the view ray so element + charge TMP sit outside the sphere.")]
    [SerializeField] float elementLabelBeyondBodyMargin = 0.035f;

    public static TMP_FontAsset GetDefaultFont()
    {
        var font = TMP_Settings.defaultFontAsset;
        if (font != null)
            return font;
        font = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF");
        if (font != null)
            return font;
        return Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
    }

    void CreateElementLabel()
    {
        var labelObj = new GameObject("ElementLabel");
        labelObj.transform.SetParent(transform);
        labelObj.transform.localPosition = Vector3.zero;
        labelObj.transform.localRotation = Quaternion.identity;
        labelObj.transform.localScale = Vector3.one;
        var tmp = labelObj.AddComponent<TextMeshPro>();
        tmp.font = GetDefaultFont();
        tmp.text = GetElementSymbol(atomicNumber);
        tmp.fontSize = 6f;
        tmp.alignment = TextAlignmentOptions.Center;
        if (tmp.rectTransform != null)
        {
            tmp.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            tmp.rectTransform.anchorMin = tmp.rectTransform.anchorMax = new Vector2(0.5f, 0.5f);
            tmp.rectTransform.sizeDelta = elementLabelSize;
        }
        var labelFg = atomicNumber == 1
            ? Color.white
            : ContrastingLabelColor(PeriodicTableUI.GetAtomSphereColor(atomicNumber));
        tmp.color = labelFg;

        var chargeObj = new GameObject("ChargeLabel");
        chargeObj.transform.SetParent(labelObj.transform);
        chargeObj.transform.localPosition = new Vector3(
            elementLabelSize.x * chargeLabelOffset.x,
            elementLabelSize.y * chargeLabelOffset.y,
            0f);
        chargeObj.transform.localRotation = Quaternion.identity;
        chargeObj.transform.localScale = Vector3.one;
        var chargeTmp = chargeObj.AddComponent<TextMeshPro>();
        chargeTmp.font = GetDefaultFont();
        chargeTmp.fontSize = 4f;
        if (chargeTmp.rectTransform != null)
            chargeTmp.rectTransform.sizeDelta = chargeLabelSize;
        chargeTmp.alignment = TextAlignmentOptions.BottomLeft;
        chargeTmp.color = labelFg;
        RefreshChargeLabel();
        elementLabelTransform = labelObj.transform;
    }

    public static string GetElementSymbol(int z)
    {
        if (z <= 0 || z > ElementSymbols.Length) return "?";
        return ElementSymbols[z - 1];
    }

    static readonly string[] ElementSymbols =
    {
        "H", "He", "Li", "Be", "B", "C", "N", "O", "F", "Ne", "Na", "Mg", "Al", "Si", "P", "S", "Cl", "Ar",
        "K", "Ca", "Sc", "Ti", "V", "Cr", "Mn", "Fe", "Co", "Ni", "Cu", "Zn", "Ga", "Ge", "As", "Se", "Br", "Kr",
        "Rb", "Sr", "Y", "Zr", "Nb", "Mo", "Tc", "Ru", "Rh", "Pd", "Ag", "Cd", "In", "Sn", "Sb", "Te", "I", "Xe",
        "Cs", "Ba", "La", "Ce", "Pr", "Nd", "Pm", "Sm", "Eu", "Gd", "Tb", "Dy", "Ho", "Er", "Tm", "Yb", "Lu",
        "Hf", "Ta", "W", "Re", "Os", "Ir", "Pt", "Au", "Hg", "Tl", "Pb", "Bi", "Po", "At", "Rn",
        "Fr", "Ra", "Ac", "Th", "Pa", "U", "Np", "Pu", "Am", "Cm", "Bk", "Cf", "Es", "Fm", "Md", "No", "Lr",
        "Rf", "Db", "Sg", "Bh", "Hs", "Mt", "Ds", "Rg", "Cn", "Nh", "Fl", "Mc", "Lv", "Ts", "Og"
    };

    // Group 1-18 by atomic number. Lanthanides (57-71) and actinides (89-103) = group 3.
    static readonly int[] GroupByAtomicNumber =
    {
        0, 1, 18, 1, 2, 13, 14, 15, 16, 17, 18, 1, 2, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
        1, 2, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18
    };

    static int GetGroupFromAtomicNumber(int z)
    {
        if (z <= 0 || z > 118) return 1;
        return GroupByAtomicNumber[z];
    }

    static int GetValenceFromGroup(int group) => group switch
    {
        1 => 1, 2 => 2, 13 => 3, 14 => 4, 15 => 5, 16 => 6, 17 => 7, 18 => 8,
        _ => Mathf.Min(group, 8)
    };

    void CreateOrbitalsWithValence(int valence)
    {
        if (orbitalPrefab == null) return;
        int orbitalCount = GetOrbitalSlotCount();
        float[] angles = GetSlotAnglesForCount(orbitalCount);
        float offset = bondRadius * 0.6f;

        // Distribute electrons: prefer 1 per orbital first (for sigma bonding), then fill to 2
        int[] electronsPerOrbital = new int[orbitalCount];
        int remaining = valence;
        for (int i = 0; i < orbitalCount && remaining > 0; i++)
        {
            electronsPerOrbital[i] = Mathf.Min(1, remaining);
            remaining -= electronsPerOrbital[i];
        }
        for (int i = 0; i < orbitalCount && remaining > 0; i++)
        {
            int add = Mathf.Min(ElectronOrbitalFunction.MaxElectrons - electronsPerOrbital[i], remaining);
            electronsPerOrbital[i] += add;
            remaining -= add;
        }

        if (OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            var dirs = VseprLayout.GetIdealLocalDirections(orbitalCount);
            for (int i = 0; i < orbitalCount; i++)
            {
                var orbital = Instantiate(orbitalPrefab, transform);
                var (pos, rot) = ElectronOrbitalFunction.GetCanonicalSlotPositionFromLocalDirection(dirs[i], bondRadius);
                orbital.transform.localPosition = pos;
                orbital.transform.localScale = Vector3.one * 0.6f;
                orbital.transform.localRotation = rot;
                orbital.ElectronCount = electronsPerOrbital[i];
                orbital.SetBondedAtom(this);
                BondOrbital(orbital);
            }
        }
        else
        {
            for (int i = 0; i < orbitalCount; i++)
            {
                var orbital = Instantiate(orbitalPrefab, transform);
                float angleDeg = angles[i];
                float rad = angleDeg * Mathf.Deg2Rad;
                Vector3 dir = new Vector3(Mathf.Cos(rad), Mathf.Sin(rad), 0f);
                orbital.transform.localPosition = dir * offset;
                orbital.transform.localScale = Vector3.one * 0.6f;
                orbital.transform.localRotation = Quaternion.Euler(0, 0, angleDeg);
                orbital.ElectronCount = electronsPerOrbital[i];
                orbital.SetBondedAtom(this);
                BondOrbital(orbital);
            }
        }
        SetupIgnoreCollisions();
    }
}
