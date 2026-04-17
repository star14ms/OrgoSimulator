using System.Collections.Generic;
using UnityEngine;

/// <summary>Edit-mode toolbar functional groups: attach to a free orbital or replace selected hydrogen.</summary>
public enum FunctionalGroupKind
{
    AmineNH2,
    Hydroxyl,
    Methoxy,
    Aldehyde,
    Carboxyl,
    Sulfo,
    Nitrile,
    Nitro,
    PhosphateDihydrogen
}

/// <summary>
/// Creates preset molecules: cycloalkanes (C3-C6), benzene. Uses programmatic bond formation.
/// </summary>
public class MoleculeBuilder : MonoBehaviour
{
    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;
    /// <summary>Functional-group trigonal-planar diagnostics (C/N sp2 + fallback π). Default on for triage; set false for quiet runs.</summary>
    public static bool DebugLogFunctionalGroupTrigonalPlanarity = true;

    static void LogFunctionalGroupTrigonalPlanarity(string message)
    {
        if (!DebugLogFunctionalGroupTrigonalPlanarity) return;
        Debug.Log("[fg-sp2] " + message);
    }

    /// <summary>Bond length matches manual bonding: 2 * (bondRadius * 0.6) = 1.2 * bondRadius, from GetCanonicalSlotPosition in ElectronOrbitalFunction.</summary>
    float GetBondLength() => atomPrefab != null && atomPrefab.TryGetComponent<AtomFunction>(out var a) ? 1.2f * a.BondRadius : 0.96f;

    /// <summary>Ring radius for regular n-gon with chord length = bondLength. R = bondLength / (2 * sin(π/n)).</summary>
    static float GetRingRadius(int ringSize, float bondLength) =>
        bondLength / (2f * Mathf.Sin(Mathf.PI / ringSize));

    static void RescaleRingEdges(Vector3[] pts, Vector3 center, float targetEdge)
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
            pts[i] = center + (pts[i] - center) * s;
    }

    static Vector3[] CycloalkaneCarbonPositionsPlanar(int ringSize, float bondLength, Vector3 center, float rotOffsetDeg)
    {
        float ringRadius = GetRingRadius(ringSize, bondLength);
        var pts = new Vector3[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            float angleDeg = ringSize == 4
                ? 45f + 90f * i
                : 360f * i / ringSize - 90f;
            angleDeg += rotOffsetDeg;
            float angle = angleDeg * Mathf.Deg2Rad;
            pts[i] = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * ringRadius;
        }
        return pts;
    }

    /// <summary>Realistic 3D puckering: C3 shallow pyramid, C4 butterfly, C5 envelope, C6 chair.</summary>
    static Vector3[] CycloalkaneCarbonPositions3D(int ringSize, float bondLength, Vector3 center)
    {
        switch (ringSize)
        {
            case 3:
                return PuckeredCyclopropane(bondLength, center);
            case 4:
                return PuckeredCyclobutane(bondLength, center);
            case 5:
                return EnvelopeCyclopentane(bondLength, center);
            case 6:
                return ChairCyclohexane(bondLength, center);
            default:
                return CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, center, 0f);
        }
    }

    /// <summary>Slight non-planarity so each C—C—C plane has a distinct normal (improves out-of-plane H).</summary>
    static Vector3[] PuckeredCyclopropane(float bondLength, Vector3 center)
    {
        float r = GetRingRadius(3, bondLength);
        float lift = bondLength * 0.14f;
        var pts = new Vector3[3];
        for (int i = 0; i < 3; i++)
        {
            float ang = (360f * i / 3f - 90f) * Mathf.Deg2Rad;
            float z = i == 0 ? lift : (i == 1 ? -lift * 0.45f : -lift * 0.55f);
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] PuckeredCyclobutane(float bondLength, Vector3 center)
    {
        float r = bondLength * 0.74f;
        float d = bondLength * 0.22f;
        var pts = new Vector3[4];
        float[] zs = { d, -d, d, -d };
        for (int i = 0; i < 4; i++)
        {
            float ang = (45f + 90f * i) * Mathf.Deg2Rad;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, zs[i]);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] EnvelopeCyclopentane(float bondLength, Vector3 center)
    {
        int n = 5;
        float r = bondLength / (2f * Mathf.Sin(Mathf.PI / n));
        float flap = bondLength * 0.35f;
        float ripple = bondLength * 0.08f;
        var pts = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float ang = (360f * i / n - 90f) * Mathf.Deg2Rad;
            float z = (i == 2 ? flap : 0f) + Mathf.Sin(i * 2f * Mathf.PI / n) * ripple;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    static Vector3[] ChairCyclohexane(float bondLength, Vector3 center)
    {
        // Alternating ±h on a regular hexagon (radius r): all ring edges have length √(r²+4h²), and
        // cos(C–C–C) = (−½r²+4h²)/(r²+4h²). Tetrahedral 109.47° (cos = −⅓) ⇔ (h/r)² = 1/32.
        float r = bondLength * 0.72f;
        float h = r * Mathf.Sqrt(1f / 32f);
        var pts = new Vector3[6];
        for (int i = 0; i < 6; i++)
        {
            float ang = (60f * i - 90f) * Mathf.Deg2Rad;
            float z = i % 2 == 0 ? h : -h;
            pts[i] = center + new Vector3(Mathf.Cos(ang) * r, Mathf.Sin(ang) * r, z);
        }
        RescaleRingEdges(pts, center, bondLength);
        return pts;
    }

    void Awake()
    {
        if (atomPrefab == null)
        {
            var quickAdd = GetComponent<AtomQuickAddUI>();
            if (quickAdd != null)
                atomPrefab = quickAdd.GetAtomPrefab();
        }
    }

    public void SetAtomPrefab(GameObject prefab) => atomPrefab = prefab;

    /// <param name="attachToSelectedOrbital">When true (e.g. Shift+click), fuse the new ring to the edit selection’s free 1e orbital.</param>
    public void CreateCycloalkane(int ringSize, bool attachToSelectedOrbital = false)
    {
        if (ringSize < 3 || ringSize > 6 || atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        AtomFunction anchorAtom = null;
        ElectronOrbitalFunction anchorOrbital = null;

        bool canAnchor = editMode != null && editMode.SelectedAtom != null && editMode.SelectedOrbital != null
            && editMode.SelectedOrbital.Bond == null && editMode.SelectedOrbital.ElectronCount == 1;
        bool anchoredToSelection = canAnchor && attachToSelectedOrbital;
        Vector3 center = GetViewportCenter();
        Vector3[] carbonPos = CycloalkaneCarbonPositions3D(ringSize, bondLength, center);

        if (anchoredToSelection)
        {
            anchorAtom = editMode.SelectedAtom;
            anchorOrbital = editMode.SelectedOrbital;
            Vector3 dirOrb = anchorOrbital.transform.TransformDirection(Vector3.right).normalized;
            if (dirOrb.sqrMagnitude < 1e-6f) dirOrb = Vector3.right;

            carbonPos = CycloalkaneCarbonPositions3D(ringSize, bondLength, Vector3.zero);
            Vector3 p0 = carbonPos[0];
            Vector3 edge01 = (carbonPos[1] - p0).normalized;
            if (edge01.sqrMagnitude > 1e-8f)
            {
                Quaternion align = Quaternion.FromToRotation(edge01, dirOrb);
                for (int i = 0; i < ringSize; i++)
                    carbonPos[i] = align * (carbonPos[i] - p0) + p0;
            }
            Vector3 targetFirstC = anchorAtom.transform.position + dirOrb * bondLength;
            Vector3 delta = targetFirstC - carbonPos[0];
            for (int i = 0; i < ringSize; i++)
                carbonPos[i] += delta;
        }

        var atoms = new AtomFunction[ringSize];
        for (int i = 0; i < ringSize; i++)
        {
            var go = Instantiate(atomPrefab, carbonPos[i], Quaternion.identity);
            if (go.TryGetComponent<AtomFunction>(out var atom))
            {
                atom.AtomicNumber = 6;
                atom.ForceInitialize();
                atoms[i] = atom;
            }
        }

        for (int i = 0; i < ringSize; i++)
        {
            int next = (i + 1) % ringSize;
            FormSigmaBondInstant(atoms[i], atoms[next]);
        }

        if (anchorAtom != null && anchorOrbital != null && atoms[0] != null)
        {
            Vector3 dirToAnchor = (anchorAtom.transform.position - atoms[0].transform.position).normalized;
            var orb0 = atoms[0].GetLoneOrbitalWithOneElectron(dirToAnchor);
            if (orb0 != null)
                FormSigmaBondInstant(anchorAtom, atoms[0], anchorOrbital, orb0, true, true);
        }

        if (editMode != null)
        {
            editMode.SaturateCycloalkaneWithHydrogen(atoms);
            editMode.RefreshSelectedMoleculeAfterBondChange();
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    public void CreateBenzene()
    {
        if (atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        float ringRadius = GetRingRadius(6, bondLength);
        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        AtomFunction anchorAtom = null;
        ElectronOrbitalFunction anchorOrbital = null;
        Vector3 center;

        if (editMode != null && editMode.SelectedAtom != null && editMode.SelectedOrbital != null
            && editMode.SelectedOrbital.Bond == null && editMode.SelectedOrbital.ElectronCount == 1)
        {
            anchorAtom = editMode.SelectedAtom;
            anchorOrbital = editMode.SelectedOrbital;
            Vector3 dir = anchorOrbital.transform.TransformDirection(Vector3.right);
            float angle0 = -90f * Mathf.Deg2Rad; // atoms[0] at top
            Vector3 atom0Pos = anchorAtom.transform.position + dir * bondLength;
            center = atom0Pos - ringRadius * new Vector3(Mathf.Cos(angle0), Mathf.Sin(angle0), 0);
        }
        else
        {
            center = GetViewportCenter();
        }

        var atoms = new AtomFunction[6];
        for (int i = 0; i < 6; i++)
        {
            float angle = (60f * i - 90f) * Mathf.Deg2Rad;
            Vector3 pos = center + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0) * ringRadius;
            var go = Instantiate(atomPrefab, pos, Quaternion.identity);
            if (go.TryGetComponent<AtomFunction>(out var atom))
            {
                atom.AtomicNumber = 6;
                atom.ForceInitialize();
                atoms[i] = atom;
            }
        }

        // Full σ framework, then all π (Kekulé doubles on 0–1, 2–3, 4–5), then one redistribute.
        // Deferring per-bond redistribution keeps the first VSEPR pass from treating ring C as sp³;
        // with π present, orbitals settle as trigonal planar so H-auto stays in the ring plane.
        for (int i = 0; i < 6; i++)
        {
            int next = (i + 1) % 6;
            FormSigmaBondInstant(atoms[i], atoms[next], redistributeEndpoints: false);
        }

        if (anchorAtom != null && anchorOrbital != null && atoms[0] != null)
        {
            Vector3 dirToAnchor = (anchorAtom.transform.position - atoms[0].transform.position).normalized;
            var orb0 = atoms[0].GetLoneOrbitalWithOneElectron(dirToAnchor);
            if (orb0 != null)
                FormSigmaBondInstant(anchorAtom, atoms[0], anchorOrbital, orb0, false, false);
        }

        for (int i = 0; i < 3; i++)
        {
            int a = i * 2;
            int b = (a + 1) % 6;
            FormPiBondInstant(atoms[a], atoms[b], redistributeEndpoints: false);
        }

        if (editMode != null)
        {
            foreach (var a in atoms)
                if (a != null) editMode.SaturateWithHydrogen(a);
            editMode.RefreshSelectedMoleculeAfterBondChange();
        }

        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    /// <summary>Spawns only an electron at the work-plane (same prefab as orbital lone pairs), for 3D electron tests.</summary>
    public void CreateFreeElectronAtViewport()
    {
        if (atomPrefab == null || Camera.main == null) return;
        if (!atomPrefab.TryGetComponent<AtomFunction>(out var atomFn) || atomFn.OrbitalPrefab == null) return;
        var ep = atomFn.OrbitalPrefab.ElectronPrefab;
        if (ep == null) return;

        Vector2 screenElectron = ElectronCarryInput.PrimaryPointerScreen();
        Vector3 pos = PlanarPointerInteraction.SnapWorldToWorkPlaneIfPresent(
            PlanarPointerInteraction.ScreenToWorldPoint(screenElectron));
        var e = Instantiate(ep, pos, Quaternion.identity);
        AtomFunction.SetupGlobalIgnoreCollisions();
        ElectronCarryInput.Instance.StartCarrying(e);
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atomA == null || atomB == null) return;
        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        FormSigmaBondInstant(atomA, atomB, orbA, orbB, redistributeEndpoints, redistributeEndpoints, null, null, freezeSigmaNeighborSubtreeRoot);
    }

    void FormSigmaBondInstant(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        bool redistributeAtomA = true,
        bool redistributeAtomB = true,
        HashSet<AtomFunction> pinAtomsForSigmaRelaxAtomA = null,
        HashSet<AtomFunction> pinAtomsForSigmaRelaxAtomB = null,
        AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atomA == null || atomB == null || orbA == null || orbB == null) return;

        AtomPoseDirectionDebugLog.LogCarbonCarbonSigmaBeforeBond(atomA, atomB, "MoleculeBuilder.FormSigmaBondInstant");

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            atomA.RefreshCharge();
            atomB.RefreshCharge();

            if (redistributeAtomA || redistributeAtomB)
            {
                ElectronRedistributionOrchestrator.RunElectronRedistributionForBondEvent(
                    ElectronRedistributionOrchestrator.BondRedistributionEventId.SigmaFormation12,
                    atomA,
                    atomB,
                    orbA,
                    bond);
            }
        }
    }

    void FormPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 1) return;

        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        // Use bond-formation helper so O (and similar) can donate π from a 2e lone pair after σ is already in place.
        var orbA = atomA.GetLoneOrbitalForBondFormation(dirAtoB);
        var orbB = atomB.GetLoneOrbitalForBondFormation(dirBtoA);
        if (orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            _ = redistributeEndpoints;
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    /// <summary>Second π between the same pair (σ + π already present) for approximate triple bonds.</summary>
    void FormSecondPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        if (atomA == null || atomB == null) return;
        if (atomA.GetBondsTo(atomB) != 2) return;

        // Period-2 C–O: carbonyl is double (C=O); triple (C≡O) would consume the σ slot needed for C—H / R—C.
        if (atomA.AtomicNumber <= 10 && atomB.AtomicNumber <= 10)
        {
            bool co = (atomA.AtomicNumber == 6 && atomB.AtomicNumber == 8) || (atomA.AtomicNumber == 8 && atomB.AtomicNumber == 6);
            if (co) return;
        }

        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalForBondFormation(dirAtoB);
        var orbB = atomB.GetLoneOrbitalForBondFormation(dirBtoA);
        if (orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            _ = redistributeEndpoints;
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    /// <param name="formalCharge">Lewis formal charge before bonding; affects valence e⁻ count at init (e.g. nitro N⁺ → 4 e⁻, all 1e⁻ orbitals).</param>
    AtomFunction SpawnAtomElement(int z, Vector3 pos, int formalCharge = 0)
    {
        var go = Instantiate(atomPrefab, pos, Quaternion.identity);
        if (!go.TryGetComponent<AtomFunction>(out var a))
        {
            Destroy(go);
            return null;
        }
        a.AtomicNumber = z;
        a.Charge = formalCharge;
        a.ForceInitialize();
        return a;
    }

    /// <summary>σ bond with optional per-end RedistributeOrbitals (both true = next bond from each center sees updated VSEPR).</summary>
    bool BondSigma(AtomFunction a, AtomFunction b, bool redistributeAtomA = true, bool redistributeAtomB = true, HashSet<AtomFunction> pinAtomsForSigmaRelax = null, AtomFunction freezeSigmaNeighborSubtreeRoot = null)
    {
        Vector3 dAB = (b.transform.position - a.transform.position).normalized;
        Vector3 dBA = -dAB;
        var oa = a.GetLoneOrbitalForBondFormation(dAB);
        var ob = b.GetLoneOrbitalWithOneElectron(dBA);
        if (oa == null || ob == null) return false;
        FormSigmaBondInstant(a, b, oa, ob, redistributeAtomA, redistributeAtomB, pinAtomsForSigmaRelax, pinAtomsForSigmaRelax, freezeSigmaNeighborSubtreeRoot);
        return true;
    }

    /// <summary>
    /// After FG H saturation: Newman-stagger each heavy along the σ bond toward <paramref name="parent"/> (breadth order),
    /// including π-bearing centers (carbonyl C, nitrile C, …). Then sync σ hybrid directions on <paramref name="parent"/>
    /// and FG atoms from current nuclei + lobes so bond/orbital visuals match stagger and attachment.
    /// </summary>
    static void FinalizeFunctionalGroupNewmanAndSigmaHybridSync(AtomFunction parent, AtomFunction anchor, List<AtomFunction> touched)
    {
        if (parent == null || touched == null) return;
        var touchedSet = new HashSet<AtomFunction>();
        foreach (var a in touched)
            if (a != null) touchedSet.Add(a);
        if (!touchedSet.Contains(parent)) return;

        var depth = new Dictionary<AtomFunction, int>();
        var towardParent = new Dictionary<AtomFunction, AtomFunction>();
        var q = new Queue<AtomFunction>();
        depth[parent] = 0;
        q.Enqueue(parent);
        while (q.Count > 0)
        {
            var u = q.Dequeue();
            int du = depth[u];
            foreach (var b in u.CovalentBonds)
            {
                if (b == null) continue;
                var v = b.AtomA == u ? b.AtomB : b.AtomA;
                if (v == null || !touchedSet.Contains(v) || depth.ContainsKey(v)) continue;
                depth[v] = du + 1;
                towardParent[v] = u;
                q.Enqueue(v);
            }
        }

        int maxD = 0;
        foreach (var kv in depth)
            if (kv.Value > maxD) maxD = kv.Value;

        for (int lv = 1; lv <= maxD; lv++)
        {
            foreach (var atom in touched)
            {
                if (atom == null || atom == parent) continue;
                if (!depth.TryGetValue(atom, out int ad) || ad != lv) continue;
                if (atom.AtomicNumber <= 1) continue;
                if (!towardParent.TryGetValue(atom, out var partner) || partner == null) continue;
                // Trigonal sp² (e.g. aldehyde/carboxyl C, nitro N): Newman twist about R–X pulls σ substituents / lobes out of the π plane.
                int z = atom.AtomicNumber;
                if ((z == 6 || z == 7) && atom.GetPiBondCount() > 0 && atom.GetDistinctSigmaNeighborCount() == 3)
                {
                    LogFunctionalGroupTrigonalPlanarity(
                        "skip_newman_sp2 atom=" + atom.name + "(Z=" + atom.AtomicNumber + ") pi=" + atom.GetPiBondCount() +
                        " sigmaN=" + atom.GetDistinctSigmaNeighborCount());
                    continue;
                }
                atom.TryStaggerNewmanRelativeToPartner(partner);
            }
        }

        // Force sp2 electron geometry at π centers in the FG path (nitro-like N, carbonyl C, etc.)
        // after H saturation/Newman so local σ neighbors settle onto trigonal-planar when applicable.
        foreach (var atom in touched)
        {
            if (atom == null || atom == parent) continue;
            if (atom.GetPiBondCount() <= 0) continue;
            if (atom.GetDistinctSigmaNeighborCount() != 3) continue;
            if (!towardParent.TryGetValue(atom, out var partner) || partner == null) continue;
            LogFunctionalGroupTrigonalPlanarity(
                "post_finalize_sp2_redistribute atom=" + atom.name + "(Z=" + atom.AtomicNumber + ") pi=" + atom.GetPiBondCount() +
                " sigmaN=" + atom.GetDistinctSigmaNeighborCount() + " partner=" + partner.name + "(Z=" + partner.AtomicNumber + ")");
            CovalentBond piOp = null;
            foreach (var b in atom.CovalentBonds)
            {
                if (b == null || b.AtomA == null || b.AtomB == null || b.IsSigmaBondLine()) continue;
                var oth = b.AtomA == atom ? b.AtomB : b.AtomA;
                if (oth == partner) { piOp = b; break; }
            }
            ForceTrigonalPlanarNeighborPositionsForPiCenter(atom, partner);
        }

        if (anchor != null)
            parent.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(anchor);

        foreach (var atom in touched)
        {
            if (atom == null || atom == parent) continue;
            if (!towardParent.TryGetValue(atom, out var p) || p == null) continue;
            atom.RefreshSigmaBondOrbitalHybridAlignmentAfterFormationRedistribute(p);
        }
    }

    /// <summary>
    /// Hard-enforce trigonal-planar neighbor positions (120°) for π C/N FG centers after final redistribute.
    /// This is a deterministic fallback for cases where TryComputeTrigonalPlanarSigmaNeighborRelaxTargets returns targets
    /// but maps to near-zero world movement due to rigid-fragment mapping/gauge.
    /// </summary>
    static void ForceTrigonalPlanarNeighborPositionsForPiCenter(AtomFunction center, AtomFunction towardParentPartner)
    {
        if (center == null || towardParentPartner == null) return;
        int z = center.AtomicNumber;
        if (z != 6 && z != 7) return;
        if (center.GetPiBondCount() <= 0) return;

        var neighbors = center.GetDistinctSigmaNeighborAtoms();
        if (neighbors == null || neighbors.Count != 3) return;

        Vector3 refW = towardParentPartner.transform.position - center.transform.position;
        if (refW.sqrMagnitude < 1e-10f) return;
        refW.Normalize();

        Vector3 refL = center.transform.InverseTransformDirection(refW);
        if (refL.sqrMagnitude < 1e-10f) refL = Vector3.right;
        else refL.Normalize();

        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), refL);
        var idealW = new List<Vector3>(3);
        for (int i = 0; i < 3; i++)
            idealW.Add(center.transform.TransformDirection(tri[i]).normalized);

        // Reserve slot nearest to the parent-side bond so that axis stays anchored.
        int parentSlot = 0;
        float parentBest = -2f;
        for (int i = 0; i < idealW.Count; i++)
        {
            float d = Vector3.Dot(idealW[i], refW);
            if (d > parentBest)
            {
                parentBest = d;
                parentSlot = i;
            }
        }

        var movableNeighbors = new List<AtomFunction>();
        foreach (var n in neighbors)
            if (n != null && n != towardParentPartner)
                movableNeighbors.Add(n);
        if (movableNeighbors.Count == 0) return;

        var freeSlots = new List<int>();
        for (int i = 0; i < 3; i++)
            if (i != parentSlot)
                freeSlots.Add(i);

        foreach (var n in movableNeighbors)
        {
            Vector3 d = n.transform.position - center.transform.position;
            if (d.sqrMagnitude < 1e-10f) continue;
            d.Normalize();

            int bestSlot = -1;
            float bestDot = -2f;
            for (int i = 0; i < freeSlots.Count; i++)
            {
                int slot = freeSlots[i];
                float dot = Vector3.Dot(d, idealW[slot]);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    bestSlot = slot;
                }
            }
            if (bestSlot < 0) continue;

            float bondLen = Vector3.Distance(center.transform.position, n.transform.position);
            Vector3 before = n.transform.position;
            Vector3 targetPos = center.transform.position + idealW[bestSlot] * bondLen;
            Vector3 oldDir = before - center.transform.position;
            Vector3 newDir = targetPos - center.transform.position;
            if (oldDir.sqrMagnitude > 1e-10f && newDir.sqrMagnitude > 1e-10f)
            {
                Quaternion r = Quaternion.FromToRotation(oldDir.normalized, newDir.normalized);
                var frag = center.GetAtomsOnSideOfSigmaBond(n);
                foreach (var a in frag)
                {
                    if (a == null) continue;
                    a.transform.position = center.transform.position + r * (a.transform.position - center.transform.position);
                }
            }
            else
                n.transform.position = targetPos;
            LogFunctionalGroupTrigonalPlanarity(
                "force_sp2_neighbor center=" + center.name + "(Z=" + center.AtomicNumber + ") neighbor=" +
                n.name + "(Z=" + n.AtomicNumber + ") moved=" + Vector3.Distance(before, n.transform.position).ToString("F5"));
            freeSlots.Remove(bestSlot);
            if (freeSlots.Count == 0) break;
        }
    }

    /// <summary>
    /// After σ framework: add π bonds toward FG-internal neighbors (not <paramref name="attachmentRoot"/>).
    /// Caps π by bond-order headroom from <see cref="AtomFunction.GetMaxBondOrderSumAroundAtom"/> (five for period-3+ group 15, six for group 16),
    /// minus σ reserved via <see cref="GetMinReservedBondOrderForPiPass"/> (e.g. aldehyde C—H). Expanded-valence centers skip
    /// the lone-1e pre-count cap so π can consume headroom; others use Min(initialOneE, headroom). FormSecondPiBondInstant
    /// still blocks period-2 C≡O.
    /// </summary>
    void TryFormPiBondsForFunctionalGroupCenter(AtomFunction center, AtomFunction attachmentRoot)
    {
        if (center == null) return;
        int slots = center.GetOrbitalSlotCount();
        // Period-3+ group 15/16 only exceed four slots today; need lone→empty transfer for π headroom like phosphate / sulfo.
        if (slots > 4)
            center.TryTransferElectronFromLonePairToEmptyOrbitals();
        int v = center.GetMaxBondOrderSumAroundAtom();
        int s0 = center.GetSumBondOrderToNeighbors();
        int reserved = GetMinReservedBondOrderForPiPass(center);
        int initialOneE = center.GetLoneOrbitalsWithOneElectronSortedByAngle().Count;
        int headroom = Mathf.Max(0, v - s0 - reserved);
        bool expandedOctetCenter = slots > 4;
        int maxPiBonds = expandedOctetCenter ? headroom : Mathf.Min(initialOneE, headroom);
        if (maxPiBonds <= 0) return;

        for (int round = 0; round < 8; round++)
        {
            if (center.GetPiBondCount() >= maxPiBonds) break;

            bool progress = false;
            var neighbors = new List<AtomFunction>();
            var seen = new HashSet<AtomFunction>();
            foreach (var b in center.CovalentBonds)
            {
                if (b == null) continue;
                var other = b.AtomA == center ? b.AtomB : b.AtomA;
                if (other == null || !seen.Add(other)) continue;
                neighbors.Add(other);
            }

            foreach (var other in neighbors)
            {
                if (center.GetPiBondCount() >= maxPiBonds) break;

                if (attachmentRoot != null && other == attachmentRoot)
                    continue;

                int bo = center.GetBondsTo(other);
                if (bo <= 0 || bo >= 3) continue;

                int piBefore = center.GetPiBondCount();
                if (bo == 1)
                    FormPiBondInstant(center, other, redistributeEndpoints: true, freezeSigmaNeighborSubtreeRoot: attachmentRoot);
                else if (bo == 2)
                    FormSecondPiBondInstant(center, other, redistributeEndpoints: true, freezeSigmaNeighborSubtreeRoot: attachmentRoot);

                if (center.GetPiBondCount() != piBefore)
                    progress = true;
            }

            if (!progress) break;
        }
    }

    /// <summary>
    /// FG safety pass: if a trigonal center (C/N) still has no π after build/saturation, try one π to an O neighbor.
    /// This keeps carbonyl-like C and nitrate/nitro-like N in sp2 electron geometry for FG-internal centers.
    /// The attachment root (substrate atom) is excluded to avoid unintended bond-order promotion on the parent molecule.
    /// </summary>
    void EnsureSinglePiOnTrigonalCnCenters(List<AtomFunction> touched, AtomFunction attachmentRoot)
    {
        if (touched == null || touched.Count == 0) return;
        foreach (var center in touched)
        {
            if (center == null) continue;
            if (attachmentRoot != null && center == attachmentRoot) continue;
            int z = center.AtomicNumber;
            if (z != 6 && z != 7) continue;
            if (center.GetPiBondCount() > 0) continue;
            if (center.GetDistinctSigmaNeighborCount() != 3) continue;

            AtomFunction bestO = null;
            foreach (var b in center.CovalentBonds)
            {
                if (b?.AtomA == null || b?.AtomB == null) continue;
                var other = b.AtomA == center ? b.AtomB : b.AtomA;
                if (other == null || other == attachmentRoot) continue;
                if (other.AtomicNumber != 8) continue;
                if (center.GetBondsTo(other) != 1) continue;
                bestO = other;
                break;
            }
            if (bestO == null) continue;

            int piBefore = center.GetPiBondCount();
            LogFunctionalGroupTrigonalPlanarity(
                "ensure_pi_try atom=" + center.name + "(Z=" + z + ") piBefore=" + piBefore +
                " sigmaN=" + center.GetDistinctSigmaNeighborCount() + " targetO=" + bestO.name + "(Z=" + bestO.AtomicNumber + ")");
            FormPiBondInstant(center, bestO, redistributeEndpoints: true, freezeSigmaNeighborSubtreeRoot: attachmentRoot);
            if (center.GetPiBondCount() == piBefore)
            {
                // Retry once after lone→empty rebalance if no 1e donor was available on first attempt.
                LogFunctionalGroupTrigonalPlanarity(
                    "ensure_pi_retry atom=" + center.name + "(Z=" + z + ") reason=no_pi_progress");
                center.TryTransferElectronFromLonePairToEmptyOrbitals();
                bestO.TryTransferElectronFromLonePairToEmptyOrbitals();
                FormPiBondInstant(center, bestO, redistributeEndpoints: true, freezeSigmaNeighborSubtreeRoot: attachmentRoot);
            }
            LogFunctionalGroupTrigonalPlanarity(
                "ensure_pi_result atom=" + center.name + "(Z=" + z + ") piAfter=" + center.GetPiBondCount() +
                " bondsToO=" + center.GetBondsTo(bestO));
        }
    }

    /// <summary>
    /// Bond-order units to leave for σ bonds that will form after the π pass (e.g. aldehyde C: one half-filled orbital pairs with H).
    /// Nitrile C has no O neighbor with σ-only to carbon, so reserve stays 0 (both 1e lobes can go into π toward N).
    /// </summary>
    static int GetMinReservedBondOrderForPiPass(AtomFunction center)
    {
        if (center == null) return 0;
        if (center.AtomicNumber != 6 || center.AtomicNumber > 10) return 0;
        int oneE = center.GetLoneOrbitalsWithOneElectronSortedByAngle().Count;
        if (oneE < 2) return 0;
        foreach (var b in center.CovalentBonds)
        {
            if (b?.AtomA == null || b?.AtomB == null) continue;
            var other = b.AtomA == center ? b.AtomB : b.AtomA;
            if (other == null || other.AtomicNumber != 8) continue;
            if (center.GetBondsTo(other) == 1) return 1;
        }
        return 0;
    }

    static void GetAttachBasis(Vector3 dirParentToAnchor, out Vector3 right, out Vector3 up)
    {
        Vector3 f = dirParentToAnchor.normalized;
        Vector3 refAxis = Mathf.Abs(Vector3.Dot(f, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
        right = Vector3.Cross(refAxis, f).normalized;
        up = Vector3.Cross(f, right).normalized;
    }

    /// <summary>First atom of the group is placed at <paramref name="anchorWorldPos"/> and σ-bonded to <paramref name="parent"/>.</summary>
    /// <param name="preserveAttachmentParentGeometry">Kept for callers; attachment <paramref name="parent"/> is never VSEPR-redistributed during FG build and σ-relax treats it as frozen (no O(N) full-molecule pin set).</param>
    public bool BuildFunctionalGroup(
        FunctionalGroupKind kind,
        AtomFunction parent,
        Vector3 anchorWorldPos,
        EditModeManager edit,
        out AtomFunction selectionAnchor,
        bool preserveAttachmentParentGeometry = false)
    {
        selectionAnchor = null;
        if (parent == null || atomPrefab == null) return false;

        Vector3 dirOut = anchorWorldPos - parent.transform.position;
        if (dirOut.sqrMagnitude < 1e-10f) return false;
        dirOut.Normalize();

        int z0 = kind switch
        {
            FunctionalGroupKind.AmineNH2 => 7,
            FunctionalGroupKind.Hydroxyl => 8,
            FunctionalGroupKind.Methoxy => 8,
            FunctionalGroupKind.Aldehyde => 6,
            FunctionalGroupKind.Carboxyl => 6,
            FunctionalGroupKind.Sulfo => 16,
            FunctionalGroupKind.Nitrile => 6,
            FunctionalGroupKind.Nitro => 7,
            FunctionalGroupKind.PhosphateDihydrogen => 15,
            _ => 6
        };

        // Nitro R—NO₂: nitrogen is modeled as N⁺ (one valence e⁻ removed) → 4 e⁻ in 4 orbitals, all single-electron (no 2e lone pair).
        int anchorFormalCharge = kind == FunctionalGroupKind.Nitro ? 1 : 0;
        var anchor = SpawnAtomElement(z0, anchorWorldPos, anchorFormalCharge);
        if (anchor == null) return false;

        AtomFunction.SuppressAutoGlobalIgnoreCollisions++;
        try
        {
        // Substrate frozen via freezeSigmaNeighborSubtreeRoot ((parent) on FG redistributes — no O(N) pin hash, no fragment relax over parent.
        bool bondOk = BondSigma(parent, anchor, redistributeAtomA: false, redistributeAtomB: true, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
        if (!bondOk)
        {
            Destroy(anchor.gameObject);
            return false;
        }

        selectionAnchor = anchor;
        float L = GetBondLength();
        var touched = new List<AtomFunction> { parent, anchor };
        GetAttachBasis(dirOut, out var right, out var up);

        bool ok = true;
        switch (kind)
        {
            case FunctionalGroupKind.AmineNH2:
                // Two N—H from SaturateAtomsWithHydrogenPass (FG atoms only) after σ + redistribute.
                break;
            case FunctionalGroupKind.Hydroxyl:
                // O—H from saturation pass.
                break;
            case FunctionalGroupKind.Methoxy:
            {
                Vector3 oc = anchor.transform.position + right * L;
                var c = SpawnAtomElement(6, oc);
                if (c == null) { ok = false; break; }
                touched.Add(c);
                ok = BondSigma(anchor, c, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                break;
            }
            case FunctionalGroupKind.Aldehyde:
            {
                // Place C=O along trigonal 120° layout (not GetAttachBasis "right", which is ~90° to R—C and breaks sp² angles).
                Vector3 cPos = anchor.transform.position;
                ComputeTrigonalDirsTowardParentForFunctionalGroup(cPos, parent.transform.position, out Vector3 dirCarbonylO, out _);
                var o = SpawnAtomElement(8, cPos + dirCarbonylO * L);
                if (o == null) { ok = false; break; }
                touched.Add(o);
                ok = BondSigma(anchor, o, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                // C=O π from TryFormPiBondsForFunctionalGroupCenter after σ.
                // Aldehyde C—H from saturation pass after π (third σ ~120° from R—C and C=O).
                break;
            }
            case FunctionalGroupKind.Carboxyl:
            {
                Vector3 cPos = anchor.transform.position;
                ComputeTrigonalDirsTowardParentForFunctionalGroup(cPos, parent.transform.position, out Vector3 dirCarbonylO, out Vector3 dirHydroxylO);

                var oC = SpawnAtomElement(8, cPos + dirCarbonylO * L);
                var oH = SpawnAtomElement(8, cPos + dirHydroxylO * L);
                if (oC == null || oH == null) { ok = false; break; }
                touched.Add(oC);
                touched.Add(oH);
                ok = BondSigma(anchor, oC, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent) && BondSigma(anchor, oH, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                // C=O π from TryFormPiBondsForFunctionalGroupCenter.
                // Carboxylic O—H from saturation pass.
                break;
            }
            case FunctionalGroupKind.Sulfo:
            {
                Vector3 sPos = anchor.transform.position;
                ComputeTetraThreeOxyDirsTowardParentForSulfo(sPos, parent.transform.position, out Vector3 d1, out Vector3 d2, out Vector3 d3);
                var o1 = SpawnAtomElement(8, sPos + d1 * L);
                var o2 = SpawnAtomElement(8, sPos + d2 * L);
                var o3 = SpawnAtomElement(8, sPos + d3 * L);
                if (o1 == null || o2 == null || o3 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                touched.Add(o3);
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent)
                    && BondSigma(anchor, o2, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent)
                    && BondSigma(anchor, o3, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                // Two S=O π (headroom cap for Z=16, still 4 orbitals) + single S—O⁻/OH; O—H from saturation.
                break;
            }
            case FunctionalGroupKind.Nitrile:
            {
                var n = SpawnAtomElement(7, anchor.transform.position + dirOut * L);
                if (n == null) { ok = false; break; }
                touched.Add(n);
                ok = BondSigma(anchor, n, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                // C≡N: two π from TryFormPiBondsForFunctionalGroupCenter.
                break;
            }
            case FunctionalGroupKind.Nitro:
            {
                // Planar nitro: trigonal σ framework (~120°) in the R—N plane; one N=O π from TryFormPiBondsForFunctionalGroupCenter.
                // N⁺ was spawned with +1 formal charge → four 1e⁻ orbitals (no lone pair lobe).
                Vector3 nPos = anchor.transform.position;
                ComputeTrigonalDirsTowardParentForFunctionalGroup(nPos, parent.transform.position, out Vector3 dirO1, out Vector3 dirO2);
                var o1 = SpawnAtomElement(8, nPos + dirO1 * L);
                var o2 = SpawnAtomElement(8, nPos + dirO2 * L);
                if (o1 == null || o2 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent) && BondSigma(anchor, o2, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                break;
            }
            case FunctionalGroupKind.PhosphateDihydrogen:
            {
                // R—P(=O)(OH)₂: tetrahedral at P (same σ layout as sulfo); two hydroxyl O + one P=O.
                Vector3 pPos = anchor.transform.position;
                ComputeTetraThreeOxyDirsTowardParentForSulfo(pPos, parent.transform.position, out Vector3 d1, out Vector3 d2, out Vector3 d3);
                var o1 = SpawnAtomElement(8, pPos + d1 * L);
                var o2 = SpawnAtomElement(8, pPos + d2 * L);
                var o3 = SpawnAtomElement(8, pPos + d3 * L);
                if (o1 == null || o2 == null || o3 == null) { ok = false; break; }
                touched.Add(o1);
                touched.Add(o2);
                touched.Add(o3);
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent)
                    && BondSigma(anchor, o2, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent)
                    && BondSigma(anchor, o3, pinAtomsForSigmaRelax: null, freezeSigmaNeighborSubtreeRoot: parent);
                // P=O π + two P—OH (π cap for Z=15); O—H from saturation.
                break;
            }
        }

        if (!ok)
        {
            selectionAnchor = null;
            return false;
        }

        TryFormPiBondsForFunctionalGroupCenter(anchor, parent);

        if (edit != null)
        {
            var fgOnly = new List<AtomFunction>();
            foreach (var a in touched)
                if (a != null && a != parent)
                {
                    // Nitro is modeled as R—N(+)(=O)—O: do not H-auto the anchor N.
                    // If N gets saturated here, an unintended N—H can be formed and later sp2 cleanup can move that H oddly.
                    if (kind == FunctionalGroupKind.Nitro && a == anchor)
                    {
                        LogFunctionalGroupTrigonalPlanarity(
                            "nitro_skip_anchor_hauto atom=" + a.name + "(Z=" + a.AtomicNumber + ")");
                        continue;
                    }
                    fgOnly.Add(a);
                }
            if (kind == FunctionalGroupKind.Nitro && anchor != null)
            {
                // Consistency with protonated FG style: nitro as R—N(=O)—OH (one H on the singly bonded oxygen only).
                // Restrict saturation to that O so we avoid ambiguous/multiple nitro H placements.
                AtomFunction singleBondO = null;
                foreach (var b in anchor.CovalentBonds)
                {
                    if (b?.AtomA == null || b?.AtomB == null) continue;
                    var o = b.AtomA == anchor ? b.AtomB : b.AtomA;
                    if (o == null || o.AtomicNumber != 8) continue;
                    if (anchor.GetBondsTo(o) == 1) { singleBondO = o; break; }
                }
                fgOnly.Clear();
                if (singleBondO != null)
                {
                    fgOnly.Add(singleBondO);
                    LogFunctionalGroupTrigonalPlanarity(
                        "nitro_protonated_target_O atom=" + singleBondO.name + "(Z=" + singleBondO.AtomicNumber + ")");
                }
            }
            if (fgOnly.Count > 0)
                edit.SaturateAtomsWithHydrogenPass(
                    fgOnly,
                    pinSigmaRelaxNeighbors: null,
                    incrementalIgnoreSubstrateParent: parent,
                    incrementalIgnoreGroupAnchor: anchor,
                    freezeSigmaNeighborSubtreeRoot: parent);
        }

        EnsureSinglePiOnTrigonalCnCenters(touched, parent);

        // H saturation and post-saturation redistribute rebuild local tetrahedra; Newman + σ hybrid sync last.
        FinalizeFunctionalGroupNewmanAndSigmaHybridSync(parent, anchor, touched);

        return true;
        }
        finally
        {
            AtomFunction.SuppressAutoGlobalIgnoreCollisions--;
            if (selectionAnchor != null && parent != null)
                AtomFunction.SetupIgnoreCollisionsInvolvingAtoms(parent.GetAtomsOnSideOfSigmaBond(selectionAnchor));
            else
                AtomFunction.SetupGlobalIgnoreCollisions();
        }
    }

    /// <summary>~120° coplanar σ directions for two substituents (e.g. nitro O—N—O, aldehyde/carbonyl O vs hydroxyl O) given center→parent bond.</summary>
    static void ComputeTrigonalDirsTowardParentForFunctionalGroup(Vector3 centerWorld, Vector3 parentWorld, out Vector3 dirSubA, out Vector3 dirSubB)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), uR);
        dirSubA = tri[1].normalized;
        dirSubB = tri[2].normalized;
    }

    /// <summary>Three σ directions from a tetrahedral frame aligned to center→parent (first arm); for sulfo/phosphate O placement.</summary>
    static void ComputeTetraThreeOxyDirsTowardParentForSulfo(Vector3 centerWorld, Vector3 parentWorld, out Vector3 d1, out Vector3 d2, out Vector3 d3)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tet = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(4), uR);
        d1 = tet[1].normalized;
        d2 = tet[2].normalized;
        d3 = tet[3].normalized;
    }

    static void ProjectFunctionalGroupDirToXY(ref Vector3 v, Vector3 uR, Vector3? sumPrior)
    {
        v.z = 0f;
        if (v.sqrMagnitude > 1e-10f)
        {
            v.Normalize();
            return;
        }
        if (!sumPrior.HasValue)
            v = new Vector3(-uR.y, uR.x, 0f);
        else
        {
            Vector3 s = sumPrior.Value;
            s.z = 0f;
            v = (-uR - s);
        }
        if (v.sqrMagnitude > 1e-10f)
            v.Normalize();
        else
            v = new Vector3(-uR.y, uR.x, 0f).normalized;
    }

    Vector3 GetViewportCenter() => PlanarPointerInteraction.ViewportCenterOnWorkPlane(viewportMargin);
}
