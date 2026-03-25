using System.Collections.Generic;
using System.Text;
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
    /// <summary>Console logs tagged [FG-OrbDist] for Nitro / Carboxyl attach (orbital redistribution path).</summary>
    public static bool DebugFunctionalGroupOrbitalDistribution;

    [SerializeField] GameObject atomPrefab;
    [SerializeField] float viewportMargin = 0.1f;

    static void LogFgOrbit(string msg)
    {
        if (!DebugFunctionalGroupOrbitalDistribution) return;
        Debug.Log($"[FG-OrbDist] {msg}");
    }

    static string DescribeConnectedFragment(AtomFunction anchor)
    {
        if (anchor == null) return "anchor=null\n";
        var sb = new StringBuilder();
        foreach (var a in anchor.GetConnectedMolecule())
        {
            if (a == null) continue;
            int loneLobes = 0;
            foreach (var o in a.GetComponentsInChildren<ElectronOrbitalFunction>())
            {
                if (o.transform.parent != a.transform) continue;
                if (o.Bond == null) loneLobes++;
            }
            sb.AppendLine(
                $"  Z={a.AtomicNumber} id={a.GetInstanceID()} π={a.GetPiBondCount()} bondObjs={a.CovalentBonds.Count} loneLobes(Bond==null)={loneLobes} 1eOrbs={a.GetLoneOrbitalsWithOneElectronSortedByAngle().Count}");
        }
        return sb.ToString();
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

    /// <param name="attachToSelectedOrbital">
    /// Perspective (3D): bond ring to edit selection only when true (e.g. Shift+click). Orthographic (2D): restores legacy behavior — attach whenever selection has a free 1e orbital, ignoring this flag.
    /// </param>
    public void CreateCycloalkane(int ringSize, bool attachToSelectedOrbital = false)
    {
        if (ringSize < 3 || ringSize > 6 || atomPrefab == null || Camera.main == null) return;

        float bondLength = GetBondLength();
        var editMode = UnityEngine.Object.FindFirstObjectByType<EditModeManager>();
        AtomFunction anchorAtom = null;
        ElectronOrbitalFunction anchorOrbital = null;

        bool use3DRing = OrbitalAngleUtility.UseFull3DOrbitalGeometry;
        bool canAnchor = editMode != null && editMode.SelectedAtom != null && editMode.SelectedOrbital != null
            && editMode.SelectedOrbital.Bond == null && editMode.SelectedOrbital.ElectronCount == 1;
        // 2D: always fuse to selection when possible (previous toolbar behavior). 3D: only with explicit attach (Shift).
        bool anchoredToSelection = canAnchor && (!use3DRing || attachToSelectedOrbital);
        Vector3 center = GetViewportCenter();
        Vector3[] carbonPos = use3DRing
            ? CycloalkaneCarbonPositions3D(ringSize, bondLength, center)
            : CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, center, 0f);

        if (anchoredToSelection)
        {
            anchorAtom = editMode.SelectedAtom;
            anchorOrbital = editMode.SelectedOrbital;
            Vector3 dirOrb = anchorOrbital.transform.TransformDirection(Vector3.right).normalized;
            if (dirOrb.sqrMagnitude < 1e-6f) dirOrb = Vector3.right;
            if (!use3DRing)
            {
                dirOrb.z = 0f;
                if (dirOrb.sqrMagnitude < 1e-8f) dirOrb = Vector3.right;
                else dirOrb.Normalize();
            }

            carbonPos = use3DRing
                ? CycloalkaneCarbonPositions3D(ringSize, bondLength, Vector3.zero)
                : CycloalkaneCarbonPositionsPlanar(ringSize, bondLength, Vector3.zero, 0f);
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

        foreach (var a in atoms)
            if (a != null) a.RedistributeOrbitals();
        if (anchorAtom != null) anchorAtom.RedistributeOrbitals();

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

        foreach (var a in atoms)
            if (a != null) a.RedistributeOrbitals();
        if (anchorAtom != null) anchorAtom.RedistributeOrbitals();

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

        Vector3 pos = GetViewportCenter() + new Vector3(0.18f, 0.1f, 0f);
        Instantiate(ep, pos, Quaternion.identity);
        AtomFunction.SetupGlobalIgnoreCollisions();
    }

    void FormSigmaBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
    {
        if (atomA == null || atomB == null) return;
        var dirAtoB = (atomB.transform.position - atomA.transform.position).normalized;
        var dirBtoA = (atomA.transform.position - atomB.transform.position).normalized;

        var orbA = atomA.GetLoneOrbitalWithOneElectron(dirAtoB);
        var orbB = atomB.GetLoneOrbitalWithOneElectron(dirBtoA);
        if (orbA == null || orbB == null) return;

        FormSigmaBondInstant(atomA, atomB, orbA, orbB, redistributeEndpoints, redistributeEndpoints);
    }

    void FormSigmaBondInstant(
        AtomFunction atomA,
        AtomFunction atomB,
        ElectronOrbitalFunction orbA,
        ElectronOrbitalFunction orbB,
        bool redistributeAtomA = true,
        bool redistributeAtomB = true,
        HashSet<AtomFunction> pinAtomsForSigmaRelaxAtomA = null,
        HashSet<AtomFunction> pinAtomsForSigmaRelaxAtomB = null)
    {
        if (atomA == null || atomB == null || orbA == null || orbB == null) return;

        int merged = orbA.ElectronCount + orbB.ElectronCount;
        int sigmaBeforeA = atomA.GetDistinctSigmaNeighborCount();
        int sigmaBeforeB = atomB.GetDistinctSigmaNeighborCount();
        atomA.UnbondOrbital(orbA);
        atomB.UnbondOrbital(orbB);

        var bond = CovalentBond.Create(atomA, atomB, orbA, atomA, animateOrbitalToBond: false);
        if (bond != null)
        {
            orbA.ElectronCount = merged;
            Destroy(orbB.gameObject);
            if (redistributeAtomA)
                atomA.RedistributeOrbitals(newSigmaBondPartnerHint: atomB, sigmaNeighborCountBeforeHint: sigmaBeforeA, pinAtomsForSigmaRelax: pinAtomsForSigmaRelaxAtomA);
            if (redistributeAtomB)
                atomB.RedistributeOrbitals(newSigmaBondPartnerHint: atomA, sigmaNeighborCountBeforeHint: sigmaBeforeB, pinAtomsForSigmaRelax: pinAtomsForSigmaRelaxAtomB);
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    void FormPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
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
            if (redistributeEndpoints)
            {
                atomA.RedistributeOrbitals();
                atomB.RedistributeOrbitals();
            }
            atomA.RefreshCharge();
            atomB.RefreshCharge();
        }
    }

    /// <summary>Second π between the same pair (σ + π already present) for approximate triple bonds.</summary>
    void FormSecondPiBondInstant(AtomFunction atomA, AtomFunction atomB, bool redistributeEndpoints = true)
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
            if (redistributeEndpoints)
            {
                atomA.RedistributeOrbitals();
                atomB.RedistributeOrbitals();
            }
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
    bool BondSigma(AtomFunction a, AtomFunction b, bool redistributeAtomA = true, bool redistributeAtomB = true, HashSet<AtomFunction> pinAtomsForSigmaRelax = null)
    {
        Vector3 dAB = (b.transform.position - a.transform.position).normalized;
        Vector3 dBA = -dAB;
        var oa = a.GetLoneOrbitalForBondFormation(dAB);
        var ob = b.GetLoneOrbitalWithOneElectron(dBA);
        if (oa == null || ob == null) return false;
        FormSigmaBondInstant(a, b, oa, ob, redistributeAtomA, redistributeAtomB, pinAtomsForSigmaRelax, pinAtomsForSigmaRelax);
        return true;
    }

    static void RedistributeOrbitalsOnConnectedMolecule(AtomFunction anyAtomInFragment)
    {
        if (anyAtomInFragment == null) return;
        foreach (var atom in anyAtomInFragment.GetConnectedMolecule())
            if (atom != null) atom.RedistributeOrbitals();
    }

    /// <summary>Like <see cref="RedistributeOrbitalsOnConnectedMolecule"/> but skips <paramref name="skipAtom"/> (e.g. attachment carbon when preserving —CH₃ lobe/H geometry).</summary>
    static void RedistributeOrbitalsOnConnectedMoleculeExcept(AtomFunction anyAtomInFragment, AtomFunction skipAtom, HashSet<AtomFunction> pinAtomsForSigmaRelax = null)
    {
        if (anyAtomInFragment == null) return;
        foreach (var atom in anyAtomInFragment.GetConnectedMolecule())
        {
            if (atom == null || atom == skipAtom) continue;
            atom.RedistributeOrbitals(pinAtomsForSigmaRelax: pinAtomsForSigmaRelax);
        }
    }

    /// <summary>
    /// For each heavy in the FG (BFS from <paramref name="parent"/> along <paramref name="touched"/> only), Newman-stagger vs the σ partner one step toward parent (before H saturation).
    /// </summary>
    static void TryStaggerFunctionalGroupBackboneHeaviesTowardParent(AtomFunction parent, List<AtomFunction> touched)
    {
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry || parent == null || touched == null) return;
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
                if (atom.GetPiBondCount() > 0) continue;
                if (!towardParent.TryGetValue(atom, out var partner) || partner == null) continue;
                atom.TryStaggerNewmanRelativeToPartner(partner);
            }
        }
    }

    /// <summary>
    /// After σ framework: add π bonds toward FG-internal neighbors (not <paramref name="attachmentRoot"/>).
    /// Caps π by (1) octet / valence headroom: max total bond-order sum around the center minus current σ/π sum minus σ
    /// still needed (e.g. period-2 C with an O neighbor at single-bond order reserves one unit for the eventual C—H),
    /// (2) half-filled lone orbitals available before this pass, and (3) FormSecondPiBondInstant blocks period-2 C≡O
    /// (carbonyl stays C=O). Ethers stay π-free when headroom is 0.
    /// </summary>
    void TryFormPiBondsForFunctionalGroupCenter(AtomFunction center, AtomFunction attachmentRoot)
    {
        if (center == null) return;
        int v = center.GetMaxBondOrderSumAroundAtom();
        int s0 = center.GetSumBondOrderToNeighbors();
        int reserved = GetMinReservedBondOrderForPiPass(center);
        int initialOneE = center.GetLoneOrbitalsWithOneElectronSortedByAngle().Count;
        int maxPiBonds = Mathf.Min(initialOneE, Mathf.Max(0, v - s0 - reserved));
        LogFgOrbit(
            $"TryFormPiBonds center Z={center.AtomicNumber} id={center.GetInstanceID()}: valenceCap={v} sumBO={s0} reservedπ={reserved} 1eLone={initialOneE} maxPiForm={maxPiBonds} πNow={center.GetPiBondCount()}");
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
                    FormPiBondInstant(center, other, redistributeEndpoints: true);
                else if (bo == 2)
                    FormSecondPiBondInstant(center, other, redistributeEndpoints: true);

                if (center.GetPiBondCount() != piBefore)
                    progress = true;
            }

            if (!progress) break;
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
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            f.z = 0f;
            if (f.sqrMagnitude < 1e-8f) f = Vector3.right;
            else f.Normalize();
            right = new Vector3(-f.y, f.x, 0f);
            if (right.sqrMagnitude < 1e-8f) right = Vector3.up;
            else right.Normalize();
            up = Vector3.forward;
            return;
        }
        Vector3 refAxis = Mathf.Abs(Vector3.Dot(f, Vector3.up)) < 0.95f ? Vector3.up : Vector3.right;
        right = Vector3.Cross(refAxis, f).normalized;
        up = Vector3.Cross(f, right).normalized;
    }

    /// <summary>First atom of the group is placed at <paramref name="anchorWorldPos"/> and σ-bonded to <paramref name="parent"/>.</summary>
    /// <param name="preserveAttachmentParentGeometry">Kept for callers; attachment <paramref name="parent"/> is never VSEPR-redistributed during FG build and σ-relax pins keep it fixed.</param>
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

        bool traceNitroOrCarboxyl = DebugFunctionalGroupOrbitalDistribution
            && (kind == FunctionalGroupKind.Nitro || kind == FunctionalGroupKind.Carboxyl);
        if (traceNitroOrCarboxyl)
            LogFgOrbit($"BuildFunctionalGroup BEGIN kind={kind} use3D={OrbitalAngleUtility.UseFull3DOrbitalGeometry}");

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
        var pinFramework = new HashSet<AtomFunction>();
        foreach (var a in parent.GetConnectedMolecule())
            if (a != null) pinFramework.Add(a);
        // Keep attachment heavy un-redistributed; pins = whole pre-existing molecule so σ-relax on the new FG atom cannot rigid-rotate the framework (pinning only parent is insufficient).
        bool bondOk = BondSigma(parent, anchor, redistributeAtomA: false, redistributeAtomB: true, pinAtomsForSigmaRelax: pinFramework);
        if (!bondOk)
        {
            Destroy(anchor.gameObject);
            return false;
        }

        pinFramework = new HashSet<AtomFunction>(anchor.GetAtomsOnSideOfSigmaBond(parent));

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
                ok = BondSigma(anchor, c, pinAtomsForSigmaRelax: pinFramework);
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
                ok = BondSigma(anchor, o, pinAtomsForSigmaRelax: pinFramework);
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
                ok = BondSigma(anchor, oC, pinAtomsForSigmaRelax: pinFramework) && BondSigma(anchor, oH, pinAtomsForSigmaRelax: pinFramework);
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
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: pinFramework)
                    && BondSigma(anchor, o2, pinAtomsForSigmaRelax: pinFramework)
                    && BondSigma(anchor, o3, pinAtomsForSigmaRelax: pinFramework);
                // S=O π bonds from TryFormPiBondsForFunctionalGroupCenter; O—H from saturation pass.
                break;
            }
            case FunctionalGroupKind.Nitrile:
            {
                var n = SpawnAtomElement(7, anchor.transform.position + dirOut * L);
                if (n == null) { ok = false; break; }
                touched.Add(n);
                ok = BondSigma(anchor, n, pinAtomsForSigmaRelax: pinFramework);
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
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: pinFramework) && BondSigma(anchor, o2, pinAtomsForSigmaRelax: pinFramework);
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
                ok = BondSigma(anchor, o1, pinAtomsForSigmaRelax: pinFramework)
                    && BondSigma(anchor, o2, pinAtomsForSigmaRelax: pinFramework)
                    && BondSigma(anchor, o3, pinAtomsForSigmaRelax: pinFramework);
                // P=O π from TryFormPiBondsForFunctionalGroupCenter; two P—OH: O—H from saturation pass.
                break;
            }
        }

        if (!ok)
        {
            selectionAnchor = null;
            return false;
        }

            if (traceNitroOrCarboxyl)
            {
                LogFgOrbit($"After σ framework (before π): anchorZ={anchor.AtomicNumber} parentZ={parent.AtomicNumber}");
                LogFgOrbit($"Connected fragment snapshot:\n{DescribeConnectedFragment(anchor)}");
            }

            TryFormPiBondsForFunctionalGroupCenter(anchor, parent);

            if (traceNitroOrCarboxyl)
            {
                LogFgOrbit($"After TryFormPiBonds: anchor π={anchor.GetPiBondCount()} sumBO={anchor.GetSumBondOrderToNeighbors()}");
                LogFgOrbit($"Fragment before RedistributeOrbitals (post-π):\n{DescribeConnectedFragment(anchor)}");
            }

            // Skip attachment atom: keeps parent framework fixed; pins prevent σ-relax on the FG from dragging parent.
            RedistributeOrbitalsOnConnectedMoleculeExcept(anchor, parent, pinFramework);

            if (traceNitroOrCarboxyl)
                LogFgOrbit($"After RedistributeOrbitals #1:\n{DescribeConnectedFragment(anchor)}");

            TryStaggerFunctionalGroupBackboneHeaviesTowardParent(parent, touched);

            if (edit != null)
            {
                var fgOnly = new List<AtomFunction>();
                foreach (var a in touched)
                    if (a != null && a != parent)
                        fgOnly.Add(a);
                if (fgOnly.Count > 0)
                    edit.SaturateAtomsWithHydrogenPass(fgOnly, pinSigmaRelaxNeighbors: pinFramework);
            }

            RedistributeOrbitalsOnConnectedMoleculeExcept(anchor, parent, pinFramework);

            if (traceNitroOrCarboxyl)
            {
                LogFgOrbit($"After RedistributeOrbitals #2 (post H-saturation):\n{DescribeConnectedFragment(anchor)}");
                LogFgOrbit("BuildFunctionalGroup END");
            }

        AtomFunction.SetupGlobalIgnoreCollisions();
        return true;
    }

    /// <summary>~120° coplanar σ directions for two substituents (e.g. nitro O—N—O, aldehyde/carbonyl O vs hydroxyl O) given center→parent bond.</summary>
    static void ComputeTrigonalDirsTowardParentForFunctionalGroup(Vector3 centerWorld, Vector3 parentWorld, out Vector3 dirSubA, out Vector3 dirSubB)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            uR.z = 0f;
            if (uR.sqrMagnitude < 1e-10f) uR = Vector3.right;
            else uR.Normalize();
        }
        else if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tri = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(3), uR);
        dirSubA = tri[1].normalized;
        dirSubB = tri[2].normalized;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            ProjectFunctionalGroupDirToXY(ref dirSubA, uR, null);
            ProjectFunctionalGroupDirToXY(ref dirSubB, uR, dirSubA);
        }
    }

    /// <summary>Three σ directions from a tetrahedral frame aligned to center→parent (first arm); for sulfo/phosphate O placement.</summary>
    static void ComputeTetraThreeOxyDirsTowardParentForSulfo(Vector3 centerWorld, Vector3 parentWorld, out Vector3 d1, out Vector3 d2, out Vector3 d3)
    {
        Vector3 uR = parentWorld - centerWorld;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            uR.z = 0f;
            if (uR.sqrMagnitude < 1e-10f) uR = Vector3.right;
            else uR.Normalize();
        }
        else if (uR.sqrMagnitude < 1e-10f)
            uR = Vector3.right;
        else
            uR.Normalize();

        var tet = VseprLayout.AlignFirstDirectionTo(VseprLayout.GetIdealLocalDirections(4), uR);
        d1 = tet[1].normalized;
        d2 = tet[2].normalized;
        d3 = tet[3].normalized;
        if (!OrbitalAngleUtility.UseFull3DOrbitalGeometry)
        {
            ProjectFunctionalGroupDirToXY(ref d1, uR, null);
            ProjectFunctionalGroupDirToXY(ref d2, uR, d1);
            ProjectFunctionalGroupDirToXY(ref d3, uR, d1 + d2);
        }
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
